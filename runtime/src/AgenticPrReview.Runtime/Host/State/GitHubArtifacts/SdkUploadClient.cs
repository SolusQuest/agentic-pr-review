using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgenticPrReview.Runtime.Host.State.GitHubArtifacts;

internal sealed record SdkUploadRequest(
    [property: JsonRequired] string Protocol,
    [property: JsonRequired] string BuildDiscriminator,
    [property: JsonRequired] string CorrelationId,
    [property: JsonRequired] string RunId,
    [property: JsonRequired] string RunAttempt,
    [property: JsonRequired] string PhysicalName,
    [property: JsonRequired] string SourceRelativePath,
    [property: JsonRequired] string SourceSize,
    [property: JsonRequired] string SourceSha256,
    [property: JsonRequired] string RetentionDays,
    [property: JsonRequired] string TimeoutMs)
{
    public override string ToString() => "SdkUploadRequest";
}

internal sealed record SdkUploadReply(
    [property: JsonRequired] string Protocol,
    [property: JsonRequired] string BuildDiscriminator,
    [property: JsonRequired] string CorrelationId,
    [property: JsonRequired] string Dispatch,
    [property: JsonRequired] string Failure,
    [property: JsonRequired] ArtifactUploadReceiptDocument? Receipt)
{
    public override string ToString() => "SdkUploadReply";
}

[JsonSerializable(typeof(SdkUploadRequest))]
[JsonSerializable(typeof(SdkUploadReply))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = false, DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
    MaxDepth = 4)]
internal sealed partial class SdkUploadJsonContext : JsonSerializerContext;

internal sealed class SdkUploadClient(string endpoint, IArtifactBridgeConnectionFactory? connectionFactory = null)
{
    private readonly IArtifactBridgeConnectionFactory connections = connectionFactory ?? new ArtifactBridgeEndpointConnectionFactory();
    internal const string Protocol = "apr.sdk.upload.cr2";

    internal async Task<SdkUploadReply> UploadAsync(SdkUploadRequest request, CancellationToken cancellationToken)
    {
        var dispatched = false;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, SdkUploadJsonContext.Default.SdkUploadRequest);
        byte[]? replyBytes = null;
        try
        {
            if (bytes.Length is < 1 or > 262144) throw new IOException("upload_frame_invalid");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(Math.Min(30_000, int.Parse(request.TimeoutMs, System.Globalization.CultureInfo.InvariantCulture))));
            await using var stream = await connections.ConnectAsync(endpoint, deadline.Token).ConfigureAwait(false);
            var prefix = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(prefix, checked((uint)bytes.Length));
            // Mark before the first write: partial transport is conservatively possible dispatch.
            dispatched = true;
            await stream.WriteAsync(prefix, deadline.Token).ConfigureAwait(false);
            await stream.WriteAsync(bytes, deadline.Token).ConfigureAwait(false);
            connections.CompleteUploadRequest(stream);
            await stream.ReadExactlyAsync(prefix, deadline.Token).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadUInt32BigEndian(prefix);
            if (length is < 1 or > 262144) throw new IOException("upload_frame_invalid");
            replyBytes = new byte[checked((int)length)];
            await stream.ReadExactlyAsync(replyBytes, deadline.Token).ConfigureAwait(false);
            if (await stream.ReadAsync(prefix.AsMemory(0, 1), deadline.Token).ConfigureAwait(false) != 0)
                throw new IOException("upload_frame_trailing");
            var reply = JsonSerializer.Deserialize(replyBytes, SdkUploadJsonContext.Default.SdkUploadReply);
            if (reply is null || !ValidReply(reply, request)) throw new IOException("upload_reply_invalid");
            return reply;
        }
        catch (Exception error) when (error is IOException or SocketException or JsonException or OperationCanceledException or FormatException or OverflowException)
        {
            throw new ArtifactBridgeExchangeException(dispatched, cancellationToken.IsCancellationRequested);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (replyBytes is not null) CryptographicOperations.ZeroMemory(replyBytes);
        }
    }

    internal static bool ValidReply(SdkUploadReply reply, SdkUploadRequest request)
    {
        if (reply.Protocol != Protocol || reply.BuildDiscriminator != request.BuildDiscriminator || reply.CorrelationId != request.CorrelationId) return false;
        var receipt = reply.Receipt;
        var validReceipt = receipt is not null && ArtifactTransportFormat.PositiveDecimal(receipt.ArtifactId, out _) &&
            ArtifactTransportFormat.Digest(receipt.ArchiveSha256) && ArtifactTransportFormat.PositiveDecimal(receipt.ArchiveSize, out var size) && size <= ArtifactBridgeLimits.MaximumArchiveBytes;
        return reply.Dispatch switch
        {
            "not_dispatched" => receipt is null && reply.Failure is "invalid" or "cancelled" or "io" or "cleanup",
            "dispatched" => reply.Failure switch
            {
                "none" => validReceipt,
                "conflict" => receipt is null,
                "outcome_unknown" => receipt is null || validReceipt,
                _ => false,
            },
            _ => false,
        };
    }
}

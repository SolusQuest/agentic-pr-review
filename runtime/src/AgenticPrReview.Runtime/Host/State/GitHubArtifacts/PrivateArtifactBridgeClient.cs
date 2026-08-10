using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text.Json.Serialization.Metadata;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.Serialization;

namespace AgenticPrReview.Runtime.Host.State.GitHubArtifacts;

internal interface IArtifactBridgeConnectionFactory
{
    ValueTask<Stream> ConnectAsync(
        string endpoint,
        CancellationToken cancellationToken);
}

internal sealed class ArtifactBridgeEndpointConnectionFactory
    : IArtifactBridgeConnectionFactory
{
    public async ValueTask<Stream> ConnectAsync(
        string endpoint,
        CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            const string prefix = @"\\.\pipe\";
            if (!endpoint.StartsWith(prefix, StringComparison.Ordinal) ||
                endpoint.Length <= prefix.Length)
            {
                throw new IOException("artifact_bridge_endpoint_invalid");
            }

            var stream = new NamedPipeClientStream(
                ".",
                endpoint[prefix.Length..],
                PipeDirection.InOut,
                PipeOptions.Asynchronous,
                TokenImpersonationLevel.Anonymous);
            try
            {
                await stream.ConnectAsync(cancellationToken)
                    .ConfigureAwait(false);
                return stream;
            }
            catch
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        var socket = new Socket(
            AddressFamily.Unix,
            SocketType.Stream,
            ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(
                    new UnixDomainSocketEndPoint(endpoint),
                    cancellationToken)
                .ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

internal sealed class ArtifactBridgeExchangeException(
    bool requestDispatched) : IOException("artifact_bridge_exchange_failed")
{
    internal bool RequestDispatched { get; } = requestDispatched;
}

internal sealed class PrivateArtifactBridgeClient(
    string endpoint,
    string buildDiscriminator,
    IArtifactBridgeConnectionFactory? connectionFactory = null)
{
    private readonly IArtifactBridgeConnectionFactory connectionFactory =
        connectionFactory ?? new ArtifactBridgeEndpointConnectionFactory();

    internal async Task<ArtifactBridgeResultDocument> ExchangeAsync<TCommand>(
        TCommand command,
        JsonTypeInfo<ActionHostPrivateCommandEnvelope<TCommand>> commandType,
        CancellationToken logicalCancellationToken)
        where TCommand : class, IActionHostPrivateCommandDocument
    {
        logicalCancellationToken.ThrowIfCancellationRequested();
        var envelope = new ActionHostPrivateCommandEnvelope<TCommand>(
            buildDiscriminator,
            command);
        if (!ActionHostPrivateCommandCodec.TryWriteCommand(
                envelope,
                buildDiscriminator,
                commandType,
                out var commandBytes))
        {
            throw new ArtifactBridgeExchangeException(
                requestDispatched: false);
        }

        var frame = new byte[checked(commandBytes.Length + sizeof(int))];
        BinaryPrimitives.WriteUInt32BigEndian(
            frame,
            checked((uint)commandBytes.Length));
        commandBytes.CopyTo(frame, sizeof(int));
        var dispatched = false;
        try
        {
            await using var stream = await WithRequestDeadlineAsync(
                    token => connectionFactory.ConnectAsync(endpoint, token),
                    logicalCancellationToken)
                .ConfigureAwait(false);
            await WithRequestDeadlineAsync(
                    async token =>
                    {
                        dispatched = true;
                        await stream.WriteAsync(frame, token)
                            .ConfigureAwait(false);
                        await stream.FlushAsync(token).ConfigureAwait(false);
                    },
                    logicalCancellationToken)
                .ConfigureAwait(false);

            var header = new byte[sizeof(int)];
            await ReadExactlyWithDeadlineAsync(
                    stream,
                    header,
                    logicalCancellationToken)
                .ConfigureAwait(false);
            var length = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (length is 0 or > ArtifactBridgeLimits.MaximumDocumentBytes)
            {
                throw new ArtifactBridgeExchangeException(dispatched);
            }
            var resultBytes = new byte[checked((int)length)];
            await ReadExactlyWithDeadlineAsync(
                    stream,
                    resultBytes,
                    logicalCancellationToken)
                .ConfigureAwait(false);
            var trailing = new byte[1];
            var trailingCount = await WithRequestDeadlineAsync(
                    token => stream.ReadAsync(trailing, token),
                    logicalCancellationToken)
                .ConfigureAwait(false);
            if (trailingCount != 0)
            {
                throw new ArtifactBridgeExchangeException(dispatched);
            }

            var resultType = RequireTypeInfo<
                ActionHostPrivateCommandResultEnvelope<
                    ArtifactBridgeResultDocument>>(
                ArtifactBridgeJsonContext.Default);
            if (!ActionHostPrivateCommandCodec.TryReadResult(
                    resultBytes,
                    buildDiscriminator,
                    resultType,
                    out ActionHostPrivateCommandResultEnvelope<
                        ArtifactBridgeResultDocument>? result) ||
                result?.Payload is null)
            {
                throw new ArtifactBridgeExchangeException(dispatched);
            }
            return result.Payload;
        }
        catch (OperationCanceledException) when (
            !logicalCancellationToken.IsCancellationRequested)
        {
            throw new ArtifactBridgeExchangeException(dispatched);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArtifactBridgeExchangeException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or
            SocketException or
            UnauthorizedAccessException or
            ObjectDisposedException)
        {
            throw new ArtifactBridgeExchangeException(dispatched);
        }
    }

    private static async Task ReadExactlyWithDeadlineAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken logicalCancellationToken)
    {
        await WithRequestDeadlineAsync(
                async token =>
                {
                    var offset = 0;
                    while (offset < destination.Length)
                    {
                        var read = await stream.ReadAsync(
                                destination[offset..],
                                token)
                            .ConfigureAwait(false);
                        if (read == 0)
                        {
                            throw new ArtifactBridgeExchangeException(
                                requestDispatched: true);
                        }
                        offset += read;
                    }
                },
                logicalCancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<T> WithRequestDeadlineAsync<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken logicalCancellationToken)
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(
            logicalCancellationToken);
        request.CancelAfter(ArtifactBridgeLimits.RequestTimeout);
        return await operation(request.Token).ConfigureAwait(false);
    }

    private static async Task WithRequestDeadlineAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken logicalCancellationToken)
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(
            logicalCancellationToken);
        request.CancelAfter(ArtifactBridgeLimits.RequestTimeout);
        await operation(request.Token).ConfigureAwait(false);
    }

    private static JsonTypeInfo<T> RequireTypeInfo<T>(
        ArtifactBridgeJsonContext context) =>
        context.GetTypeInfo(typeof(T)) as JsonTypeInfo<T> ??
        throw new InvalidOperationException(
            "artifact_bridge_json_metadata_missing");
}

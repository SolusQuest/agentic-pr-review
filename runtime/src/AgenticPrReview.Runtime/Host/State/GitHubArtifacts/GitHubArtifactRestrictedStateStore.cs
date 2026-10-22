using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;

namespace AgenticPrReview.Runtime.Host.State.GitHubArtifacts;

internal sealed class GitHubArtifactRestrictedStateStore : IRestrictedStateStore, IDisposable
{
    private readonly ActionHostLaunchContract launch;
    private readonly GitHubArtifactRestClient rest;
    private readonly SdkUploadClient upload;
    private readonly Lazy<ArtifactBridgeStaging> staging;
    internal GitHubArtifactRestClient Rest => rest;

    internal GitHubArtifactRestrictedStateStore(ActionHostLaunchContract launch,
        GitHubArtifactRestClient? rest = null, string? stagingRoot = null,
        IArtifactBridgeConnectionFactory? connectionFactory = null)
    {
        this.launch = launch;
        this.rest = rest ?? new(launch);
        upload = new(launch.ArtifactBridgeEndpoint, connectionFactory);
        staging = new(() => new(stagingRoot ?? ArtifactBridgeStaging.DeriveRoot(launch.ArtifactBridgeEndpoint)));
    }

    public Task<OpaqueStoreListResult> ListExactAsync(OpaqueStoreListRequest request, CancellationToken cancellationToken)
    {
        if (!OpaqueStoreValidation.IsValid(request)) return Task.FromResult(OpaqueStoreListResult.Fail(OpaqueStoreFailure.Invalid));
        return ReadAsync(async operation =>
        {
            rest.Budget.RequireObservedAllocation(11);
            int? expectedTotal = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = ImmutableArray.CreateBuilder<OpaqueStoreObjectReference>();
            try
            {
                for (var page = 1; page <= 11; page++)
                {
                    using var document = await rest.GetAsync(rest.ListPath(page), operation).ConfigureAwait(false);
                    var root = document.RootElement;
                    var total = root.GetProperty("total_count").GetInt32();
                    var records = root.GetProperty("artifacts");
                    Incomplete(total is >= 0 and <= 1024 && (expectedTotal is null || total == expectedTotal));
                    Incomplete(records.ValueKind == JsonValueKind.Array && records.GetArrayLength() <= 100);
                    expectedTotal = total;
                    foreach (var item in records.EnumerateArray())
                    {
                        var id = Id(item.GetProperty("id"));
                        var name = item.GetProperty("name").GetString();
                        Incomplete(id is not null && name is not null);
                        if (!seen.Add(id!)) throw new ArtifactTransportException(OpaqueStoreFailure.Duplicate);
                        Incomplete(name != request.Name.Value &&
                            !(name!.StartsWith(ArtifactTransportFormat.Family(request.Name.Value), StringComparison.Ordinal) &&
                              !ArtifactTransportFormat.Member(request.Name.Value, name)));
                        if (ArtifactTransportFormat.Member(request.Name.Value, name))
                            result.Add(new(request.Name, new(id!)));
                        Incomplete(seen.Count <= total && result.Count <= request.MaximumObjects);
                    }
                    if (seen.Count == total)
                        return new OpaqueStoreListResult(OpaqueStoreFailure.None,
                            result.OrderBy(item => long.Parse(item.ObjectId.Value, CultureInfo.InvariantCulture)).ToImmutableArray(), true);
                    Incomplete(records.GetArrayLength() == 100);
                }
                throw new ArtifactTransportException(OpaqueStoreFailure.Incomplete);
            }
            catch
            {
                rest.InvalidateLists();
                throw;
            }
        }, OpaqueStoreListResult.Fail, cancellationToken);
    }

    public Task<OpaqueStoreMetadataResult> ReadMetadataAsync(OpaqueStoreMetadataRequest request, CancellationToken cancellationToken)
    {
        if (!OpaqueStoreValidation.IsValid(request) || !SafeReference(request.Reference))
            return Task.FromResult(OpaqueStoreMetadataResult.Fail(OpaqueStoreFailure.Invalid));
        return ReadAsync(async operation =>
        {
            rest.Budget.RequireObservedAllocation(3);
            var record = await ReadRecordAsync(request.Reference, request.MaximumBytes, operation).ConfigureAwait(false);
            try { return new OpaqueStoreMetadataResult(OpaqueStoreFailure.None, record.Metadata); }
            finally { CryptographicOperations.ZeroMemory(record.Bytes); }
        }, OpaqueStoreMetadataResult.Fail, cancellationToken);
    }

    public Task<OpaqueStoreDownloadResult> DownloadAsync(OpaqueStoreDownloadRequest request, CancellationToken cancellationToken)
    {
        if (!OpaqueStoreValidation.IsValid(request) || !SafeReference(request.Expected.Reference))
            return Task.FromResult(OpaqueStoreDownloadResult.Fail(OpaqueStoreFailure.Invalid));
        return ReadAsync(async operation =>
        {
            rest.Budget.RequireObservedAllocation(3);
            var record = await ReadRecordAsync(request.Expected.Reference, request.MaximumBytes, operation).ConfigureAwait(false);
            if (record.Metadata != request.Expected)
            {
                CryptographicOperations.ZeroMemory(record.Bytes);
                rest.Invalidate(request.Expected.Reference.ObjectId.Value);
                throw new ArtifactTransportException(OpaqueStoreFailure.Conflict);
            }
            return new OpaqueStoreDownloadResult(OpaqueStoreFailure.None, record.Metadata, record.Bytes);
        }, OpaqueStoreDownloadResult.Fail, cancellationToken);
    }

    public Task<OpaqueStoreReadBackResult> ReadBackExactAsync(OpaqueStoreReadBackRequest request, CancellationToken cancellationToken)
    {
        if (!OpaqueStoreValidation.IsValid(request) || !SafeReference(request.Expected.Reference))
            return Task.FromResult(OpaqueStoreReadBackResult.Fail(OpaqueStoreFailure.Invalid));
        return ReadAsync(async operation =>
        {
            rest.Budget.RequireObservedAllocation(3);
            var record = await ReadRecordAsync(request.Expected.Reference, checked((int)request.Expected.Size), operation).ConfigureAwait(false);
            try
            {
                if (record.Metadata != request.Expected)
                {
                    rest.Invalidate(request.Expected.Reference.ObjectId.Value);
                    throw new ArtifactTransportException(OpaqueStoreFailure.Conflict);
                }
                return new OpaqueStoreReadBackResult(OpaqueStoreFailure.None, record.Metadata);
            }
            finally { CryptographicOperations.ZeroMemory(record.Bytes); }
        }, OpaqueStoreReadBackResult.Fail, cancellationToken);
    }

    public async Task<OpaqueStoreUploadResult> UploadImmutableAsync(OpaqueStoreUploadRequest request, CancellationToken cancellationToken)
    {
        if (!OpaqueStoreValidation.IsValid(request) || OpaqueStoreHash.Sha256(request.EncryptedBytes.Span) != request.EncryptedObjectDigest.Sha256)
            return OpaqueStoreUploadResult.Fail(OpaqueStoreFailure.Invalid);
        using var operation = new ArtifactRestOperation(cancellationToken, rest.Clock);
        await rest.Lane.WaitAsync(operation.Token).ConfigureAwait(false);
        ArtifactBridgeStagingScope? scope = null;
        var possibleDispatch = false;
        var sdkSettled = false;
        byte[]? carrier = null;
        OpaqueStoreUploadResult result;
        try
        {
            await rest.Budget.ReserveMutationAsync(3, 8, operation).ConfigureAwait(false);
            var retentionDays = Math.Max(1, (long)Math.Ceiling((request.MinimumExpiresAtUnixSeconds - rest.Clock.UnixSeconds()) / 86_400d));
            if (retentionDays > 90) throw new ArtifactTransportException(OpaqueStoreFailure.Invalid);
            carrier = ArtifactTransportFormat.EncodeCarrier(Decimal(launch.RunId), launch.RunAttempt,
                request.EncryptedBytes.Span, request.EncryptedObjectDigest.Sha256);
            scope = await staging.Value.StageCarrierAsync(carrier, operation.Token).ConfigureAwait(false);
            await rest.Budget.DispatchSdkAsync(operation).ConfigureAwait(false);
            operation.Check();
            var command = new SdkUploadRequest(SdkUploadClient.Protocol, launch.BuildDiscriminator, UploadCorrelation(request),
                Decimal(launch.RunId), Decimal(launch.RunAttempt), ArtifactTransportFormat.PhysicalName(request.Name.Value, request.EncryptedObjectDigest.Sha256),
                scope.RelativePath, Decimal(carrier.Length), OpaqueStoreHash.Sha256(carrier), Decimal(retentionDays), Decimal(operation.RemainingMilliseconds));
            rest.InvalidateLists();
            possibleDispatch = true;
            var reply = await upload.UploadAsync(command, operation.Token).ConfigureAwait(false);
            if (!SdkUploadClient.ValidReply(reply, command)) throw new ArtifactTransportException(OpaqueStoreFailure.OutcomeUnknown);
            sdkSettled = reply.Dispatch == "not_dispatched" || reply.Failure is "none" or "conflict";
            if (reply.Dispatch == "not_dispatched")
            {
                possibleDispatch = false;
                result = OpaqueStoreUploadResult.Fail(ParseUploadFailure(reply.Failure));
            }
            else if (reply.Failure == "conflict")
            {
                result = OpaqueStoreUploadResult.Fail(OpaqueStoreFailure.Conflict);
            }
            else if (reply.Failure != "none")
            {
                result = OpaqueStoreUploadResult.Fail(OpaqueStoreFailure.OutcomeUnknown, OpaqueStoreMutationState.OutcomeUnknown);
            }
            else
            {
                var receipt = reply.Receipt!;
                rest.Invalidate(receipt.ArtifactId);
                var reference = new OpaqueStoreObjectReference(request.Name, new(receipt.ArtifactId));
                var record = await ReadRecordAsync(reference, request.EncryptedBytes.Length, operation).ConfigureAwait(false);
                try
                {
                    var platform = record.Platform;
                    if (platform.Digest != receipt.ArchiveSha256 || Decimal(platform.Size) != receipt.ArchiveSize ||
                        record.Metadata.ProducingRun.Identity != Decimal(launch.RunId) || record.Metadata.ProducingRun.Attempt != launch.RunAttempt ||
                        record.Metadata.EncryptedObjectDigest != request.EncryptedObjectDigest || record.Metadata.Size != request.EncryptedBytes.Length ||
                        record.Metadata.ExpiresAtUnixSeconds < request.MinimumExpiresAtUnixSeconds)
                    {
                        rest.Invalidate(receipt.ArtifactId);
                        throw new ArtifactTransportException(OpaqueStoreFailure.Conflict);
                    }
                    result = new(OpaqueStoreFailure.None, OpaqueStoreMutationState.Committed, record.Metadata);
                }
                finally { CryptographicOperations.ZeroMemory(record.Bytes); }
            }
        }
        catch (ArtifactBridgeExchangeException exception)
        {
            possibleDispatch = exception.RequestDispatched;
            result = OpaqueStoreUploadResult.Fail(possibleDispatch ? OpaqueStoreFailure.OutcomeUnknown :
                exception.Cancelled ? OpaqueStoreFailure.Cancelled : OpaqueStoreFailure.Io,
                possibleDispatch ? OpaqueStoreMutationState.OutcomeUnknown : OpaqueStoreMutationState.NotCommitted);
        }
        catch (Exception exception) when (TransportFailure(exception))
        {
            result = OpaqueStoreUploadResult.Fail(possibleDispatch ? OpaqueStoreFailure.OutcomeUnknown : Failure(exception),
                possibleDispatch ? OpaqueStoreMutationState.OutcomeUnknown : OpaqueStoreMutationState.NotCommitted);
        }
        finally
        {
            if (carrier is not null) CryptographicOperations.ZeroMemory(carrier);
            try
            {
                if (scope is not null)
                {
                    if (possibleDispatch && !sdkSettled) scope.RetainForBridgeCleanup();
                    else await scope.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally { rest.Lane.Release(); }
        }
        return scope is { CleanupSucceeded: false }
            ? OpaqueStoreUploadResult.Fail(OpaqueStoreFailure.Cleanup, result.MutationState, result.Metadata) : result;
    }

    public async Task<OpaqueStoreDeleteResult> DeleteExactAsync(OpaqueStoreDeleteRequest request, CancellationToken cancellationToken)
    {
        if (!OpaqueStoreValidation.IsValid(request) || !SafeReference(request.Expected.Reference))
            return OpaqueStoreDeleteResult.Fail(OpaqueStoreFailure.Invalid);
        using var operation = new ArtifactRestOperation(cancellationToken, rest.Clock);
        await rest.Lane.WaitAsync(operation.Token).ConfigureAwait(false);
        var dispatched = false;
        try
        {
            await rest.Budget.ReserveMutationAsync(3, 7, operation).ConfigureAwait(false);
            var expected = request.Expected;
            var id = expected.Reference.ObjectId.Value;
            var platform = await PlatformAsync(expected.Reference, operation).ConfigureAwait(false);
            if (platform.PhysicalName != ArtifactTransportFormat.PhysicalName(expected.Reference.Name.Value, expected.EncryptedObjectDigest.Sha256) ||
                platform.Run != expected.ProducingRun.Identity || platform.Digest != expected.ArchiveDigest.Sha256 || platform.Expiry != expected.ExpiresAtUnixSeconds)
                throw new ArtifactTransportException(OpaqueStoreFailure.Conflict);
            var status = await rest.DeleteAsync(id, operation, () =>
            {
                rest.InvalidateLists(); rest.Invalidate(id); dispatched = true;
            }).ConfigureAwait(false);
            if (status != HttpStatusCode.NoContent)
                return OpaqueStoreDeleteResult.Fail(OpaqueStoreFailure.OutcomeUnknown, OpaqueStoreMutationState.OutcomeUnknown);
            rest.Invalidate(id);
            try { await PlatformAsync(expected.Reference, operation).ConfigureAwait(false); }
            catch (ArtifactTransportException exception) when (exception.Failure == OpaqueStoreFailure.NotFound)
            { return new(OpaqueStoreFailure.None, OpaqueStoreMutationState.Committed); }
            return OpaqueStoreDeleteResult.Fail(OpaqueStoreFailure.OutcomeUnknown, OpaqueStoreMutationState.OutcomeUnknown);
        }
        catch (Exception exception) when (TransportFailure(exception))
        {
            return OpaqueStoreDeleteResult.Fail(dispatched ? OpaqueStoreFailure.OutcomeUnknown : Failure(exception),
                dispatched ? OpaqueStoreMutationState.OutcomeUnknown : OpaqueStoreMutationState.NotCommitted);
        }
        finally { rest.Lane.Release(); }
    }

    private sealed record Platform(string PhysicalName, string Run, string Digest, long Size, long Expiry, bool Expired);
    private sealed record Record(OpaqueStoreObjectMetadata Metadata, byte[] Bytes, Platform Platform);

    private async Task<Platform> PlatformAsync(OpaqueStoreObjectReference reference, ArtifactRestOperation operation)
    {
        try
        {
            using var document = await rest.GetAsync(rest.ArtifactPath(reference.ObjectId.Value), operation).ConfigureAwait(false);
            var value = document.RootElement;
            var name = value.GetProperty("name").GetString();
            var run = Id(value.GetProperty("workflow_run").GetProperty("id"));
            var digest = value.GetProperty("digest").GetString();
            var size = value.GetProperty("size_in_bytes").GetInt64();
            var expiry = value.GetProperty("expires_at").GetString();
            ArtifactTransportFormat.Require(Id(value.GetProperty("id")) == reference.ObjectId.Value &&
                ArtifactTransportFormat.Member(reference.Name.Value, name) && run is not null &&
                digest is not null && digest.StartsWith("sha256:", StringComparison.Ordinal) && ArtifactTransportFormat.Digest(digest[7..]) &&
                size is > 0 and <= ArtifactBridgeLimits.MaximumArchiveBytes && expiry is { Length: <= 64 } &&
                DateTimeOffset.TryParse(expiry, CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
            var expires = DateTimeOffset.Parse(expiry!, CultureInfo.InvariantCulture).ToUnixTimeSeconds();
            ArtifactTransportFormat.Require(expires is > 0 and <= RestrictedStateFormat.MaximumUnixSeconds);
            return new(name!, run!, digest![7..], size, expires, value.GetProperty("expired").GetBoolean());
        }
        catch { rest.Invalidate(reference.ObjectId.Value); throw; }
    }

    private async Task<Record> ReadRecordAsync(OpaqueStoreObjectReference reference, int maximum, ArtifactRestOperation operation)
    {
        var platform = await PlatformAsync(reference, operation).ConfigureAwait(false);
        try
        {
            NotExpired(platform);
            ArtifactTransportFormat.Require(platform.Size <= ArtifactTransportFormat.ArchiveLimit(maximum));
            var key = "record:" + reference.ObjectId.Value + ":" + platform.PhysicalName + ":" + platform.Digest + ":" + platform.Run + ":" + Decimal(platform.Size) + ":" + Decimal(platform.Expiry);
            var cached = rest.Cache.Get(key);
            if (cached is { Metadata: not null })
            {
                if (cached.Metadata.Reference == reference && cached.Bytes.Length <= maximum)
                    return new(cached.Metadata, cached.Bytes, platform);
                CryptographicOperations.ZeroMemory(cached.Bytes);
            }
            var archive = await rest.DownloadAsync(reference.ObjectId.Value, ArtifactTransportFormat.ArchiveLimit(maximum), operation).ConfigureAwait(false);
            ArtifactTransportEnvelope? envelope = null;
            try
            {
                if (archive.LongLength != platform.Size) throw new ArtifactTransportException(OpaqueStoreFailure.DigestMismatch);
                envelope = await ArtifactTransportFormat.DecodeAsync(archive, platform.Digest, maximum, operation.Token).ConfigureAwait(false);
                if (ArtifactTransportFormat.PhysicalName(reference.Name.Value, envelope.Digest) != platform.PhysicalName)
                    throw new ArtifactTransportException(OpaqueStoreFailure.DigestMismatch);
                if (envelope.RunId != platform.Run) throw new ArtifactTransportException(OpaqueStoreFailure.Conflict);
                var attemptPath = rest.AttemptPath(envelope.RunId, envelope.Attempt);
                using var attempt = await rest.GetAsync(attemptPath, operation).ConfigureAwait(false);
                if (Id(attempt.RootElement.GetProperty("id")) != envelope.RunId ||
                    Id(attempt.RootElement.GetProperty("run_attempt")) != Decimal(envelope.Attempt))
                {
                    rest.Cache.Remove("json:" + attemptPath);
                    throw new ArtifactTransportException(OpaqueStoreFailure.Conflict);
                }
                operation.Check();
                NotExpired(platform);
                var metadata = new OpaqueStoreObjectMetadata(reference, new(envelope.RunId, envelope.Attempt),
                    new(platform.Digest), new(envelope.Digest), platform.Expiry, envelope.Bytes.Length);
                rest.Cache.Put(key, envelope.Bytes, metadata: metadata);
                var result = new Record(metadata, envelope.Bytes, platform);
                envelope = null;
                return result;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(archive);
                if (envelope is not null) CryptographicOperations.ZeroMemory(envelope.Bytes);
            }
        }
        catch { rest.Invalidate(reference.ObjectId.Value); throw; }
    }

    private void NotExpired(Platform platform)
    {
        if (platform.Expired || platform.Expiry <= rest.Clock.UnixSeconds()) throw new ArtifactTransportException(OpaqueStoreFailure.Expired);
    }

    private async Task<T> ReadAsync<T>(Func<ArtifactRestOperation, Task<T>> call, Func<OpaqueStoreFailure, T> fail, CancellationToken caller)
    {
        using var operation = new ArtifactRestOperation(caller, rest.Clock);
        var acquired = false;
        try
        {
            await rest.Lane.WaitAsync(operation.Token).ConfigureAwait(false);
            acquired = true;
            var result = await call(operation).ConfigureAwait(false);
            try { operation.Check(); }
            catch
            {
                if (result is OpaqueStoreDownloadResult downloaded)
                    CryptographicOperations.ZeroMemory(MemoryMarshal.AsMemory(downloaded.EncryptedBytes).Span);
                throw;
            }
            return result;
        }
        catch (OperationCanceledException) when (caller.IsCancellationRequested) { throw; }
        catch (Exception exception) when (TransportFailure(exception)) { return fail(Failure(exception)); }
        finally { if (acquired) rest.Lane.Release(); }
    }

    private static bool SafeReference(OpaqueStoreObjectReference reference) => ArtifactTransportFormat.PositiveDecimal(reference.ObjectId.Value, out _);
    private static string? Id(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var id) &&
        ArtifactTransportFormat.PositiveDecimal(Decimal(id), out _) ? Decimal(id) : null;
    private static string Decimal(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static void Incomplete(bool condition) { if (!condition) throw new ArtifactTransportException(OpaqueStoreFailure.Incomplete); }
    private static bool TransportFailure(Exception exception) => exception is IOException or HttpRequestException or JsonException or FormatException or
        OperationCanceledException or InvalidOperationException or ArgumentException or OverflowException or KeyNotFoundException;
    private static OpaqueStoreFailure Failure(Exception exception) => exception switch
    {
        ArtifactTransportException transport => transport.Failure,
        OperationCanceledException => OpaqueStoreFailure.Cancelled,
        HttpRequestException => OpaqueStoreFailure.Io,
        IOException => OpaqueStoreFailure.Io,
        _ => OpaqueStoreFailure.Invalid,
    };
    private static OpaqueStoreFailure ParseUploadFailure(string failure) => failure switch
    {
        "invalid" => OpaqueStoreFailure.Invalid, "cancelled" => OpaqueStoreFailure.Cancelled,
        "cleanup" => OpaqueStoreFailure.Cleanup, _ => OpaqueStoreFailure.Io,
    };

    private static string UploadCorrelation(OpaqueStoreUploadRequest request)
    {
        var correlation = request.CorrelationId.Value;
        var name = request.Name.Value;
        var digest = request.EncryptedObjectDigest.Sha256;
        return OpaqueStoreHash.Sha256(Encoding.UTF8.GetBytes("apr.private-artifact-upload-correlation.s1\0" +
            Decimal(correlation.Length) + ":" + correlation + Decimal(name.Length) + ":" + name + Decimal(digest.Length) + ":" + digest));
    }

    public void Dispose() { if (staging.IsValueCreated) staging.Value.Dispose(); rest.Dispose(); }
}

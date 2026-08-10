using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Serialization.Metadata;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;

namespace AgenticPrReview.Runtime.Host.State.GitHubArtifacts;

internal sealed class GitHubArtifactRestrictedStateStore
    : IRestrictedStateStore
{
    private readonly PrivateArtifactBridgeClient client;
    private readonly ArtifactBridgeStaging staging;

    internal GitHubArtifactRestrictedStateStore(
        string artifactBridgeEndpoint,
        string buildDiscriminator,
        string? explicitBridgeOwnedStagingRoot = null,
        IArtifactBridgeConnectionFactory? connectionFactory = null)
    {
        client = new PrivateArtifactBridgeClient(
            artifactBridgeEndpoint,
            buildDiscriminator,
            connectionFactory);
        staging = new ArtifactBridgeStaging(
            explicitBridgeOwnedStagingRoot ??
            ArtifactBridgeStaging.DeriveRoot(artifactBridgeEndpoint));
    }

    public async Task<OpaqueStoreListResult> ListExactAsync(
        OpaqueStoreListRequest request,
        CancellationToken cancellationToken)
    {
        if (!OpaqueStoreValidation.IsValid(request) ||
            request.MaximumObjects > ArtifactBridgeLimits.MaximumRecords)
        {
            return OpaqueStoreListResult.Fail(OpaqueStoreFailure.Invalid);
        }
        var correlation = Correlation();
        var command = new ArtifactBridgeListExactCommandDocument(
            "list_exact",
            correlation,
            request.Name.Value,
            Decimal(request.MaximumObjects));
        ArtifactBridgeResultDocument result;
        try
        {
            result = await ExchangeAsync(command, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return OpaqueStoreListResult.Fail(OpaqueStoreFailure.Cancelled);
        }
        catch (ArtifactBridgeExchangeException)
        {
            return OpaqueStoreListResult.Fail(OpaqueStoreFailure.Io);
        }
        if (!ResultMatches(result, command.Operation, correlation))
        {
            return OpaqueStoreListResult.Fail(OpaqueStoreFailure.Invalid);
        }
        var failure = ParseFailure(result.Failure);
        if (failure != OpaqueStoreFailure.None)
        {
            if (result.Complete is not false ||
                result.Objects is not null ||
                result.Metadata is not null ||
                result.MutationState is not null)
            {
                return OpaqueStoreListResult.Fail(
                    OpaqueStoreFailure.Invalid);
            }
            return OpaqueStoreListResult.Fail(failure);
        }
        if (result is not
            {
                Complete: true,
                Objects: { } objects,
                Metadata: null,
                MutationState: null,
            } ||
            objects.Length > request.MaximumObjects ||
            objects.Length > ArtifactBridgeLimits.MaximumRecords)
        {
            return OpaqueStoreListResult.Fail(OpaqueStoreFailure.Invalid);
        }
        var builder = ImmutableArray.CreateBuilder<
            OpaqueStoreObjectReference>(objects.Length);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in objects)
        {
            if (item is null ||
                !StringComparer.Ordinal.Equals(item.Name, request.Name.Value) ||
                !IsSafePositiveDecimal(item.ObjectId) ||
                !identities.Add(item.ObjectId))
            {
                return OpaqueStoreListResult.Fail(
                    item is not null && identities.Contains(item.ObjectId)
                        ? OpaqueStoreFailure.Duplicate
                        : OpaqueStoreFailure.Invalid);
            }
            builder.Add(new OpaqueStoreObjectReference(
                request.Name,
                new OpaqueStoreObjectId(item.ObjectId)));
        }
        return new OpaqueStoreListResult(
            OpaqueStoreFailure.None,
            builder.ToImmutable(),
            Complete: true);
    }

    public async Task<OpaqueStoreMetadataResult> ReadMetadataAsync(
        OpaqueStoreMetadataRequest request,
        CancellationToken cancellationToken)
    {
        if (!OpaqueStoreValidation.IsValid(request) ||
            !IsSafePositiveDecimal(request.Reference.ObjectId.Value))
        {
            return OpaqueStoreMetadataResult.Fail(OpaqueStoreFailure.Invalid);
        }
        var correlation = Correlation();
        var command = new ArtifactBridgeMetadataCommandDocument(
            "metadata",
            correlation,
            request.Reference.Name.Value,
            request.Reference.ObjectId.Value);
        var exchange = await ExchangeReadAsync(
                command,
                cancellationToken)
            .ConfigureAwait(false);
        if (exchange.Failure != OpaqueStoreFailure.None ||
            exchange.Result is null)
        {
            return OpaqueStoreMetadataResult.Fail(exchange.Failure);
        }
        return TryMetadata(exchange.Result, out var metadata) &&
            metadata!.Reference == request.Reference
            ? new OpaqueStoreMetadataResult(
                OpaqueStoreFailure.None,
                metadata)
            : OpaqueStoreMetadataResult.Fail(OpaqueStoreFailure.Invalid);
    }

    public async Task<OpaqueStoreDownloadResult> DownloadAsync(
        OpaqueStoreDownloadRequest request,
        CancellationToken cancellationToken)
    {
        if (!OpaqueStoreValidation.IsValid(request) ||
            !TryWireMetadata(request.Expected, out var expected))
        {
            return OpaqueStoreDownloadResult.Fail(OpaqueStoreFailure.Invalid);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var scope = staging.PrepareDownload();
        OpaqueStoreDownloadResult result;
        try
        {
            var correlation = Correlation();
            var command = new ArtifactBridgeDownloadCommandDocument(
                "download",
                correlation,
                expected!,
                scope.RelativePath,
                Decimal(request.MaximumBytes));
            var exchange = await ExchangeReadAsync(
                    command,
                    cancellationToken)
                .ConfigureAwait(false);
            if (exchange.Failure != OpaqueStoreFailure.None ||
                exchange.Result is null)
            {
                result = OpaqueStoreDownloadResult.Fail(exchange.Failure);
            }
            else if (!TryMetadata(exchange.Result, out var metadata) ||
                metadata != request.Expected)
            {
                result = OpaqueStoreDownloadResult.Fail(
                    OpaqueStoreFailure.Invalid);
            }
            else
            {
                var bytes = await staging.ReadDownloadAsync(
                        scope,
                        request.MaximumBytes,
                        cancellationToken)
                    .ConfigureAwait(false);
                result = bytes.LongLength == request.Expected.Size &&
                    StringComparer.Ordinal.Equals(
                        OpaqueStoreHash.Sha256(bytes),
                        request.Expected.EncryptedObjectDigest.Sha256)
                    ? new OpaqueStoreDownloadResult(
                        OpaqueStoreFailure.None,
                        request.Expected,
                        bytes)
                    : OpaqueStoreDownloadResult.Fail(
                        OpaqueStoreFailure.DigestMismatch);
            }
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            result = OpaqueStoreDownloadResult.Fail(
                OpaqueStoreFailure.Cancelled);
        }
        catch (ArtifactBridgeExchangeException)
        {
            result = OpaqueStoreDownloadResult.Fail(OpaqueStoreFailure.Io);
        }
        catch (IOException)
        {
            result = OpaqueStoreDownloadResult.Fail(
                OpaqueStoreFailure.Invalid);
        }
        await scope.DisposeAsync().ConfigureAwait(false);
        return scope.CleanupSucceeded
            ? result
            : OpaqueStoreDownloadResult.Fail(OpaqueStoreFailure.Cleanup);
    }

    public async Task<OpaqueStoreUploadResult> UploadImmutableAsync(
        OpaqueStoreUploadRequest request,
        CancellationToken cancellationToken)
    {
        if (!OpaqueStoreValidation.IsValid(request) ||
            !StringComparer.Ordinal.Equals(
                OpaqueStoreHash.Sha256(request.EncryptedBytes.Span),
                request.EncryptedObjectDigest.Sha256))
        {
            return OpaqueStoreUploadResult.Fail(OpaqueStoreFailure.Invalid);
        }
        cancellationToken.ThrowIfCancellationRequested();
        ArtifactBridgeStagingScope? scope = null;
        OpaqueStoreUploadResult result;
        try
        {
            scope = await staging.StageUploadAsync(
                    request.EncryptedBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            var command = new ArtifactBridgeUploadCommandDocument(
                "upload_immutable",
                request.CorrelationId.Value,
                request.Name.Value,
                scope.RelativePath,
                request.EncryptedObjectDigest.Sha256,
                Decimal(request.MinimumExpiresAtUnixSeconds));
            var document = await ExchangeAsync(command, cancellationToken)
                .ConfigureAwait(false);
            if (!ResultMatches(
                    document,
                    command.Operation,
                    command.CorrelationId))
            {
                result = OpaqueStoreUploadResult.Fail(
                    OpaqueStoreFailure.Invalid,
                    OpaqueStoreMutationState.OutcomeUnknown);
            }
            else
            {
                result = MapUploadResult(document, request);
            }
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            result = OpaqueStoreUploadResult.Fail(
                OpaqueStoreFailure.OutcomeUnknown,
                OpaqueStoreMutationState.OutcomeUnknown);
        }
        catch (ArtifactBridgeExchangeException exception)
        {
            result = OpaqueStoreUploadResult.Fail(
                exception.RequestDispatched
                    ? OpaqueStoreFailure.OutcomeUnknown
                    : OpaqueStoreFailure.Io,
                exception.RequestDispatched
                    ? OpaqueStoreMutationState.OutcomeUnknown
                    : OpaqueStoreMutationState.NotCommitted);
        }
        catch (IOException)
        {
            result = OpaqueStoreUploadResult.Fail(OpaqueStoreFailure.Io);
        }
        if (scope is not null)
        {
            await scope.DisposeAsync().ConfigureAwait(false);
            if (!scope.CleanupSucceeded)
            {
                return OpaqueStoreUploadResult.Fail(
                    OpaqueStoreFailure.Cleanup,
                    result.MutationState,
                    result.Metadata);
            }
        }
        return result;
    }

    public async Task<OpaqueStoreReadBackResult> ReadBackExactAsync(
        OpaqueStoreReadBackRequest request,
        CancellationToken cancellationToken)
    {
        if (!OpaqueStoreValidation.IsValid(request) ||
            !TryWireMetadata(request.Expected, out var expected))
        {
            return OpaqueStoreReadBackResult.Fail(OpaqueStoreFailure.Invalid);
        }
        var correlation = Correlation();
        var command = new ArtifactBridgeReadBackCommandDocument(
            "readback_exact",
            correlation,
            expected!);
        var exchange = await ExchangeReadAsync(
                command,
                cancellationToken)
            .ConfigureAwait(false);
        if (exchange.Failure != OpaqueStoreFailure.None ||
            exchange.Result is null)
        {
            return OpaqueStoreReadBackResult.Fail(exchange.Failure);
        }
        return TryMetadata(exchange.Result, out var metadata) &&
            metadata == request.Expected
            ? new OpaqueStoreReadBackResult(
                OpaqueStoreFailure.None,
                metadata)
            : OpaqueStoreReadBackResult.Fail(OpaqueStoreFailure.Invalid);
    }

    public async Task<OpaqueStoreDeleteResult> DeleteExactAsync(
        OpaqueStoreDeleteRequest request,
        CancellationToken cancellationToken)
    {
        if (!OpaqueStoreValidation.IsValid(request) ||
            !TryWireMetadata(request.Expected, out var expected))
        {
            return OpaqueStoreDeleteResult.Fail(OpaqueStoreFailure.Invalid);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var correlation = Correlation();
        var command = new ArtifactBridgeDeleteCommandDocument(
            "delete_exact",
            correlation,
            expected!);
        try
        {
            var result = await ExchangeAsync(command, cancellationToken)
                .ConfigureAwait(false);
            if (!ResultMatches(result, command.Operation, correlation))
            {
                return OpaqueStoreDeleteResult.Fail(
                    OpaqueStoreFailure.Invalid,
                    OpaqueStoreMutationState.OutcomeUnknown);
            }
            var failure = ParseFailure(result.Failure);
            var mutation = ParseMutation(result.MutationState);
            if (failure == OpaqueStoreFailure.Invalid || mutation is null ||
                result.Metadata is not null || result.Objects is not null ||
                result.Complete is not null)
            {
                return OpaqueStoreDeleteResult.Fail(
                    OpaqueStoreFailure.Invalid,
                    OpaqueStoreMutationState.OutcomeUnknown);
            }
            return new OpaqueStoreDeleteResult(failure, mutation.Value);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return OpaqueStoreDeleteResult.Fail(
                OpaqueStoreFailure.OutcomeUnknown,
                OpaqueStoreMutationState.OutcomeUnknown);
        }
        catch (ArtifactBridgeExchangeException exception)
        {
            return OpaqueStoreDeleteResult.Fail(
                exception.RequestDispatched
                    ? OpaqueStoreFailure.OutcomeUnknown
                    : OpaqueStoreFailure.Io,
                exception.RequestDispatched
                    ? OpaqueStoreMutationState.OutcomeUnknown
                    : OpaqueStoreMutationState.NotCommitted);
        }
    }

    private async Task<ArtifactBridgeResultDocument> ExchangeAsync<TCommand>(
        TCommand command,
        CancellationToken cancellationToken)
        where TCommand : class, IArtifactBridgeCommandDocument
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var logical = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        logical.CancelAfter(ArtifactBridgeLimits.LogicalOperationTimeout);
        var commandType = RequireTypeInfo<
            ActionHostPrivateCommandEnvelope<TCommand>>(
            ArtifactBridgeJsonContext.Default);
        return await client.ExchangeAsync(
                command,
                commandType,
                logical.Token)
            .ConfigureAwait(false);
    }

    private async Task<ArtifactBridgeReadExchange> ExchangeReadAsync<TCommand>(
        TCommand command,
        CancellationToken cancellationToken)
        where TCommand : class, IArtifactBridgeCommandDocument
    {
        try
        {
            var result = await ExchangeAsync(command, cancellationToken)
                .ConfigureAwait(false);
            if (!ResultMatches(
                    result,
                    command.Operation,
                    command.CorrelationId))
            {
                return ArtifactBridgeReadExchange.Fail(
                    OpaqueStoreFailure.Invalid);
            }
            var failure = ParseFailure(result.Failure);
            if (failure != OpaqueStoreFailure.None)
            {
                if (result.MutationState is not null ||
                    result.Complete is not null ||
                    result.Objects is not null ||
                    result.Metadata is not null)
                {
                    return ArtifactBridgeReadExchange.Fail(
                        OpaqueStoreFailure.Invalid);
                }
                return ArtifactBridgeReadExchange.Fail(failure);
            }
            if (result.MutationState is not null ||
                result.Complete is not null ||
                result.Objects is not null ||
                result.Metadata is null)
            {
                return ArtifactBridgeReadExchange.Fail(
                    OpaqueStoreFailure.Invalid);
            }
            return new ArtifactBridgeReadExchange(
                OpaqueStoreFailure.None,
                result);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ArtifactBridgeReadExchange.Fail(
                OpaqueStoreFailure.Cancelled);
        }
        catch (ArtifactBridgeExchangeException)
        {
            return ArtifactBridgeReadExchange.Fail(OpaqueStoreFailure.Io);
        }
    }

    private static OpaqueStoreUploadResult MapUploadResult(
        ArtifactBridgeResultDocument result,
        OpaqueStoreUploadRequest request)
    {
        var failure = ParseFailure(result.Failure);
        var mutation = ParseMutation(result.MutationState);
        OpaqueStoreObjectMetadata? metadata = null;
        if (result.Metadata is not null &&
            !TryMetadata(result, out metadata))
        {
            return OpaqueStoreUploadResult.Fail(
                OpaqueStoreFailure.Invalid,
                OpaqueStoreMutationState.OutcomeUnknown);
        }
        if (mutation is null || result.Complete is not null ||
            result.Objects is not null ||
            (metadata is not null &&
                mutation != OpaqueStoreMutationState.Committed) ||
            (failure == OpaqueStoreFailure.None &&
                (mutation != OpaqueStoreMutationState.Committed ||
                    metadata is null ||
                    metadata.Reference.Name != request.Name ||
                    metadata.EncryptedObjectDigest !=
                        request.EncryptedObjectDigest ||
                    metadata.Size != request.EncryptedBytes.Length ||
                    metadata.ExpiresAtUnixSeconds <
                        request.MinimumExpiresAtUnixSeconds)))
        {
            return OpaqueStoreUploadResult.Fail(
                OpaqueStoreFailure.Invalid,
                OpaqueStoreMutationState.OutcomeUnknown);
        }
        return new OpaqueStoreUploadResult(
            failure,
            mutation.Value,
            metadata);
    }

    private static bool TryMetadata(
        ArtifactBridgeResultDocument result,
        out OpaqueStoreObjectMetadata? metadata) =>
        TryMetadata(result.Metadata, out metadata);

    private static bool TryMetadata(
        ArtifactBridgeMetadataDocument? document,
        out OpaqueStoreObjectMetadata? metadata)
    {
        metadata = null;
        if (document is null ||
            !IsSafePositiveDecimal(document.ObjectId) ||
            !IsSafePositiveDecimal(document.ProducingRunId) ||
            !long.TryParse(
                document.ProducingRunAttempt,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var attempt) || attempt <= 0 ||
            !long.TryParse(
                document.ExpiresAtUnixSeconds,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var expiresAt) ||
            !long.TryParse(
                document.Size,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var size))
        {
            return false;
        }
        var parsed = new OpaqueStoreObjectMetadata(
            new OpaqueStoreObjectReference(
                new OpaqueStoreName(document.Name),
                new OpaqueStoreObjectId(document.ObjectId)),
            new OpaqueStoreProducingRun(document.ProducingRunId, attempt),
            new OpaqueStoreArchiveDigest(document.ArchiveDigest),
            new OpaqueStoreEncryptedObjectDigest(
                document.EncryptedObjectDigest),
            expiresAt,
            size);
        if (!OpaqueStoreValidation.IsValid(parsed))
        {
            return false;
        }
        metadata = parsed;
        return true;
    }

    private static bool TryWireMetadata(
        OpaqueStoreObjectMetadata metadata,
        out ArtifactBridgeMetadataDocument? document)
    {
        document = null;
        if (!OpaqueStoreValidation.IsValid(metadata) ||
            !IsSafePositiveDecimal(metadata.Reference.ObjectId.Value) ||
            !IsSafePositiveDecimal(metadata.ProducingRun.Identity) ||
            metadata.ProducingRun.Attempt <= 0)
        {
            return false;
        }
        document = new ArtifactBridgeMetadataDocument(
            metadata.Reference.Name.Value,
            metadata.Reference.ObjectId.Value,
            metadata.ProducingRun.Identity,
            Decimal(metadata.ProducingRun.Attempt),
            metadata.ArchiveDigest.Sha256,
            metadata.EncryptedObjectDigest.Sha256,
            Decimal(metadata.ExpiresAtUnixSeconds),
            Decimal(metadata.Size));
        return true;
    }

    private static bool ResultMatches(
        ArtifactBridgeResultDocument result,
        string operation,
        string correlation) =>
        StringComparer.Ordinal.Equals(result.Operation, operation) &&
        StringComparer.Ordinal.Equals(result.CorrelationId, correlation);

    private static OpaqueStoreFailure ParseFailure(string? value) =>
        value switch
        {
            "none" => OpaqueStoreFailure.None,
            "cancelled" => OpaqueStoreFailure.Cancelled,
            "invalid" => OpaqueStoreFailure.Invalid,
            "not_found" => OpaqueStoreFailure.NotFound,
            "incomplete" => OpaqueStoreFailure.Incomplete,
            "duplicate" => OpaqueStoreFailure.Duplicate,
            "conflict" => OpaqueStoreFailure.Conflict,
            "expired" => OpaqueStoreFailure.Expired,
            "digest_mismatch" => OpaqueStoreFailure.DigestMismatch,
            "outcome_unknown" => OpaqueStoreFailure.OutcomeUnknown,
            "cleanup" => OpaqueStoreFailure.Cleanup,
            "io" => OpaqueStoreFailure.Io,
            _ => OpaqueStoreFailure.Invalid,
        };

    private static OpaqueStoreMutationState? ParseMutation(string? value) =>
        value switch
        {
            "not_committed" => OpaqueStoreMutationState.NotCommitted,
            "committed" => OpaqueStoreMutationState.Committed,
            "outcome_unknown" => OpaqueStoreMutationState.OutcomeUnknown,
            _ => null,
        };

    private static bool IsSafePositiveDecimal(string? value) =>
        long.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var parsed) &&
        parsed > 0 &&
        StringComparer.Ordinal.Equals(value, Decimal(parsed)) &&
        parsed <= 9_007_199_254_740_991;

    private static string Correlation() => Guid.NewGuid().ToString("N");

    private static string Decimal(long value) =>
        value.ToString(CultureInfo.InvariantCulture);

    private static JsonTypeInfo<T> RequireTypeInfo<T>(
        ArtifactBridgeJsonContext context) =>
        context.GetTypeInfo(typeof(T)) as JsonTypeInfo<T> ??
        throw new InvalidOperationException(
            "artifact_bridge_json_metadata_missing");
}

internal sealed record ArtifactBridgeReadExchange(
    OpaqueStoreFailure Failure,
    ArtifactBridgeResultDocument? Result)
{
    internal static ArtifactBridgeReadExchange Fail(
        OpaqueStoreFailure failure) => new(failure, null);
}

using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.Restore;

namespace AgenticPrReview.Runtime.ActionHostVerifierFixture;

internal sealed class FrameworkStateDependencies(
    string scenarioRoot,
    IActionHostGitObjectTransportFactory gitFactory) :
    IAcceptedStateProductionDependencies
{
    private readonly AcceptedStateProductionDependencies inner =
        new(gitFactory);

    public IRestrictedStateStore CreateArtifactStore(
        ActionHostLaunchContract launch) => new RecordingStore(
            scenarioRoot,
            inner.CreateArtifactStore(launch));

    public IActionHostGitObjectTransport CreateAncestryTransport(
        ActionHostGitHubToken token) => inner.CreateAncestryTransport(token);

    private sealed class RecordingStore(
        string scenarioRoot,
        IRestrictedStateStore inner) : IRestrictedStateStore
    {
        public async Task<OpaqueStoreListResult> ListExactAsync(
            OpaqueStoreListRequest request,
            CancellationToken cancellationToken)
        {
            var result = await inner.ListExactAsync(request, cancellationToken)
                .ConfigureAwait(false);
            Record("list", result.Failure,
                result.Objects.IsDefault ? -1 : result.Objects.Length);
            RecordIdentity("list", request.Name.Value, "-", "-",
                result.Failure.ToString(), "-",
                result.Objects.IsDefault
                    ? "-"
                    : string.Join(',', result.Objects.Select(value =>
                        value.ObjectId.Value)));
            return result;
        }

        public async Task<OpaqueStoreMetadataResult> ReadMetadataAsync(
            OpaqueStoreMetadataRequest request,
            CancellationToken cancellationToken)
        {
            var result = await inner.ReadMetadataAsync(request,
                cancellationToken).ConfigureAwait(false);
            Record("metadata", result.Failure, result.Metadata is null ? 0 : 1);
            RecordIdentity("metadata", request.Reference.Name.Value,
                request.Reference.ObjectId.Value, "-",
                result.Failure.ToString(), "-", Describe(result.Metadata));
            return result;
        }

        public async Task<OpaqueStoreDownloadResult> DownloadAsync(
            OpaqueStoreDownloadRequest request,
            CancellationToken cancellationToken)
        {
            var result = await inner.DownloadAsync(request, cancellationToken)
                .ConfigureAwait(false);
            Record("download", result.Failure, result.EncryptedBytes.Length);
            RecordIdentity("download", request.Expected.Reference.Name.Value,
                request.Expected.Reference.ObjectId.Value,
                request.Expected.EncryptedObjectDigest.Sha256,
                result.Failure.ToString(), "-", Describe(result.Metadata));
            return result;
        }

        public async Task<OpaqueStoreUploadResult> UploadImmutableAsync(
            OpaqueStoreUploadRequest request,
            CancellationToken cancellationToken)
        {
            var result = await inner.UploadImmutableAsync(request,
                cancellationToken).ConfigureAwait(false);
            Record("upload", result.Failure, (int)result.MutationState);
            RecordIdentity("upload", request.Name.Value,
                request.CorrelationId.Value,
                request.EncryptedObjectDigest.Sha256,
                result.Failure.ToString(), result.MutationState.ToString(),
                Describe(result.Metadata));
            return result;
        }

        public async Task<OpaqueStoreReadBackResult> ReadBackExactAsync(
            OpaqueStoreReadBackRequest request,
            CancellationToken cancellationToken)
        {
            var result = await inner.ReadBackExactAsync(request,
                cancellationToken).ConfigureAwait(false);
            Record("readback", result.Failure, result.Metadata is null ? 0 : 1);
            RecordIdentity("readback", request.Expected.Reference.Name.Value,
                request.Expected.Reference.ObjectId.Value,
                request.Expected.EncryptedObjectDigest.Sha256,
                result.Failure.ToString(), "-", Describe(result.Metadata));
            return result;
        }

        public async Task<OpaqueStoreDeleteResult> DeleteExactAsync(
            OpaqueStoreDeleteRequest request,
            CancellationToken cancellationToken)
        {
            var result = await inner.DeleteExactAsync(request,
                cancellationToken).ConfigureAwait(false);
            Record("delete", result.Failure, (int)result.MutationState);
            RecordIdentity("delete", request.Expected.Reference.Name.Value,
                request.Expected.Reference.ObjectId.Value,
                request.Expected.EncryptedObjectDigest.Sha256,
                result.Failure.ToString(), result.MutationState.ToString(),
                Describe(request.Expected));
            return result;
        }

        private void Record(
            string operation,
            OpaqueStoreFailure failure,
            int structuralValue) => File.AppendAllText(
            Path.Join(scenarioRoot, "state-operations.tsv"),
            operation + "\t" + failure + "\t" +
                structuralValue.ToString(
                    System.Globalization.CultureInfo.InvariantCulture) + "\n");

        private void RecordIdentity(
            string operation,
            string name,
            string requestedIdentity,
            string requestedDigest,
            string failure,
            string mutation,
            string result) => File.AppendAllText(
            Path.Join(scenarioRoot, "state-operation-identities.tsv"),
            string.Join('\t', operation, name, requestedIdentity,
                requestedDigest, failure, mutation, result) + "\n");

        private static string Describe(OpaqueStoreObjectMetadata? metadata) =>
            metadata is null
                ? "-"
                : string.Join('|',
                    metadata.Reference.ObjectId.Value,
                    metadata.ArchiveDigest.Sha256,
                    metadata.EncryptedObjectDigest.Sha256,
                    metadata.ExpiresAtUnixSeconds.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    metadata.ProducingRun.Identity,
                    metadata.ProducingRun.Attempt.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    metadata.Size.ToString(
                        System.Globalization.CultureInfo.InvariantCulture));
    }
}

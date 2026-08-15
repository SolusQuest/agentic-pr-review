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
            return result;
        }

        public async Task<OpaqueStoreMetadataResult> ReadMetadataAsync(
            OpaqueStoreMetadataRequest request,
            CancellationToken cancellationToken)
        {
            var result = await inner.ReadMetadataAsync(request,
                cancellationToken).ConfigureAwait(false);
            Record("metadata", result.Failure, result.Metadata is null ? 0 : 1);
            return result;
        }

        public async Task<OpaqueStoreDownloadResult> DownloadAsync(
            OpaqueStoreDownloadRequest request,
            CancellationToken cancellationToken)
        {
            var result = await inner.DownloadAsync(request, cancellationToken)
                .ConfigureAwait(false);
            Record("download", result.Failure, result.EncryptedBytes.Length);
            return result;
        }

        public async Task<OpaqueStoreUploadResult> UploadImmutableAsync(
            OpaqueStoreUploadRequest request,
            CancellationToken cancellationToken)
        {
            var result = await inner.UploadImmutableAsync(request,
                cancellationToken).ConfigureAwait(false);
            Record("upload", result.Failure, (int)result.MutationState);
            return result;
        }

        public async Task<OpaqueStoreReadBackResult> ReadBackExactAsync(
            OpaqueStoreReadBackRequest request,
            CancellationToken cancellationToken)
        {
            var result = await inner.ReadBackExactAsync(request,
                cancellationToken).ConfigureAwait(false);
            Record("readback", result.Failure, result.Metadata is null ? 0 : 1);
            return result;
        }

        public async Task<OpaqueStoreDeleteResult> DeleteExactAsync(
            OpaqueStoreDeleteRequest request,
            CancellationToken cancellationToken)
        {
            var result = await inner.DeleteExactAsync(request,
                cancellationToken).ConfigureAwait(false);
            Record("delete", result.Failure, (int)result.MutationState);
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
    }
}

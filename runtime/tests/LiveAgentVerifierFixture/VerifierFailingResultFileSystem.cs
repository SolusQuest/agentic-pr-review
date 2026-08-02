using AgenticPrReview.Runtime.Host.State;

namespace AgenticPrReview.Runtime.LiveAgentVerifierFixture;

internal sealed class VerifierFailingResultFileSystem(
    ILiveAgentFreshProcessFileSystem inner,
    string target) : ILiveAgentFreshProcessFileSystem
{
    private byte[]? attemptedResult;

    internal int PublishResultAttempts { get; private set; }

    internal bool ReplacementConflictObserved { get; private set; }

    internal byte[]? AttemptedResult => attemptedResult?.ToArray();

    public LiveAgentFreshProcessAuthorizationRead? ReadAuthorization() =>
        inner.ReadAuthorization();

    public bool TryAuthorizeLayout(
        LiveAgentFreshProcessAuthorizationRead authorization,
        AuthorizedStateAccess access,
        bool lineageExpected,
        out LiveAgentFreshProcessAuthorizedRoot? authorizedRoot) =>
        inner.TryAuthorizeLayout(
            authorization,
            access,
            lineageExpected,
            out authorizedRoot);

    public LiveAgentFreshProcessRead? ReadReviewedInput(
        LiveAgentFreshProcessAuthorizedRoot root) =>
        inner.ReadReviewedInput(root);

    public LiveAgentFreshProcessRead? ReadSnapshotManifest(
        LiveAgentFreshProcessAuthorizedRoot root) =>
        inner.ReadSnapshotManifest(root);

    public LiveAgentFreshProcessRead? ReadLineage(
        LiveAgentFreshProcessAuthorizedRoot root) => inner.ReadLineage(root);

    public LiveAgentFreshProcessAtomicWriteReceipt? PublishLineage(
        LiveAgentFreshProcessAuthorizedRoot root,
        byte[] bytes,
        LiveAgentFreshProcessFileVersion? expectedPrior) =>
        inner.PublishLineage(root, bytes, expectedPrior);

    public LiveAgentFreshProcessAtomicWriteReceipt? PublishResult(
        LiveAgentFreshProcessAuthorizedRoot root,
        byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        PublishResultAttempts++;
        attemptedResult = bytes.ToArray();
        try
        {
            using var stream = new FileStream(
                target,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4_096,
                FileOptions.WriteThrough);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        catch (IOException)
        {
            ReplacementConflictObserved = true;
        }

        return null;
    }
}

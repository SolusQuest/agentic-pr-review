using System.Collections.Immutable;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Tests.Host.State.Lineage;
using AgenticPrReview.Runtime.Tests.Host.State.Locator;

namespace AgenticPrReview.Runtime.Tests.Host.State.Restore;

public sealed class AcceptedStateRestoreServiceTests
{
    [Fact]
    public async Task RealEncryptedAgentSessionPassesRetainedR3Admission()
    {
        var fixture = await AgentSessionStateBoundaryTests.BuildSessionAsync(
            AcceptedStateTestData.RepositoryId.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            AcceptedStateTestData.PullRequestNumber,
            AcceptedStateTestData.SessionId);
        var document = fixture.Artifact.Document;
        var stateScope = new RestrictedStateScope(
            document.RepositoryId,
            document.WorkflowIdentity,
            document.ReviewTarget,
            document.SessionId,
            document.ProviderId,
            document.ModelId,
            document.AdapterId,
            document.PolicySha256,
            document.LimitsSha256,
            document.ToolsetSha256,
            document.BuildId);
        var stateAccess = RestrictedStateTestData.Access(stateScope);
        using var locatorAccess = LocatorTestData.Access(document.RepositoryId);
        using var keys = LocatorTestData.KeyRing(
            locatorAccess,
            repositoryId: document.RepositoryId);
        var time = new MutableLineageTimeProvider(
            AcceptedStateTestData.AcceptedAtUnixSeconds);
        Assert.True(LocatorContext.TryCreate(
            locatorAccess,
            keys,
            LineageTestData.Root,
            currentSingletonProven: true,
            AcceptedStateTestData.LogicalExpiresAtUnixSeconds + 3_600,
            time,
            out var locator));
        using var locatorContext = Assert.IsType<LocatorContext>(locator);
        var binding = new RestrictedStateBinding(
            stateScope,
            document.ProducerBaseSha,
            document.ProducerHeadSha,
            document.Generation,
            document.PredecessorStateSha256,
            AcceptedStateTestData.AcceptedAtUnixSeconds,
            AcceptedStateTestData.LogicalExpiresAtUnixSeconds);
        var resolver = new LocatorTestKeyResolver(
            stateAccess,
            locatorAccess,
            locatorContext);
        Assert.True(RestrictedStateEnvelope.TryEncrypt(
            stateAccess,
            binding,
            fixture.Artifact.Plaintext,
            resolver,
            out var envelope,
            out var encryptCode),
            encryptCode);
        Assert.NotNull(envelope);

        var publication = AcceptedStateTestData.Publication(
            out var publicationBytes,
            fixture.Identity,
            document.BuildId);
        var generation = new StateGenerationRecordV1(
            ImmutableArray.CreateRange(envelope),
            RestrictedStateEnvelope.EnvelopeSha256(envelope),
            fixture.Artifact.SessionSha256,
            document.ProducerBaseSha,
            document.ProducerHeadSha,
            document.Generation,
            document.PredecessorStateSha256,
            PreviousLogicalGenerationIdentity: null,
            AcceptedStateTestData.AcceptedAtUnixSeconds,
            AcceptedStateTestData.LogicalExpiresAtUnixSeconds,
            ImmutableArray.CreateRange(publicationBytes),
            AcceptedStateRecordValidation.Sha256(publicationBytes),
            AcceptedStateTestData.PolicySha256,
            AcceptedStateTestData.ConfigSha256,
            AcceptedStateTestData.InstructionsSha256,
            AcceptedStateTestData.PayloadSha256,
            document.BuildId);
        Assert.True(AcceptedStateGenerationRecordCodec.TryEncode(
            generation,
            out var generationBytes));
        Assert.True(AcceptedStateIdentity.TryComputeLogicalGeneration(
            generationBytes,
            AcceptedStateTestData.BaseScopeDigest,
            AcceptedStateTestData.Epoch,
            document.SessionId,
            previousAcceptanceReceiptIdentity: null,
            out var logicalIdentity));
        var receipt = AcceptedStateTestData.Receipt(
            logicalIdentity,
            AcceptedStateTestData.OriginalCandidateIdentity,
            out var receiptBytes,
            identity: fixture.Identity,
            buildDiscriminator: document.BuildId);
        Assert.Equal(publication.ReviewedHeadSha, receipt.ReviewedHeadSha);

        var candidatePhysical = Physical(
            StateObjectClass.Candidate,
            objectId: "20",
            AcceptedStateTestData.OriginalCandidateIdentity,
            predecessorIdentity: null,
            document.SessionId,
            generationBytes);
        var receiptPhysical = Physical(
            StateObjectClass.Acceptance,
            objectId: "30",
            AcceptedStateTestData.ReceiptIdentity,
            predecessorIdentity: null,
            document.SessionId,
            receiptBytes);
        var head = Head(document.SessionId);
        var selection = new AcceptedStateSelection(
            new SelectedAcceptedGeneration(
                candidatePhysical,
                generation,
                receipt,
                receiptPhysical,
                logicalIdentity,
                AcceptedStateTestData.OriginalCandidateIdentity),
            ImmediatePredecessor: null,
            head,
            AcceptedStateTestData.LogicalExpiresAtUnixSeconds);
        using var transport = new NoCallGitObjectTransport();

        var result = await new AcceptedStateRestoreService().RestoreAsync(
            stateAccess,
            locatorAccess,
            locatorContext,
            selection,
            new AcceptedStatePolicyBinding(
                AcceptedStateTestData.PolicySha256,
                AcceptedStateTestData.ConfigSha256,
                AcceptedStateTestData.InstructionsSha256,
                AcceptedStateTestData.PayloadSha256,
                document.BuildId),
            fixture.Trusted,
            fixture.Identity,
            new ProjectChatMessage(
                "user",
                [new ProjectTextContent("next synthetic review context")]),
            DeepSeekReasoningContinuationCodec.Instance,
            new TrustedHeadAncestryClassifier(transport, time),
            AcceptedStateTestData.RepositoryName,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Code);
        using var context = Assert.IsType<AcceptedStateContext>(result.Context);
        Assert.True(context.TryGetAdmittedValue(out var admitted));
        Assert.NotNull(admitted);
        Assert.Equal(logicalIdentity, context.LogicalGenerationIdentity);
        Assert.Equal(0, transport.CommitCalls);
    }

    private static AuthenticatedStateObject Physical(
        StateObjectClass objectClass,
        string objectId,
        string objectIdentity,
        string? predecessorIdentity,
        string sessionId,
        byte[] payload) =>
        new(
            Metadata(objectClass, objectId),
            new StateControlHeaderV1(
                AcceptedStateTestData.BaseScopeDigest,
                AcceptedStateTestData.Epoch,
                sessionId,
                objectClass,
                KeyId: new string('a', 64),
                objectIdentity,
                predecessorIdentity,
                SuccessorIdentity: null,
                ProducingRunIdentity: "workflow-run-42",
                ProducingRunAttempt: 1,
                AcceptedStateTestData.AcceptedAtUnixSeconds,
                AcceptedStateTestData.LogicalExpiresAtUnixSeconds,
                RequiredPlatformExpiresAtUnixSeconds:
                    AcceptedStateTestData.LogicalExpiresAtUnixSeconds + 3_600),
            payload);

    private static LineageHeadCandidate Head(string sessionId)
    {
        var metadata = Metadata(StateObjectClass.LineageHead, "1");
        var header = new StateControlHeaderV1(
            AcceptedStateTestData.BaseScopeDigest,
            AcceptedStateTestData.Epoch,
            sessionId,
            StateObjectClass.LineageHead,
            KeyId: new string('a', 64),
            ObjectIdentity: new string('b', 64),
            PredecessorIdentity: null,
            SuccessorIdentity: null,
            ProducingRunIdentity: "workflow-run-42",
            ProducingRunAttempt: 1,
            AcceptedStateTestData.AcceptedAtUnixSeconds,
            AcceptedStateTestData.LogicalExpiresAtUnixSeconds,
            RequiredPlatformExpiresAtUnixSeconds:
                AcceptedStateTestData.LogicalExpiresAtUnixSeconds + 3_600);
        var head = new LineageHeadV1(
            LineageTransitionKind.Initial,
            Ordinal: 0,
            new ReviewedTransitionFacts(
                new string('4', 40),
                new string('5', 40)),
            PreviousEpoch: null,
            PreviousHeadIdentity: null,
            TransitionEvidenceIdentity: null,
            ExpiryBoundaryUnixSeconds: null,
            PhysicalPredecessors:
                ImmutableArray<LineageArtifactEvidence>.Empty,
            PhysicalSuperseded:
                ImmutableArray<LineageArtifactEvidence>.Empty,
            Superseded: ImmutableArray<LineageArtifactEvidence>.Empty,
            CompletedCleanup: ImmutableArray<LineageArtifactEvidence>.Empty);
        return new LineageHeadCandidate(metadata, header, head);
    }

    private static OpaqueStoreObjectMetadata Metadata(
        StateObjectClass objectClass,
        string objectId) =>
        new(
            new OpaqueStoreObjectReference(
                new OpaqueStoreName(StateObjectClasses.ToWireName(objectClass)),
                new OpaqueStoreObjectId(objectId)),
            new OpaqueStoreProducingRun("workflow-run-42", 1),
            new OpaqueStoreArchiveDigest(new string('c', 64)),
            new OpaqueStoreEncryptedObjectDigest(new string('d', 64)),
            AcceptedStateTestData.LogicalExpiresAtUnixSeconds,
            Size: 1024);

    private sealed class LocatorTestKeyResolver(
        AuthorizedStateAccess stateAuthority,
        AuthorizedLocatorAccess locatorAuthority,
        LocatorContext locator) : IRestrictedStateKeyResolver
    {
        public bool TryGetCurrentWriteKey(
            AuthorizedStateAccess access,
            out RestrictedStateKey? key)
        {
            key = null;
            if (!ReferenceEquals(access, stateAuthority))
            {
                return false;
            }

            Span<byte> material = stackalloc byte[RestrictedStateFormat.KeyBytes];
            if (!locator.TryCopyCurrentStateKey(
                    locatorAuthority,
                    material,
                    out var keyId))
            {
                return false;
            }

            key = new RestrictedStateKey(keyId, material);
            return true;
        }

        public bool TryGetApprovedReadKey(
            AuthorizedStateAccess access,
            string keyId,
            long expiresAtUnixSeconds,
            out RestrictedStateKey? key)
        {
            key = null;
            return false;
        }
    }

    private sealed class NoCallGitObjectTransport : IActionHostGitObjectTransport
    {
        internal int CommitCalls { get; private set; }

        public Task<ActionHostGitObjectResult<ActionHostGitCommitObject>>
            GetCommitObjectAsync(
                string repositoryName,
                string commitSha,
                CancellationToken cancellationToken)
        {
            CommitCalls++;
            throw new InvalidOperationException("Commit transport was called.");
        }

        public Task<ActionHostGitObjectResult<ActionHostGitTreeObject>>
            GetTreeObjectAsync(
                string repositoryName,
                string treeSha,
                CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Tree transport was called.");

        public Task<ActionHostGitObjectResult<ActionHostGitBlobObject>>
            GetBlobObjectAsync(
                string repositoryName,
                string blobSha,
                ActionHostGitBlobReadBudget budget,
                CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Blob transport was called.");

        public void Dispose() { }
    }
}

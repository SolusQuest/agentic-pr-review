using System.Collections.Immutable;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
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
    [Theory]
    [InlineData("none")]
    [InlineData("repository-id")]
    [InlineData("source-repository-id")]
    [InlineData("repository-name")]
    [InlineData("workflow-path")]
    [InlineData("workflow-ref")]
    [InlineData("pull-request")]
    [InlineData("comment-url")]
    [InlineData("scope-policy")]
    [InlineData("payload-build")]
    [InlineData("reviewed-head")]
    [InlineData("body")]
    [InlineData("payload")]
    [InlineData("build")]
    [InlineData("publication-corruption")]
    [InlineData("predecessor-publication")]
    [InlineData("predecessor-session")]
    [InlineData("trusted-v2-different-current")]
    [InlineData("trusted-v2-pair-mismatch")]
    public async Task RealEncryptedAgentSessionRequiresExactPublicationBinding(
        string mutation)
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

        var authorizedPublicationScope = new R4PublicationScopeV1(
            (ulong)AcceptedStateTestData.RepositoryId,
            (ulong)AcceptedStateTestData.RepositoryId,
            ".github/workflows/review.yml",
            "refs/heads/main",
            (ulong)AcceptedStateTestData.PullRequestNumber,
            AcceptedStateTestData.PolicySha256,
            "action-contract/v1+payload-build:runtime-payload-v1");
        var publicationScope = mutation switch
        {
            "repository-id" => authorizedPublicationScope with
            {
                RepositoryId = authorizedPublicationScope.RepositoryId + 1,
            },
            "source-repository-id" => authorizedPublicationScope with
            {
                WorkflowSourceRepositoryId =
                    authorizedPublicationScope.WorkflowSourceRepositoryId + 1,
            },
            "workflow-path" => authorizedPublicationScope with
            {
                WorkflowPath = ".github/workflows/other.yml",
            },
            "workflow-ref" => authorizedPublicationScope with
            {
                WorkflowRef = "refs/heads/other",
            },
            "pull-request" => authorizedPublicationScope with
            {
                PullRequestNumber =
                    authorizedPublicationScope.PullRequestNumber + 1,
            },
            "scope-policy" => authorizedPublicationScope with
            {
                PolicyIdentitySha256 = new string('e', 64),
            },
            "payload-build" => authorizedPublicationScope with
            {
                ActionContractPayloadIdentity = "other-payload-build",
            },
            _ => authorizedPublicationScope,
        };
        var publicationIdentity = new ReviewedIdentity(
            publicationScope.RepositoryId.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            (long)publicationScope.PullRequestNumber,
            fixture.Identity.BaseSha,
            mutation == "reviewed-head"
                ? new string('e', 40)
                : fixture.Identity.HeadSha);
        var publicationRepositoryName = mutation == "repository-name"
            ? "other/repository"
            : AcceptedStateTestData.RepositoryName;
        var publicationPolicy = publicationScope.PolicyIdentitySha256;
        var publicationPayload = mutation is "payload" or
                "trusted-v2-pair-mismatch"
            ? new string('e', 64)
            : AcceptedStateTestData.PayloadSha256;
        var publicationBuild = mutation == "build"
            ? "other-runtime-payload"
            : document.BuildId;
        var publication = AcceptedStateTestData.Publication(
            out var publicationBytes,
            publicationIdentity,
            publicationBuild,
            publicationScope,
            (long)publicationScope.RepositoryId,
            publicationRepositoryName,
            (long)publicationScope.PullRequestNumber,
            publicationPolicy,
            publicationPayload);
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
            identity: publicationIdentity,
            buildDiscriminator: publicationBuild,
            scope: publicationScope,
            repositoryId: (long)publicationScope.RepositoryId,
            repositoryName: publicationRepositoryName,
            pullRequestNumber: (long)publicationScope.PullRequestNumber,
            policyIdentitySha256: publicationPolicy,
            payloadSha256: publicationPayload);
        if (mutation == "comment-url")
        {
            receipt = receipt with
            {
                CommentUrl = $"https://github.com/other/repository/pull/" +
                    $"{receipt.PullRequestNumber}#issuecomment-" +
                    receipt.CommentId,
            };
        }
        else if (mutation == "body")
        {
            receipt = receipt with { BodySha256 = new string('e', 64) };
        }
        if (mutation == "publication-corruption")
        {
            var corruptedPublication = publicationBytes.ToArray();
            corruptedPublication[^1] ^= 0x01;
            generation = generation with
            {
                PublicationPayloadBytes =
                    ImmutableArray.CreateRange(corruptedPublication),
                PublicationPayloadSha256 =
                    AcceptedStateRecordValidation.Sha256(
                        corruptedPublication),
            };
        }
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
        var current = new SelectedAcceptedGeneration(
            candidatePhysical,
            generation,
            receipt,
            receiptPhysical,
            logicalIdentity,
            AcceptedStateTestData.OriginalCandidateIdentity);
        var immediatePredecessor = mutation is
            "predecessor-publication" or "predecessor-session"
            ? current with
            {
                Receipt = mutation == "predecessor-publication"
                    ? receipt with
                    {
                        ScopeSha256 = new string('e', 64),
                    }
                    : receipt,
            }
            : null;
        var selection = new AcceptedStateSelection(
            current,
            immediatePredecessor,
            head,
            AcceptedStateTestData.LogicalExpiresAtUnixSeconds);
        using var transport = new NoCallGitObjectTransport();
        var trustedV2 = mutation.StartsWith(
            "trusted-v2-",
            StringComparison.Ordinal);
        var currentPayload = trustedV2
            ? new string('b', 64)
            : AcceptedStateTestData.PayloadSha256;

        var result = await new AcceptedStateRestoreService().RestoreAsync(
            stateAccess,
            locatorAccess,
            locatorContext,
            selection,
            new AcceptedStatePolicyBinding(
                AcceptedStateTestData.PolicySha256,
                AcceptedStateTestData.ConfigSha256,
                AcceptedStateTestData.InstructionsSha256,
                currentPayload,
                trustedV2
                    ? ActionHostPayloadContinuityMode.ExactSource
                    : ActionHostPayloadContinuityMode.ExactExecutable,
                document.BuildId),
            new AcceptedStatePublicationBinding(
                authorizedPublicationScope,
                R4PublicationIdentityV1.ComputeScopeSha256(
                    authorizedPublicationScope),
                AcceptedStateTestData.RepositoryName,
                currentPayload,
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

        if (mutation is "none" or "trusted-v2-different-current")
        {
            Assert.True(result.Succeeded, result.Code);
            using var context = Assert.IsType<AcceptedStateContext>(
                result.Context);
            Assert.True(context.TryGetAdmittedValue(out var admitted));
            Assert.NotNull(admitted);
            Assert.Equal(logicalIdentity, context.LogicalGenerationIdentity);
            Assert.Equal(0, transport.CommitCalls);
        }
        else
        {
            Assert.Equal(
                mutation == "predecessor-session"
                    ? AcceptedStateCodes.IncompatibleCurrent
                    : AcceptedStateCodes.ScopeMismatch,
                result.Code);
            Assert.Null(result.Context);
            Assert.Equal(0, transport.CommitCalls);
        }
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

        public Task<ActionHostGitObjectResult<ActionHostGitArchiveReader>>
            GetHeadArchiveAsync(
                string repositoryName,
                string headSha,
                CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Archive transport was called.");

        public void Dispose() { }
    }
}

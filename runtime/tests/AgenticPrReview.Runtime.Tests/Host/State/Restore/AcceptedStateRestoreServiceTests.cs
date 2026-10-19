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
    [InlineData("trusted-v2-different-current")]
    [InlineData("trusted-v2-pair-mismatch")]
    public void RealEncryptedCurrentRecordRequiresExactPublicationBinding(
        string mutation)
    {
        var stateScope = RestrictedStateTestData.Scope() with
        {
            RepositoryId = AcceptedStateTestData.RepositoryId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ReviewTarget = AcceptedStateTestData.PullRequestNumber,
            SessionId = AcceptedStateTestData.SessionId,
            BuildId = AcceptedStateTestData.BuildDiscriminator,
        };
        var identity = new ReviewedIdentity(stateScope.RepositoryId, stateScope.ReviewTarget,
            new string('a', 40), new string('b', 40));
        var compatibility = new SolusRestrictedStateCompatibility(SolusRestrictedStateFormat.PackageVersion,
            SolusRestrictedStateFormat.Profile, SolusRestrictedStateFormat.CompletedCandidateMode,
            SolusRestrictedStateFormat.Implementation, SolusRestrictedStateFormat.ContextFormat,
            SolusRestrictedStateFormat.ContextCompatibility, SolusRestrictedStateFormat.PackageClosureSha256,
            new string('1', 64), new string('2', 64));
        Assert.True(SolusRestrictedStateRecord.TryCreate(compatibility,
            new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), [1, 2, 3], out var captured));
        Assert.True(SolusRestrictedStateSnapshotCodec.TryWrite(captured!, out var plaintext));
        var stateAccess = RestrictedStateTestData.Access(stateScope);
        using var locatorAccess = LocatorTestData.Access(stateScope.RepositoryId);
        using var keys = LocatorTestData.KeyRing(
            locatorAccess,
            repositoryId: stateScope.RepositoryId);
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
            identity.BaseSha,
            identity.HeadSha,
            0,
            null,
            AcceptedStateTestData.AcceptedAtUnixSeconds,
            AcceptedStateTestData.LogicalExpiresAtUnixSeconds);
        var resolver = new LocatorTestKeyResolver(
            stateAccess,
            locatorAccess,
            locatorContext);
        Assert.True(RestrictedStateEnvelope.TryEncrypt(
            stateAccess,
            binding,
            plaintext!,
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
            identity.BaseSha,
            mutation == "reviewed-head"
                ? new string('e', 40)
                : identity.HeadSha);
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
            : stateScope.BuildId;
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
            SolusRestrictedStateSnapshotCodec.RecordSha256(plaintext!),
            identity.BaseSha,
            identity.HeadSha,
            0,
            null,
            PreviousLogicalGenerationIdentity: null,
            AcceptedStateTestData.AcceptedAtUnixSeconds,
            AcceptedStateTestData.LogicalExpiresAtUnixSeconds,
            ImmutableArray.CreateRange(publicationBytes),
            AcceptedStateRecordValidation.Sha256(publicationBytes),
            AcceptedStateTestData.PolicySha256,
            AcceptedStateTestData.ConfigSha256,
            AcceptedStateTestData.InstructionsSha256,
            AcceptedStateTestData.PayloadSha256,
            stateScope.BuildId);
        Assert.True(AcceptedStateGenerationRecordCodec.TryEncode(
            generation,
            out var generationBytes));
        Assert.True(AcceptedStateIdentity.TryComputeLogicalGeneration(
            generationBytes,
            AcceptedStateTestData.BaseScopeDigest,
            AcceptedStateTestData.Epoch,
            stateScope.SessionId,
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
            stateScope.SessionId,
            generationBytes);
        var receiptPhysical = Physical(
            StateObjectClass.Acceptance,
            objectId: "30",
            AcceptedStateTestData.ReceiptIdentity,
            predecessorIdentity: null,
            stateScope.SessionId,
            receiptBytes);
        var head = Head(stateScope.SessionId);
        var current = new SelectedAcceptedGeneration(
            candidatePhysical,
            generation,
            receipt,
            receiptPhysical,
            logicalIdentity,
            AcceptedStateTestData.OriginalCandidateIdentity);
        var immediatePredecessor = mutation == "predecessor-publication"
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
        var trustedV2 = mutation.StartsWith("trusted-v2-", StringComparison.Ordinal);
        var currentPayload = trustedV2 ? new string('b', 64) : AcceptedStateTestData.PayloadSha256;
        var policyBinding = new AcceptedStatePolicyBinding(AcceptedStateTestData.PolicySha256,
            AcceptedStateTestData.ConfigSha256, AcceptedStateTestData.InstructionsSha256, currentPayload,
            trustedV2 ? ActionHostPayloadContinuityMode.ExactSource : ActionHostPayloadContinuityMode.ExactExecutable,
            stateScope.BuildId);
        var publicationBinding = new AcceptedStatePublicationBinding(authorizedPublicationScope,
            R4PublicationIdentityV1.ComputeScopeSha256(authorizedPublicationScope),
            AcceptedStateTestData.RepositoryName, currentPayload, stateScope.BuildId);
        // This is the exact predicate used by current Solus source selection,
        // followed by the actual authenticated current-record reader. It does
        // not qualify runtime import; installed-package tests own that proof.
        var matches = AcceptedStateRestoreService.MatchesPublication(selection.Current, policyBinding, publicationBinding) &&
            (selection.ImmediatePredecessor is null || AcceptedStateRestoreService.MatchesPublication(
                selection.ImmediatePredecessor, policyBinding, publicationBinding));
        Assert.Equal(mutation is "none" or "trusted-v2-different-current", matches);
        if (matches)
        {
            var lineage = new AcceptedLineage(stateScope, 0, generation.SessionSha256,
                generation.StateEnvelopeSha256, null, binding.AcceptedAtUnixSeconds, binding.ExpiresAtUnixSeconds, true);
            Assert.True(SolusRestrictedStateSnapshotCodec.TryRestore(stateAccess, binding, lineage, envelope,
                compatibility, binding.AcceptedAtUnixSeconds, resolver, out var restored, out var failure), failure.ToString());
            Assert.Equal(captured!.Checkpoint, restored!.Checkpoint);
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
            AuthorizedStateAccess access, string keyId, long expiresAtUnixSeconds,
            out RestrictedStateKey? key)
        {
            key = null;
            if (!ReferenceEquals(access, stateAuthority) || !LineageValidation.IsSha256(keyId) ||
                !LineageValidation.IsTime(expiresAtUnixSeconds)) return false;
            Span<byte> material = stackalloc byte[RestrictedStateFormat.KeyBytes];
            try
            {
                if (!locator.TryCopyApprovedReadKey(locatorAuthority, keyId, material)) return false;
                key = new(keyId, material);
                return true;
            }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(material); }
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

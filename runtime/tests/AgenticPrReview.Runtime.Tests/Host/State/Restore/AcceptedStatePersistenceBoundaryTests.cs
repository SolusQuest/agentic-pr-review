using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Canonical;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Tests.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Tests.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Tests.Host.State.Lineage;
using AgenticPrReview.Runtime.Tests.Host.State.Locator;

namespace AgenticPrReview.Runtime.Tests.Host.State.Restore;

public sealed class AcceptedStatePersistenceBoundaryTests
{
    [Theory]
    [MemberData(nameof(R4StickyPublicationByteVectors.Names),
        MemberType = typeof(R4StickyPublicationByteVectors))]
    public void FrozenP1VectorsRoundTripByteExactlyThroughS5(
        string name)
    {
        var vector = R4StickyPublicationByteVectors.Get(name);
        Assert.True(ValidatedPublicationPayloadV1.TryCreate(
            vector.Rendered.Comment,
            AcceptedStateTestData.RepositoryId,
            AcceptedStateTestData.RepositoryName,
            AcceptedStateTestData.PullRequestNumber,
            AcceptedStateTestData.PolicySha256,
            AcceptedStateTestData.PayloadSha256,
            AcceptedStateTestData.BuildDiscriminator,
            AcceptedStateFormat.RenderingVersion,
            out var publication));
        Assert.NotNull(publication);
        Assert.True(AcceptedStatePublicationPayloadCodec.TryEncode(
            publication!,
            out var publicationBytes));
        Assert.True(AcceptedStatePublicationPayloadCodec.TryDecode(
            publicationBytes,
            out var decodedPublication));
        Assert.NotNull(decodedPublication);
        Assert.Equal(
            Encoding.UTF8.GetBytes(vector.Rendered.Comment),
            decodedPublication!.FinalizedCommentUtf8);

        var template = AcceptedStateTestData.Generation(out _);
        var generation = template with
        {
            PublicationPayloadBytes =
                ImmutableArray.CreateRange(publicationBytes),
            PublicationPayloadSha256 =
                AcceptedStateRecordValidation.Sha256(publicationBytes),
        };
        Assert.True(AcceptedStateGenerationRecordCodec.TryEncode(
            generation,
            out var generationBytes));
        Assert.True(AcceptedStateGenerationRecordCodec.TryDecode(
            generationBytes,
            out var decodedGeneration));
        Assert.NotNull(decodedGeneration);

        var copy = new AcceptedStatePhysicalCopyV1(
            ImmutableArray.CreateRange(generationBytes),
            new string('a', 64),
            AcceptedStateTestData.OriginalCandidateIdentity,
            "9007199254740991",
            new string('b', 64),
            new string('c', 64));
        Assert.True(AcceptedStatePhysicalCopyCodec.TryEncode(
            copy,
            out var copyBytes));
        Assert.True(AcceptedStatePhysicalCopyCodec.TryDecode(
            copyBytes,
            out var decodedCopy));
        Assert.NotNull(decodedCopy);
        Assert.True(generationBytes.AsSpan().SequenceEqual(
            decodedCopy!.CanonicalGenerationBytes.AsSpan()));

        Assert.True(AcceptedStateGenerationRecordCodec.TryDecode(
            decodedCopy.CanonicalGenerationBytes.AsSpan(),
            out var recoveredGeneration));
        Assert.True(AcceptedStatePublicationPayloadCodec.TryDecode(
            recoveredGeneration!.PublicationPayloadBytes.AsSpan(),
            out var recoveredPublication));
        var recoveredComment = Encoding.UTF8.GetString(
            recoveredPublication!.FinalizedCommentUtf8.AsSpan());
        Assert.Equal(vector.Rendered.Comment, recoveredComment);
        Assert.True(StickyCommentSerializer.TrySerialize(
            recoveredComment,
            out var requestBytes));
        Assert.Equal(vector.CommentSha256, Hash(recoveredComment));
        Assert.Equal(vector.RequestSha256, Hash(requestBytes!));
        Assert.InRange(
            requestBytes!.Length,
            1,
            BoundedGitHubPublisherPolicy.MaximumStickyRequestBytes);
    }

    [Fact]
    public async Task MaximumSessionAndPublicationCompositeFitsAllLayers()
    {
        var lineageTemplate = LineageTestData.Scope();
        var fixture = await AgentSessionStateBoundaryTests
            .BuildSessionAsync(
                AcceptedStateTestData.RepositoryId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                AcceptedStateTestData.PullRequestNumber,
                AcceptedStateTestData.SessionId,
                lineageTemplate.TrustedWorkflowIdentity,
                buildId: AcceptedStateTestData.BuildDiscriminator,
                baseSha: R4PublicationTestData.BaseSha,
                headSha: R4PublicationTestData.HeadSha);
        var admittedSession = MaximumAdmittedSession(fixture);
        var document = admittedSession.Value.Artifact.Document;
        Assert.Equal(
            AgentLimits.SessionPlaintextBytes,
            admittedSession.Plaintext.Length);

        var stateScope = RestrictedScope(document);
        var stateAccess = RestrictedStateTestData.Access(stateScope);
        var binding = new RestrictedStateBinding(
            stateScope,
            document.ProducerBaseSha,
            document.ProducerHeadSha,
            document.Generation,
            document.PredecessorStateSha256,
            AcceptedStateTestData.AcceptedAtUnixSeconds,
            AcceptedStateTestData.LogicalExpiresAtUnixSeconds);
        var keyResolver = new TestKeyResolver();
        Assert.True(RestrictedStateEnvelope.TryEncrypt(
            stateAccess,
            binding,
            admittedSession.Plaintext,
            keyResolver,
            out var stateEnvelope,
            out var stateFailure), stateFailure);
        Assert.NotNull(stateEnvelope);
        Assert.Equal(document.Generation, binding.Generation);
        Assert.Equal(
            document.PredecessorStateSha256,
            binding.PredecessorEnvelopeSha256);
        Assert.InRange(
            stateEnvelope!.Length,
            1,
            AgentLimits.StateEnvelopeBytes);

        var vector = R4StickyPublicationByteVectors.All
            .MaxBy(item => Encoding.UTF8.GetByteCount(
                item.Rendered.Comment))!;
        Assert.True(ValidatedPublicationPayloadV1.TryCreate(
            vector.Rendered.Comment,
            AcceptedStateTestData.RepositoryId,
            AcceptedStateTestData.RepositoryName,
            AcceptedStateTestData.PullRequestNumber,
            AcceptedStateTestData.PolicySha256,
            AcceptedStateTestData.PayloadSha256,
            AcceptedStateTestData.BuildDiscriminator,
            AcceptedStateFormat.RenderingVersion,
            out var publication));
        Assert.True(AcceptedStatePublicationPayloadCodec.TryEncode(
            publication!,
            out var publicationBytes));

        var previousLogicalGenerationIdentity = new string('f', 64);
        var generation = new StateGenerationRecordV1(
            ImmutableArray.CreateRange(stateEnvelope),
            RestrictedStateEnvelope.EnvelopeSha256(stateEnvelope),
            admittedSession.SessionSha256,
            document.ProducerBaseSha,
            document.ProducerHeadSha,
            document.Generation,
            document.PredecessorStateSha256,
            previousLogicalGenerationIdentity,
            binding.AcceptedAtUnixSeconds,
            binding.ExpiresAtUnixSeconds,
            ImmutableArray.CreateRange(publicationBytes),
            AcceptedStateRecordValidation.Sha256(publicationBytes),
            AcceptedStateTestData.PolicySha256,
            AcceptedStateTestData.ConfigSha256,
            document.PolicySha256,
            AcceptedStateTestData.PayloadSha256,
            document.BuildId);
        Assert.True(AcceptedStateGenerationRecordCodec.TryEncode(
            generation,
            out var generationBytes));
        Assert.InRange(
            generationBytes.Length,
            1,
            AcceptedStateFormat.MaximumGenerationPayloadBytes);

        var scope = new LineageBaseScope(
            document.RepositoryId,
            document.WorkflowIdentity,
            lineageTemplate.TrustedSourceIdentity,
            document.ReviewTarget,
            document.ProviderId,
            document.ModelId,
            document.AdapterId,
            AcceptedStateTestData.ConfigSha256,
            document.PolicySha256,
            document.ToolsetSha256,
            document.LimitsSha256,
            document.BuildId);
        using var locatorAccess = LocatorTestData.Access(document.RepositoryId);
        using var locatorKeys = LocatorTestData.KeyRing(
            locatorAccess,
            repositoryId: document.RepositoryId);
        var time = new MutableLineageTimeProvider(
            AcceptedStateTestData.AcceptedAtUnixSeconds);
        Assert.True(LocatorContext.TryCreate(
            locatorAccess,
            locatorKeys,
            LineageTestData.Root,
            currentSingletonProven: true,
            AcceptedStateTestData.LogicalExpiresAtUnixSeconds + 3_600,
            time,
            out var createdContext));
        using var locatorContext = Assert.IsType<LocatorContext>(
            createdContext);
        Assert.True(LineageBaseScopeCodec.TryEncode(
            scope,
            out var scopeBytes));
        byte[]? sourceEnvelope = null;
        byte[]? copyBytes = null;
        byte[]? copyEnvelope = null;
        byte[]? recoveredCopyBytes = null;
        byte[]? recoveredSessionBytes = null;
        try
        {
            Assert.True(LineageBaseScopeCodec.TryDigest(
                scope,
                out var scopeDigest));
            Assert.True(locatorContext.TryDeriveOpaqueName(
                locatorAccess,
                StateObjectClasses.ToWireName(StateObjectClass.Candidate),
                scopeBytes,
                out var name));
            Assert.NotNull(name);
            Assert.True(AcceptedStateIdentity.TryComputeLogicalGeneration(
                generationBytes,
                scopeDigest,
                AcceptedStateTestData.Epoch,
                document.SessionId,
                previousLogicalGenerationIdentity,
                out var logicalGenerationIdentity));
            var sourceDraft = new StateControlHeaderDraft(
                scopeDigest,
                AcceptedStateTestData.Epoch,
                document.SessionId,
                StateObjectClass.Candidate,
                previousLogicalGenerationIdentity,
                SuccessorIdentity: null,
                "boundary-source",
                1,
                AcceptedStateTestData.AcceptedAtUnixSeconds,
                AcceptedStateTestData.LogicalExpiresAtUnixSeconds,
                AcceptedStateTestData.LogicalExpiresAtUnixSeconds + 3_600);
            Assert.True(StateControlEnvelopeV1Codec.TryEncrypt(
                locatorContext,
                locatorAccess,
                name!,
                sourceDraft,
                generationBytes,
                out sourceEnvelope,
                out var sourceHeader,
                out var sourceFailure), sourceFailure);
            Assert.NotNull(sourceHeader);

            var store = new ScriptedLocatorStore
            {
                UseNumericObjectIds = true,
            };
            var sourceUpload = new OpaqueStoreUploadRequest(
                name!,
                new OpaqueStoreCorrelationId("s5-boundary-source"),
                sourceEnvelope,
                new OpaqueStoreEncryptedObjectDigest(
                    OpaqueStoreHash.Sha256(sourceEnvelope)),
                AcceptedStateTestData.LogicalExpiresAtUnixSeconds + 3_600);
            Assert.True(OpaqueStoreValidation.IsValid(sourceUpload));
            var uploadedSource = await store.UploadImmutableAsync(
                sourceUpload,
                CancellationToken.None);
            Assert.True(uploadedSource.Succeeded);
            var sourceMetadata = Assert.IsType<OpaqueStoreObjectMetadata>(
                uploadedSource.Metadata);
            Assert.True((await store.ReadBackExactAsync(
                new OpaqueStoreReadBackRequest(sourceMetadata),
                CancellationToken.None)).Succeeded);

            var copy = new AcceptedStatePhysicalCopyV1(
                ImmutableArray.CreateRange(generationBytes),
                logicalGenerationIdentity,
                sourceHeader!.ObjectIdentity,
                sourceMetadata.Reference.ObjectId.Value,
                sourceMetadata.ArchiveDigest.Sha256,
                sourceMetadata.EncryptedObjectDigest.Sha256);
            Assert.True(AcceptedStatePhysicalCopyCodec.TryEncode(
                copy,
                out copyBytes));
            Assert.InRange(
                copyBytes.Length,
                1,
                AcceptedStateFormat.MaximumPhysicalCopyPayloadBytes);

            var copyDraft = sourceDraft with
            {
                ProducingRunIdentity = "boundary-copy",
            };
            Assert.True(StateControlEnvelopeV1Codec.TryEncrypt(
                locatorContext,
                locatorAccess,
                name!,
                copyDraft,
                copyBytes,
                out copyEnvelope,
                out var copyHeader,
                out var copyFailure), copyFailure);
            Assert.NotNull(copyHeader);
            Assert.NotEqual(
                sourceHeader.ObjectIdentity,
                copyHeader!.ObjectIdentity);
            Assert.InRange(
                copyEnvelope.Length,
                1,
                LineageFormat.MaximumEnvelopeBytes);

            var copyUpload = new OpaqueStoreUploadRequest(
                name!,
                new OpaqueStoreCorrelationId("s5-boundary-copy"),
                copyEnvelope,
                new OpaqueStoreEncryptedObjectDigest(
                    OpaqueStoreHash.Sha256(copyEnvelope)),
                AcceptedStateTestData.LogicalExpiresAtUnixSeconds + 3_600);
            Assert.True(OpaqueStoreValidation.IsValid(copyUpload));
            var uploadedCopy = await store.UploadImmutableAsync(
                copyUpload,
                CancellationToken.None);
            Assert.True(uploadedCopy.Succeeded);
            var copyMetadata = Assert.IsType<OpaqueStoreObjectMetadata>(
                uploadedCopy.Metadata);
            Assert.NotEqual(
                sourceMetadata.Reference.ObjectId.Value,
                copyMetadata.Reference.ObjectId.Value);
            Assert.True((await store.ReadBackExactAsync(
                new OpaqueStoreReadBackRequest(copyMetadata),
                CancellationToken.None)).Succeeded);
            var downloadedCopy = await store.DownloadAsync(
                new OpaqueStoreDownloadRequest(
                    copyMetadata,
                    OpaqueStoreLimits.MaximumObjectBytes),
                CancellationToken.None);
            Assert.True(downloadedCopy.Succeeded);

            Assert.True(StateControlEnvelopeV1Codec.TryDecrypt(
                locatorContext,
                locatorAccess,
                name!,
                downloadedCopy.EncryptedBytes.Span,
                out var recoveredHeader,
                out recoveredCopyBytes,
                out var outerFailure), outerFailure);
            Assert.Equal(copyHeader, recoveredHeader);
            Assert.True(AcceptedStatePhysicalCopyCodec.TryDecode(
                recoveredCopyBytes,
                out var recoveredCopy));
            Assert.NotNull(recoveredCopy);
            Assert.Equal(
                logicalGenerationIdentity,
                recoveredCopy!.LogicalGenerationIdentity);
            Assert.Equal(
                sourceHeader.ObjectIdentity,
                recoveredCopy.OriginalCandidateObjectIdentity);
            Assert.Equal(
                sourceMetadata.Reference.ObjectId.Value,
                recoveredCopy.SourceArtifactId);
            Assert.Equal(
                sourceMetadata.ArchiveDigest.Sha256,
                recoveredCopy.SourceArchiveSha256);
            Assert.Equal(
                sourceMetadata.EncryptedObjectDigest.Sha256,
                recoveredCopy.SourceEncryptedEnvelopeSha256);
            Assert.True(AcceptedStateGenerationRecordCodec.TryDecode(
                recoveredCopy.CanonicalGenerationBytes.AsSpan(),
                out var recoveredGeneration));
            Assert.NotNull(recoveredGeneration);
            Assert.Equal(document.Generation, recoveredGeneration!.Generation);
            Assert.Equal(
                document.PredecessorStateSha256,
                recoveredGeneration.PredecessorEnvelopeSha256);
            Assert.Equal(
                previousLogicalGenerationIdentity,
                recoveredGeneration.PreviousLogicalGenerationIdentity);

            var recoveredBinding = new RestrictedStateBinding(
                stateScope,
                recoveredGeneration.ProducerBaseSha,
                recoveredGeneration.ProducerHeadSha,
                recoveredGeneration.Generation,
                recoveredGeneration.PredecessorEnvelopeSha256,
                recoveredGeneration.PreparedAtUnixSeconds,
                recoveredGeneration.PreparedExpiresAtUnixSeconds);
            Assert.True(RestrictedStateEnvelope.TryDecrypt(
                stateAccess,
                recoveredBinding,
                recoveredGeneration.EncryptedStateEnvelope.AsSpan(),
                keyResolver,
                out recoveredSessionBytes,
                out var decryptFailure), decryptFailure);
            Assert.NotNull(recoveredSessionBytes);
            Assert.Equal(admittedSession.Plaintext, recoveredSessionBytes);
            var readmitted = new AgentSessionRestrictedStateAdmission().Admit(
                stateAccess,
                recoveredSessionBytes,
                AdmissionContext(
                    fixture,
                    document,
                    recoveredGeneration.StateEnvelopeSha256));
            Assert.True(readmitted.Succeeded);
            Assert.NotNull(readmitted.Session);
            Assert.Equal(
                recoveredGeneration.SessionSha256,
                readmitted.Session!.SessionSha256);
            Assert.Equal(document.Generation, readmitted.Session.Generation);
            Assert.Equal(
                document.PredecessorStateSha256,
                readmitted.Session.PredecessorEnvelopeSha256);

            Assert.False(StateControlEnvelopeV1Codec.TryDecrypt(
                locatorContext,
                locatorAccess,
                name!,
                new byte[LineageFormat.MaximumEnvelopeBytes + 1],
                out _,
                out _,
                out _));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(scopeBytes);
            Zero(sourceEnvelope);
            Zero(copyBytes);
            Zero(copyEnvelope);
            Zero(recoveredCopyBytes);
            Zero(recoveredSessionBytes);
        }

        Assert.False(AcceptedStatePublicationPayloadCodec.TryDecode(
            new byte[AcceptedStateFormat.MaximumPublicationPayloadBytes + 1],
            out _));
        Assert.False(AcceptedStateGenerationRecordCodec.TryDecode(
            new byte[AcceptedStateFormat.MaximumGenerationPayloadBytes + 1],
            out _));
        Assert.False(AcceptedStatePhysicalCopyCodec.TryDecode(
            new byte[AcceptedStateFormat.MaximumPhysicalCopyPayloadBytes + 1],
            out _));

        var exactStoreCap = new byte[OpaqueStoreLimits.MaximumObjectBytes];
        var exactUpload = new OpaqueStoreUploadRequest(
            new OpaqueStoreName("s5-boundary"),
            new OpaqueStoreCorrelationId("s5-boundary"),
            exactStoreCap,
            new OpaqueStoreEncryptedObjectDigest(
                OpaqueStoreHash.Sha256(exactStoreCap)),
            AcceptedStateTestData.LogicalExpiresAtUnixSeconds);
        Assert.True(OpaqueStoreValidation.IsValid(exactUpload));
        Assert.False(OpaqueStoreValidation.IsValid(exactUpload with
        {
            EncryptedBytes =
                new byte[OpaqueStoreLimits.MaximumObjectBytes + 1],
        }));

        Zero(admittedSession.Plaintext);
        Zero(admittedSession.Value.Artifact.Plaintext);
    }

    private static RestrictedStateScope RestrictedScope(
        AgentSessionDocument document) =>
        new(
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

    private static RestrictedStateSessionAdmissionContext AdmissionContext(
        AgentSessionStateBoundaryTests.SessionFixture fixture,
        AgentSessionDocument document,
        string envelopeSha256) =>
        new(
            document.ProducerBaseSha,
            document.ProducerHeadSha,
            document.Generation,
            document.PredecessorStateSha256,
            new AgentSessionStateAdmissionContext(
                fixture.Trusted,
                document.SessionId,
                fixture.Identity,
                new ProjectChatMessage(
                    "user",
                    [new ProjectTextContent("current review context")]),
                AgentSessionHeadTransition.SameHead,
                DeepSeekReasoningContinuationCodec.Instance,
                envelopeSha256));

    private static void Zero(byte[]? bytes)
    {
        if (bytes is not null)
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static RestrictedStateAdmittedSession MaximumAdmittedSession(
        AgentSessionStateBoundaryTests.SessionFixture fixture)
    {
        const int completedRuns = 16;
        var original = fixture.Artifact.Document;
        var predecessorEnvelopeSha256 = new string('d', 64);
        var priorSessionSha256 = new string('e', 64);
        var expandedRoot = original with
        {
            Generation = completedRuns - 1,
            PredecessorStateSha256 = predecessorEnvelopeSha256,
            PriorSessionSha256 = priorSessionSha256,
        };
        var latestPlanSha256 = AgentCanonical.StablePlanSha256(
            AgentSessionValidation.PlanFromRoot(
                expandedRoot,
                priorSessionSha256));
        var runs = Enumerable.Range(0, completedRuns)
            .Select(index => CloneRun(
                original.CompletedRuns[0],
                index,
                index == 0
                    ? original.CompletedRuns[0].StablePlanSha256
                    : latestPlanSha256,
                "x"))
            .ToImmutableArray();
        var baselineDocument = expandedRoot with
        {
            CompletedRuns = runs,
        };
        Assert.True(AgentSessionCodec.TryWrite(
            baselineDocument,
            out var baselineArtifact,
            out var baselineFailure), baselineFailure);

        var remaining = AgentLimits.SessionPlaintextBytes -
            baselineArtifact!.Plaintext.Length;
        Assert.InRange(
            remaining,
            1,
            completedRuns * (AgentLimits.ContentBytes - 1));
        var paddedRuns = runs.ToBuilder();
        for (var index = 0;
            index < paddedRuns.Count && remaining > 0;
            index++)
        {
            var additional = Math.Min(
                remaining,
                AgentLimits.ContentBytes - 1);
            paddedRuns[index] = WithReviewContextLength(
                paddedRuns[index],
                additional + 1);
            remaining -= additional;
        }

        Assert.Equal(0, remaining);
        var maximumDocument = baselineDocument with
        {
            CompletedRuns = paddedRuns.MoveToImmutable(),
        };
        Assert.True(AgentSessionCodec.TryWrite(
            maximumDocument,
            out var maximumArtifact,
            out var maximumFailure), maximumFailure);
        Assert.Equal(
            AgentLimits.SessionPlaintextBytes,
            maximumArtifact!.Plaintext.Length);
        Assert.True(AgentSessionValidation.TryValidateRoot(
            maximumDocument,
            out var rootFailure), rootFailure);
        Assert.True(AgentSessionValidation.TryValidateRecords(
            maximumDocument,
            DeepSeekReasoningContinuationCodec.Instance,
            out var recordsFailure), recordsFailure);

        var scope = new RestrictedStateScope(
            maximumDocument.RepositoryId,
            maximumDocument.WorkflowIdentity,
            maximumDocument.ReviewTarget,
            maximumDocument.SessionId,
            maximumDocument.ProviderId,
            maximumDocument.ModelId,
            maximumDocument.AdapterId,
            maximumDocument.PolicySha256,
            maximumDocument.LimitsSha256,
            maximumDocument.ToolsetSha256,
            maximumDocument.BuildId);
        var access = RestrictedStateTestData.Access(scope);
        var admitted = new AgentSessionRestrictedStateAdmission().Admit(
            access,
            maximumArtifact.Plaintext,
            new RestrictedStateSessionAdmissionContext(
                maximumDocument.ProducerBaseSha,
                maximumDocument.ProducerHeadSha,
                maximumDocument.Generation,
                maximumDocument.PredecessorStateSha256,
                new AgentSessionStateAdmissionContext(
                    fixture.Trusted,
                    maximumDocument.SessionId,
                    fixture.Identity,
                    new ProjectChatMessage(
                        "user",
                        [new ProjectTextContent("current review context")]),
                    AgentSessionHeadTransition.SameHead,
                    DeepSeekReasoningContinuationCodec.Instance,
                    EnvelopeSha256: null)));
        Assert.True(admitted.Succeeded);
        Assert.NotNull(admitted.Session);
        Assert.Equal(
            maximumArtifact.SessionSha256,
            admitted.Session!.SessionSha256);
        return admitted.Session;
    }

    private static AgentSessionCompletedRun CloneRun(
        AgentSessionCompletedRun template,
        int runIndex,
        string stablePlanSha256,
        string reviewContext)
    {
        var context = Assert.IsType<AgentSessionReviewContextRecord>(
            template.Records[0]);
        var message = Assert.IsType<AgentSessionAssistantMessageRecord>(
            template.Records[1]);
        var outcome = Assert.IsType<AgentSessionReviewOutcomeRecord>(
            template.Records[2]);
        var continuation = Assert.Single(template.Continuation.Items);
        var messageId = $"message_{runIndex}";
        var callId = $"finish_{runIndex}";
        var continuationId = $"continuation_{runIndex}";
        var contents = message.Contents.Select(content => content switch
        {
            AgentSessionContinuationSlotContent slot => slot with
            {
                ContinuationItemId = continuationId,
            },
            AgentSessionTerminalCallContent terminal => terminal with
            {
                CallId = callId,
            },
            _ => content,
        }).ToImmutableArray();

        return template with
        {
            RunId = $"run_{runIndex}",
            RunOrdinal = runIndex,
            StablePlanSha256 = stablePlanSha256,
            Records =
            [
                context with
                {
                    Id = $"context_{runIndex}",
                    Text = reviewContext,
                },
                message with
                {
                    Id = messageId,
                    Contents = contents,
                },
                outcome with
                {
                    Id = $"outcome_{runIndex}",
                    TerminalMessageId = messageId,
                    TerminalCallId = callId,
                },
            ],
            Continuation = template.Continuation with
            {
                Items =
                [
                    continuation with
                    {
                        ItemId = continuationId,
                        MessageId = messageId,
                        PayloadSha256 = AgentSessionCodec
                            .ContinuationPayloadSha256(
                                template.Continuation.CodecId,
                                template.Continuation.CodecDiscriminator,
                                continuationId,
                                continuation.Encoding,
                                continuation.PayloadBytes),
                    },
                ],
            },
        };
    }

    private static AgentSessionCompletedRun WithReviewContextLength(
        AgentSessionCompletedRun run,
        int length)
    {
        var context = Assert.IsType<AgentSessionReviewContextRecord>(
            run.Records[0]);
        return run with
        {
            Records = run.Records.SetItem(
                0,
                context with { Text = new string('x', length) }),
        };
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static string Hash(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
}

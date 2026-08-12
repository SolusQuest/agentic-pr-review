using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Tests.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Tests.Host.State.Lineage;

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
        var session = await AgentSessionStateBoundaryTests.BuildSessionAsync();
        var emptyWorkflow = session.Artifact.Document with
        {
            WorkflowIdentity = string.Empty,
        };
        Assert.True(AgentSessionCodec.TryWrite(
            emptyWorkflow,
            out var baseArtifact,
            out var baseFailure), baseFailure);
        var maximumSession = emptyWorkflow with
        {
            WorkflowIdentity = new string(
                'w',
                AgentLimits.SessionPlaintextBytes -
                    baseArtifact!.Plaintext.Length),
        };
        Assert.True(AgentSessionCodec.TryWrite(
            maximumSession,
            out var sessionArtifact,
            out var sessionFailure), sessionFailure);
        Assert.Equal(
            AgentLimits.SessionPlaintextBytes,
            sessionArtifact!.Plaintext.Length);

        var stateAccess = RestrictedStateTestData.Access();
        var binding = RestrictedStateTestData.Binding();
        var keyResolver = new TestKeyResolver();
        Assert.True(RestrictedStateEnvelope.TryEncrypt(
            stateAccess,
            binding,
            sessionArtifact.Plaintext,
            keyResolver,
            out var stateEnvelope,
            out var stateFailure), stateFailure);
        Assert.NotNull(stateEnvelope);
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

        var template = AcceptedStateTestData.Generation(out _);
        var generation = template with
        {
            EncryptedStateEnvelope =
                ImmutableArray.CreateRange(stateEnvelope),
            StateEnvelopeSha256 =
                RestrictedStateEnvelope.EnvelopeSha256(stateEnvelope),
            SessionSha256 = sessionArtifact.SessionSha256,
            PublicationPayloadBytes =
                ImmutableArray.CreateRange(publicationBytes),
            PublicationPayloadSha256 =
                AcceptedStateRecordValidation.Sha256(publicationBytes),
        };
        Assert.True(AcceptedStateGenerationRecordCodec.TryEncode(
            generation,
            out var generationBytes));
        Assert.InRange(
            generationBytes.Length,
            1,
            AcceptedStateFormat.MaximumGenerationPayloadBytes);

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
        Assert.InRange(
            copyBytes.Length,
            1,
            AcceptedStateFormat.MaximumPhysicalCopyPayloadBytes);

        using var lease = LineageTestData.Context(
            AcceptedStateTestData.AcceptedAtUnixSeconds);
        var scope = LineageTestData.Scope();
        Assert.True(LineageBaseScopeCodec.TryEncode(
            scope,
            out var scopeBytes));
        try
        {
            Assert.True(LineageBaseScopeCodec.TryDigest(
                scope,
                out var scopeDigest));
            Assert.True(lease.Context.TryDeriveOpaqueName(
                lease.Access,
                StateObjectClasses.ToWireName(StateObjectClass.Candidate),
                scopeBytes,
                out var name));
            Assert.NotNull(name);
            var draft = new StateControlHeaderDraft(
                scopeDigest,
                AcceptedStateTestData.Epoch,
                AcceptedStateTestData.SessionId,
                StateObjectClass.Candidate,
                PredecessorIdentity: null,
                SuccessorIdentity: null,
                "boundary-run",
                1,
                AcceptedStateTestData.AcceptedAtUnixSeconds,
                AcceptedStateTestData.LogicalExpiresAtUnixSeconds,
                AcceptedStateTestData.LogicalExpiresAtUnixSeconds + 3_600);
            Assert.True(StateControlEnvelopeV1Codec.TryEncrypt(
                lease.Context,
                lease.Access,
                name!,
                draft,
                copyBytes,
                out var outerEnvelope,
                out _,
                out var outerFailure), outerFailure);
            Assert.InRange(
                outerEnvelope.Length,
                1,
                LineageFormat.MaximumEnvelopeBytes);

            var upload = new OpaqueStoreUploadRequest(
                name!,
                new OpaqueStoreCorrelationId("s5-boundary"),
                outerEnvelope,
                new OpaqueStoreEncryptedObjectDigest(
                    OpaqueStoreHash.Sha256(outerEnvelope)),
                AcceptedStateTestData.LogicalExpiresAtUnixSeconds + 3_600);
            Assert.True(OpaqueStoreValidation.IsValid(upload));

            Assert.False(StateControlEnvelopeV1Codec.TryDecrypt(
                lease.Context,
                lease.Access,
                name!,
                new byte[LineageFormat.MaximumEnvelopeBytes + 1],
                out _,
                out _,
                out _));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(scopeBytes);
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
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static string Hash(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
}

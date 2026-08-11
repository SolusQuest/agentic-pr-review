using System.Collections.Immutable;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Tests.Host.Publishing.Rendering;

namespace AgenticPrReview.Runtime.Tests.Host.State.Restore;

internal static class AcceptedStateTestData
{
    internal const string RepositoryName = "owner/repository";
    internal const long RepositoryId = 123_456_789;
    internal const long PullRequestNumber = 42;
    internal const string BuildDiscriminator = "runtime-payload-v1";
    internal const long AcceptedAtUnixSeconds = 1_700_000_000;
    internal const long LogicalExpiresAtUnixSeconds =
        AcceptedAtUnixSeconds + AcceptedStateFormat.LogicalWindowSeconds;
    internal static readonly string PolicySha256 = new('1', 64);
    internal static readonly string ConfigSha256 = new('2', 64);
    internal static readonly string InstructionsSha256 = new('3', 64);
    internal static readonly string PayloadSha256 = new('4', 64);
    internal static readonly string Epoch = new('5', 64);
    internal static readonly string SessionId = new('6', 64);
    internal static readonly string BaseScopeDigest = new('7', 64);
    internal static readonly string OriginalCandidateIdentity = new('8', 64);
    internal static readonly string ReceiptIdentity = new('9', 64);

    internal static ValidatedPublicationPayloadV1 Publication(
        out byte[] bytes,
        ReviewedIdentity? identity = null,
        string? buildDiscriminator = null)
    {
        var rendered = R4PublicationTestData.Render(identity: identity);
        Assert.True(ValidatedPublicationPayloadV1.TryCreate(
            rendered.Comment,
            RepositoryId,
            RepositoryName,
            PullRequestNumber,
            PolicySha256,
            PayloadSha256,
            buildDiscriminator ?? BuildDiscriminator,
            AcceptedStateFormat.RenderingVersion,
            out var publication));
        Assert.NotNull(publication);
        Assert.True(AcceptedStatePublicationPayloadCodec.TryEncode(
            publication,
            out bytes));
        return publication!;
    }

    internal static StateGenerationRecordV1 Generation(
        out byte[] bytes,
        long generation = 0,
        string? predecessorEnvelopeSha256 = null,
        string? previousLogicalGenerationIdentity = null)
    {
        _ = Publication(out var publicationBytes);
        var access = RestrictedStateTestData.Access();
        var keys = new TestKeyResolver();
        var state = RestrictedStateTestData.Candidate(
            access,
            keys,
            generation,
            predecessorEnvelopeSha256);
        var value = new StateGenerationRecordV1(
            ImmutableArray.CreateRange(state.Envelope),
            state.EnvelopeSha256,
            state.SessionSha256,
            R4PublicationTestData.BaseSha,
            R4PublicationTestData.HeadSha,
            generation,
            predecessorEnvelopeSha256,
            previousLogicalGenerationIdentity,
            AcceptedAtUnixSeconds,
            LogicalExpiresAtUnixSeconds,
            ImmutableArray.CreateRange(publicationBytes),
            AcceptedStateRecordValidation.Sha256(publicationBytes),
            PolicySha256,
            ConfigSha256,
            InstructionsSha256,
            PayloadSha256,
            BuildDiscriminator);
        Assert.True(AcceptedStateGenerationRecordCodec.TryEncode(
            value,
            out bytes));
        return value;
    }

    internal static AcceptanceReceiptV1 Receipt(
        string logicalGenerationIdentity,
        string originalCandidateIdentity,
        out byte[] bytes,
        string? previousLogicalGenerationIdentity = null,
        string? previousAcceptanceReceiptIdentity = null,
        long acceptedAtUnixSeconds = AcceptedAtUnixSeconds,
        ReviewedIdentity? identity = null,
        string? buildDiscriminator = null)
    {
        var publication = Publication(
            out var publicationBytes,
            identity,
            buildDiscriminator);
        var value = new AcceptanceReceiptV1(
            logicalGenerationIdentity,
            originalCandidateIdentity,
            previousLogicalGenerationIdentity,
            previousAcceptanceReceiptIdentity,
            publication.ReviewedHeadSha,
            StickyPublicationOperation.Observed,
            publication.RepositoryId,
            publication.PullRequestNumber,
            CommentId: 99,
            $"https://github.com/{RepositoryName}/pull/" +
                $"{PullRequestNumber}#issuecomment-99",
            publication.ScopeSha256,
            publication.BodySha256,
            AcceptedStateRecordValidation.Sha256(publicationBytes),
            "workflow-run-42",
            ProducingRunAttempt: 1,
            acceptedAtUnixSeconds,
            acceptedAtUnixSeconds +
                AcceptedStateFormat.LogicalWindowSeconds);
        Assert.True(AcceptedStateAcceptanceReceiptCodec.TryEncode(
            value,
            out bytes));
        return value;
    }

    internal static AcceptedStatePhysicalCopyV1 PhysicalCopy(
        out byte[] bytes)
    {
        _ = Generation(out var generationBytes);
        Assert.True(AcceptedStateIdentity.TryComputeLogicalGeneration(
            generationBytes,
            BaseScopeDigest,
            Epoch,
            SessionId,
            previousAcceptanceReceiptIdentity: null,
            out var logicalIdentity));
        var value = new AcceptedStatePhysicalCopyV1(
            ImmutableArray.CreateRange(generationBytes),
            logicalIdentity,
            OriginalCandidateIdentity,
            SourceArtifactId: "42",
            SourceArchiveSha256: new string('a', 64),
            SourceEncryptedEnvelopeSha256: new string('b', 64));
        Assert.True(AcceptedStatePhysicalCopyCodec.TryEncode(
            value,
            out bytes));
        return value;
    }
}

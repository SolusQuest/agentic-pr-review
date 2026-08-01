using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Host.State;

namespace AgenticPrReview.Runtime;

internal sealed record LiveAgentFreshProcessAdmittedLineage(
    AcceptedLineage Lineage,
    ReviewedIdentity ProducerIdentity,
    string InvocationIdentitySha256,
    LiveAgentFreshProcessFileVersion FileVersion,
    string RawSha256);

internal sealed record LiveAgentFreshProcessLineagePublicationReceipt(
    LiveAgentLineagePublicationOutcome Outcome,
    string LineageSha256,
    long Generation,
    string SessionSha256,
    string EnvelopeSha256,
    LiveAgentFreshProcessFileVersion FileVersion);

internal static class LiveAgentFreshProcessLineageAdmission
{
    internal static bool TryAdmit(
        LiveAgentFreshProcessRead read,
        string expectedRawSha256,
        RestrictedStateScope authorizedScope,
        out LiveAgentFreshProcessAdmittedLineage? admitted) => TryAdmit(
            read,
            expectedRawSha256,
            authorizedScope,
            LiveAgentFreshProcessCodec.ReadLineage,
            out admitted);

    internal static bool TryAdmit(
        LiveAgentFreshProcessRead read,
        string expectedRawSha256,
        RestrictedStateScope authorizedScope,
        Func<byte[], LiveAgentFreshProcessLineageDocument?> decode,
        out LiveAgentFreshProcessAdmittedLineage? admitted)
    {
        admitted = null;
        if (read is null ||
            !LiveAgentFreshProcessDomain.IsSha256(expectedRawSha256) ||
            !StringComparer.Ordinal.Equals(
                LiveAgentFreshProcessDomain.RawSha256(read.Bytes),
                expectedRawSha256))
        {
            return false;
        }

        if (decode is null)
        {
            return false;
        }

        var document = decode(read.Bytes);
        if (document is null ||
            !StringComparer.Ordinal.Equals(
                document.Kind,
                LiveAgentFreshProcessDomain.LineageKind) ||
            !LiveAgentFreshProcessDomain.TryMapScope(
                document.Scope,
                out var scope) ||
            scope != authorizedScope ||
            !LiveAgentFreshProcessDomain.IsSha256(
                document.SessionSha256) ||
            !LiveAgentFreshProcessDomain.IsSha256(
                document.EnvelopeSha256) ||
            document.ExpectedPredecessorEnvelopeSha256 is not null &&
                !LiveAgentFreshProcessDomain.IsSha256(
                    document.ExpectedPredecessorEnvelopeSha256) ||
            !LiveAgentFreshProcessDomain.IsSha256(
                document.InvocationIdentitySha256) ||
            !LiveAgentFreshProcessDomain.IsCommitSha(
                document.ProducerBaseSha) ||
            !LiveAgentFreshProcessDomain.IsCommitSha(
                document.ProducerHeadSha))
        {
            return false;
        }

        var producer = new ReviewedIdentity(
            scope.RepositoryId,
            scope.ReviewTarget,
            document.ProducerBaseSha,
            document.ProducerHeadSha);
        var lineage = new AcceptedLineage(
            scope,
            document.Generation,
            document.SessionSha256,
            document.EnvelopeSha256,
            document.ExpectedPredecessorEnvelopeSha256,
            document.AcceptedAtUnixSeconds,
            document.ExpiresAtUnixSeconds,
            document.TransitionAuthorized);
        if (!producer.IsValid() ||
            !RestrictedStateValidation.IsValidLineage(lineage))
        {
            return false;
        }

        admitted = new LiveAgentFreshProcessAdmittedLineage(
            lineage,
            producer,
            document.InvocationIdentitySha256,
            read.Version,
            expectedRawSha256);
        return true;
    }
}

internal sealed class LiveAgentFreshProcessLineageSink(
    ILiveAgentFreshProcessFileSystem fileSystem,
    LiveAgentFreshProcessAuthorizedRoot root,
    ReviewedIdentity producerIdentity,
    string invocationIdentitySha256,
    LiveAgentFreshProcessAdmittedLineage? prior)
    : ILiveAgentAcceptedLineageSink
{
    private int calls;

    internal LiveAgentFreshProcessLineagePublicationReceipt? PublicationReceipt
    { get; private set; }

    public LiveAgentLineagePublicationOutcome PublishAtomically(
        AcceptedLineage? priorLineage,
        AcceptedLineage acceptedLineage,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref calls) != 1 ||
            cancellationToken.IsCancellationRequested ||
            fileSystem is null ||
            root is null ||
            producerIdentity is null ||
            !producerIdentity.IsValid() ||
            !LiveAgentFreshProcessDomain.IsSha256(
                invocationIdentitySha256) ||
            acceptedLineage is null ||
            !RestrictedStateValidation.IsValidLineage(acceptedLineage) ||
            acceptedLineage.Scope != root.Access.Scope ||
            priorLineage != prior?.Lineage ||
            (prior is null) != (acceptedLineage.Generation == 0) ||
            prior is not null &&
                (!StringComparer.Ordinal.Equals(
                    prior.Lineage.EnvelopeSha256,
                    acceptedLineage.ExpectedPredecessorEnvelopeSha256) ||
                 acceptedLineage.Generation != prior.Lineage.Generation + 1))
        {
            return LiveAgentLineagePublicationOutcome.Unavailable;
        }

        var document = new LiveAgentFreshProcessLineageDocument(
            LiveAgentFreshProcessDomain.LineageKind,
            LiveAgentFreshProcessDomain.ScopeDocument(acceptedLineage.Scope),
            producerIdentity.BaseSha,
            producerIdentity.HeadSha,
            acceptedLineage.Generation,
            acceptedLineage.SessionSha256,
            acceptedLineage.EnvelopeSha256,
            acceptedLineage.ExpectedPredecessorEnvelopeSha256,
            acceptedLineage.AcceptedAtUnixSeconds,
            acceptedLineage.ExpiresAtUnixSeconds,
            acceptedLineage.TransitionAuthorized,
            invocationIdentitySha256);
        var bytes = LiveAgentFreshProcessCodec.Write(document);
        if (bytes.Length > LiveAgentFreshProcessCodec.LineageBytes)
        {
            return LiveAgentLineagePublicationOutcome.Unavailable;
        }

        var sha256 = LiveAgentFreshProcessDomain.RawSha256(bytes);
        var write = fileSystem.PublishLineage(
            root,
            bytes,
            prior?.FileVersion);
        if (write is null)
        {
            return LiveAgentLineagePublicationOutcome.Unavailable;
        }

        PublicationReceipt =
            new LiveAgentFreshProcessLineagePublicationReceipt(
                LiveAgentLineagePublicationOutcome.Ready,
                sha256,
                acceptedLineage.Generation,
                acceptedLineage.SessionSha256,
                acceptedLineage.EnvelopeSha256,
                write.Version);
        return LiveAgentLineagePublicationOutcome.Ready;
    }
}

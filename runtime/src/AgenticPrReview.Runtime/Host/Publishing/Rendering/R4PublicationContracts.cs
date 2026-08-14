using System.Collections.Immutable;
using System.Globalization;
using AgenticPrReview.Runtime.Agent.Core;

namespace AgenticPrReview.Runtime.Host.Publishing.Rendering;

internal static class R4PublicationFailureCodes
{
    internal const string BodyOverflow = "publication_body_overflow";
    internal const string IdentityInvalid = "publication_identity_invalid";
    internal const string ReviewInvalid = "publication_review_invalid";
    internal const string FingerprintDuplicate = "publication_fingerprint_duplicate";
}

internal sealed class R4PublicationException : Exception
{
    internal R4PublicationException(string code)
        : base(code)
    {
        Code = code;
    }

    internal string Code { get; }
}

internal sealed record R4PublicationScopeV1(
    ulong RepositoryId,
    ulong WorkflowSourceRepositoryId,
    string WorkflowPath,
    string WorkflowRef,
    ulong PullRequestNumber,
    string PolicyIdentitySha256,
    string ActionContractPayloadIdentity);

internal sealed class R4ValidatedPublicationReview
{
    private R4ValidatedPublicationReview(
        AgentTerminalReview review,
        ReviewedIdentity reviewedIdentity,
        R4PublicationScopeV1 scope)
    {
        Review = review;
        ReviewedIdentity = reviewedIdentity;
        Scope = scope;
    }

    internal AgentTerminalReview Review { get; }

    internal ReviewedIdentity ReviewedIdentity { get; }

    internal R4PublicationScopeV1 Scope { get; }

    internal static bool TryCreate(
        AgentRunOutcome? outcome,
        R4PublicationScopeV1? scope,
        out R4ValidatedPublicationReview? review)
    {
        review = null;
        if (outcome is null ||
            scope is null ||
            !outcome.CompletedSessionEligible ||
            outcome.Review is null ||
            outcome.ReviewedIdentity is null ||
            !outcome.ReviewedIdentity.IsValid() ||
            !R4PublicationIdentityV1.IsValidScope(scope) ||
            !TryReadCanonicalRepositoryId(
                outcome.ReviewedIdentity.RepositoryId,
                out var repositoryId) ||
            repositoryId != scope.RepositoryId ||
            outcome.ReviewedIdentity.ReviewTarget < 1 ||
            (ulong)outcome.ReviewedIdentity.ReviewTarget != scope.PullRequestNumber)
        {
            return false;
        }

        review = new R4ValidatedPublicationReview(
            outcome.Review,
            outcome.ReviewedIdentity,
            scope);
        return true;
    }

    internal static bool TryCreateProjection(
        AgentTerminalReview? terminal,
        ReviewedIdentity? reviewedIdentity,
        R4PublicationScopeV1? scope,
        out R4ValidatedPublicationReview? review)
    {
        review = null;
        if (terminal is null ||
            reviewedIdentity is null ||
            scope is null ||
            !reviewedIdentity.IsValid() ||
            !R4PublicationIdentityV1.IsValidScope(scope) ||
            !TryReadCanonicalRepositoryId(
                reviewedIdentity.RepositoryId,
                out var repositoryId) ||
            repositoryId != scope.RepositoryId ||
            reviewedIdentity.ReviewTarget < 1 ||
            (ulong)reviewedIdentity.ReviewTarget != scope.PullRequestNumber)
        {
            return false;
        }

        review = new R4ValidatedPublicationReview(
            terminal,
            reviewedIdentity,
            scope);
        return true;
    }

    private static bool TryReadCanonicalRepositoryId(
        string value,
        out ulong repositoryId)
    {
        if (!ulong.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out repositoryId) ||
            repositoryId == 0)
        {
            return false;
        }

        return StringComparer.Ordinal.Equals(
            value,
            repositoryId.ToString(CultureInfo.InvariantCulture));
    }
}

internal sealed partial record R4PublicationIdentityV1(
    string ScopeSha256,
    string BodySha256,
    string HeadSha);

internal sealed record R4FindingIdentityV1(
    AgentFinding Finding,
    string FingerprintSha256);

internal sealed record R4RenderedStickyComment(
    string Comment,
    string Body,
    R4PublicationIdentityV1 Identity,
    ImmutableArray<R4FindingIdentityV1> OrderedFindings,
    int RenderedFindingCount,
    int OmittedFindingCount);

internal enum R4StickyInspectionKind
{
    NoR4Marker,
    ValidR4,
    InvalidR4,
}

internal enum R4StickyInvalidReason
{
    Duplicate,
    Malformed,
    NonTerminal,
    WrongVersion,
    WrongCase,
    ExtraField,
    Separator,
    TrailingBytes,
    InvalidUnicode,
    InvalidLf,
    BodyDigestMismatch,
}

internal sealed record R4StickyInspection(
    R4StickyInspectionKind Kind,
    string? Body,
    R4PublicationIdentityV1? Identity,
    R4StickyInvalidReason? InvalidReason)
{
    internal static R4StickyInspection NoMarker() =>
        new(R4StickyInspectionKind.NoR4Marker, null, null, null);

    internal static R4StickyInspection Valid(
        string body,
        R4PublicationIdentityV1 identity) =>
        new(R4StickyInspectionKind.ValidR4, body, identity, null);

    internal static R4StickyInspection Invalid(R4StickyInvalidReason reason) =>
        new(R4StickyInspectionKind.InvalidR4, null, null, reason);
}

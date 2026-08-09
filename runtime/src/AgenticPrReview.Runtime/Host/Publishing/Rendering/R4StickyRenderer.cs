using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace AgenticPrReview.Runtime.Host.Publishing.Rendering;

internal static class R4StickyRenderer
{
    internal const string TruncationNoticePrefix = "> Review truncated: ";
    internal const string TruncationNoticeSuffix =
        " validated finding(s) omitted by R4 publication limits.";

    internal static R4RenderedStickyComment Render(
        R4ValidatedPublicationReview publicationReview)
    {
        ArgumentNullException.ThrowIfNull(publicationReview);
        var ordered = R4PublicationIdentityV1.IdentifyAndOrder(
            publicationReview);
        var scopeSha256 = R4PublicationIdentityV1.ComputeScopeSha256(
            publicationReview.Scope);
        var fixedBody = BuildFixedBody(publicationReview, ordered.IsEmpty);
        var blocks = BuildFindingBlocks(ordered);
        var plan = R4StickyBodyPlanner.Select(
            fixedBody,
            blocks,
            R4PublicationBudget.MaximumScalars,
            R4PublicationBudget.MaximumUtf8Bytes);
        var bodySha256 = R4PublicationIdentityV1.ComputeBodySha256(plan.Body);
        var identity = new R4PublicationIdentityV1(
            scopeSha256,
            bodySha256,
            publicationReview.ReviewedIdentity.HeadSha);
        var marker = R4StickyMarker.Create(identity);
        var comment = string.Concat(plan.Body, "\n\n", marker);
        if (!R4PublicationBudget.Fits(
                comment,
                R4PublicationBudget.MaximumScalars,
                R4PublicationBudget.MaximumUtf8Bytes))
        {
            throw new R4PublicationException(
                R4PublicationFailureCodes.BodyOverflow);
        }

        return new R4RenderedStickyComment(
            comment,
            plan.Body,
            identity,
            ordered,
            plan.RenderedFindingCount,
            plan.OmittedFindingCount);
    }

    private static string BuildFixedBody(
        R4ValidatedPublicationReview publicationReview,
        bool noFindings)
    {
        var builder = new StringBuilder();
        builder.Append("## Agentic PR Review\n\nReviewed head: `");
        builder.Append(publicationReview.ReviewedIdentity.HeadSha);
        builder.Append("`\n\n### Summary\n\n");
        builder.Append(R4Markdown.Escape(publicationReview.Review.Summary));
        builder.Append("\n\n### Findings");
        if (noFindings)
        {
            builder.Append("\n\nNo actionable findings.");
        }

        return builder.ToString();
    }

    private static ImmutableArray<string> BuildFindingBlocks(
        ImmutableArray<R4FindingIdentityV1> findings)
    {
        var blocks = ImmutableArray.CreateBuilder<string>(findings.Length);
        foreach (var identified in findings)
        {
            var builder = new StringBuilder();
            builder.Append("#### ");
            builder.Append(SeverityLabel(identified.Finding.Severity));
            builder.Append(": ");
            builder.Append(R4Markdown.Escape(identified.Finding.Title));
            builder.Append("\n\n");
            builder.Append(R4Markdown.Escape(identified.Finding.Message));
            builder.Append("\n\n- Fingerprint: `");
            builder.Append(identified.FingerprintSha256);
            builder.Append("`\n- Evidence:");
            foreach (var evidence in identified.Finding.Evidence)
            {
                builder.Append("\n  - ");
                builder.Append(R4Markdown.Escape(evidence.Path));
                builder.Append(" (lines ");
                builder.Append(evidence.StartLine.ToString(
                    CultureInfo.InvariantCulture));
                builder.Append('-');
                builder.Append(evidence.EndLine.ToString(
                    CultureInfo.InvariantCulture));
                builder.Append("; observation `");
                builder.Append(evidence.ObservationId);
                builder.Append("`)");
            }

            blocks.Add(builder.ToString());
        }

        return blocks.ToImmutable();
    }

    private static string SeverityLabel(string severity) => severity switch
    {
        "critical" => "CRITICAL",
        "high" => "HIGH",
        "medium" => "MEDIUM",
        "low" => "LOW",
        _ => throw new R4PublicationException(
            R4PublicationFailureCodes.ReviewInvalid),
    };
}

internal sealed record R4StickyBodyPlan(
    string Body,
    int RenderedFindingCount,
    int OmittedFindingCount);

internal static class R4StickyBodyPlanner
{
    private const int SeparatorAndMarkerScalars = R4StickyMarker.MarkerLength + 2;
    private const int SeparatorAndMarkerUtf8Bytes = R4StickyMarker.MarkerLength + 2;

    internal static R4StickyBodyPlan Select(
        string fixedBody,
        IReadOnlyList<string> findingBlocks,
        int maximumCommentScalars,
        int maximumCommentUtf8Bytes)
    {
        ArgumentNullException.ThrowIfNull(fixedBody);
        ArgumentNullException.ThrowIfNull(findingBlocks);
        if (findingBlocks.Count == 0)
        {
            if (!BodyFits(
                    fixedBody,
                    maximumCommentScalars,
                    maximumCommentUtf8Bytes))
            {
                throw new R4PublicationException(
                    R4PublicationFailureCodes.BodyOverflow);
            }

            return new R4StickyBodyPlan(fixedBody, 0, 0);
        }

        for (var rendered = findingBlocks.Count; rendered >= 0; rendered--)
        {
            var omitted = findingBlocks.Count - rendered;
            var body = Compose(fixedBody, findingBlocks, rendered, omitted);
            if (BodyFits(
                    body,
                    maximumCommentScalars,
                    maximumCommentUtf8Bytes))
            {
                return new R4StickyBodyPlan(body, rendered, omitted);
            }
        }

        throw new R4PublicationException(
            R4PublicationFailureCodes.BodyOverflow);
    }

    private static string Compose(
        string fixedBody,
        IReadOnlyList<string> findingBlocks,
        int rendered,
        int omitted)
    {
        var builder = new StringBuilder(fixedBody);
        for (var index = 0; index < rendered; index++)
        {
            builder.Append("\n\n");
            builder.Append(findingBlocks[index]);
        }

        if (omitted > 0)
        {
            builder.Append("\n\n");
            builder.Append(R4StickyRenderer.TruncationNoticePrefix);
            builder.Append(omitted.ToString(CultureInfo.InvariantCulture));
            builder.Append(R4StickyRenderer.TruncationNoticeSuffix);
        }

        return builder.ToString();
    }

    private static bool BodyFits(
        string body,
        int maximumCommentScalars,
        int maximumCommentUtf8Bytes) =>
        R4Markdown.TryMeasure(body, out var bodyScalars, out var bodyBytes) &&
        bodyScalars <= maximumCommentScalars - SeparatorAndMarkerScalars &&
        bodyBytes <= maximumCommentUtf8Bytes - SeparatorAndMarkerUtf8Bytes;
}

using System.Collections.Immutable;
using System.Text.Json;
using AgenticPrReview.Runtime.Canonical;
using AgenticPrReview.Runtime.Ledger;

namespace AgenticPrReview.Runtime.Prefix;

/// <summary>
/// Canonical logical projection (issue #50, D3/D5): closed segment shapes,
/// absolute segment order, and the reference canonical provider-block
/// projection (D6).
/// </summary>
internal static class LogicalProjection
{
    internal const string TemplateKind = "template";
    internal const string PolicyKind = "policy";
    internal const string ToolsKind = "tools";
    internal const string ReviewContextKind = "review_context";
    internal const string ReviewOutcomeKind = "review_outcome";

    internal static ImmutableArray<byte> ProjectTemplateSegment(ValidatedEnvelope template)
    {
        var writer = new Rfc8785Writer(template.CanonicalBytes.Length + 64);
        writer.WriteObjectStart();
        writer.WriteProperty("definition");
        JsonElementCanonicalizer.WriteCanonicalValue(ref writer, template.Raw.GetProperty("definition"));
        writer.WriteComma();
        writer.WriteProperty("kind");
        writer.WriteString(TemplateKind);
        writer.WriteComma();
        writer.WriteProperty("templateVersion");
        writer.WriteNumber(template.Raw.GetProperty("templateVersion").GetInt64());
        writer.WriteObjectEnd();
        return writer.ToImmutableArray();
    }

    internal static ImmutableArray<byte> ProjectPolicySegment(ValidatedEnvelope policy)
    {
        var writer = new Rfc8785Writer(policy.CanonicalBytes.Length + 64);
        writer.WriteObjectStart();
        writer.WriteProperty("constraints");
        JsonElementCanonicalizer.WriteCanonicalValue(ref writer, policy.Raw.GetProperty("constraints"));
        writer.WriteComma();
        writer.WriteProperty("instructions");
        JsonElementCanonicalizer.WriteCanonicalValue(ref writer, policy.Raw.GetProperty("instructions"));
        writer.WriteComma();
        writer.WriteProperty("kind");
        writer.WriteString(PolicyKind);
        writer.WriteComma();
        writer.WriteProperty("policyVersion");
        writer.WriteNumber(policy.Raw.GetProperty("policyVersion").GetInt64());
        writer.WriteObjectEnd();
        return writer.ToImmutableArray();
    }

    internal static ImmutableArray<byte> ProjectToolsSegment(ValidatedEnvelope tools)
    {
        var writer = new Rfc8785Writer(tools.CanonicalBytes.Length + 64);
        writer.WriteObjectStart();
        writer.WriteProperty("definitions");
        JsonElementCanonicalizer.WriteCanonicalValue(ref writer, tools.Raw.GetProperty("definitions"));
        writer.WriteComma();
        writer.WriteProperty("kind");
        writer.WriteString(ToolsKind);
        writer.WriteComma();
        writer.WriteProperty("toolsetVersion");
        writer.WriteNumber(tools.Raw.GetProperty("toolsetVersion").GetInt64());
        writer.WriteObjectEnd();
        return writer.ToImmutableArray();
    }

    internal static ImmutableArray<byte> ProjectReviewContextSegment(ReviewContextRecord record)
    {
        var writer = new Rfc8785Writer(2048);
        writer.WriteObjectStart();
        writer.WriteProperty("cacheContractDigest");
        writer.WriteString(record.CacheContractDigest);
        writer.WriteComma();
        writer.WriteProperty("changedFiles");
        WriteChangedFiles(ref writer, record.ChangedFiles);
        writer.WriteComma();
        writer.WriteProperty("interactionOrdinal");
        writer.WriteNumber(record.InteractionOrdinal);
        writer.WriteComma();
        writer.WriteProperty("kind");
        writer.WriteString(ReviewContextKind);
        writer.WriteComma();
        writer.WriteProperty("reviewedBaseSha");
        writer.WriteString(record.ReviewedBaseSha);
        writer.WriteComma();
        writer.WriteProperty("reviewedHeadSha");
        writer.WriteString(record.ReviewedHeadSha);
        writer.WriteComma();
        writer.WriteProperty("subjectDigest");
        writer.WriteString(record.SubjectDigest);
        writer.WriteObjectEnd();
        return writer.ToImmutableArray();
    }

    internal static ImmutableArray<byte> ProjectReviewOutcomeSegment(ReviewOutcomeRecord record)
    {
        var writer = new Rfc8785Writer(2048);
        writer.WriteObjectStart();
        writer.WriteProperty("findings");
        writer.WriteArrayStart();
        for (var i = 0; i < record.Findings.Length; i++)
        {
            if (i > 0)
            {
                writer.WriteComma();
            }

            WriteFinding(ref writer, record.Findings[i]);
        }

        writer.WriteArrayEnd();
        writer.WriteComma();
        writer.WriteProperty("interactionOrdinal");
        writer.WriteNumber(record.InteractionOrdinal);
        writer.WriteComma();
        writer.WriteProperty("kind");
        writer.WriteString(ReviewOutcomeKind);
        writer.WriteComma();
        writer.WriteProperty("limitations");
        writer.WriteArrayStart();
        for (var i = 0; i < record.Limitations.Length; i++)
        {
            if (i > 0)
            {
                writer.WriteComma();
            }

            writer.WriteString(record.Limitations[i]);
        }

        writer.WriteArrayEnd();
        writer.WriteComma();
        writer.WriteProperty("summary");
        writer.WriteString(record.Summary);
        writer.WriteObjectEnd();
        return writer.ToImmutableArray();
    }

    private static void WriteChangedFiles(ref Rfc8785Writer writer, ImmutableArray<LedgerChangedFile> files)
    {
        writer.WriteArrayStart();
        for (var i = 0; i < files.Length; i++)
        {
            if (i > 0)
            {
                writer.WriteComma();
            }

            var file = files[i];
            writer.WriteObjectStart();
            writer.WriteProperty("additions");
            writer.WriteNumber(file.Additions);
            writer.WriteComma();
            writer.WriteProperty("changes");
            writer.WriteNumber(file.Changes);
            writer.WriteComma();
            writer.WriteProperty("deletions");
            writer.WriteNumber(file.Deletions);
            if (file.Patch is not null)
            {
                writer.WriteComma();
                writer.WriteProperty("patch");
                writer.WriteObjectStart();
                writer.WriteProperty("maxChars");
                writer.WriteNumber(file.Patch.MaxChars);
                writer.WriteComma();
                writer.WriteProperty("sha256");
                writer.WriteString(file.Patch.Sha256);
                writer.WriteComma();
                writer.WriteProperty("truncated");
                writer.WriteBoolean(file.Patch.Truncated);
                writer.WriteObjectEnd();
            }

            writer.WriteComma();
            writer.WriteProperty("path");
            writer.WriteString(file.Path);
            if (file.PreviousPath is not null)
            {
                writer.WriteComma();
                writer.WriteProperty("previousPath");
                writer.WriteString(file.PreviousPath);
            }

            writer.WriteComma();
            writer.WriteProperty("status");
            writer.WriteString(file.Status);
            writer.WriteObjectEnd();
        }

        writer.WriteArrayEnd();
    }

    private static void WriteFinding(ref Rfc8785Writer writer, LedgerFinding finding)
    {
        writer.WriteObjectStart();
        writer.WriteProperty("body");
        writer.WriteString(finding.Body);
        writer.WriteComma();
        writer.WriteProperty("category");
        writer.WriteString(finding.Category);
        writer.WriteComma();
        writer.WriteProperty("confidence");
        writer.WriteString(finding.Confidence);
        if (finding.EndLine is not null)
        {
            writer.WriteComma();
            writer.WriteProperty("endLine");
            writer.WriteNumber(finding.EndLine.Value);
        }

        if (finding.Evidence is not null)
        {
            writer.WriteComma();
            writer.WriteProperty("evidence");
            writer.WriteString(finding.Evidence);
        }

        if (finding.InlinePreference is not null)
        {
            writer.WriteComma();
            writer.WriteProperty("inlinePreference");
            writer.WriteString(finding.InlinePreference);
        }

        if (finding.Path is not null)
        {
            writer.WriteComma();
            writer.WriteProperty("path");
            writer.WriteString(finding.Path);
        }

        writer.WriteComma();
        writer.WriteProperty("severity");
        writer.WriteString(finding.Severity);
        if (finding.StartLine is not null)
        {
            writer.WriteComma();
            writer.WriteProperty("startLine");
            writer.WriteNumber(finding.StartLine.Value);
        }

        if (finding.SuggestedAction is not null)
        {
            writer.WriteComma();
            writer.WriteProperty("suggestedAction");
            writer.WriteString(finding.SuggestedAction);
        }

        writer.WriteComma();
        writer.WriteProperty("title");
        writer.WriteString(finding.Title);
        writer.WriteObjectEnd();
    }
}

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
        writer.WriteProperty("kind");
        writer.WriteString(TemplateKind);
        writer.WriteProperty("templateVersion");
        writer.WriteNumber((long)template.Raw.GetProperty("templateVersion").GetDouble());
        writer.WriteObjectEnd();
        return writer.ToImmutableArray();
    }

    internal static ImmutableArray<byte> ProjectPolicySegment(ValidatedEnvelope policy)
    {
        var writer = new Rfc8785Writer(policy.CanonicalBytes.Length + 64);
        writer.WriteObjectStart();
        writer.WriteProperty("constraints");
        JsonElementCanonicalizer.WriteCanonicalValue(ref writer, policy.Raw.GetProperty("constraints"));
        writer.WriteProperty("instructions");
        JsonElementCanonicalizer.WriteCanonicalValue(ref writer, policy.Raw.GetProperty("instructions"));
        writer.WriteProperty("kind");
        writer.WriteString(PolicyKind);
        writer.WriteProperty("policyVersion");
        writer.WriteNumber((long)policy.Raw.GetProperty("policyVersion").GetDouble());
        writer.WriteObjectEnd();
        return writer.ToImmutableArray();
    }

    internal static ImmutableArray<byte> ProjectToolsSegment(ValidatedEnvelope tools)
    {
        var writer = new Rfc8785Writer(tools.CanonicalBytes.Length + 64);
        writer.WriteObjectStart();
        writer.WriteProperty("definitions");
        JsonElementCanonicalizer.WriteCanonicalValue(ref writer, tools.Raw.GetProperty("definitions"));
        writer.WriteProperty("kind");
        writer.WriteString(ToolsKind);
        writer.WriteProperty("toolsetVersion");
        writer.WriteNumber((long)tools.Raw.GetProperty("toolsetVersion").GetDouble());
        writer.WriteObjectEnd();
        return writer.ToImmutableArray();
    }

    internal static ImmutableArray<byte> ProjectReviewContextSegment(ReviewContextRecord record)
    {
        var writer = new Rfc8785Writer(2048);
        writer.WriteObjectStart();
        writer.WriteProperty("cacheContractDigest");
        writer.WriteString(record.CacheContractDigest);
        writer.WriteProperty("changedFiles");
        WriteChangedFiles(ref writer, record.ChangedFiles);
        writer.WriteProperty("interactionOrdinal");
        writer.WriteNumber(record.InteractionOrdinal);
        writer.WriteProperty("kind");
        writer.WriteString(ReviewContextKind);
        writer.WriteProperty("reviewedBaseSha");
        writer.WriteString(record.ReviewedBaseSha);
        writer.WriteProperty("reviewedHeadSha");
        writer.WriteString(record.ReviewedHeadSha);
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
        writer.WriteProperty("interactionOrdinal");
        writer.WriteNumber(record.InteractionOrdinal);
        writer.WriteProperty("kind");
        writer.WriteString(ReviewOutcomeKind);
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
            writer.WriteProperty("changes");
            writer.WriteNumber(file.Changes);
            writer.WriteProperty("deletions");
            writer.WriteNumber(file.Deletions);
            if (file.Patch is not null)
            {
                writer.WriteProperty("patch");
                writer.WriteObjectStart();
                writer.WriteProperty("maxChars");
                writer.WriteNumber(file.Patch.MaxChars);
                writer.WriteProperty("sha256");
                writer.WriteString(file.Patch.Sha256);
                writer.WriteProperty("truncated");
                writer.WriteBoolean(file.Patch.Truncated);
                writer.WriteObjectEnd();
            }

            writer.WriteProperty("path");
            writer.WriteString(file.Path);
            if (file.PreviousPath is not null)
            {
                writer.WriteProperty("previousPath");
                writer.WriteString(file.PreviousPath);
            }

            writer.WriteProperty("status");
            writer.WriteString(file.Status);
            writer.WriteObjectEnd();
        }

        writer.WriteArrayEnd();
    }

    private static void WriteFinding(ref Rfc8785Writer writer, LedgerFinding finding)
    {
        // #49 requires path/startLine/endLine to be present (explicit null when
        // absent); only evidence/suggestedAction/inlinePreference are omissible.
        writer.WriteObjectStart();
        writer.WriteProperty("body");
        writer.WriteString(finding.Body);
        writer.WriteProperty("category");
        writer.WriteString(finding.Category);
        writer.WriteProperty("confidence");
        writer.WriteString(finding.Confidence);
        writer.WriteProperty("endLine");
        if (finding.EndLine is not null)
        {
            writer.WriteNumber(finding.EndLine.Value);
        }
        else
        {
            writer.WriteNull();
        }

        if (finding.Evidence is not null)
        {
            writer.WriteProperty("evidence");
            writer.WriteString(finding.Evidence);
        }

        if (finding.InlinePreference is not null)
        {
            writer.WriteProperty("inlinePreference");
            writer.WriteString(finding.InlinePreference);
        }

        writer.WriteProperty("path");
        if (finding.Path is not null)
        {
            writer.WriteString(finding.Path);
        }
        else
        {
            writer.WriteNull();
        }

        writer.WriteProperty("severity");
        writer.WriteString(finding.Severity);
        writer.WriteProperty("startLine");
        if (finding.StartLine is not null)
        {
            writer.WriteNumber(finding.StartLine.Value);
        }
        else
        {
            writer.WriteNull();
        }

        if (finding.SuggestedAction is not null)
        {
            writer.WriteProperty("suggestedAction");
            writer.WriteString(finding.SuggestedAction);
        }

        writer.WriteProperty("title");
        writer.WriteString(finding.Title);
        writer.WriteObjectEnd();
    }
}

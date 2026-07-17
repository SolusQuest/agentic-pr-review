using System.Collections.Immutable;
using System.Text;
using AgenticPrReview.Runtime.Ledger;

namespace AgenticPrReview.Runtime.Tests.Ledger;

public sealed class LedgerBuilderTests
{
    private static readonly ExpectedIdentities Identities = LedgerTestBaseline.Identities;

    [Fact]
    public void BuildBootstrapCandidateSucceeds()
    {
        var context = BuildContext(interactionOrdinal: 0);
        var outcome = BuildOutcome(interactionOrdinal: 0);
        var expected = new BootstrapTransition(Identities, "aaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbb", 0);

        var candidateOutcome = LedgerBuilder.CreateBootstrap(expected, context.Value!, outcome.Value!);

        Assert.NotNull(candidateOutcome.Candidate);
        Assert.Empty(candidateOutcome.Diagnostics);
        Assert.Equal("bootstrap", candidateOutcome.Candidate!.Model.Header.Kind);
    }

    [Fact]
    public void BuildOverBoundAppendInteractionsFails()
    {
        // Predecessor at maxItems (64 records = 32 pairs). Appending one more pair exceeds the bound.
        var predecessor = ParseLedger(BuildMaxedPredecessorJson());
        var expected = new ContinuationTransition(
            Identities, "aaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbb",
            predecessor.ContentSha256, "bbbbbbbbbbbbbbbbbbbbbb", 0, 1);

        var context = BuildContextRecord(32, "0000000000000000000000000000000000000000000000000000000000000000");
        var outcome = BuildOutcomeRecord(32, "0000000000000000000000000000000000000000000000000000000000000000");
        var candidateOutcome = LedgerBuilder.AppendContinuation(expected, predecessor, context, outcome);

        Assert.Null(candidateOutcome.Candidate);
        Assert.Single(candidateOutcome.Diagnostics);
        Assert.Equal(LedgerDiagnosticCodes.OverBoundAppend, candidateOutcome.Diagnostics[0].Code);
        Assert.Equal(LedgerDiagnosticCodes.InteractionLimitExceeded, candidateOutcome.Diagnostics[0].CauseCode);
    }

    [Fact]
    public void BuildOverBoundAppendMultiDefectPrefersInteractionLimit()
    {
        // Multi-defect precedence: the candidate exceeds BOTH the interaction limit (66
        // records) and the canonical byte limit (predecessor near the cap plus a padded
        // pair). The interaction limit is the earlier bound, so it owns the CauseCode.
        var predecessor = ParseLedger(BuildPaddedMaxedPredecessorJson());
        Assert.True(predecessor.ByteLength <= LedgerParser.LedgerCanonicalByteLimit);
        var expected = new ContinuationTransition(
            Identities, "aaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbb",
            predecessor.ContentSha256, "bbbbbbbbbbbbbbbbbbbbbb", 0, 1);

        var context = BuildContextRecord(32, "0000000000000000000000000000000000000000000000000000000000000000");
        var outcome = new ReviewOutcomeRecord
        {
            Role = "review_outcome",
            InteractionId = "0000000000000000000000000000000000000000000000000000000000000000",
            InteractionOrdinal = 32,
            Summary = "Summary text.",
            Findings = ImmutableArray<LedgerFinding>.Empty,
            Limitations = PaddedLimitations(entries: 16, length: 1200)
        };
        var candidateOutcome = LedgerBuilder.AppendContinuation(expected, predecessor, context, outcome);

        Assert.Null(candidateOutcome.Candidate);
        Assert.Single(candidateOutcome.Diagnostics);
        Assert.Equal(LedgerDiagnosticCodes.OverBoundAppend, candidateOutcome.Diagnostics[0].Code);
        Assert.Equal(LedgerDiagnosticCodes.InteractionLimitExceeded, candidateOutcome.Diagnostics[0].CauseCode);
    }

    [Fact]
    public void BuildReviewContextRejectsLoneSurrogate()
    {
        var source = new ValidatedContextSource
        {
            SubjectDigest = "1111111111111111111111111111111111111111111111111111111111111111",
            ReviewedHeadSha = "0000000000000000000000000000000000000000",
            ReviewedBaseSha = "1111111111111111111111111111111111111111",
            ChangedFiles = ImmutableArray<LedgerChangedFile>.Empty
        };
        var interaction = new InteractionIdentity("\uD8000000000000000000000000000000000000000000000000000000000000", 0);

        var outcome = LedgerBuilder.BuildReviewContext(source, Identities, interaction);

        Assert.Null(outcome.Value);
        Assert.Single(outcome.Diagnostics);
        Assert.Equal(LedgerDiagnosticCodes.InvalidUnicode, outcome.Diagnostics[0].Code);
    }

    [Fact]
    public void CreateRecoveryRootRejectsInvalidUnicodeRecord()
    {
        var context = BuildContextRecord(0, "\uD8000000000000000000000000000000000000000000000000000000000000");
        var outcome = BuildOutcomeRecord(0, "0000000000000000000000000000000000000000000000000000000000000000");
        var expected = new RecoveryRootTransition(Identities, "dddddddddddddddddddddd", "eeeeeeeeeeeeeeeeeeeeee", 0, "integrity_mismatch");

        var candidateOutcome = LedgerBuilder.CreateRecoveryRoot(expected, context, outcome);

        Assert.Null(candidateOutcome.Candidate);
        Assert.Single(candidateOutcome.Diagnostics);
        Assert.Equal(LedgerDiagnosticCodes.InvalidUnicode, candidateOutcome.Diagnostics[0].Code);
    }

    // Null-field strategy (review-confirmed): malformed caller-fabricated DTO content —
    // runtime-null strings, null collection elements, default (null) ImmutableArrays —
    // never escapes as an unhandled exception and never reaches the canonical writer.
    // The Unicode scans tolerate nulls, the tolerant schema projection writes them as
    // JSON null, and the schema's type checks own the resulting ledger_schema_violation
    // diagnostic. Null argument objects themselves are caller bugs and fail fast with
    // ArgumentNullException.

    [Fact]
    public void BuildReviewOutcomeRejectsNullSummary()
    {
        var source = new ValidatedOutcomeSource
        {
            Summary = null!,
            Findings = ImmutableArray<LedgerFinding>.Empty,
            Limitations = ImmutableArray<string>.Empty
        };
        var interaction = new InteractionIdentity("0000000000000000000000000000000000000000000000000000000000000000", 0);

        var outcome = LedgerBuilder.BuildReviewOutcome(source, interaction);

        Assert.Null(outcome.Value);
        Assert.Single(outcome.Diagnostics);
        Assert.Equal(LedgerDiagnosticCodes.SchemaViolation, outcome.Diagnostics[0].Code);
    }

    [Fact]
    public void BuildReviewContextRejectsNullChangedFilePath()
    {
        var source = new ValidatedContextSource
        {
            SubjectDigest = "1111111111111111111111111111111111111111111111111111111111111111",
            ReviewedHeadSha = "0000000000000000000000000000000000000000",
            ReviewedBaseSha = "1111111111111111111111111111111111111111",
            ChangedFiles = ImmutableArray.Create(new LedgerChangedFile
            {
                Path = null!,
                Status = "modified",
                Additions = 1,
                Deletions = 2,
                Changes = 3
            })
        };
        var interaction = new InteractionIdentity("0000000000000000000000000000000000000000000000000000000000000000", 0);

        var outcome = LedgerBuilder.BuildReviewContext(source, Identities, interaction);

        Assert.Null(outcome.Value);
        Assert.Single(outcome.Diagnostics);
        Assert.Equal(LedgerDiagnosticCodes.SchemaViolation, outcome.Diagnostics[0].Code);
    }

    [Fact]
    public void BuildReviewOutcomeRejectsNullFindingTitle()
    {
        var source = new ValidatedOutcomeSource
        {
            Summary = "Summary text.",
            Findings = ImmutableArray.Create(new LedgerFinding
            {
                Severity = "high",
                Confidence = "high",
                Category = "correctness",
                Title = null!,
                Body = "Finding body."
            }),
            Limitations = ImmutableArray<string>.Empty
        };
        var interaction = new InteractionIdentity("0000000000000000000000000000000000000000000000000000000000000000", 0);

        var outcome = LedgerBuilder.BuildReviewOutcome(source, interaction);

        Assert.Null(outcome.Value);
        Assert.Single(outcome.Diagnostics);
        Assert.Equal(LedgerDiagnosticCodes.SchemaViolation, outcome.Diagnostics[0].Code);
    }

    [Fact]
    public void CreateBootstrapRejectsFabricatedOutcomeWithNullSummary()
    {
        // The component schema replay must classify the malformed record instead of
        // crashing inside the canonical writer.
        var context = BuildContextRecord(0, "0000000000000000000000000000000000000000000000000000000000000000");
        var outcome = new ReviewOutcomeRecord
        {
            Role = "review_outcome",
            InteractionId = "0000000000000000000000000000000000000000000000000000000000000000",
            InteractionOrdinal = 0,
            Summary = null!,
            Findings = ImmutableArray<LedgerFinding>.Empty,
            Limitations = ImmutableArray<string>.Empty
        };
        var expected = new BootstrapTransition(Identities, "aaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbb", 0);

        var candidateOutcome = LedgerBuilder.CreateBootstrap(expected, context, outcome);

        Assert.Null(candidateOutcome.Candidate);
        Assert.Single(candidateOutcome.Diagnostics);
        Assert.Equal(LedgerDiagnosticCodes.SchemaViolation, candidateOutcome.Diagnostics[0].Code);
    }

    [Fact]
    public void CreateRecoveryRootRejectsFabricatedFindingWithNullTitle()
    {
        var context = BuildContextRecord(0, "0000000000000000000000000000000000000000000000000000000000000000");
        var outcome = new ReviewOutcomeRecord
        {
            Role = "review_outcome",
            InteractionId = "0000000000000000000000000000000000000000000000000000000000000000",
            InteractionOrdinal = 0,
            Summary = "Summary text.",
            Findings = ImmutableArray.Create(new LedgerFinding
            {
                Severity = "high",
                Confidence = "high",
                Category = "correctness",
                Title = null!,
                Body = "Finding body."
            }),
            Limitations = ImmutableArray<string>.Empty
        };
        var expected = new RecoveryRootTransition(Identities, "dddddddddddddddddddddd", "eeeeeeeeeeeeeeeeeeeeee", 0, "integrity_mismatch");

        var candidateOutcome = LedgerBuilder.CreateRecoveryRoot(expected, context, outcome);

        Assert.Null(candidateOutcome.Candidate);
        Assert.Single(candidateOutcome.Diagnostics);
        Assert.Equal(LedgerDiagnosticCodes.SchemaViolation, candidateOutcome.Diagnostics[0].Code);
    }

    [Fact]
    public void CreateBootstrapRejectsFabricatedChangedFileWithNullPath()
    {
        var context = new ReviewContextRecord
        {
            Role = "review_context",
            InteractionId = "0000000000000000000000000000000000000000000000000000000000000000",
            InteractionOrdinal = 0,
            SubjectDigest = "1111111111111111111111111111111111111111111111111111111111111111",
            CacheContractDigest = LedgerCanonicalizer.ComputeCacheContractDigest(Identities),
            ReviewedHeadSha = "0000000000000000000000000000000000000000",
            ReviewedBaseSha = "1111111111111111111111111111111111111111",
            ChangedFiles = ImmutableArray.Create(new LedgerChangedFile
            {
                Path = null!,
                Status = "modified",
                Additions = 1,
                Deletions = 2,
                Changes = 3
            })
        };
        var outcome = BuildOutcomeRecord(0, "0000000000000000000000000000000000000000000000000000000000000000");
        var expected = new BootstrapTransition(Identities, "aaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbb", 0);

        var candidateOutcome = LedgerBuilder.CreateBootstrap(expected, context, outcome);

        Assert.Null(candidateOutcome.Candidate);
        Assert.Single(candidateOutcome.Diagnostics);
        Assert.Equal(LedgerDiagnosticCodes.SchemaViolation, candidateOutcome.Diagnostics[0].Code);
    }

    [Fact]
    public void NullArgumentObjectsThrowArgumentNullException()
    {
        var interaction = new InteractionIdentity("0000000000000000000000000000000000000000000000000000000000000000", 0);
        var contextSource = new ValidatedContextSource
        {
            SubjectDigest = "1111111111111111111111111111111111111111111111111111111111111111",
            ReviewedHeadSha = "0000000000000000000000000000000000000000",
            ReviewedBaseSha = "1111111111111111111111111111111111111111",
            ChangedFiles = ImmutableArray<LedgerChangedFile>.Empty
        };
        var outcomeSource = new ValidatedOutcomeSource
        {
            Summary = "Summary text.",
            Findings = ImmutableArray<LedgerFinding>.Empty,
            Limitations = ImmutableArray<string>.Empty
        };
        var context = BuildContextRecord(0, "0000000000000000000000000000000000000000000000000000000000000000");
        var outcome = BuildOutcomeRecord(0, "0000000000000000000000000000000000000000000000000000000000000000");
        var expected = new BootstrapTransition(Identities, "aaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbb", 0);

        Assert.Throws<ArgumentNullException>(() => LedgerBuilder.BuildReviewContext(null!, Identities, interaction));
        Assert.Throws<ArgumentNullException>(() => LedgerBuilder.BuildReviewContext(contextSource, null!, interaction));
        Assert.Throws<ArgumentNullException>(() => LedgerBuilder.BuildReviewContext(contextSource, Identities, null!));
        Assert.Throws<ArgumentNullException>(() => LedgerBuilder.BuildReviewOutcome(null!, interaction));
        Assert.Throws<ArgumentNullException>(() => LedgerBuilder.BuildReviewOutcome(outcomeSource, null!));
        Assert.Throws<ArgumentNullException>(() => LedgerBuilder.CreateBootstrap(null!, context, outcome));
        Assert.Throws<ArgumentNullException>(() => LedgerBuilder.CreateBootstrap(expected, null!, outcome));
        Assert.Throws<ArgumentNullException>(() => LedgerBuilder.CreateBootstrap(expected, context, null!));
    }

    private static BuildOutcome<ReviewContextRecord> BuildContext(long interactionOrdinal)
    {
        var source = new ValidatedContextSource
        {
            SubjectDigest = "1111111111111111111111111111111111111111111111111111111111111111",
            ReviewedHeadSha = "0000000000000000000000000000000000000000",
            ReviewedBaseSha = "1111111111111111111111111111111111111111",
            ChangedFiles = ImmutableArray<LedgerChangedFile>.Empty
        };
        var interaction = new InteractionIdentity("0000000000000000000000000000000000000000000000000000000000000000", interactionOrdinal);
        return LedgerBuilder.BuildReviewContext(source, Identities, interaction);
    }

    private static BuildOutcome<ReviewOutcomeRecord> BuildOutcome(long interactionOrdinal)
    {
        var source = new ValidatedOutcomeSource
        {
            Summary = "Summary text.",
            Findings = ImmutableArray<LedgerFinding>.Empty,
            Limitations = ImmutableArray<string>.Empty
        };
        var interaction = new InteractionIdentity("0000000000000000000000000000000000000000000000000000000000000000", interactionOrdinal);
        return LedgerBuilder.BuildReviewOutcome(source, interaction);
    }

    private static ReviewContextRecord BuildContextRecord(long ordinal, string interactionId)
    {
        return new ReviewContextRecord
        {
            Role = "review_context",
            InteractionId = interactionId,
            InteractionOrdinal = ordinal,
            SubjectDigest = "1111111111111111111111111111111111111111111111111111111111111111",
            CacheContractDigest = LedgerCanonicalizer.ComputeCacheContractDigest(Identities),
            ReviewedHeadSha = "0000000000000000000000000000000000000000",
            ReviewedBaseSha = "1111111111111111111111111111111111111111",
            ChangedFiles = ImmutableArray<LedgerChangedFile>.Empty
        };
    }

    private static ReviewOutcomeRecord BuildOutcomeRecord(long ordinal, string interactionId)
    {
        return new ReviewOutcomeRecord
        {
            Role = "review_outcome",
            InteractionId = interactionId,
            InteractionOrdinal = ordinal,
            Summary = "Summary text.",
            Findings = ImmutableArray<LedgerFinding>.Empty,
            Limitations = ImmutableArray<string>.Empty
        };
    }

    private static ValidatedLedger ParseLedger(string json)
    {
        var outcome = LedgerParser.ParseAndValidate(Encoding.UTF8.GetBytes(json));
        Assert.NotNull(outcome.Ledger);
        return outcome.Ledger!;
    }

    private static string BuildMaxedPredecessorJson()
    {
        // Byte-level canonical (compact, RFC 8785 key order): the parser's canonical-form
        // stage compares raw bytes against the re-serialized model.
        var digest = LedgerCanonicalizer.ComputeCacheContractDigest(Identities);
        var sb = new StringBuilder();
        sb.Append(
            $$"""{"header":{"adapterId":"{{LedgerTestBaseline.AdapterId}}","cacheConfigId":"{{LedgerTestBaseline.CacheConfigId}}","headRepository":"owner/repo","kind":"bootstrap","ledgerEpoch":"bbbbbbbbbbbbbbbbbbbbbb","modelId":"{{LedgerTestBaseline.ModelId}}","policyId":"{{LedgerTestBaseline.PolicyId}}","predecessorLedgerSha256":"bootstrap","providerId":"provider","pullRequest":1,"repository":"owner/repo","sessionEpoch":"aaaaaaaaaaaaaaaaaaaaaa","stateGeneration":0,"templateId":"{{LedgerTestBaseline.TemplateId}}","toolDefinitionId":"{{LedgerTestBaseline.ToolDefinitionId}}","trustedExecutionDomain":"trusted","workflowIdentity":"ci"},"prefixContractVersion":1,"records":[""");

        for (var i = 0; i < 32; i++)
        {
            if (i > 0) sb.Append(',');
            var interactionId = $"{i:x64}";
            sb.Append(
                $$"""{"cacheContractDigest":"{{digest}}","changedFiles":[],"interactionId":"{{interactionId}}","interactionOrdinal":{{i}},"reviewedBaseSha":"1111111111111111111111111111111111111111","reviewedHeadSha":"0000000000000000000000000000000000000000","role":"review_context","subjectDigest":"1111111111111111111111111111111111111111111111111111111111111111"},{"findings":[],"interactionId":"{{interactionId}}","interactionOrdinal":{{i}},"limitations":[],"role":"review_outcome","summary":"Summary text."}""");
        }

        sb.Append("],\"schemaVersion\":1}");
        return sb.ToString();
    }

    private static string BuildPaddedMaxedPredecessorJson()
    {
        // The 32-pair maxed predecessor with limitations padding, sized to stay under the
        // canonical byte cap while leaving less than one padded pair of headroom.
        var digest = LedgerCanonicalizer.ComputeCacheContractDigest(Identities);
        var sb = new StringBuilder();
        sb.Append(
            $$"""{"header":{"adapterId":"{{LedgerTestBaseline.AdapterId}}","cacheConfigId":"{{LedgerTestBaseline.CacheConfigId}}","headRepository":"owner/repo","kind":"bootstrap","ledgerEpoch":"bbbbbbbbbbbbbbbbbbbbbb","modelId":"{{LedgerTestBaseline.ModelId}}","policyId":"{{LedgerTestBaseline.PolicyId}}","predecessorLedgerSha256":"bootstrap","providerId":"provider","pullRequest":1,"repository":"owner/repo","sessionEpoch":"aaaaaaaaaaaaaaaaaaaaaa","stateGeneration":0,"templateId":"{{LedgerTestBaseline.TemplateId}}","toolDefinitionId":"{{LedgerTestBaseline.ToolDefinitionId}}","trustedExecutionDomain":"trusted","workflowIdentity":"ci"},"prefixContractVersion":1,"records":[""");

        for (var i = 0; i < 32; i++)
        {
            if (i > 0) sb.Append(',');
            var interactionId = $"{i:x64}";
            sb.Append(
                $$"""{"cacheContractDigest":"{{digest}}","changedFiles":[],"interactionId":"{{interactionId}}","interactionOrdinal":{{i}},"reviewedBaseSha":"1111111111111111111111111111111111111111","reviewedHeadSha":"0000000000000000000000000000000000000000","role":"review_context","subjectDigest":"1111111111111111111111111111111111111111111111111111111111111111"},{"findings":[],"interactionId":"{{interactionId}}","interactionOrdinal":{{i}},"limitations":[""");
            for (var j = 0; j < 6; j++)
            {
                if (j > 0) sb.Append(',');
                sb.Append('"').Append(new string('x', 1200)).Append('"');
            }

            sb.Append(
                $$"""],"role":"review_outcome","summary":"Summary text."}""");
        }

        sb.Append("],\"schemaVersion\":1}");
        return sb.ToString();
    }

    private static ImmutableArray<string> PaddedLimitations(int entries, int length)
    {
        var builder = ImmutableArray.CreateBuilder<string>(entries);
        for (var i = 0; i < entries; i++)
        {
            builder.Add(new string('x', length));
        }

        return builder.MoveToImmutable();
    }

    private static string MinimalBootstrapJson()
    {
        return $$"""{"header":{"adapterId":"{{LedgerTestBaseline.AdapterId}}","cacheConfigId":"{{LedgerTestBaseline.CacheConfigId}}","headRepository":"owner/repo","kind":"bootstrap","ledgerEpoch":"bbbbbbbbbbbbbbbbbbbbbb","modelId":"{{LedgerTestBaseline.ModelId}}","policyId":"{{LedgerTestBaseline.PolicyId}}","predecessorLedgerSha256":"bootstrap","providerId":"provider","pullRequest":1,"repository":"owner/repo","sessionEpoch":"aaaaaaaaaaaaaaaaaaaaaa","stateGeneration":0,"templateId":"{{LedgerTestBaseline.TemplateId}}","toolDefinitionId":"{{LedgerTestBaseline.ToolDefinitionId}}","trustedExecutionDomain":"trusted","workflowIdentity":"ci"},"prefixContractVersion":1,"records":[{"cacheContractDigest":"{{LedgerTestBaseline.CacheContractDigest}}","changedFiles":[],"interactionId":"0000000000000000000000000000000000000000000000000000000000000000","interactionOrdinal":0,"reviewedBaseSha":"1111111111111111111111111111111111111111","reviewedHeadSha":"0000000000000000000000000000000000000000","role":"review_context","subjectDigest":"1111111111111111111111111111111111111111111111111111111111111111"},{"findings":[],"interactionId":"0000000000000000000000000000000000000000000000000000000000000000","interactionOrdinal":0,"limitations":[],"role":"review_outcome","summary":"Summary text."}],"schemaVersion":1}""";
    }
}

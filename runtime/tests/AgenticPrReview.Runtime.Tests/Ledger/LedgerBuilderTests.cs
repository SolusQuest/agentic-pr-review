using System.Text;
using AgenticPrReview.Runtime.Ledger;

namespace AgenticPrReview.Runtime.Tests.Ledger;

/// <summary>
/// Focused tests for LedgerBuilder projection and candidate assembly.
/// Fixture-driven positive coverage lives in LedgerFixtureTests; this class
/// covers per-check failure paths and the deterministic behavior of pure
/// projection helpers.
/// </summary>
public sealed class LedgerBuilderTests
{
    private static ExpectedIdentities Identities => new(
        Repository: "acme/example",
        HeadRepository: "acme/example",
        PullRequest: 123,
        WorkflowIdentity: "acme/example/.github/workflows/ci.yml",
        TrustedExecutionDomain: "github-actions",
        SessionEpoch: "epoch-0",
        ProviderId: "provider.reference",
        ModelId: "model-2026-01",
        AdapterId: new string('a', 64),
        TemplateId: new string('b', 64),
        PolicyId: new string('c', 64),
        ToolDefinitionId: new string('d', 64),
        CacheConfigId: new string('e', 64));

    private static ValidatedContextSource ContextSource() => new(
        ReviewedHeadSha: "1111111111111111111111111111111111111111",
        ReviewedBaseSha: "2222222222222222222222222222222222222222",
        ChangedFiles: ImmutableArray<ValidatedChangedFileSource>.Empty);

    private static ValidatedOutcomeSource OutcomeSource(string summary = "s") => new(
        Summary: summary,
        Findings: ImmutableArray<ValidatedFindingSource>.Empty,
        Limitations: ImmutableArray<string>.Empty);

    private static InteractionIdentity IId(int ord) =>
        new(new string('0', 64 - ord.ToString("x").Length) + ord.ToString("x"), ord);

    // ---------- BuildReviewContext ----------

    [Fact]
    public void BuildReviewContext_RejectsControlCharacterInIdentity()
    {
        var identities = Identities with { WorkflowIdentity = "acme\u0001example" };
        var outcome = LedgerBuilder.BuildReviewContext(ContextSource(), identities, IId(0));
        Assert.Null(outcome.Record);
        Assert.Equal(LedgerDiagnosticCodes.ControlCharacterInIdentity, outcome.Failure!.Code);
    }

    [Fact]
    public void BuildReviewContext_RejectsIdentityUtf8ByteLengthOverCap()
    {
        // 200 characters of a two-byte codepoint = 400 UTF-8 bytes, exceeds 256.
        var overlong = new string('\u00E9', 200);
        var identities = Identities with { WorkflowIdentity = overlong };
        var outcome = LedgerBuilder.BuildReviewContext(ContextSource(), identities, IId(0));
        Assert.Null(outcome.Record);
        Assert.Equal(LedgerDiagnosticCodes.IdentityByteLengthExceeded, outcome.Failure!.Code);
    }

    [Fact]
    public void BuildReviewContext_RejectsUnsupportedChangeStatus()
    {
        var source = ContextSource() with
        {
            ChangedFiles = ImmutableArray.Create(new ValidatedChangedFileSource(
                Path: "src/a.cs", PreviousPath: null, Status: "weird",
                Additions: 1, Deletions: 0, Changes: 1, Patch: null)),
        };
        var outcome = LedgerBuilder.BuildReviewContext(source, Identities, IId(0));
        Assert.Null(outcome.Record);
        Assert.Equal(LedgerDiagnosticCodes.UnsupportedChangeStatus, outcome.Failure!.Code);
    }

    [Fact]
    public void BuildReviewContext_RejectsAbsolutePath()
    {
        var source = ContextSource() with
        {
            ChangedFiles = ImmutableArray.Create(new ValidatedChangedFileSource(
                Path: "/etc/passwd", PreviousPath: null, Status: "modified",
                Additions: 1, Deletions: 0, Changes: 1, Patch: null)),
        };
        var outcome = LedgerBuilder.BuildReviewContext(source, Identities, IId(0));
        Assert.Null(outcome.Record);
        Assert.Equal(LedgerDiagnosticCodes.SchemaViolation, outcome.Failure!.Code);
    }

    [Fact]
    public void BuildReviewContext_RejectsNulCharacterInPath()
    {
        var source = ContextSource() with
        {
            ChangedFiles = ImmutableArray.Create(new ValidatedChangedFileSource(
                Path: "src/a\u0000.cs", PreviousPath: null, Status: "modified",
                Additions: 1, Deletions: 0, Changes: 1, Patch: null)),
        };
        var outcome = LedgerBuilder.BuildReviewContext(source, Identities, IId(0));
        Assert.Null(outcome.Record);
        Assert.Equal(LedgerDiagnosticCodes.InvalidUnicode, outcome.Failure!.Code);
    }

    [Fact]
    public void BuildReviewContext_AcceptsAllFiveChangedFileStatuses()
    {
        foreach (var status in new[] { "added", "modified", "removed", "renamed", "copied" })
        {
            var source = ContextSource() with
            {
                ChangedFiles = ImmutableArray.Create(new ValidatedChangedFileSource(
                    Path: "src/a.cs", PreviousPath: null, Status: status,
                    Additions: 0, Deletions: 0, Changes: 0, Patch: null)),
            };
            var outcome = LedgerBuilder.BuildReviewContext(source, Identities, IId(0));
            Assert.NotNull(outcome.Record);
        }
    }

    // ---------- BuildReviewOutcome ----------

    [Fact]
    public void BuildReviewOutcome_RejectsOverlongSummary()
    {
        var source = OutcomeSource(new string('x', LedgerLimits.MaxSummaryChars + 1));
        var outcome = LedgerBuilder.BuildReviewOutcome(source, IId(0));
        Assert.Null(outcome.Record);
        Assert.Equal(LedgerDiagnosticCodes.OverlongValue, outcome.Failure!.Code);
    }

    [Fact]
    public void BuildReviewOutcome_RejectsNulInSummary()
    {
        var source = OutcomeSource("hello\u0000world");
        var outcome = LedgerBuilder.BuildReviewOutcome(source, IId(0));
        Assert.Null(outcome.Record);
        Assert.Equal(LedgerDiagnosticCodes.InvalidUnicode, outcome.Failure!.Code);
    }

    [Fact]
    public void BuildReviewOutcome_RejectsTooManyFindings()
    {
        var findings = ImmutableArray.CreateBuilder<ValidatedFindingSource>();
        for (var i = 0; i <= LedgerLimits.MaxFindingsPerOutcome; i++)
        {
            findings.Add(new ValidatedFindingSource(
                "low", "medium", "correctness", "t", "b", null, null, null, null, null, null));
        }
        var source = OutcomeSource() with { Findings = findings.ToImmutable() };
        var outcome = LedgerBuilder.BuildReviewOutcome(source, IId(0));
        Assert.Null(outcome.Record);
        Assert.Equal(LedgerDiagnosticCodes.FindingLimitExceeded, outcome.Failure!.Code);
    }

    [Fact]
    public void BuildReviewOutcome_RejectsFindingLocationMismatch()
    {
        var source = OutcomeSource() with
        {
            Findings = ImmutableArray.Create(new ValidatedFindingSource(
                "low", "medium", "correctness", "t", "b",
                Path: "src/a.cs",
                StartLine: 5,
                EndLine: null,
                Evidence: null, SuggestedAction: null, InlinePreference: null)),
        };
        var outcome = LedgerBuilder.BuildReviewOutcome(source, IId(0));
        Assert.Null(outcome.Record);
        Assert.Equal(LedgerDiagnosticCodes.FindingLocationMismatch, outcome.Failure!.Code);
    }

    [Fact]
    public void BuildReviewOutcome_RejectsFindingLineRangeInvalid()
    {
        var source = OutcomeSource() with
        {
            Findings = ImmutableArray.Create(new ValidatedFindingSource(
                "low", "medium", "correctness", "t", "b",
                Path: "src/a.cs", StartLine: 10, EndLine: 5,
                Evidence: null, SuggestedAction: null, InlinePreference: null)),
        };
        var outcome = LedgerBuilder.BuildReviewOutcome(source, IId(0));
        Assert.Null(outcome.Record);
        Assert.Equal(LedgerDiagnosticCodes.FindingLineRangeInvalid, outcome.Failure!.Code);
    }

    [Fact]
    public void BuildReviewOutcome_RejectsFindingLocationMissingPath()
    {
        var source = OutcomeSource() with
        {
            Findings = ImmutableArray.Create(new ValidatedFindingSource(
                "low", "medium", "correctness", "t", "b",
                Path: null, StartLine: 5, EndLine: 10,
                Evidence: null, SuggestedAction: null, InlinePreference: null)),
        };
        var outcome = LedgerBuilder.BuildReviewOutcome(source, IId(0));
        Assert.Null(outcome.Record);
        Assert.Equal(LedgerDiagnosticCodes.FindingLocationMissingPath, outcome.Failure!.Code);
    }

    [Fact]
    public void BuildReviewOutcome_AcceptsSingleLineFinding()
    {
        var source = OutcomeSource() with
        {
            Findings = ImmutableArray.Create(new ValidatedFindingSource(
                "low", "medium", "correctness", "t", "b",
                Path: "src/a.cs", StartLine: 5, EndLine: 5,
                Evidence: null, SuggestedAction: null, InlinePreference: null)),
        };
        var outcome = LedgerBuilder.BuildReviewOutcome(source, IId(0));
        Assert.NotNull(outcome.Record);
    }

    // ---------- Determinism ----------

    [Fact]
    public void Builder_IsDeterministicAcrossRuns()
    {
        var context = LedgerBuilder.BuildReviewContext(ContextSource(), Identities, IId(0));
        Assert.NotNull(context.Record);
        var outcomeSource = OutcomeSource("Bootstrap review complete.");
        var outcome = LedgerBuilder.BuildReviewOutcome(outcomeSource, IId(0));
        Assert.NotNull(outcome.Record);
        var build1 = LedgerBuilder.CreateBootstrap(
            new BootstrapTransition(Identities, 0, 1), context.Record!, outcome.Record!);
        var build2 = LedgerBuilder.CreateBootstrap(
            new BootstrapTransition(Identities, 0, 1), context.Record!, outcome.Record!);
        Assert.NotNull(build1.Ledger);
        Assert.NotNull(build2.Ledger);
        Assert.Equal(build1.Ledger!.ContentSha256, build2.Ledger!.ContentSha256);
        Assert.Equal(build1.Ledger.ToCanonicalByteArray(), build2.Ledger.ToCanonicalByteArray());
    }

    [Fact]
    public void Builder_MutatingSourceArrayDoesNotAffectLedger()
    {
        var files = new ValidatedChangedFileSource[]
        {
            new("src/a.cs", null, "modified", 1, 0, 1, null),
        };
        var source = ContextSource() with { ChangedFiles = files.ToImmutableArray() };
        var contextOutcome = LedgerBuilder.BuildReviewContext(source, Identities, IId(0));
        Assert.NotNull(contextOutcome.Record);
        var originalRecord = contextOutcome.Record!;
        // Mutating the source builder's ImmutableArray is impossible; but mutating the
        // underlying array through unsafe means, or re-invoking with a modified source,
        // must not change the already-produced record.
        var mutatedSource = source with
        {
            ChangedFiles = ImmutableArray.Create(new ValidatedChangedFileSource(
                "src/other.cs", null, "modified", 99, 99, 99, null)),
        };
        var contextOutcome2 = LedgerBuilder.BuildReviewContext(mutatedSource, Identities, IId(0));
        Assert.NotNull(contextOutcome2.Record);
        Assert.NotEqual(originalRecord.ChangedFiles[0].Path, contextOutcome2.Record!.ChangedFiles[0].Path);
        Assert.Equal("src/a.cs", originalRecord.ChangedFiles[0].Path);
    }

    // ---------- Bootstrap builder-level guards ----------

    [Fact]
    public void CreateBootstrap_RejectsNonZeroStateGeneration()
    {
        var context = LedgerBuilder.BuildReviewContext(ContextSource(), Identities, IId(0)).Record!;
        var outcome = LedgerBuilder.BuildReviewOutcome(OutcomeSource(), IId(0)).Record!;
        var build = LedgerBuilder.CreateBootstrap(new BootstrapTransition(Identities, 3, 1), context, outcome);
        Assert.Null(build.Ledger);
        Assert.Equal(LedgerDiagnosticCodes.BootstrapShapeViolation, build.Failure!.Code);
    }

    [Fact]
    public void AppendContinuation_RejectsPredecessorHashMismatch()
    {
        var predecessor = MakeValidBootstrap();
        var context = LedgerBuilder.BuildReviewContext(ContextSource() with
        {
            ReviewedHeadSha = "3333333333333333333333333333333333333333",
        }, Identities, IId(1)).Record!;
        var outcome = LedgerBuilder.BuildReviewOutcome(OutcomeSource("cont"), IId(1)).Record!;
        var wrongHash = new string('f', 64);
        var expected = new ContinuationTransition(Identities, wrongHash, 0, 1, 1);
        var build = LedgerBuilder.AppendContinuation(predecessor, expected, context, outcome);
        Assert.Null(build.Ledger);
        Assert.Equal(LedgerDiagnosticCodes.PredecessorHashMismatch, build.Failure!.Code);
    }

    private static ValidatedLedger MakeValidBootstrap()
    {
        var context = LedgerBuilder.BuildReviewContext(ContextSource(), Identities, IId(0)).Record!;
        var outcome = LedgerBuilder.BuildReviewOutcome(OutcomeSource("Bootstrap"), IId(0)).Record!;
        return LedgerBuilder.CreateBootstrap(new BootstrapTransition(Identities, 0, 1), context, outcome).Ledger!;
    }


    // ---------- Composite over-bound-append cause precedence ----------

    [Fact]
    public void AppendContinuation_InteractionLimit_ReportsInteractionCause()
    {
        // Predecessor is at the cap of 32 pairs (last ordinal = 31), so the
        // legal next pair must use ordinal 32; the append then pushes the
        // total records/2 to 33, which is one over MaxInteractionPairs.
        var maxBytes = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "protocol", "fixtures", "v1",
                "provider-session-ledger", "continuation-max-interactions.json"));
        var predecessor = LedgerParser.ParseAndValidate(maxBytes).Ledger!;
        var identities = Identities;
        var context = LedgerBuilder.BuildReviewContext(ContextSource(), identities, IId(32)).Record!;
        var outcome = LedgerBuilder.BuildReviewOutcome(OutcomeSource("cont"), IId(32)).Record!;
        var expected = new ContinuationTransition(
            identities, predecessor.ContentSha256,
            PredecessorStateGeneration: predecessor.Model.Header.StateGeneration,
            StateGeneration: predecessor.Model.Header.StateGeneration + 1,
            LedgerEpoch: predecessor.Model.Header.LedgerEpoch);
        var build = LedgerBuilder.AppendContinuation(predecessor, expected, context, outcome);
        Assert.Null(build.Ledger);
        Assert.Equal(LedgerDiagnosticCodes.OverBoundAppend, build.Failure!.Code);
        Assert.Equal(LedgerDiagnosticCodes.InteractionLimitExceeded, build.Failure.CauseCode);
    }

    [Fact]
    public void CreateBootstrap_CanonicalByteLimit_ReportsCanonicalByteCause()
    {
        var giantSummary = new string('a', LedgerLimits.MaxSummaryChars);
        var lims = ImmutableArray.CreateBuilder<string>();
        for (var i = 0; i < LedgerLimits.MaxLimitationsPerOutcome; i++)
            lims.Add(new string('b', LedgerLimits.MaxLimitationsItemChars));
        var findings = ImmutableArray.CreateBuilder<ValidatedFindingSource>();
        for (var i = 0; i < LedgerLimits.MaxFindingsPerOutcome; i++)
        {
            findings.Add(new ValidatedFindingSource(
                Severity: "low", Confidence: "medium", Category: "correctness",
                Title: "t", Body: new string('c', LedgerLimits.MaxFindingBodyChars),
                Path: null, StartLine: null, EndLine: null,
                Evidence: new string('d', LedgerLimits.MaxFindingEvidenceChars),
                SuggestedAction: null, InlinePreference: null));
        }
        var outcomeSource = OutcomeSource(giantSummary) with
        {
            Limitations = lims.ToImmutable(),
            Findings = findings.ToImmutable(),
        };
        var outcome = LedgerBuilder.BuildReviewOutcome(outcomeSource, IId(0)).Record!;
        var context = LedgerBuilder.BuildReviewContext(ContextSource(), Identities, IId(0)).Record!;
        var build = LedgerBuilder.CreateBootstrap(
            new BootstrapTransition(Identities, 0, 1), context, outcome);
        Assert.Null(build.Ledger);
        Assert.Equal(LedgerDiagnosticCodes.OverBoundAppend, build.Failure!.Code);
        Assert.Equal(LedgerDiagnosticCodes.CanonicalByteLimitExceeded, build.Failure.CauseCode);
    }

    // ---------- Null-safe / lone-surrogate preflight ----------

    private const string LoneSurrogate = "\uD800";

    [Fact]
    public void BuildReviewContext_NullReviewedHeadSha_IsRejectedAsInvalidUnicode()
    {
        var source = ContextSource() with { ReviewedHeadSha = null! };
        var outcome = LedgerBuilder.BuildReviewContext(source, Identities, IId(0));
        Assert.Null(outcome.Record);
        Assert.Equal(LedgerDiagnosticCodes.InvalidUnicode, outcome.Failure!.Code);
    }

    [Fact]
    public void BuildReviewContext_LoneSurrogateInReviewedBaseSha_IsRejectedAsInvalidUnicode()
    {
        var source = ContextSource() with { ReviewedBaseSha = new string('1', 39) + LoneSurrogate };
        var outcome = LedgerBuilder.BuildReviewContext(source, Identities, IId(0));
        Assert.Null(outcome.Record);
        Assert.Equal(LedgerDiagnosticCodes.InvalidUnicode, outcome.Failure!.Code);
    }

    [Fact]
    public void BuildReviewOutcome_NullSummary_IsRejectedAsInvalidUnicode()
    {
        var source = OutcomeSource() with { Summary = null! };
        var outcome = LedgerBuilder.BuildReviewOutcome(source, IId(0));
        Assert.Null(outcome.Record);
        Assert.Equal(LedgerDiagnosticCodes.InvalidUnicode, outcome.Failure!.Code);
    }

    [Fact]
    public void BuildReviewOutcome_OverlongSummaryContainingLoneSurrogate_ReportsInvalidUnicode()
    {
        var summary = new string('x', LedgerLimits.MaxSummaryChars + 100) + LoneSurrogate;
        var source = OutcomeSource() with { Summary = summary };
        var outcome = LedgerBuilder.BuildReviewOutcome(source, IId(0));
        Assert.Null(outcome.Record);
        Assert.Equal(LedgerDiagnosticCodes.InvalidUnicode, outcome.Failure!.Code);
    }

    [Fact]
    public void CreateRecovery_LoneSurrogateReason_IsRejectedAsInvalidUnicode()
    {
        var context = LedgerBuilder.BuildReviewContext(ContextSource(), Identities, IId(0)).Record!;
        var outcome = LedgerBuilder.BuildReviewOutcome(OutcomeSource(), IId(0)).Record!;
        var expected = new RecoveryTransition(Identities, 0, 1, LoneSurrogate);
        var build = LedgerBuilder.CreateRecovery(expected, context, outcome);
        Assert.Null(build.Ledger);
        Assert.Equal(LedgerDiagnosticCodes.InvalidUnicode, build.Failure!.Code);
    }

    [Fact]
    public void CreateReset_LoneSurrogateReason_IsRejectedAsInvalidUnicode()
    {
        var predecessor = MakeValidBootstrap();
        var context = LedgerBuilder.BuildReviewContext(ContextSource(), Identities, IId(0)).Record!;
        var outcome = LedgerBuilder.BuildReviewOutcome(OutcomeSource(), IId(0)).Record!;
        var expected = new ResetTransition(
            Identities, predecessor.ContentSha256, new string('7', 64),
            PredecessorStateGeneration: 0, StateGeneration: 1, LedgerEpoch: 2,
            ResetReason: LoneSurrogate);
        var build = LedgerBuilder.CreateReset(predecessor, expected, context, outcome);
        Assert.Null(build.Ledger);
        Assert.Equal(LedgerDiagnosticCodes.InvalidUnicode, build.Failure!.Code);
    }



    // ---------- Per-record limits precede aggregate property/array causes ----------

    [Fact]
    public void CreateBootstrap_ChangedFileLimitPrecedesArrayAggregate()
    {
        // Directly construct a ReviewContextRecord with more than 200 changed
        // files (bypassing BuildReviewContext). AssembleAndValidate must
        // report the per-record ledger_changed_file_limit_exceeded, not the
        // aggregate ledger_json_array_length_exceeded cause.
        var files = ImmutableArray.CreateBuilder<ChangedFileEntry>();
        for (var i = 0; i < LedgerLimits.MaxChangedFilesPerContext + 1; i++)
        {
            files.Add(new ChangedFileEntry("src/f" + i + ".cs", null, "modified", 0, 0, 0, null));
        }
        var subject = new string('a', 64);
        var cacheContract = new string('b', 64);
        var ctx = new ReviewContextRecord(
            InteractionId: new string('0', 64), InteractionOrdinal: 0,
            ReviewedHeadSha: new string('1', 40),
            ReviewedBaseSha: new string('2', 40),
            SubjectDigest: subject,
            CacheContractDigest: cacheContract,
            ChangedFiles: files.ToImmutable());
        var oc = new ReviewOutcomeRecord(
            InteractionId: new string('0', 64), InteractionOrdinal: 0,
            Summary: "s",
            Findings: ImmutableArray<LedgerFinding>.Empty,
            Limitations: ImmutableArray<string>.Empty);
        var build = LedgerBuilder.CreateBootstrap(
            new BootstrapTransition(Identities, 0, 1), ctx, oc);
        Assert.Null(build.Ledger);
        Assert.Equal(LedgerDiagnosticCodes.ChangedFileLimitExceeded, build.Failure!.Code);
    }

    [Fact]
    public void CreateBootstrap_FindingLimitPrecedesArrayAggregate()
    {
        var findings = ImmutableArray.CreateBuilder<LedgerFinding>();
        for (var i = 0; i < LedgerLimits.MaxFindingsPerOutcome + 1; i++)
        {
            findings.Add(new LedgerFinding(
                Severity: "low", Confidence: "medium", Category: "correctness",
                Title: "t", Body: "b", Path: null, StartLine: null, EndLine: null,
                Evidence: null, SuggestedAction: null, InlinePreference: null));
        }
        var context = LedgerBuilder.BuildReviewContext(ContextSource(), Identities, IId(0)).Record!;
        var oc = new ReviewOutcomeRecord(
            InteractionId: new string('0', 64), InteractionOrdinal: 0,
            Summary: "s",
            Findings: findings.ToImmutable(),
            Limitations: ImmutableArray<string>.Empty);
        var build = LedgerBuilder.CreateBootstrap(
            new BootstrapTransition(Identities, 0, 1), context, oc);
        Assert.Null(build.Ledger);
        Assert.Equal(LedgerDiagnosticCodes.FindingLimitExceeded, build.Failure!.Code);
    }

    [Fact]
    public void CreateBootstrap_ManualRecordInvalidReviewedSha_ReportsSchemaViolationNotDigestMismatch()
    {
        // Schema-first ordering: an invalid reviewedHeadSha must surface as
        // ledger_schema_violation, not as ledger_digest_mismatch (which would
        // otherwise fire because subjectDigest is recomputed against the
        // invalid SHA).
        var ctx = new ReviewContextRecord(
            InteractionId: new string('0', 64), InteractionOrdinal: 0,
            ReviewedHeadSha: "not-a-sha",
            ReviewedBaseSha: new string('2', 40),
            SubjectDigest: new string('a', 64),
            CacheContractDigest: new string('b', 64),
            ChangedFiles: ImmutableArray<ChangedFileEntry>.Empty);
        var oc = new ReviewOutcomeRecord(
            InteractionId: new string('0', 64), InteractionOrdinal: 0,
            Summary: "s",
            Findings: ImmutableArray<LedgerFinding>.Empty,
            Limitations: ImmutableArray<string>.Empty);
        var build = LedgerBuilder.CreateBootstrap(
            new BootstrapTransition(Identities, 0, 1), ctx, oc);
        Assert.Null(build.Ledger);
        Assert.Equal(LedgerDiagnosticCodes.SchemaViolation, build.Failure!.Code);
    }



    // ---------- Round-7 regressions ----------

    [Fact]
    public void CreateBootstrap_FindingSeverityInfo_IsSchemaViolation()
    {
        // "info" is NOT part of the authoritative ledger severity enum
        // (low / medium / high) even though earlier drafts used it.
        var findings = ImmutableArray.CreateBuilder<LedgerFinding>();
        findings.Add(new LedgerFinding(
            Severity: "info", Confidence: "medium", Category: "correctness",
            Title: "t", Body: "b", Path: null, StartLine: null, EndLine: null,
            Evidence: null, SuggestedAction: null, InlinePreference: null));
        var context = LedgerBuilder.BuildReviewContext(ContextSource(), Identities, IId(0)).Record!;
        var oc = new ReviewOutcomeRecord(
            InteractionId: new string('0', 64), InteractionOrdinal: 0,
            Summary: "s",
            Findings: findings.ToImmutable(),
            Limitations: ImmutableArray<string>.Empty);
        var build = LedgerBuilder.CreateBootstrap(new BootstrapTransition(Identities, 0, 1), context, oc);
        Assert.Null(build.Ledger);
        Assert.Equal(LedgerDiagnosticCodes.SchemaViolation, build.Failure!.Code);
    }

    [Fact]
    public void CreateBootstrap_FindingInlinePreferenceAllowed_IsAccepted()
    {
        // "allowed" IS a valid schema value (unlike "inline").
        var findings = ImmutableArray.CreateBuilder<LedgerFinding>();
        findings.Add(new LedgerFinding(
            Severity: "low", Confidence: "medium", Category: "documentation",
            Title: "t", Body: "b", Path: null, StartLine: null, EndLine: null,
            Evidence: null, SuggestedAction: null, InlinePreference: "allowed"));
        var context = LedgerBuilder.BuildReviewContext(ContextSource(), Identities, IId(0)).Record!;
        var oc = new ReviewOutcomeRecord(
            InteractionId: new string('0', 64), InteractionOrdinal: 0,
            Summary: "s",
            Findings: findings.ToImmutable(),
            Limitations: ImmutableArray<string>.Empty);
        var build = LedgerBuilder.CreateBootstrap(new BootstrapTransition(Identities, 0, 1), context, oc);
        Assert.NotNull(build.Ledger);
    }

    [Fact]
    public void BuildReviewOutcome_WhitespaceOnlySummary_IsSchemaViolation()
    {
        // \S pattern is enforced by both projection helper AND model validator
        // so parser / projection / candidate paths classify identically.
        var source = OutcomeSource() with { Summary = "     " };
        var outcome = LedgerBuilder.BuildReviewOutcome(source, IId(0));
        Assert.Null(outcome.Record);
        Assert.Equal(LedgerDiagnosticCodes.SchemaViolation, outcome.Failure!.Code);
    }

    [Fact]
    public void BuildReviewContext_NullChangedFileElement_IsSchemaViolation()
    {
        var files = ImmutableArray.CreateBuilder<ValidatedChangedFileSource>();
        files.Add(null!);
        var source = ContextSource() with { ChangedFiles = files.ToImmutable() };
        var outcome = LedgerBuilder.BuildReviewContext(source, Identities, IId(0));
        Assert.Null(outcome.Record);
        Assert.Equal(LedgerDiagnosticCodes.SchemaViolation, outcome.Failure!.Code);
    }

    [Fact]
    public void BuildReviewOutcome_NullFindingElement_IsSchemaViolation()
    {
        var findings = ImmutableArray.CreateBuilder<ValidatedFindingSource>();
        findings.Add(null!);
        var source = OutcomeSource() with { Findings = findings.ToImmutable() };
        var outcome = LedgerBuilder.BuildReviewOutcome(source, IId(0));
        Assert.Null(outcome.Record);
        Assert.Equal(LedgerDiagnosticCodes.SchemaViolation, outcome.Failure!.Code);
    }

    [Fact]
    public void BuildReviewOutcome_NullLimitationElement_IsSchemaViolation()
    {
        var lims = ImmutableArray.CreateBuilder<string>();
        lims.Add(null!);
        var source = OutcomeSource() with { Limitations = lims.ToImmutable() };
        var outcome = LedgerBuilder.BuildReviewOutcome(source, IId(0));
        Assert.Null(outcome.Record);
        Assert.Equal(LedgerDiagnosticCodes.SchemaViolation, outcome.Failure!.Code);
    }

    [Fact]
    public void BuildReviewContext_DefaultChangedFiles_IsSchemaViolation()
    {
        var source = ContextSource() with { ChangedFiles = default };
        var outcome = LedgerBuilder.BuildReviewContext(source, Identities, IId(0));
        Assert.Null(outcome.Record);
        Assert.Equal(LedgerDiagnosticCodes.SchemaViolation, outcome.Failure!.Code);
    }

    [Fact]
    public void BuildReviewOutcome_DefaultFindings_IsSchemaViolation()
    {
        var source = OutcomeSource() with { Findings = default };
        var outcome = LedgerBuilder.BuildReviewOutcome(source, IId(0));
        Assert.Null(outcome.Record);
        Assert.Equal(LedgerDiagnosticCodes.SchemaViolation, outcome.Failure!.Code);
    }

    [Fact]
    public void CreateBootstrap_IdentityMaxLengthExceededBeforeByteCap_ReportsOverlong()
    {
        // Identity char maxLength (256) is a schema-owned rule and must map
        // to ledger_overlong_value on every path (mapper / projection /
        // candidate). Byte-length cap only fires when the char count is <=256
        // but UTF-8 encoding exceeds 256 bytes (multi-byte identity chars).
        var identities = Identities with
        {
            WorkflowIdentity = new string('w', 257),
        };
        var context = LedgerBuilder.BuildReviewContext(ContextSource(), identities, IId(0));
        Assert.Null(context.Record);
        Assert.Equal(LedgerDiagnosticCodes.OverlongValue, context.Failure!.Code);
    }

    // -----------------------------------------------------------------
    // R8: schema-first and public-boundary defenses.

    [Fact]
    public void BuildReviewContext_NullSource_ReturnsSchemaViolation()
    {
        var o = LedgerBuilder.BuildReviewContext(null!, Identities, IId(0));
        Assert.Null(o.Record);
        Assert.Equal(LedgerDiagnosticCodes.SchemaViolation, o.Failure!.Code);
    }

    [Fact]
    public void BuildReviewContext_NullIdentities_ReturnsSchemaViolation()
    {
        var o = LedgerBuilder.BuildReviewContext(ContextSource(), null!, IId(0));
        Assert.Null(o.Record);
        Assert.Equal(LedgerDiagnosticCodes.SchemaViolation, o.Failure!.Code);
    }

    [Fact]
    public void BuildReviewContext_NullInteraction_ReturnsSchemaViolation()
    {
        var o = LedgerBuilder.BuildReviewContext(ContextSource(), Identities, null!);
        Assert.Null(o.Record);
        Assert.Equal(LedgerDiagnosticCodes.SchemaViolation, o.Failure!.Code);
    }

    [Fact]
    public void BuildReviewOutcome_NullSource_ReturnsSchemaViolation()
    {
        var o = LedgerBuilder.BuildReviewOutcome(null!, IId(0));
        Assert.Null(o.Record);
        Assert.Equal(LedgerDiagnosticCodes.SchemaViolation, o.Failure!.Code);
    }

    [Fact]
    public void BuildReviewOutcome_NullInteraction_ReturnsSchemaViolation()
    {
        var o = LedgerBuilder.BuildReviewOutcome(OutcomeSource(), null!);
        Assert.Null(o.Record);
        Assert.Equal(LedgerDiagnosticCodes.SchemaViolation, o.Failure!.Code);
    }

    [Fact]
    public void CreateBootstrap_NullContextRecord_ReturnsSchemaViolation()
    {
        var expected = new BootstrapTransition(Identities, 0, 1);
        var outcomeR = LedgerBuilder.BuildReviewOutcome(OutcomeSource(), IId(0)).Record!;
        var b = LedgerBuilder.CreateBootstrap(expected, null!, outcomeR);
        Assert.Null(b.Ledger);
        Assert.Equal(LedgerDiagnosticCodes.SchemaViolation, b.Failure!.Code);
    }

    [Fact]
    public void CreateBootstrap_NullOutcomeRecord_ReturnsSchemaViolation()
    {
        var expected = new BootstrapTransition(Identities, 0, 1);
        var contextR = LedgerBuilder.BuildReviewContext(ContextSource(), Identities, IId(0)).Record!;
        var b = LedgerBuilder.CreateBootstrap(expected, contextR, null!);
        Assert.Null(b.Ledger);
        Assert.Equal(LedgerDiagnosticCodes.SchemaViolation, b.Failure!.Code);
    }

    [Fact]
    public void CreateBootstrap_NullExpected_ReturnsSchemaViolation()
    {
        var contextR = LedgerBuilder.BuildReviewContext(ContextSource(), Identities, IId(0)).Record!;
        var outcomeR = LedgerBuilder.BuildReviewOutcome(OutcomeSource(), IId(0)).Record!;
        var b = LedgerBuilder.CreateBootstrap(null!, contextR, outcomeR);
        Assert.Null(b.Ledger);
        Assert.Equal(LedgerDiagnosticCodes.SchemaViolation, b.Failure!.Code);
    }

    [Fact]
    public void BuildReviewContext_WhitespaceOnlyPath_IsSchemaViolation()
    {
        var source = ContextSource() with
        {
            ChangedFiles = System.Collections.Immutable.ImmutableArray.Create(
                new ValidatedChangedFileSource("   ", null, "modified", 0, 0, 0, null)),
        };
        var o = LedgerBuilder.BuildReviewContext(source, Identities, IId(0));
        Assert.Null(o.Record);
        Assert.Equal(LedgerDiagnosticCodes.SchemaViolation, o.Failure!.Code);
    }

    [Fact]
    public void BuildReviewContext_WhitespaceOnlyPreviousPath_IsSchemaViolation()
    {
        var source = ContextSource() with
        {
            ChangedFiles = System.Collections.Immutable.ImmutableArray.Create(
                new ValidatedChangedFileSource("src/a.ts", "   ", "renamed", 0, 0, 0, null)),
        };
        var o = LedgerBuilder.BuildReviewContext(source, Identities, IId(0));
        Assert.Null(o.Record);
        Assert.Equal(LedgerDiagnosticCodes.SchemaViolation, o.Failure!.Code);
    }

    [Fact]
    public void BuildReviewOutcome_WhitespaceOnlyFindingPath_IsSchemaViolation()
    {
        var source = OutcomeSource() with
        {
            Findings = System.Collections.Immutable.ImmutableArray.Create(
                new ValidatedFindingSource(
                    Severity: "medium", Confidence: "medium", Category: "correctness",
                    Title: "t", Body: "b", Path: "   ",
                    StartLine: null, EndLine: null, Evidence: null, SuggestedAction: null, InlinePreference: null)),
        };
        var o = LedgerBuilder.BuildReviewOutcome(source, IId(0));
        Assert.Null(o.Record);
        Assert.Equal(LedgerDiagnosticCodes.SchemaViolation, o.Failure!.Code);
    }

    [Fact]
    public void BuildReviewOutcome_SummaryOverlongAndWhitespaceOnly_ReportsOverlongFirst()
    {
        // maxLength (schema step 6) precedes \S pattern (schema step 7).
        var source = OutcomeSource() with { Summary = new string(' ', 4001) };
        var o = LedgerBuilder.BuildReviewOutcome(source, IId(0));
        Assert.Null(o.Record);
        Assert.Equal(LedgerDiagnosticCodes.OverlongValue, o.Failure!.Code);
    }

    [Fact]
    public void BuildReviewOutcome_LimitationOverlongAndWhitespaceOnly_ReportsOverlongFirst()
    {
        var source = OutcomeSource() with
        {
            Limitations = System.Collections.Immutable.ImmutableArray.Create(new string(' ', 1201)),
        };
        var o = LedgerBuilder.BuildReviewOutcome(source, IId(0));
        Assert.Null(o.Record);
        Assert.Equal(LedgerDiagnosticCodes.OverlongValue, o.Failure!.Code);
    }

    [Fact]
    public void BuildReviewContext_IdentityCharMaxLengthPrecedesByteCap_ReportsOverlong()
    {
        // 257 ASCII chars: char maxLength (256) fires before UTF-8 byte cap.
        var identities = Identities with { ProviderId = new string('p', 257) };
        var o = LedgerBuilder.BuildReviewContext(ContextSource(), identities, IId(0));
        Assert.Null(o.Record);
        Assert.Equal(LedgerDiagnosticCodes.OverlongValue, o.Failure!.Code);
    }

    [Fact]
    public void BuildReviewContext_IdentityMultiByteCharsWithinCharCapExceedsByteCap_ReportsByteLength()
    {
        // 129 x 4-byte code points = 516 bytes but only 258 UTF-16 units
        // (>256 chars). Char maxLength catches this first as OverlongValue.
        // To exercise the byte cap classification we need a string of <=256
        // chars whose UTF-8 encoding still exceeds 256 bytes: 100 x U+1F600
        // = 100 chars in .NET string API (surrogate pairs count 2) = actually
        // 200 UTF-16 units. UTF-8 encoding = 400 bytes. String.Length in C#
        // returns UTF-16 unit count, so a 200-length string of 100 emoji
        // still has s.Length = 200 <= 256 but byte-count 400 > 256.
        var emoji = new string('a', 0);
        for (var i = 0; i < 100; i++) emoji += "\uD83D\uDE00"; // U+1F600
        Assert.True(emoji.Length <= 256);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(emoji) > 256);
        var identities = Identities with { ProviderId = emoji };
        var o = LedgerBuilder.BuildReviewContext(ContextSource(), identities, IId(0));
        Assert.Null(o.Record);
        Assert.Equal(LedgerDiagnosticCodes.IdentityByteLengthExceeded, o.Failure!.Code);
    }

}

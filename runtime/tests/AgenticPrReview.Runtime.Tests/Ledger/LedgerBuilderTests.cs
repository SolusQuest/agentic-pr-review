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
        new(new string('0', 63) + ord.ToString("x1"), ord);

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
        Assert.Equal(build1.Ledger.CanonicalBytes.ToArray(), build2.Ledger.CanonicalBytes.ToArray());
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
}

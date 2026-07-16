using System.Collections.Immutable;
using System.Text;
using AgenticPrReview.Runtime.Ledger;

namespace AgenticPrReview.Runtime.Tests.Ledger;

/// <summary>
/// Sanity tests for <see cref="LedgerBuilder"/>. Verifies the 5-step pipeline
/// produces a byte-round-trippable candidate on the happy path and that the
/// per-record limit codes remain schema-stage codes (not composite
/// <c>ledger_over_bound_append</c> causes).
/// </summary>
public sealed class LedgerBuilderTests
{
    [Fact]
    public void CreateBootstrap_HappyPath_MintsValidatedLedger()
    {
        var transition = new BootstrapTransition(Fixtures.Ident, Fixtures.SessionEpochA, 0, Fixtures.LedgerEpoch1);
        var outcome = LedgerBuilder.CreateBootstrap(
            transition,
            Fixtures.ContextSource(0), Fixtures.Interaction(0),
            Fixtures.OutcomeSource(), Fixtures.Interaction(0));
        Assert.Empty(outcome.Diagnostics);
        Assert.NotNull(outcome.Candidate);

        // Bytes round-trip cleanly through the parser.
        var bytes = outcome.Candidate!.ToCanonicalByteArray();
        var reparsed = LedgerParser.ParseAndValidate(bytes);
        Assert.NotNull(reparsed.Ledger);
        Assert.Equal(outcome.Candidate.ContentSha256, reparsed.Ledger!.ContentSha256);
    }

    [Fact]
    public void AppendContinuation_HappyPath_MintsValidatedLedger()
    {
        var predecessor = Fixtures.Bootstrap();
        var expected = new ContinuationTransition(
            Fixtures.Ident, Fixtures.SessionEpochA,
            predecessor.ContentSha256,
            predecessor.Model.Header.StateGeneration,
            predecessor.Model.Header.LedgerEpoch,
            predecessor.Model.Header.StateGeneration + 1,
            predecessor.Model.Header.LedgerEpoch);
        var outcome = LedgerBuilder.AppendContinuation(
            expected, predecessor,
            Fixtures.ContextSource(1), Fixtures.Interaction(1),
            Fixtures.OutcomeSource("cont"), Fixtures.Interaction(1));
        Assert.Empty(outcome.Diagnostics);
        Assert.NotNull(outcome.Candidate);
        Assert.Equal(4, outcome.Candidate!.Model.Records.Length);
    }

    [Fact]
    public void BuildReviewOutcome_FindingLimitExceeded_IsSchemaStageNotComposite()
    {
        // Per Issue #49 section 9: per-record limits (findings > 50) are
        // schema-stage codes and MUST NOT surface as composite
        // ledger_over_bound_append causes.
        var findings = ImmutableArray.CreateBuilder<LedgerFinding>();
        for (var i = 0; i < LedgerLimits.MaxFindingsPerOutcome + 1; i++)
        {
            findings.Add(new LedgerFinding
            {
                Severity = "info",
                Confidence = "low",
                Category = "style",
                Title = "t",
                Body = "b",
                Path = null,
                StartLine = null,
                EndLine = null,
            });
        }
        var source = new ValidatedOutcomeSource
        {
            Summary = "Overflow",
            Findings = findings.ToImmutable(),
            Limitations = ImmutableArray<string>.Empty,
        };
        var outcome = LedgerBuilder.BuildReviewOutcome(source, Fixtures.Interaction(0));
        Assert.Null(outcome.Record);
        Assert.Contains(outcome.Diagnostics, d => d.Code == LedgerDiagnosticCodes.FindingLimitExceeded);
        Assert.DoesNotContain(outcome.Diagnostics, d => d.Code == LedgerDiagnosticCodes.OverBoundAppend);
    }
}

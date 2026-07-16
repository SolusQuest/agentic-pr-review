using System.Collections.Immutable;
using System.Text;
using AgenticPrReview.Runtime.Ledger;

namespace AgenticPrReview.Runtime.Tests.Ledger;

/// <summary>
/// Helpers for constructing minimal ValidatedLedger fixtures directly through
/// the runtime API. Larger, richer corpus lives on disk under
/// <c>protocol/fixtures/v1/provider-session-ledger/</c>.
/// </summary>
internal static class Fixtures
{
    public const string SessionEpochA = "AAAAAAAAAAAAAAAAAAAAAA";
    public const string LedgerEpoch1  = "BBBBBBBBBBBBBBBBBBBBBB";
    public const string LedgerEpoch2  = "CCCCCCCCCCCCCCCCCCCCCC";

    public static readonly ExpectedIdentities Ident = new(
        Repository: "acme/example",
        HeadRepository: "acme/example",
        PullRequest: 1,
        WorkflowIdentity: "acme/example/.github/workflows/ci.yml",
        TrustedExecutionDomain: "github-actions",
        ProviderId: "provider.reference",
        ModelId: "model-2026-01",
        AdapterId: new string('a', 64),
        TemplateId: new string('b', 64),
        PolicyId: new string('c', 64),
        ToolDefinitionId: new string('d', 64),
        CacheConfigId: new string('e', 64));

    public static ValidatedContextSource ContextSource(int ordinal)
    {
        var interactionId = InteractionIdHex(ordinal);
        var subject = LedgerCanonicalizer.ComputeSha256Hex(Encoding.UTF8.GetBytes("fixture-subject/" + interactionId));
        return new ValidatedContextSource
        {
            SubjectDigest = subject,
            ReviewedHeadSha = new string('1', 40),
            ReviewedBaseSha = new string('2', 40),
            ChangedFiles = ImmutableArray<LedgerChangedFile>.Empty,
        };
    }

    public static ValidatedOutcomeSource OutcomeSource(string summary = "Fixture outcome.")
        => new()
        {
            Summary = summary,
            Findings = ImmutableArray<LedgerFinding>.Empty,
            Limitations = ImmutableArray<string>.Empty,
        };

    public static InteractionIdentity Interaction(int ordinal)
        => new(InteractionIdHex(ordinal), ordinal);

    public static string InteractionIdHex(int ordinal)
        => ordinal.ToString("x8") + new string('0', 56);

    public static ValidatedLedger Bootstrap()
    {
        var transition = new BootstrapTransition(Ident, SessionEpochA, StateGeneration: 0, LedgerEpoch: LedgerEpoch1);
        var outcome = LedgerBuilder.CreateBootstrap(
            transition,
            ContextSource(0), Interaction(0),
            OutcomeSource(), Interaction(0));
        return outcome.Candidate ?? throw new InvalidOperationException(FirstCode(outcome.Diagnostics));
    }

    public static ValidatedLedger Continuation(ValidatedLedger predecessor)
    {
        var expected = new ContinuationTransition(
            Identities: Ident,
            SessionEpoch: SessionEpochA,
            PredecessorLedgerSha256: predecessor.ContentSha256,
            PredecessorStateGeneration: predecessor.Model.Header.StateGeneration,
            PredecessorLedgerEpoch: predecessor.Model.Header.LedgerEpoch,
            StateGeneration: predecessor.Model.Header.StateGeneration + 1,
            LedgerEpoch: predecessor.Model.Header.LedgerEpoch);
        var outcome = LedgerBuilder.AppendContinuation(
            expected, predecessor,
            ContextSource(1), Interaction(1),
            OutcomeSource("Continuation summary."), Interaction(1));
        return outcome.Candidate ?? throw new InvalidOperationException(FirstCode(outcome.Diagnostics));
    }

    public static ValidatedLedger RecoveryRoot()
    {
        var transition = new RecoveryRootTransition(
            Identities: Ident, SessionEpoch: SessionEpochA,
            LedgerEpoch: LedgerEpoch1,
            RecoveryReason: "unavailable_accepted_artifact");
        var outcome = LedgerBuilder.CreateRecoveryRoot(
            transition,
            ContextSource(0), Interaction(0),
            OutcomeSource("Recovery-root summary."), Interaction(0));
        return outcome.Candidate ?? throw new InvalidOperationException(FirstCode(outcome.Diagnostics));
    }

    private static string FirstCode(ImmutableArray<LedgerDiagnostic> diags)
        => diags.IsDefaultOrEmpty ? "unknown" : diags[0].Code;
}

using System.Collections.Immutable;
using System.Text;
using AgenticPrReview.Runtime.Ledger;

namespace AgenticPrReview.Runtime.Tests.Ledger;

/// <summary>
/// Focused unit tests for <see cref="LedgerAppend"/>. Verifies the kind-guard
/// short-circuit and the category-once accumulation for the four transition
/// kinds. Full transition-matrix coverage is provided by the fixture corpus
/// (ledger-transition manifest entries).
/// </summary>
public sealed class LedgerAppendTests
{
    [Fact]
    public void ValidateBootstrap_KindMismatch_ReturnsOnlyTransitionKindMismatch()
    {
        var candidate = Fixtures.Bootstrap();
        // A continuation-shaped expected on a bootstrap candidate would fail identity, epoch, etc.,
        // but the kind guard fires FIRST and short-circuits the validator.
        var badExpected = new ContinuationTransition(
            Identities: Fixtures.Ident, SessionEpoch: Fixtures.SessionEpochA,
            PredecessorLedgerSha256: candidate.ContentSha256,
            PredecessorStateGeneration: 0,
            PredecessorLedgerEpoch: Fixtures.LedgerEpoch1,
            StateGeneration: 1,
            LedgerEpoch: Fixtures.LedgerEpoch1);
        var outcome = LedgerAppend.ValidateContinuation(badExpected, candidate, candidate);
        Assert.Single(outcome.Diagnostics);
        Assert.Equal(LedgerDiagnosticCodes.TransitionKindMismatch, outcome.Diagnostics[0].Code);
    }

    [Fact]
    public void ValidateBootstrap_HappyPath_ReturnsEmptyDiagnostics()
    {
        var candidate = Fixtures.Bootstrap();
        var expected = new BootstrapTransition(Fixtures.Ident, Fixtures.SessionEpochA, StateGeneration: 0, LedgerEpoch: Fixtures.LedgerEpoch1);
        var outcome = LedgerAppend.ValidateBootstrap(expected, candidate);
        Assert.Empty(outcome.Diagnostics);
    }

    [Fact]
    public void ValidateContinuation_HappyPath_ReturnsEmptyDiagnostics()
    {
        var predecessor = Fixtures.Bootstrap();
        var candidate = Fixtures.Continuation(predecessor);
        var expected = new ContinuationTransition(
            Identities: Fixtures.Ident, SessionEpoch: Fixtures.SessionEpochA,
            PredecessorLedgerSha256: predecessor.ContentSha256,
            PredecessorStateGeneration: predecessor.Model.Header.StateGeneration,
            PredecessorLedgerEpoch: predecessor.Model.Header.LedgerEpoch,
            StateGeneration: predecessor.Model.Header.StateGeneration + 1,
            LedgerEpoch: predecessor.Model.Header.LedgerEpoch);
        var outcome = LedgerAppend.ValidateContinuation(expected, predecessor, candidate);
        Assert.Empty(outcome.Diagnostics);
    }

    [Fact]
    public void ValidateRecoveryRoot_HappyPath_ReturnsEmptyDiagnostics()
    {
        var candidate = Fixtures.RecoveryRoot();
        var expected = new RecoveryRootTransition(
            Identities: Fixtures.Ident, SessionEpoch: Fixtures.SessionEpochA,
            LedgerEpoch: Fixtures.LedgerEpoch1,
            RecoveryReason: "unavailable_accepted_artifact");
        var outcome = LedgerAppend.ValidateRecoveryRoot(expected, candidate);
        Assert.Empty(outcome.Diagnostics);
    }

    [Fact]
    public void ValidateBootstrap_IdentityMismatch_EmitsIdentityMismatchOnce()
    {
        var candidate = Fixtures.Bootstrap();
        var expected = new BootstrapTransition(
            Fixtures.Ident with { Repository = "someone/other" },
            Fixtures.SessionEpochA,
            StateGeneration: 0,
            LedgerEpoch: Fixtures.LedgerEpoch1);
        var outcome = LedgerAppend.ValidateBootstrap(expected, candidate);
        Assert.Contains(outcome.Diagnostics, d => d.Code == LedgerDiagnosticCodes.IdentityMismatch);
        // Category-once rule: identity_mismatch appears at most once.
        Assert.Equal(1, outcome.Diagnostics.Count(d => d.Code == LedgerDiagnosticCodes.IdentityMismatch));
    }
}

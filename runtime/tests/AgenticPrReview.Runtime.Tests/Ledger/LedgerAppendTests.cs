using AgenticPrReview.Runtime.Ledger;

namespace AgenticPrReview.Runtime.Tests.Ledger;

/// <summary>
/// Focused unit tests for <see cref="LedgerAppend"/> cross-check paths that
/// cannot be reached through the fixture corpus because CommonCrossChecks may
/// short-circuit before a later, more specific mismatch code is reported.
/// Each test crafts an <see cref="ExpectedTransition"/> that agrees with the
/// candidate up to the point of interest, then asserts that the intended
/// diagnostic code is emitted.
/// </summary>
public sealed class LedgerAppendTests
{
    private static byte[] ReadFixture(string name)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "protocol", "fixtures", "v1", "provider-session-ledger");
        return File.ReadAllBytes(Path.Combine(root, name));
    }

    private static ValidatedLedger Parse(string name)
    {
        var result = LedgerParser.ParseAndValidate(ReadFixture(name));
        Assert.NotNull(result.Ledger);
        return result.Ledger!;
    }

    private static ExpectedIdentities IdentitiesFromHeader(LedgerHeader h) => new(
        Repository: h.Repository,
        HeadRepository: h.HeadRepository,
        PullRequest: h.PullRequest,
        WorkflowIdentity: h.WorkflowIdentity,
        TrustedExecutionDomain: h.TrustedExecutionDomain,
        SessionEpoch: h.SessionEpoch,
        ProviderId: h.ProviderId,
        ModelId: h.ModelId,
        AdapterId: h.AdapterId,
        TemplateId: h.TemplateId,
        PolicyId: h.PolicyId,
        ToolDefinitionId: h.ToolDefinitionId,
        CacheConfigId: h.CacheConfigId);

    // -----------------------------------------------------------------
    // ledger_state_generation_mismatch — reset variant
    //
    // A reset candidate has stateGeneration=1, ledgerEpoch=2. If the caller
    // asserts a ResetTransition with a different StateGeneration, ValidateReset
    // rejects with StateGenerationMismatch before checking the epoch.

    [Fact]
    public void ValidateReset_StateGenerationMismatch_ReportsStateGenerationMismatch()
    {
        var predecessor = Parse("bootstrap-minimal.json");
        var candidate = Parse("reset-base-changed.json");
        var identities = IdentitiesFromHeader(candidate.Model.Header);
        var expected = new ResetTransition(
            Identities: identities,
            PredecessorLedgerSha256: predecessor.ContentSha256,
            PredecessorManifestSha256: candidate.Model.Header.PredecessorManifestSha256!,
            PredecessorStateGeneration: 0,
            StateGeneration: 99, // deliberate mismatch — candidate has 1
            LedgerEpoch: candidate.Model.Header.LedgerEpoch,
            ResetReason: candidate.Model.Header.ResetReason!);
        var outcome = LedgerAppend.ValidateReset(predecessor, candidate, expected);
        Assert.Null(outcome.Candidate);
        Assert.Equal(LedgerDiagnosticCodes.StateGenerationMismatch, outcome.Failure!.Code);
    }

    // -----------------------------------------------------------------
    // ledger_reset_reason_mismatch — every other check passes but the reason
    // differs between expected and candidate.

    [Fact]
    public void ValidateReset_ResetReasonMismatch_ReportsResetReasonMismatch()
    {
        var predecessor = Parse("bootstrap-minimal.json");
        var candidate = Parse("reset-base-changed.json");
        var identities = IdentitiesFromHeader(candidate.Model.Header);
        var expected = new ResetTransition(
            Identities: identities,
            PredecessorLedgerSha256: predecessor.ContentSha256,
            PredecessorManifestSha256: candidate.Model.Header.PredecessorManifestSha256!,
            PredecessorStateGeneration: 0,
            StateGeneration: candidate.Model.Header.StateGeneration,
            LedgerEpoch: candidate.Model.Header.LedgerEpoch,
            ResetReason: "cache_contract_changed"); // candidate has "base_changed"
        var outcome = LedgerAppend.ValidateReset(predecessor, candidate, expected);
        Assert.Null(outcome.Candidate);
        Assert.Equal(LedgerDiagnosticCodes.ResetReasonMismatch, outcome.Failure!.Code);
    }

    // -----------------------------------------------------------------
    // ledger_recovery_reason_mismatch — recovery variant.

    [Fact]
    public void ValidateRecovery_RecoveryReasonMismatch_ReportsRecoveryReasonMismatch()
    {
        var candidate = Parse("recovery-predecessor-unavailable.json");
        var identities = IdentitiesFromHeader(candidate.Model.Header);
        var expected = new RecoveryTransition(
            Identities: identities,
            StateGeneration: candidate.Model.Header.StateGeneration,
            LedgerEpoch: candidate.Model.Header.LedgerEpoch,
            RecoveryReason: "predecessor_prefix_broken"); // candidate has "predecessor_unavailable"
        var outcome = LedgerAppend.ValidateRecovery(candidate, expected);
        Assert.Null(outcome.Candidate);
        Assert.Equal(LedgerDiagnosticCodes.RecoveryReasonMismatch, outcome.Failure!.Code);
    }

    // -----------------------------------------------------------------
    // ledger_reset_epoch_not_fresh — candidate ledgerEpoch equals predecessor
    // ledgerEpoch, and the caller-supplied expected also declares the same
    // epoch so CommonCrossChecks does not short-circuit with LedgerEpochMismatch.
    //
    // The reset-same-epoch fixture has ledgerEpoch=1 (== predecessor). Its
    // manifest expected declares ledgerEpoch=2, which triggers LedgerEpochMismatch
    // first. Here we align expected.LedgerEpoch=1 so we reach the fresh-epoch
    // check that lives after PredecessorHashMismatch / PredecessorManifestHash.

    [Fact]
    public void ValidateReset_EpochEqualsPredecessor_ReportsResetEpochNotFresh()
    {
        var predecessor = Parse("bootstrap-minimal.json");
        var candidate = Parse("reset-same-epoch.json");
        var identities = IdentitiesFromHeader(candidate.Model.Header);
        var expected = new ResetTransition(
            Identities: identities,
            PredecessorLedgerSha256: predecessor.ContentSha256,
            PredecessorManifestSha256: candidate.Model.Header.PredecessorManifestSha256!,
            PredecessorStateGeneration: 0,
            StateGeneration: candidate.Model.Header.StateGeneration,
            LedgerEpoch: candidate.Model.Header.LedgerEpoch, // same as predecessor
            ResetReason: candidate.Model.Header.ResetReason!);
        var outcome = LedgerAppend.ValidateReset(predecessor, candidate, expected);
        Assert.Null(outcome.Candidate);
        Assert.Equal(LedgerDiagnosticCodes.ResetEpochNotFresh, outcome.Failure!.Code);
    }

    // -----------------------------------------------------------------
    // Additional cross-check that the fixture corpus does not exercise:
    // identity mismatch on ValidateBootstrap and ValidateRecovery.

    [Fact]
    public void ValidateBootstrap_IdentityMismatch_ReportsIdentityMismatch()
    {
        var candidate = Parse("bootstrap-minimal.json");
        var identities = IdentitiesFromHeader(candidate.Model.Header)
            with { PullRequest = 999 };
        var expected = new BootstrapTransition(
            identities,
            StateGeneration: candidate.Model.Header.StateGeneration,
            LedgerEpoch: candidate.Model.Header.LedgerEpoch);
        var outcome = LedgerAppend.ValidateBootstrap(candidate, expected);
        Assert.Null(outcome.Candidate);
        Assert.Equal(LedgerDiagnosticCodes.IdentityMismatch, outcome.Failure!.Code);
    }
}

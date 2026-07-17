using System.Text;
using AgenticPrReview.Runtime.Ledger;

namespace AgenticPrReview.Runtime.Tools.LedgerFixtureGen;

/// <summary>One transition fixture: candidate file, the expected transition, and the oracle.</summary>
internal sealed record TransitionFixture(
    FixtureArtifact Artifact,
    string Kind,
    ExpectedTransition Expected,
    string? PredecessorFile,
    bool ExpectValid,
    string? ExpectCode);

/// <summary>
/// Transition-fixture scenarios (issue #49 §13 "Valid transitions" and "Transition-time
/// invalid"). Every candidate is a restore-valid ledger: valid rows are rebuilt through
/// the LedgerBuilder API, invalid rows are minimal header/record mutations of those
/// canonical bytes that only the transition validator rejects. Each entry self-checks by
/// parsing the candidate (and predecessor) and running the matching
/// LedgerTransitionValidator entry point against the declared expectation.
/// </summary>
internal static class TransitionScenarios
{
    internal const string BootstrapPredecessorFile = "provider-session-ledger/bootstrap-minimal.json";

    private const string WrongHash = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";
    private const string AltEpoch = "cccccccccccccccccccccc";
    private const string PairOneInteractionId = "3333333333333333333333333333333333333333333333333333333333333333";
    private const string DriftedModelId = "model-2024-02-02";

    // ---- Valid transitions -------------------------------------------------

    internal static TransitionFixture ValidBootstrap()
    {
        var candidate = RestoreScenarios.BootstrapMinimal(out _);
        var expected = new BootstrapTransition(
            LedgerFixtureBaseline.Identities,
            LedgerFixtureBaseline.SessionEpoch,
            LedgerFixtureBaseline.LedgerEpoch,
            StateGeneration: 0);
        return Checked("valid-bootstrap.json", candidate.Content, "bootstrap", expected, null, null, true, null);
    }

    internal static TransitionFixture ValidContinuation(ValidatedLedger bootstrap)
    {
        var candidate = RestoreScenarios.ContinuationOneAppend(bootstrap, out _);
        var expected = ContinuationAfterBootstrap(bootstrap.ContentSha256);
        return Checked("valid-continuation.json", candidate.Content, "continuation", expected, BootstrapPredecessorFile, bootstrap, true, null);
    }

    internal static TransitionFixture ValidResetCacheContractChange(ValidatedLedger bootstrap)
    {
        var candidate = RestoreScenarios.ResetCacheContractChange(bootstrap);
        var expected = ResetAfterBootstrap(bootstrap.ContentSha256, "cache_contract_change");
        return Checked("valid-reset-cache-contract-change.json", candidate.Content, "reset", expected, BootstrapPredecessorFile, bootstrap, true, null);
    }

    internal static TransitionFixture ValidRecoveryRootIntegrityMismatch()
    {
        var candidate = RestoreScenarios.RecoveryRootIntegrityMismatch();
        var expected = RecoveryBaseline();
        return Checked("valid-recovery-root-integrity-mismatch.json", candidate.Content, "recovery_root", expected, null, null, true, null);
    }

    // ---- Transition-time invalid: continuation --------------------------------

    internal static TransitionFixture ContinuationModifiedHistory(string continuationText, ValidatedLedger bootstrap)
    {
        // Rewrite the first pair's summary: the candidate stays restore-valid but no
        // longer preserves the predecessor records byte-for-byte.
        var text = MutateFirst(continuationText, "\"summary\":\"Summary text.\"", "\"summary\":\"Altered text.\"");
        return Checked(
            "continuation-modified-history.json", Bytes(text), "continuation", ContinuationAfterBootstrap(bootstrap.ContentSha256),
            BootstrapPredecessorFile, bootstrap, false, LedgerDiagnosticCodes.ContinuationPrefixMismatch);
    }

    internal static TransitionFixture ContinuationWrongPredecessorHash(string continuationText, ValidatedLedger bootstrap)
    {
        var text = InvalidRestoreScenarios.Mutate(continuationText, $"\"predecessorLedgerSha256\":\"{bootstrap.ContentSha256}\"", $"\"predecessorLedgerSha256\":\"{WrongHash}\"");
        var expected = new ContinuationTransition(
            LedgerFixtureBaseline.Identities,
            LedgerFixtureBaseline.SessionEpoch,
            LedgerFixtureBaseline.LedgerEpoch,
            WrongHash,
            LedgerFixtureBaseline.LedgerEpoch,
            PredecessorStateGeneration: 0,
            StateGeneration: 1);
        return Checked(
            "continuation-wrong-predecessor-hash.json", Bytes(text), "continuation", expected,
            BootstrapPredecessorFile, bootstrap, false, LedgerDiagnosticCodes.PredecessorHashMismatch);
    }

    internal static TransitionFixture ContinuationCacheContractChanged(string continuationText, ValidatedLedger bootstrap)
    {
        var text = DriftModelId(continuationText);
        return Checked(
            "continuation-cache-contract-changed.json", Bytes(text), "continuation", ContinuationAfterBootstrap(bootstrap.ContentSha256),
            BootstrapPredecessorFile, bootstrap, false, LedgerDiagnosticCodes.IdentityMismatch);
    }

    internal static TransitionFixture ContinuationCandidateOnlyIdentityDrift(string continuationText, ValidatedLedger bootstrap)
    {
        var text = InvalidRestoreScenarios.Mutate(continuationText, "\"workflowIdentity\":\"ci\"", "\"workflowIdentity\":\"deploy\"");
        return Checked(
            "continuation-candidate-only-identity-drift.json", Bytes(text), "continuation", ContinuationAfterBootstrap(bootstrap.ContentSha256),
            BootstrapPredecessorFile, bootstrap, false, LedgerDiagnosticCodes.IdentityMismatch);
    }

    internal static TransitionFixture ContinuationSessionEpochChanged(string continuationText, ValidatedLedger bootstrap)
    {
        var text = InvalidRestoreScenarios.Mutate(continuationText, $"\"sessionEpoch\":\"{LedgerFixtureBaseline.SessionEpoch}\"", $"\"sessionEpoch\":\"{AltEpoch}\"");
        return Checked(
            "continuation-session-epoch-changed.json", Bytes(text), "continuation", ContinuationAfterBootstrap(bootstrap.ContentSha256),
            BootstrapPredecessorFile, bootstrap, false, LedgerDiagnosticCodes.SessionEpochMismatch);
    }

    internal static TransitionFixture ContinuationLedgerEpochChanged(string continuationText, ValidatedLedger bootstrap)
    {
        var text = InvalidRestoreScenarios.Mutate(continuationText, $"\"ledgerEpoch\":\"{LedgerFixtureBaseline.LedgerEpoch}\"", $"\"ledgerEpoch\":\"{AltEpoch}\"");
        return Checked(
            "continuation-ledger-epoch-changed.json", Bytes(text), "continuation", ContinuationAfterBootstrap(bootstrap.ContentSha256),
            BootstrapPredecessorFile, bootstrap, false, LedgerDiagnosticCodes.LedgerEpochMismatch);
    }

    internal static TransitionFixture ContinuationPredecessorLedgerEpochMismatch(string continuationText, ValidatedLedger bootstrap)
    {
        var text = InvalidRestoreScenarios.Mutate(continuationText, $"\"predecessorLedgerEpoch\":\"{LedgerFixtureBaseline.LedgerEpoch}\"", $"\"predecessorLedgerEpoch\":\"{AltEpoch}\"");
        return Checked(
            "continuation-predecessor-ledger-epoch-mismatch.json", Bytes(text), "continuation", ContinuationAfterBootstrap(bootstrap.ContentSha256),
            BootstrapPredecessorFile, bootstrap, false, LedgerDiagnosticCodes.PredecessorLedgerEpochMismatch);
    }

    internal static TransitionFixture ContinuationPredecessorGenerationMismatch(string continuationText, ValidatedLedger bootstrap)
    {
        var text = InvalidRestoreScenarios.Mutate(continuationText, "\"predecessorStateGeneration\":0", "\"predecessorStateGeneration\":5");
        return Checked(
            "continuation-predecessor-generation-mismatch.json", Bytes(text), "continuation", ContinuationAfterBootstrap(bootstrap.ContentSha256),
            BootstrapPredecessorFile, bootstrap, false, LedgerDiagnosticCodes.PredecessorGenerationMismatch);
    }

    internal static TransitionFixture ContinuationMultiPredecessorDefect(string continuationText, ValidatedLedger bootstrap)
    {
        // Three predecessor-chain defects at once; the validator reports them in category
        // order, so the hash mismatch owns Diagnostics[0].
        var text = InvalidRestoreScenarios.Mutate(continuationText, $"\"predecessorLedgerSha256\":\"{bootstrap.ContentSha256}\"", $"\"predecessorLedgerSha256\":\"{WrongHash}\"");
        text = InvalidRestoreScenarios.Mutate(text, $"\"predecessorLedgerEpoch\":\"{LedgerFixtureBaseline.LedgerEpoch}\"", $"\"predecessorLedgerEpoch\":\"{AltEpoch}\"");
        text = InvalidRestoreScenarios.Mutate(text, "\"predecessorStateGeneration\":0", "\"predecessorStateGeneration\":5");
        return Checked(
            "continuation-multi-predecessor-defect.json", Bytes(text), "continuation", ContinuationAfterBootstrap(bootstrap.ContentSha256),
            BootstrapPredecessorFile, bootstrap, false, LedgerDiagnosticCodes.PredecessorHashMismatch);
    }

    internal static TransitionFixture ContinuationStateGenerationMismatch(string continuationText, ValidatedLedger bootstrap)
    {
        var text = InvalidRestoreScenarios.Mutate(continuationText, "\"stateGeneration\":1", "\"stateGeneration\":2");
        return Checked(
            "continuation-state-generation-mismatch.json", Bytes(text), "continuation", ContinuationAfterBootstrap(bootstrap.ContentSha256),
            BootstrapPredecessorFile, bootstrap, false, LedgerDiagnosticCodes.StateGenerationMismatch);
    }

    // ---- Transition-time invalid: reset ---------------------------------------

    internal static TransitionFixture ResetWithPredecessorRecords(string resetText, ValidatedLedger bootstrap)
    {
        // A reset candidate carrying two pairs instead of the required single pair.
        var text = AppendSecondPair(resetText);
        return Checked(
            "reset-with-predecessor-records.json", Bytes(text), "reset", ResetAfterBootstrap(bootstrap.ContentSha256, "base_change"),
            BootstrapPredecessorFile, bootstrap, false, LedgerDiagnosticCodes.ResetRecordsShapeMismatch);
    }

    internal static TransitionFixture ResetSameEpoch(string resetText, ValidatedLedger bootstrap)
    {
        var text = InvalidRestoreScenarios.Mutate(resetText, $"\"ledgerEpoch\":\"{LedgerFixtureBaseline.ResetLedgerEpoch}\"", $"\"ledgerEpoch\":\"{LedgerFixtureBaseline.LedgerEpoch}\"");
        var expected = new ResetTransition(
            LedgerFixtureBaseline.Identities,
            LedgerFixtureBaseline.SessionEpoch,
            LedgerFixtureBaseline.LedgerEpoch,
            bootstrap.ContentSha256,
            LedgerFixtureBaseline.PredecessorManifestSha256,
            LedgerFixtureBaseline.LedgerEpoch,
            PredecessorStateGeneration: 0,
            StateGeneration: 1,
            ResetReason: "base_change");
        return Checked(
            "reset-same-epoch.json", Bytes(text), "reset", expected,
            BootstrapPredecessorFile, bootstrap, false, LedgerDiagnosticCodes.ResetEpochNotFresh);
    }

    internal static TransitionFixture ResetWrongManifestHash(string resetText, ValidatedLedger bootstrap)
    {
        var text = InvalidRestoreScenarios.Mutate(resetText, $"\"predecessorManifestSha256\":\"{LedgerFixtureBaseline.PredecessorManifestSha256}\"", $"\"predecessorManifestSha256\":\"{WrongHash}\"");
        return Checked(
            "reset-wrong-manifest-hash.json", Bytes(text), "reset", ResetAfterBootstrap(bootstrap.ContentSha256, "base_change"),
            BootstrapPredecessorFile, bootstrap, false, LedgerDiagnosticCodes.PredecessorManifestHashMismatch);
    }

    internal static TransitionFixture ResetWrongReason(string resetText, ValidatedLedger bootstrap)
    {
        // Candidate keeps base_change; the expectation asks for cache_contract_change.
        return Checked(
            "reset-wrong-reason.json", Bytes(resetText), "reset", ResetAfterBootstrap(bootstrap.ContentSha256, "cache_contract_change"),
            BootstrapPredecessorFile, bootstrap, false, LedgerDiagnosticCodes.ResetReasonMismatch);
    }

    internal static TransitionFixture ResetSessionScopeChanged(string resetText, ValidatedLedger bootstrap)
    {
        var text = InvalidRestoreScenarios.Mutate(resetText, "\"workflowIdentity\":\"ci\"", "\"workflowIdentity\":\"deploy\"");
        return Checked(
            "reset-session-scope-changed.json", Bytes(text), "reset", ResetAfterBootstrap(bootstrap.ContentSha256, "base_change"),
            BootstrapPredecessorFile, bootstrap, false, LedgerDiagnosticCodes.IdentityMismatch);
    }

    internal static TransitionFixture ResetPredecessorGenerationMismatch(string resetText, ValidatedLedger bootstrap)
    {
        var text = InvalidRestoreScenarios.Mutate(resetText, "\"predecessorStateGeneration\":0", "\"predecessorStateGeneration\":5");
        return Checked(
            "reset-predecessor-generation-mismatch.json", Bytes(text), "reset", ResetAfterBootstrap(bootstrap.ContentSha256, "base_change"),
            BootstrapPredecessorFile, bootstrap, false, LedgerDiagnosticCodes.PredecessorGenerationMismatch);
    }

    internal static TransitionFixture ResetBaseChangeCacheContractDrift(string resetText, ValidatedLedger bootstrap)
    {
        // Cache-contract drift under a base_change reason (only cache_contract_change
        // permits it). Expected identities track the drifted candidate.
        var text = DriftModelId(resetText);
        var expected = new ResetTransition(
            LedgerFixtureBaseline.Identities with { ModelId = DriftedModelId },
            LedgerFixtureBaseline.SessionEpoch,
            LedgerFixtureBaseline.ResetLedgerEpoch,
            bootstrap.ContentSha256,
            LedgerFixtureBaseline.PredecessorManifestSha256,
            LedgerFixtureBaseline.LedgerEpoch,
            PredecessorStateGeneration: 0,
            StateGeneration: 1,
            ResetReason: "base_change");
        return Checked(
            "reset-base-change-cache-contract-drift.json", Bytes(text), "reset", expected,
            BootstrapPredecessorFile, bootstrap, false, LedgerDiagnosticCodes.IdentityMismatch);
    }

    // ---- Transition-time invalid: root kinds -----------------------------------

    internal static TransitionFixture RecoveryRootWrongReason(string recoveryRootText)
    {
        var expected = new RecoveryRootTransition(
            LedgerFixtureBaseline.Identities,
            LedgerFixtureBaseline.RecoverySessionEpoch,
            LedgerFixtureBaseline.RecoveryLedgerEpoch,
            StateGeneration: 0,
            RecoveryReason: "corrupt_accepted_artifact");
        return Checked(
            "recovery-root-wrong-reason.json", Bytes(recoveryRootText), "recovery_root", expected,
            null, null, false, LedgerDiagnosticCodes.RecoveryRootReasonMismatch);
    }

    internal static TransitionFixture BootstrapMultiPair(string bootstrapText)
    {
        var text = AppendSecondPair(bootstrapText);
        var expected = new BootstrapTransition(
            LedgerFixtureBaseline.Identities,
            LedgerFixtureBaseline.SessionEpoch,
            LedgerFixtureBaseline.LedgerEpoch,
            StateGeneration: 0);
        return Checked(
            "bootstrap-multi-pair.json", Bytes(text), "bootstrap", expected,
            null, null, false, LedgerDiagnosticCodes.RootRecordsShapeMismatch);
    }

    internal static TransitionFixture RecoveryRootMultiPair(string recoveryRootText)
    {
        var text = AppendSecondPair(recoveryRootText);
        return Checked(
            "recovery-root-multi-pair.json", Bytes(text), "recovery_root", RecoveryBaseline(),
            null, null, false, LedgerDiagnosticCodes.RootRecordsShapeMismatch);
    }

    internal static TransitionFixture BootstrapWithExpectedContinuation(string bootstrapText, ValidatedLedger bootstrap)
    {
        // A bootstrap candidate evaluated through the continuation entry point: the kind
        // guard short-circuits before any other category.
        return Checked(
            "bootstrap-with-expected-continuation.json", Bytes(bootstrapText), "continuation", ContinuationAfterBootstrap(bootstrap.ContentSha256),
            BootstrapPredecessorFile, bootstrap, false, LedgerDiagnosticCodes.TransitionKindMismatch);
    }

    // ---- Helpers ---------------------------------------------------------------

    private static ContinuationTransition ContinuationAfterBootstrap(string predecessorHash)
    {
        return new ContinuationTransition(
            LedgerFixtureBaseline.Identities,
            LedgerFixtureBaseline.SessionEpoch,
            LedgerFixtureBaseline.LedgerEpoch,
            predecessorHash,
            LedgerFixtureBaseline.LedgerEpoch,
            PredecessorStateGeneration: 0,
            StateGeneration: 1);
    }

    private static ResetTransition ResetAfterBootstrap(string predecessorHash, string reason)
    {
        return new ResetTransition(
            LedgerFixtureBaseline.Identities,
            LedgerFixtureBaseline.SessionEpoch,
            LedgerFixtureBaseline.ResetLedgerEpoch,
            predecessorHash,
            LedgerFixtureBaseline.PredecessorManifestSha256,
            LedgerFixtureBaseline.LedgerEpoch,
            PredecessorStateGeneration: 0,
            StateGeneration: 1,
            ResetReason: reason);
    }

    private static RecoveryRootTransition RecoveryBaseline()
    {
        return new RecoveryRootTransition(
            LedgerFixtureBaseline.Identities,
            LedgerFixtureBaseline.RecoverySessionEpoch,
            LedgerFixtureBaseline.RecoveryLedgerEpoch,
            StateGeneration: 0,
            RecoveryReason: "integrity_mismatch");
    }

    // Swaps the baseline model id for a drifted one and recomputes every record's
    // cacheContractDigest so the candidate remains restore-valid (same technique as
    // model-alias-latest; key order is value-independent so bytes stay canonical).
    private static string DriftModelId(string ledgerText)
    {
        var baselineDigest = InvalidRestoreScenarios.ExtractBaselineDigest(ledgerText);
        var driftedDigest = BuildDigest(LedgerFixtureBaseline.Identities with { ModelId = DriftedModelId });
        return ledgerText
            .Replace($"\"modelId\":\"{LedgerFixtureBaseline.ModelId}\"", $"\"modelId\":\"{DriftedModelId}\"", StringComparison.Ordinal)
            .Replace($"\"cacheContractDigest\":\"{baselineDigest}\"", $"\"cacheContractDigest\":\"{driftedDigest}\"", StringComparison.Ordinal);
    }

    private static string BuildDigest(ExpectedIdentities identities)
    {
        var source = new ValidatedContextSource
        {
            SubjectDigest = LedgerFixtureBaseline.SubjectDigest,
            ReviewedHeadSha = LedgerFixtureBaseline.ReviewedHeadSha,
            ReviewedBaseSha = LedgerFixtureBaseline.ReviewedBaseSha,
            ChangedFiles = System.Collections.Immutable.ImmutableArray<LedgerChangedFile>.Empty
        };
        var outcome = LedgerBuilder.BuildReviewContext(source, identities, new InteractionIdentity(LedgerFixtureBaseline.InteractionId, 0));
        if (outcome.Value is null)
        {
            throw new InvalidOperationException("drift digest build failed.");
        }

        return outcome.Value.CacheContractDigest;
    }

    // Appends a second well-formed pair (ordinal 1) to a root/reset candidate, keeping
    // the ledger restore-valid while violating the single-pair root records shape.
    private static string AppendSecondPair(string ledgerText)
    {
        var digest = InvalidRestoreScenarios.ExtractBaselineDigest(ledgerText);
        var pair = "{\"cacheContractDigest\":\"" + digest + "\",\"changedFiles\":[],\"interactionId\":\"" + PairOneInteractionId
            + "\",\"interactionOrdinal\":1,\"reviewedBaseSha\":\"" + LedgerFixtureBaseline.ReviewedBaseSha
            + "\",\"reviewedHeadSha\":\"" + LedgerFixtureBaseline.ReviewedHeadSha
            + "\",\"role\":\"review_context\",\"subjectDigest\":\"" + LedgerFixtureBaseline.SubjectDigest
            + "\"},{\"findings\":[],\"interactionId\":\"" + PairOneInteractionId
            + "\",\"interactionOrdinal\":1,\"limitations\":[],\"role\":\"review_outcome\",\"summary\":\"Summary text.\"}";
        return ledgerText[..^InvalidRestoreScenarios.SchemaVersionTail.Length] + "," + pair + InvalidRestoreScenarios.SchemaVersionTail;
    }

    private static string MutateFirst(string text, string search, string replacement)
    {
        var index = InvalidRestoreScenarios.Anchor(text, search);
        return text[..index] + replacement + text[(index + search.Length)..];
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static TransitionFixture Checked(
        string fileName,
        byte[] candidateBytes,
        string kind,
        ExpectedTransition expected,
        string? predecessorFile,
        ValidatedLedger? predecessor,
        bool expectValid,
        string? expectCode)
    {
        var parse = LedgerParser.ParseAndValidate(candidateBytes);
        if (parse.Ledger is null)
        {
            throw new InvalidOperationException($"{fileName} self-check failed: candidate must parse as a valid ledger.");
        }

        var outcome = kind switch
        {
            "bootstrap" => LedgerTransitionValidator.ValidateBootstrap((BootstrapTransition)expected, parse.Ledger),
            "continuation" => LedgerTransitionValidator.ValidateContinuation((ContinuationTransition)expected, predecessor!, parse.Ledger),
            "reset" => LedgerTransitionValidator.ValidateReset((ResetTransition)expected, predecessor!, parse.Ledger),
            "recovery_root" => LedgerTransitionValidator.ValidateRecoveryRoot((RecoveryRootTransition)expected, parse.Ledger),
            _ => throw new InvalidOperationException($"{fileName}: unknown kind {kind}.")
        };

        if (expectValid)
        {
            if (!outcome.Diagnostics.IsEmpty)
            {
                throw new InvalidOperationException($"{fileName} self-check failed: expected a valid transition, got {outcome.Diagnostics[0].Code}.");
            }
        }
        else if (outcome.Diagnostics.IsEmpty || outcome.Diagnostics[0].Code != expectCode)
        {
            var actual = outcome.Diagnostics.IsEmpty ? "<valid>" : outcome.Diagnostics[0].Code;
            throw new InvalidOperationException($"{fileName} self-check failed: expected {expectCode}, got {actual}.");
        }

        return new TransitionFixture(
            new FixtureArtifact(fileName, candidateBytes, parse.Ledger.ContentSha256, null),
            kind,
            expected,
            predecessorFile,
            expectValid,
            expectCode);
    }
}

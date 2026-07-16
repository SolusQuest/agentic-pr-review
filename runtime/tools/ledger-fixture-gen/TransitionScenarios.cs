using System.Collections.Immutable;
using System.IO;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Ledger;

namespace AgenticPrReview.Runtime.LedgerFixtureGen;

/// <summary>
/// Transition and builder scenario fixtures. Emits candidate ledger bytes to
/// disk and returns a manifest entry object that the caller writes into
/// manifest.json. Every mutation preserves parse validity (parseExpectation.valid
/// = true) so that transitionExpectation is the decisive gate.
/// </summary>
internal static partial class Program
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ---------------- Valid transitions ----------------

    internal static (string file, string kind, object expected, string? predecessor, string candidateSha)
        EmitValidBootstrap()
    {
        var ledger = BuildBootstrap();
        var name = "valid-bootstrap.json";
        Write(name, ledger);
        return (name, "bootstrap", ExpectedForBootstrap(SessionEpochA, LedgerEpoch1, 0), null, ledger.ContentSha256);
    }

    internal static (string file, string kind, object expected, string? predecessor, string candidateSha)
        EmitValidContinuation(ValidatedLedger predecessor, string predecessorFile)
    {
        var ledger = BuildContinuation(predecessor);
        var name = "valid-continuation.json";
        Write(name, ledger);
        var expected = ExpectedForContinuation(
            SessionEpochA, ledger.Model.Header.LedgerEpoch,
            predecessor.ContentSha256, predecessor.Model.Header.LedgerEpoch,
            predecessor.Model.Header.StateGeneration, ledger.Model.Header.StateGeneration);
        return (name, "continuation", expected, predecessorFile, ledger.ContentSha256);
    }

    internal static (string file, string kind, object expected, string? predecessor, string candidateSha)
        EmitValidResetCacheContract(ValidatedLedger predecessor, string predecessorFile)
    {
        var ledger = BuildResetCacheContract(predecessor);
        var name = "valid-reset-cache-contract-change.json";
        Write(name, ledger);
        var expected = ExpectedForReset(
            SessionEpochA, ledger.Model.Header.LedgerEpoch!,
            predecessor.ContentSha256, ledger.Model.Header.PredecessorManifestSha256!,
            predecessor.Model.Header.LedgerEpoch, predecessor.Model.Header.StateGeneration,
            ledger.Model.Header.StateGeneration, ledger.Model.Header.ResetReason!,
            IdentAltCache);
        return (name, "reset", expected, predecessorFile, ledger.ContentSha256);
    }

    internal static (string file, string kind, object expected, string? predecessor, string candidateSha)
        EmitValidRecoveryRoot()
    {
        var ledger = BuildRecoveryRoot();
        var name = "valid-recovery-root-unavailable-accepted-artifact.json";
        Write(name, ledger);
        var expected = ExpectedForRecoveryRoot(SessionEpochA, LedgerEpoch1, ledger.Model.Header.RecoveryReason!);
        return (name, "recovery_root", expected, null, ledger.ContentSha256);
    }

    // ---------------- Expected-transition JSON payloads ----------------

    internal static object IdentitiesJson(ExpectedIdentities id) => new
    {
        repository = id.Repository,
        headRepository = id.HeadRepository,
        pullRequest = id.PullRequest,
        workflowIdentity = id.WorkflowIdentity,
        trustedExecutionDomain = id.TrustedExecutionDomain,
        providerId = id.ProviderId,
        modelId = id.ModelId,
        adapterId = id.AdapterId,
        templateId = id.TemplateId,
        policyId = id.PolicyId,
        toolDefinitionId = id.ToolDefinitionId,
        cacheConfigId = id.CacheConfigId,
    };

    internal static object ExpectedForBootstrap(string sessionEpoch, string ledgerEpoch, long stateGen, ExpectedIdentities? id = null) => new
    {
        identities = IdentitiesJson(id ?? Ident),
        sessionEpoch,
        stateGeneration = stateGen,
        ledgerEpoch,
    };

    internal static object ExpectedForContinuation(
        string sessionEpoch, string ledgerEpoch,
        string predecessorLedgerSha256, string predecessorLedgerEpoch,
        long predecessorStateGeneration, long stateGeneration,
        ExpectedIdentities? id = null) => new
    {
        identities = IdentitiesJson(id ?? Ident),
        sessionEpoch,
        predecessorLedgerSha256,
        predecessorStateGeneration,
        predecessorLedgerEpoch,
        stateGeneration,
        ledgerEpoch,
    };

    internal static object ExpectedForReset(
        string sessionEpoch, string ledgerEpoch,
        string predecessorLedgerSha256, string predecessorManifestSha256,
        string predecessorLedgerEpoch, long predecessorStateGeneration,
        long stateGeneration, string resetReason,
        ExpectedIdentities? id = null) => new
    {
        identities = IdentitiesJson(id ?? Ident),
        sessionEpoch,
        predecessorLedgerSha256,
        predecessorManifestSha256,
        predecessorStateGeneration,
        predecessorLedgerEpoch,
        stateGeneration,
        ledgerEpoch,
        resetReason,
    };

    internal static object ExpectedForRecoveryRoot(string sessionEpoch, string ledgerEpoch, string recoveryReason, ExpectedIdentities? id = null) => new
    {
        identities = IdentitiesJson(id ?? Ident),
        sessionEpoch,
        ledgerEpoch,
        recoveryReason,
    };

    // ---------------- Transition-invalid mutations ----------------

    /// <summary>
    /// bootstrap-with-expected-continuation: a valid bootstrap candidate passed
    /// to ValidateContinuation. Kind-guard fires first.
    /// </summary>
    internal static (string file, string kind, object expected, string? predecessor, string code, bool parseValid)
        EmitBootstrapWithExpectedContinuation(ValidatedLedger bootstrap, string bootstrapFile)
    {
        var name = "bootstrap-with-expected-continuation.json";
        // Reuse the bootstrap-minimal file as the candidate; only the manifest
        // differs by declaring transition.kind = continuation.
        var expected = ExpectedForContinuation(
            SessionEpochA, bootstrap.Model.Header.LedgerEpoch,
            bootstrap.ContentSha256, bootstrap.Model.Header.LedgerEpoch,
            bootstrap.Model.Header.StateGeneration, bootstrap.Model.Header.StateGeneration + 1);
        // No new file emitted; manifest points to bootstrap-minimal.json.
        return (bootstrapFile, "continuation", expected, bootstrapFile, "ledger_transition_kind_mismatch", true);
    }

    /// <summary>
    /// continuation-wrong-predecessor-hash: expected.predecessorLedgerSha256
    /// disagrees with the actual predecessor.
    /// </summary>
    internal static (string file, string kind, object expected, string? predecessor, string code, bool parseValid)
        EmitContinuationWrongPredecessorHash(ValidatedLedger continuation, ValidatedLedger predecessor, string predecessorFile)
    {
        var name = "continuation-wrong-predecessor-hash.json";
        Write(name, continuation);
        var wrongHash = new string('9', 64);
        var expected = ExpectedForContinuation(
            SessionEpochA, continuation.Model.Header.LedgerEpoch,
            wrongHash, predecessor.Model.Header.LedgerEpoch,
            predecessor.Model.Header.StateGeneration, continuation.Model.Header.StateGeneration);
        return (name, "continuation", expected, predecessorFile, "ledger_predecessor_hash_mismatch", true);
    }

    internal static (string file, string kind, object expected, string? predecessor, string code, bool parseValid)
        EmitContinuationSessionEpochChanged(ValidatedLedger continuation, ValidatedLedger predecessor, string predecessorFile)
    {
        var name = "continuation-session-epoch-changed.json";
        Write(name, continuation);
        var expected = ExpectedForContinuation(
            "EEEEEEEEEEEEEEEEEEEEEE", continuation.Model.Header.LedgerEpoch,
            predecessor.ContentSha256, predecessor.Model.Header.LedgerEpoch,
            predecessor.Model.Header.StateGeneration, continuation.Model.Header.StateGeneration);
        return (name, "continuation", expected, predecessorFile, "ledger_session_epoch_mismatch", true);
    }

    internal static (string file, string kind, object expected, string? predecessor, string code, bool parseValid)
        EmitContinuationLedgerEpochChanged(ValidatedLedger continuation, ValidatedLedger predecessor, string predecessorFile)
    {
        var name = "continuation-ledger-epoch-changed.json";
        Write(name, continuation);
        var expected = ExpectedForContinuation(
            SessionEpochA, "EEEEEEEEEEEEEEEEEEEEEE",
            predecessor.ContentSha256, predecessor.Model.Header.LedgerEpoch,
            predecessor.Model.Header.StateGeneration, continuation.Model.Header.StateGeneration);
        return (name, "continuation", expected, predecessorFile, "ledger_ledger_epoch_mismatch", true);
    }

    internal static (string file, string kind, object expected, string? predecessor, string code, bool parseValid)
        EmitContinuationPredecessorGenerationMismatch(ValidatedLedger continuation, ValidatedLedger predecessor, string predecessorFile)
    {
        var name = "continuation-predecessor-generation-mismatch.json";
        Write(name, continuation);
        var expected = ExpectedForContinuation(
            SessionEpochA, continuation.Model.Header.LedgerEpoch,
            predecessor.ContentSha256, predecessor.Model.Header.LedgerEpoch,
            999L, continuation.Model.Header.StateGeneration);
        return (name, "continuation", expected, predecessorFile, "ledger_predecessor_generation_mismatch", true);
    }

    internal static (string file, string kind, object expected, string? predecessor, string code, bool parseValid)
        EmitContinuationStateGenerationMismatch(ValidatedLedger continuation, ValidatedLedger predecessor, string predecessorFile)
    {
        var name = "continuation-state-generation-mismatch.json";
        Write(name, continuation);
        var expected = ExpectedForContinuation(
            SessionEpochA, continuation.Model.Header.LedgerEpoch,
            predecessor.ContentSha256, predecessor.Model.Header.LedgerEpoch,
            predecessor.Model.Header.StateGeneration, 999L);
        return (name, "continuation", expected, predecessorFile, "ledger_state_generation_mismatch", true);
    }

    internal static (string file, string kind, object expected, string? predecessor, string code, bool parseValid)
        EmitContinuationIdentityDrift(ValidatedLedger continuation, ValidatedLedger predecessor, string predecessorFile)
    {
        var name = "continuation-cache-contract-changed.json";
        Write(name, continuation);
        var driftIdentity = Ident with { AdapterId = new string('f', 64) };
        var expected = ExpectedForContinuation(
            SessionEpochA, continuation.Model.Header.LedgerEpoch,
            predecessor.ContentSha256, predecessor.Model.Header.LedgerEpoch,
            predecessor.Model.Header.StateGeneration, continuation.Model.Header.StateGeneration,
            id: driftIdentity);
        return (name, "continuation", expected, predecessorFile, "ledger_identity_mismatch", true);
    }

    internal static (string file, string kind, object expected, string? predecessor, string code, bool parseValid)
        EmitResetWrongReason(ValidatedLedger reset, ValidatedLedger predecessor, string predecessorFile)
    {
        var name = "reset-wrong-reason.json";
        Write(name, reset);
        // Use a reset candidate whose ResetReason is base_change; expected declares
        // head_history_discontinuity. Cache-contract identity agrees between
        // candidate/predecessor/expected so identity_mismatch does not fire first.
        var expected = ExpectedForReset(
            SessionEpochA, reset.Model.Header.LedgerEpoch,
            predecessor.ContentSha256, reset.Model.Header.PredecessorManifestSha256!,
            predecessor.Model.Header.LedgerEpoch, predecessor.Model.Header.StateGeneration,
            reset.Model.Header.StateGeneration, "head_history_discontinuity");
        return (name, "reset", expected, predecessorFile, "ledger_reset_reason_mismatch", true);
    }

    internal static (string file, string kind, object expected, string? predecessor, string code, bool parseValid)
        EmitResetWrongManifestHash(ValidatedLedger reset, ValidatedLedger predecessor, string predecessorFile)
    {
        var name = "reset-wrong-manifest-hash.json";
        Write(name, reset);
        var expected = ExpectedForReset(
            SessionEpochA, reset.Model.Header.LedgerEpoch,
            predecessor.ContentSha256, new string('c', 64),
            predecessor.Model.Header.LedgerEpoch, predecessor.Model.Header.StateGeneration,
            reset.Model.Header.StateGeneration, reset.Model.Header.ResetReason!,
            id: IdentAltCache);
        return (name, "reset", expected, predecessorFile, "ledger_predecessor_manifest_hash_mismatch", true);
    }

    internal static (string file, string kind, object expected, string? predecessor, string code, bool parseValid)
        EmitRecoveryRootWrongReason(ValidatedLedger recoveryRoot)
    {
        var name = "recovery-root-wrong-reason.json";
        Write(name, recoveryRoot);
        var expected = ExpectedForRecoveryRoot(SessionEpochA, LedgerEpoch1, "corrupt_accepted_artifact");
        return (name, "recovery_root", expected, null, "ledger_recovery_root_reason_mismatch", true);
    }

    // ---------------- Build scenarios ----------------

    internal static object BuildValidBootstrapScenario(ValidatedLedger bootstrap, string expectedCandidateFile)
    {
        // Emit scenario JSON that reproduces bootstrap-minimal exactly.
        var contextSource = new
        {
            subjectDigest = ((ReviewContextRecord)bootstrap.Model.Records[0]).SubjectDigest,
            reviewedHeadSha = HeadShaA,
            reviewedBaseSha = BaseShaA,
            changedFiles = new object[]
            {
                new
                {
                    path = "src/main.cs",
                    status = "modified",
                    additions = 1,
                    deletions = 0,
                    changes = 1,
                    patch = new { sha256 = new string('9', 64), truncated = false, maxChars = 4000 },
                },
            },
        };
        var outcomeSource = new
        {
            summary = "Bootstrap review complete.",
            findings = Array.Empty<object>(),
            limitations = new[] { "No live provider was invoked." },
        };
        return new
        {
            transition = new
            {
                kind = "bootstrap",
                expected = ExpectedForBootstrap(SessionEpochA, LedgerEpoch1, 0),
            },
            contextSource,
            contextInteraction = new { interactionId = MakeInteractionId(0), interactionOrdinal = 0 },
            outcomeSource,
            outcomeInteraction = new { interactionId = MakeInteractionId(0), interactionOrdinal = 0 },
        };
    }

    internal static void WriteScenario(string name, object scenario)
    {
        var path = Path.Combine(root, name);
        var json = JsonSerializer.Serialize(scenario, ManifestJsonOptions);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(json));
        Console.WriteLine($"{name}: (scenario, {json.Length} bytes)");
    }
}

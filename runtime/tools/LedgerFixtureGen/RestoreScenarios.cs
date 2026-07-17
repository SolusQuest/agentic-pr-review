using System.Collections.Immutable;
using System.Text;
using AgenticPrReview.Runtime.Ledger;

namespace AgenticPrReview.Runtime.Tools.LedgerFixtureGen;

/// <summary>One generated fixture file: name, exact bytes, and its restore oracle.</summary>
internal sealed record FixtureArtifact(string FileName, byte[] Content, string? ContentSha256, string? ExpectedCode);

/// <summary>
/// Valid restore-fixture scenarios for protocol/fixtures/v1/provider-session-ledger/.
/// Every valid artifact is produced through the LedgerBuilder public API so the bytes
/// are the canonical writer output; scenarios never hand-assemble valid JSON. The
/// invalid artifacts live in InvalidRestoreScenarios and are minimal mutations of the
/// canonical bytes produced here. invalid-json.json is a raw-transport defect fixture
/// and is maintained by hand, not generated here.
/// </summary>
internal static class RestoreScenarios
{
    internal static FixtureArtifact BootstrapMinimal(out ValidatedLedger ledger)
    {
        var context = BuildContext(LedgerFixtureBaseline.Identities, LedgerFixtureBaseline.InteractionId, 0);
        var outcome = BuildOutcome(LedgerFixtureBaseline.InteractionId, 0);
        var expected = new BootstrapTransition(
            LedgerFixtureBaseline.Identities,
            LedgerFixtureBaseline.SessionEpoch,
            LedgerFixtureBaseline.LedgerEpoch,
            StateGeneration: 0);

        ledger = RequireCandidate(LedgerBuilder.CreateBootstrap(expected, context, outcome), "bootstrap-minimal");
        return Valid("bootstrap-minimal.json", ledger);
    }

    internal static FixtureArtifact ContinuationOneAppend(ValidatedLedger predecessor, out ValidatedLedger ledger)
    {
        // The appended pair keeps the genesis interaction id, matching the committed
        // fixture shape; pair invariants only constrain ordinals and per-pair ids.
        var context = BuildContext(LedgerFixtureBaseline.Identities, LedgerFixtureBaseline.InteractionId, 1);
        var outcome = BuildOutcome(LedgerFixtureBaseline.InteractionId, 1);
        var expected = ContinuationAfter(predecessor);

        ledger = RequireCandidate(
            LedgerBuilder.AppendContinuation(expected, predecessor, context, outcome), "continuation-one-append");
        return Valid("continuation-one-append.json", ledger);
    }

    internal static FixtureArtifact ResetBaseChange(ValidatedLedger predecessor)
    {
        var ledger = ResetWithReason(predecessor, "base_change", "reset-base-change");
        return Valid("reset-base-change.json", ledger);
    }

    internal static FixtureArtifact ResetCacheContractChange(ValidatedLedger predecessor)
    {
        var ledger = ResetWithReason(predecessor, "cache_contract_change", "reset-cache-contract-change");
        return Valid("reset-cache-contract-change.json", ledger);
    }

    internal static FixtureArtifact ResetHeadHistoryDiscontinuity(ValidatedLedger predecessor)
    {
        var ledger = ResetWithReason(predecessor, "head_history_discontinuity", "reset-head-history-discontinuity");
        return Valid("reset-head-history-discontinuity.json", ledger);
    }

    internal static FixtureArtifact RecoveryRootIntegrityMismatch()
    {
        var ledger = RecoveryRootWithReason("integrity_mismatch", "recovery-root-integrity-mismatch");
        return Valid("recovery-root-integrity-mismatch.json", ledger);
    }

    internal static FixtureArtifact RecoveryRootUnavailableAcceptedArtifact()
    {
        var ledger = RecoveryRootWithReason("unavailable_accepted_artifact", "recovery-root-unavailable-accepted-artifact");
        return Valid("recovery-root-unavailable-accepted-artifact.json", ledger);
    }

    internal static FixtureArtifact RecoveryRootCorruptAcceptedArtifact()
    {
        var ledger = RecoveryRootWithReason("corrupt_accepted_artifact", "recovery-root-corrupt-accepted-artifact");
        return Valid("recovery-root-corrupt-accepted-artifact.json", ledger);
    }

    internal static FixtureArtifact RecoveryRootUnsafeProvenance()
    {
        var ledger = RecoveryRootWithReason("unsafe_provenance", "recovery-root-unsafe-provenance");
        return Valid("recovery-root-unsafe-provenance.json", ledger);
    }

    internal static FixtureArtifact RecoveryRootStateKeyMismatch()
    {
        var ledger = RecoveryRootWithReason("state_key_mismatch", "recovery-root-state-key-mismatch");
        return Valid("recovery-root-state-key-mismatch.json", ledger);
    }

    internal static FixtureArtifact RecoveryRootContractVersionIncompatible()
    {
        var ledger = RecoveryRootWithReason("contract_version_incompatible", "recovery-root-contract-version-incompatible");
        return Valid("recovery-root-contract-version-incompatible.json", ledger);
    }

    internal static FixtureArtifact RecoveryRootOverBoundLedger()
    {
        var ledger = RecoveryRootWithReason("over_bound_ledger", "recovery-root-over-bound-ledger");
        return Valid("recovery-root-over-bound-ledger.json", ledger);
    }

    internal static FixtureArtifact ContinuationMaxInteractions(ValidatedLedger bootstrap)
    {
        // 32 pairs = 64 records, exactly at the schema maxItems cap.
        var ledger = bootstrap;
        for (long ordinal = 1; ordinal <= 31; ordinal++)
        {
            ledger = AppendPair(ledger, ordinal, ImmutableArray<string>.Empty, $"continuation-max-interactions pair {ordinal}");
        }

        return Valid("continuation-max-interactions.json", ledger);
    }

    internal static FixtureArtifact ContinuationNearByteLimit(ValidatedLedger bootstrap)
    {
        // Pad continuation pairs until the canonical bytes land just under the 256 KiB
        // canonical cap. The builder is deterministic, so the tuning loop is too.
        const long target = LedgerParser.LedgerCanonicalByteLimit - 64;
        var padded = FullPadding();
        var ledger = bootstrap;
        long ordinal = 0;
        while (true)
        {
            var attempt = TryAppendPair(ledger, ordinal + 1, padded);
            if (attempt.Candidate is null || attempt.Candidate.ByteLength > target)
            {
                // The fully padded pair no longer fits (or the builder refused it as
                // over-bound): stop padding and tune the final pair instead.
                break;
            }

            ledger = attempt.Candidate;
            ordinal++;
            if (ordinal > 30)
            {
                throw new InvalidOperationException("continuation-near-byte-limit padding did not converge.");
            }
        }

        ledger = AppendTuned(ledger, ordinal + 1, target);
        if (ledger.ByteLength > LedgerParser.LedgerCanonicalByteLimit ||
            LedgerParser.LedgerCanonicalByteLimit - ledger.ByteLength > 2048)
        {
            throw new InvalidOperationException(
                $"continuation-near-byte-limit self-check failed: canonical bytes {ledger.ByteLength} are not near the cap.");
        }

        return Valid("continuation-near-byte-limit.json", ledger);
    }

    internal static FixtureArtifact Sha256HeadSha()
    {
        // reviewedHeadSha / reviewedBaseSha accept the 64-hex (SHA-256) GitSha variant.
        var headSha64 = new string('a', 64);
        var baseSha64 = new string('b', 64);
        var context = BuildContext(LedgerFixtureBaseline.Identities, LedgerFixtureBaseline.InteractionId, 0, headSha64, baseSha64);
        var outcome = BuildOutcome(LedgerFixtureBaseline.InteractionId, 0);
        var expected = new BootstrapTransition(
            LedgerFixtureBaseline.Identities,
            LedgerFixtureBaseline.SessionEpoch,
            LedgerFixtureBaseline.LedgerEpoch,
            StateGeneration: 0);

        var ledger = RequireCandidate(LedgerBuilder.CreateBootstrap(expected, context, outcome), "sha-256-head-sha");
        return Valid("sha-256-head-sha.json", ledger);
    }

    internal static FixtureArtifact ModelAliasLatest(ValidatedLedger bootstrap)
    {
        // ledger_model_alias_literal is a semantic-stage defect: the bytes must clear
        // schema and structural stages first. Start from the canonical bootstrap bytes
        // and swap in the alias model id plus the cache-contract digest recomputed over
        // the aliased identities (key order is value-independent, so the result stays
        // canonical). The builder refuses this ledger at its own semantic gate, which
        // is why this one artifact is derived by substitution.
        var baselineDigest = BuildContext(LedgerFixtureBaseline.Identities, LedgerFixtureBaseline.InteractionId, 0)
            .CacheContractDigest;
        var aliasDigest = BuildContext(LedgerFixtureBaseline.ModelAliasIdentities, LedgerFixtureBaseline.InteractionId, 0)
            .CacheContractDigest;

        var text = Encoding.UTF8.GetString(bootstrap.CanonicalBytes.AsSpan())
            .Replace(
                $"\"modelId\":\"{LedgerFixtureBaseline.ModelId}\"",
                $"\"modelId\":\"{LedgerFixtureBaseline.ModelAliasLiteral}\"")
            .Replace(
                $"\"cacheContractDigest\":\"{baselineDigest}\"",
                $"\"cacheContractDigest\":\"{aliasDigest}\"");
        var bytes = Encoding.UTF8.GetBytes(text);

        var outcome = LedgerParser.ParseAndValidate(bytes);
        if (outcome.Ledger is not null ||
            outcome.Diagnostics.Length != 1 ||
            outcome.Diagnostics[0].Code != LedgerDiagnosticCodes.ModelAliasLiteral)
        {
            throw new InvalidOperationException(
                "model-alias-latest self-check failed: expected a single ledger_model_alias_literal diagnostic.");
        }

        return new FixtureArtifact("model-alias-latest.json", bytes, null, LedgerDiagnosticCodes.ModelAliasLiteral);
    }

    private static ValidatedLedger ResetWithReason(ValidatedLedger predecessor, string reason, string scenario)
    {
        var context = BuildContext(LedgerFixtureBaseline.Identities, LedgerFixtureBaseline.InteractionId, 0);
        var outcome = BuildOutcome(LedgerFixtureBaseline.InteractionId, 0);
        var expected = new ResetTransition(
            LedgerFixtureBaseline.Identities,
            LedgerFixtureBaseline.SessionEpoch,
            LedgerFixtureBaseline.ResetLedgerEpoch,
            predecessor.ContentSha256,
            LedgerFixtureBaseline.PredecessorManifestSha256,
            LedgerFixtureBaseline.LedgerEpoch,
            PredecessorStateGeneration: 0,
            StateGeneration: 1,
            ResetReason: reason);

        return RequireCandidate(LedgerBuilder.CreateReset(expected, predecessor, context, outcome), scenario);
    }

    private static ValidatedLedger RecoveryRootWithReason(string reason, string scenario)
    {
        var context = BuildContext(LedgerFixtureBaseline.Identities, LedgerFixtureBaseline.InteractionId, 0);
        var outcome = BuildOutcome(LedgerFixtureBaseline.InteractionId, 0);
        var expected = new RecoveryRootTransition(
            LedgerFixtureBaseline.Identities,
            LedgerFixtureBaseline.RecoverySessionEpoch,
            LedgerFixtureBaseline.RecoveryLedgerEpoch,
            StateGeneration: 0,
            RecoveryReason: reason);

        return RequireCandidate(LedgerBuilder.CreateRecoveryRoot(expected, context, outcome), scenario);
    }

    private static ContinuationTransition ContinuationAfter(ValidatedLedger predecessor)
    {
        return new ContinuationTransition(
            LedgerFixtureBaseline.Identities,
            LedgerFixtureBaseline.SessionEpoch,
            LedgerFixtureBaseline.LedgerEpoch,
            predecessor.ContentSha256,
            LedgerFixtureBaseline.LedgerEpoch,
            PredecessorStateGeneration: predecessor.Model.Header.StateGeneration,
            StateGeneration: predecessor.Model.Header.StateGeneration + 1);
    }

    private static ValidatedLedger AppendPair(
        ValidatedLedger predecessor, long ordinal, ImmutableArray<string> limitations, string scenario)
    {
        return RequireCandidate(TryAppendPair(predecessor, ordinal, limitations), scenario);
    }

    private static CandidateOutcome TryAppendPair(
        ValidatedLedger predecessor, long ordinal, ImmutableArray<string> limitations)
    {
        var interactionId = $"{ordinal:x64}";
        var context = BuildContext(LedgerFixtureBaseline.Identities, interactionId, ordinal);
        var outcome = BuildOutcome(interactionId, ordinal, limitations);
        return LedgerBuilder.AppendContinuation(ContinuationAfter(predecessor), predecessor, context, outcome);
    }

    // Grows the appended pair's limitations until the candidate canonical bytes land as
    // close to target as possible without exceeding it. Every adjustment is a content
    // length change on the last limitation entry, so the size delta per iteration is exact.
    private static ValidatedLedger AppendTuned(ValidatedLedger predecessor, long ordinal, long target)
    {
        var limitations = new List<string>();
        for (var attempt = 0; attempt < 64; attempt++)
        {
            var candidate = AppendPair(predecessor, ordinal, ToImmutable(limitations), "continuation-near-byte-limit tune");
            var residual = target - candidate.ByteLength;
            if (residual == 0)
            {
                return candidate;
            }

            if (residual > 0)
            {
                if (limitations.Count > 0 && limitations[^1].Length < 1200)
                {
                    var grown = (int)Math.Min(1200, limitations[^1].Length + residual);
                    limitations[^1] = new string('x', grown);
                    continue;
                }

                // A new entry costs its content plus three bytes of JSON overhead (quotes, comma).
                if (limitations.Count < 16 && residual >= 4)
                {
                    limitations.Add(new string('x', (int)Math.Min(1200, residual - 3)));
                    continue;
                }

                return candidate;
            }

            // Overshoot: shrink the last entry, keeping it non-empty (minLength 1).
            var last = limitations[^1];
            if (last.Length > 1)
            {
                limitations[^1] = new string('x', Math.Max(1, (int)(last.Length + residual)));
                continue;
            }

            return candidate;
        }

        throw new InvalidOperationException("continuation-near-byte-limit tuning did not converge.");
    }

    private static ImmutableArray<string> FullPadding()
    {
        var builder = ImmutableArray.CreateBuilder<string>(16);
        for (var i = 0; i < 16; i++)
        {
            builder.Add(new string('x', 1200));
        }

        return builder.MoveToImmutable();
    }

    private static ImmutableArray<string> ToImmutable(List<string> values)
    {
        var builder = ImmutableArray.CreateBuilder<string>(values.Count);
        foreach (var value in values)
        {
            builder.Add(value);
        }

        return builder.MoveToImmutable();
    }

    private static FixtureArtifact Valid(string fileName, ValidatedLedger ledger) =>
        new(fileName, ledger.CanonicalBytes.ToArray(), ledger.ContentSha256, null);

    private static ReviewContextRecord BuildContext(
        ExpectedIdentities identities, string interactionId, long ordinal, string? headSha = null, string? baseSha = null)
    {
        var source = new ValidatedContextSource
        {
            SubjectDigest = LedgerFixtureBaseline.SubjectDigest,
            ReviewedHeadSha = headSha ?? LedgerFixtureBaseline.ReviewedHeadSha,
            ReviewedBaseSha = baseSha ?? LedgerFixtureBaseline.ReviewedBaseSha,
            ChangedFiles = ImmutableArray<LedgerChangedFile>.Empty
        };
        var outcome = LedgerBuilder.BuildReviewContext(source, identities, new InteractionIdentity(interactionId, ordinal));
        return RequireValue(outcome, $"review_context ordinal {ordinal}");
    }

    private static ReviewOutcomeRecord BuildOutcome(
        string interactionId, long ordinal, ImmutableArray<string>? limitations = null)
    {
        var source = new ValidatedOutcomeSource
        {
            Summary = LedgerFixtureBaseline.Summary,
            Findings = ImmutableArray<LedgerFinding>.Empty,
            Limitations = limitations ?? ImmutableArray<string>.Empty
        };
        var outcome = LedgerBuilder.BuildReviewOutcome(source, new InteractionIdentity(interactionId, ordinal));
        return RequireValue(outcome, $"review_outcome ordinal {ordinal}");
    }

    private static T RequireValue<T>(BuildOutcome<T> outcome, string scenario) where T : class
    {
        if (outcome.Value is null)
        {
            throw new InvalidOperationException($"{scenario} build failed: {Describe(outcome.Diagnostics)}");
        }

        return outcome.Value;
    }

    private static ValidatedLedger RequireCandidate(CandidateOutcome outcome, string scenario)
    {
        if (outcome.Candidate is null)
        {
            throw new InvalidOperationException($"{scenario} candidate failed: {Describe(outcome.Diagnostics)}");
        }

        return outcome.Candidate;
    }

    private static string Describe(ImmutableArray<LedgerDiagnostic> diagnostics)
    {
        var builder = new StringBuilder();
        foreach (var diagnostic in diagnostics)
        {
            if (builder.Length > 0)
            {
                builder.Append("; ");
            }

            builder.Append(diagnostic.Code);
        }

        return builder.ToString();
    }
}

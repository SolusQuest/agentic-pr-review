using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Ledger;

namespace AgenticPrReview.Runtime.Tools.LedgerFixtureGen;

/// <summary>Builder inputs mirrored by a build scenario JSON file.</summary>
internal sealed record ScenarioInputs(
    ValidatedContextSource Context,
    InteractionIdentity ContextInteraction,
    ValidatedOutcomeSource Outcome,
    InteractionIdentity OutcomeInteraction);

/// <summary>One build fixture: scenario file, the expected transition, and the oracle.</summary>
internal sealed record BuildFixture(
    FixtureArtifact Artifact,
    string Kind,
    ExpectedTransition Expected,
    string? PredecessorFile,
    bool ExpectValid,
    string? ExpectCode,
    string? ExpectCauseCode,
    string? ExpectedCandidateFile,
    string? CandidateContentSha256);

/// <summary>
/// Builder-fixture scenarios (issue #49 §13 "Valid builder scenarios" and "Builder-time
/// invalid"). The scenario JSON files describe builder inputs (context/outcome sources
/// plus their interaction identities); every entry self-checks by running the full
/// LedgerBuilder pipeline. Valid rows additionally assert byte-identity with the
/// transition fixture they must reproduce (AC: construct byte-identical candidates).
/// </summary>
internal static class BuildScenarios
{
    private const string WrongHash = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";
    private const string AltInteractionId = "2222222222222222222222222222222222222222222222222222222222222222";

    // ---- Valid builder scenarios -------------------------------------------

    internal static BuildFixture BuildValidBootstrap(byte[] expectedCandidateBytes)
    {
        var expected = new BootstrapTransition(
            LedgerFixtureBaseline.Identities,
            LedgerFixtureBaseline.SessionEpoch,
            LedgerFixtureBaseline.LedgerEpoch,
            StateGeneration: 0);
        return Checked(
            "build-valid-bootstrap.json", "bootstrap", expected, null, null,
            BaselineInputs(LedgerFixtureBaseline.InteractionId, 0),
            true, null, null, "provider-session-ledger/valid-bootstrap.json", expectedCandidateBytes);
    }

    internal static BuildFixture BuildValidContinuation(ValidatedLedger bootstrap, byte[] expectedCandidateBytes)
    {
        return Checked(
            "build-valid-continuation.json", "continuation", ChainFrom(bootstrap), TransitionScenarios.BootstrapPredecessorFile, bootstrap,
            BaselineInputs(LedgerFixtureBaseline.InteractionId, 1),
            true, null, null, "provider-session-ledger/valid-continuation.json", expectedCandidateBytes);
    }

    internal static BuildFixture BuildValidReset(ValidatedLedger bootstrap, byte[] expectedCandidateBytes)
    {
        var expected = new ResetTransition(
            LedgerFixtureBaseline.Identities,
            LedgerFixtureBaseline.SessionEpoch,
            LedgerFixtureBaseline.ResetLedgerEpoch,
            bootstrap.ContentSha256,
            LedgerFixtureBaseline.PredecessorManifestSha256,
            LedgerFixtureBaseline.LedgerEpoch,
            PredecessorStateGeneration: 0,
            StateGeneration: 1,
            ResetReason: "cache_contract_change");
        return Checked(
            "build-valid-reset.json", "reset", expected, TransitionScenarios.BootstrapPredecessorFile, bootstrap,
            BaselineInputs(LedgerFixtureBaseline.InteractionId, 0),
            true, null, null, "provider-session-ledger/valid-reset-cache-contract-change.json", expectedCandidateBytes);
    }

    internal static BuildFixture BuildValidRecoveryRoot(byte[] expectedCandidateBytes)
    {
        var expected = new RecoveryRootTransition(
            LedgerFixtureBaseline.Identities,
            LedgerFixtureBaseline.RecoverySessionEpoch,
            LedgerFixtureBaseline.RecoveryLedgerEpoch,
            StateGeneration: 0,
            RecoveryReason: "integrity_mismatch");
        return Checked(
            "build-valid-recovery-root.json", "recovery_root", expected, null, null,
            BaselineInputs(LedgerFixtureBaseline.InteractionId, 0),
            true, null, null, "provider-session-ledger/valid-recovery-root-integrity-mismatch.json", expectedCandidateBytes);
    }

    // ---- Builder-time invalid -------------------------------------------------

    internal static BuildFixture BuildMismatchedInteractionIds()
    {
        var expected = new BootstrapTransition(
            LedgerFixtureBaseline.Identities,
            LedgerFixtureBaseline.SessionEpoch,
            LedgerFixtureBaseline.LedgerEpoch,
            StateGeneration: 0);
        var inputs = new ScenarioInputs(
            BaselineContext(), new InteractionIdentity(LedgerFixtureBaseline.InteractionId, 0),
            BaselineOutcome(), new InteractionIdentity(AltInteractionId, 0));
        return Checked(
            "build-mismatched-interaction-ids.json", "bootstrap", expected, null, null,
            inputs, false, LedgerDiagnosticCodes.PairInteractionIdMismatch, null, null, null);
    }

    internal static BuildFixture BuildContinuationWrongPredecessorHash(ValidatedLedger bootstrap)
    {
        // The expected chain carries a wrong predecessor hash; the builder mints the
        // candidate from it and the embedded transition validation rejects it.
        var expected = new ContinuationTransition(
            LedgerFixtureBaseline.Identities,
            LedgerFixtureBaseline.SessionEpoch,
            LedgerFixtureBaseline.LedgerEpoch,
            WrongHash,
            LedgerFixtureBaseline.LedgerEpoch,
            PredecessorStateGeneration: 0,
            StateGeneration: 1);
        return Checked(
            "build-continuation-wrong-predecessor-hash.json", "continuation", expected, TransitionScenarios.BootstrapPredecessorFile, bootstrap,
            BaselineInputs(LedgerFixtureBaseline.InteractionId, 1),
            false, LedgerDiagnosticCodes.PredecessorHashMismatch, null, null, null);
    }

    internal static BuildFixture OverBoundAppendCanonicalByte(ValidatedLedger nearByteLimitPredecessor)
    {
        // Predecessor near the 256 KiB canonical cap; a minimal pair pushes it over while
        // the record count stays within bounds.
        var ordinal = nearByteLimitPredecessor.Model.Records.Length / 2;
        return Checked(
            "over-bound-append-canonical-byte.json", "continuation", ChainFrom(nearByteLimitPredecessor),
            "provider-session-ledger/continuation-near-byte-limit.json", nearByteLimitPredecessor,
            BaselineInputs($"{ordinal:x64}", ordinal),
            false, LedgerDiagnosticCodes.OverBoundAppend, LedgerDiagnosticCodes.CanonicalByteLimitExceeded, null, null);
    }

    internal static BuildFixture OverBoundAppendInteractions(ValidatedLedger maxedPredecessor)
    {
        // Predecessor at 64 records; a minimal pair exceeds the interaction bound only.
        var ordinal = maxedPredecessor.Model.Records.Length / 2;
        return Checked(
            "over-bound-append-interactions.json", "continuation", ChainFrom(maxedPredecessor),
            "provider-session-ledger/continuation-max-interactions.json", maxedPredecessor,
            BaselineInputs($"{ordinal:x64}", ordinal),
            false, LedgerDiagnosticCodes.OverBoundAppend, LedgerDiagnosticCodes.InteractionLimitExceeded, null, null);
    }

    internal static BuildFixture OverBoundRootCanonicalByte()
    {
        var expected = new BootstrapTransition(
            LedgerFixtureBaseline.Identities,
            LedgerFixtureBaseline.SessionEpoch,
            LedgerFixtureBaseline.LedgerEpoch,
            StateGeneration: 0);
        return Checked(
            "over-bound-root-canonical-byte.json", "bootstrap", expected, null, null,
            HeavyInputs(LedgerFixtureBaseline.InteractionId, 0, findingCount: 34),
            false, LedgerDiagnosticCodes.OverBoundAppend, LedgerDiagnosticCodes.CanonicalByteLimitExceeded, null, null);
    }

    internal static BuildFixture OverBoundMultiDefect(ValidatedLedger maxedPredecessor)
    {
        // Over the interaction limit AND the canonical byte limit at once; the
        // interaction limit is the earlier bound and owns the CauseCode.
        var ordinal = maxedPredecessor.Model.Records.Length / 2;
        var interactionId = $"{ordinal:x64}";
        return Checked(
            "over-bound-multi-defect.json", "continuation", ChainFrom(maxedPredecessor),
            "provider-session-ledger/continuation-max-interactions.json", maxedPredecessor,
            HeavyInputs(interactionId, ordinal, findingCount: 32),
            false, LedgerDiagnosticCodes.OverBoundAppend, LedgerDiagnosticCodes.InteractionLimitExceeded, null, null);
    }

    // ---- Inputs ----------------------------------------------------------------

    private static ContinuationTransition ChainFrom(ValidatedLedger predecessor)
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

    private static ScenarioInputs BaselineInputs(string interactionId, long ordinal)
    {
        return new ScenarioInputs(
            BaselineContext(), new InteractionIdentity(interactionId, ordinal),
            BaselineOutcome(), new InteractionIdentity(interactionId, ordinal));
    }

    private static ValidatedContextSource BaselineContext()
    {
        return new ValidatedContextSource
        {
            SubjectDigest = LedgerFixtureBaseline.SubjectDigest,
            ReviewedHeadSha = LedgerFixtureBaseline.ReviewedHeadSha,
            ReviewedBaseSha = LedgerFixtureBaseline.ReviewedBaseSha,
            ChangedFiles = ImmutableArray<LedgerChangedFile>.Empty
        };
    }

    private static ValidatedOutcomeSource BaselineOutcome()
    {
        return new ValidatedOutcomeSource
        {
            Summary = LedgerFixtureBaseline.Summary,
            Findings = ImmutableArray<LedgerFinding>.Empty,
            Limitations = ImmutableArray<string>.Empty
        };
    }

    // Outcome padded with schema-max findings so the candidate canonical bytes exceed
    // the 256 KiB cap. Each finding is schema-valid on its own.
    private static ScenarioInputs HeavyInputs(string interactionId, long ordinal, int findingCount)
    {
        var findings = ImmutableArray.CreateBuilder<LedgerFinding>(findingCount);
        for (var i = 0; i < findingCount; i++)
        {
            findings.Add(new LedgerFinding
            {
                Severity = "high",
                Confidence = "high",
                Category = "correctness",
                Title = new string('t', 240),
                Body = new string('b', 4000),
                Evidence = new string('e', 2000),
                Path = null,
                StartLine = null,
                EndLine = null,
                SuggestedAction = new string('s', 1600),
                InlinePreference = null
            });
        }

        var outcome = new ValidatedOutcomeSource
        {
            Summary = LedgerFixtureBaseline.Summary,
            Findings = findings.MoveToImmutable(),
            Limitations = ImmutableArray<string>.Empty
        };
        return new ScenarioInputs(
            BaselineContext(), new InteractionIdentity(interactionId, ordinal),
            outcome, new InteractionIdentity(interactionId, ordinal));
    }

    // ---- Self-check --------------------------------------------------------------

    private static BuildFixture Checked(
        string fileName,
        string kind,
        ExpectedTransition expected,
        string? predecessorFile,
        ValidatedLedger? predecessor,
        ScenarioInputs inputs,
        bool expectValid,
        string? expectCode,
        string? expectCauseCode,
        string? expectedCandidateFile,
        byte[]? expectedCandidateBytes)
    {
        var context = LedgerBuilder.BuildReviewContext(inputs.Context, expected.Identities, inputs.ContextInteraction);
        if (context.Value is null)
        {
            throw new InvalidOperationException($"{fileName} self-check failed: context build failed.");
        }

        var outcome = LedgerBuilder.BuildReviewOutcome(inputs.Outcome, inputs.OutcomeInteraction);
        if (outcome.Value is null)
        {
            throw new InvalidOperationException($"{fileName} self-check failed: outcome build failed.");
        }

        var candidateOutcome = kind switch
        {
            "bootstrap" => LedgerBuilder.CreateBootstrap((BootstrapTransition)expected, context.Value, outcome.Value),
            "continuation" => LedgerBuilder.AppendContinuation((ContinuationTransition)expected, predecessor!, context.Value, outcome.Value),
            "reset" => LedgerBuilder.CreateReset((ResetTransition)expected, predecessor!, context.Value, outcome.Value),
            "recovery_root" => LedgerBuilder.CreateRecoveryRoot((RecoveryRootTransition)expected, context.Value, outcome.Value),
            _ => throw new InvalidOperationException($"{fileName}: unknown kind {kind}.")
        };

        var scenarioBytes = WriteScenario(inputs);
        if (expectValid)
        {
            if (candidateOutcome.Candidate is null)
            {
                throw new InvalidOperationException($"{fileName} self-check failed: expected a valid candidate.");
            }

            if (expectedCandidateBytes is null ||
                !candidateOutcome.Candidate.CanonicalBytes.AsSpan().SequenceEqual(expectedCandidateBytes))
            {
                throw new InvalidOperationException($"{fileName} self-check failed: candidate bytes differ from {expectedCandidateFile}.");
            }

            return new BuildFixture(
                new FixtureArtifact(fileName, scenarioBytes, null, null),
                kind, expected, predecessorFile, true, null, null, expectedCandidateFile, candidateOutcome.Candidate.ContentSha256);
        }

        if (candidateOutcome.Candidate is not null ||
            candidateOutcome.Diagnostics.IsEmpty ||
            candidateOutcome.Diagnostics[0].Code != expectCode)
        {
            var actual = candidateOutcome.Candidate is not null ? "<valid>" : candidateOutcome.Diagnostics.IsEmpty ? "<none>" : candidateOutcome.Diagnostics[0].Code;
            throw new InvalidOperationException($"{fileName} self-check failed: expected {expectCode}, got {actual}.");
        }

        if (expectCauseCode is not null && candidateOutcome.Diagnostics[0].CauseCode != expectCauseCode)
        {
            throw new InvalidOperationException($"{fileName} self-check failed: expected causeCode {expectCauseCode}, got {candidateOutcome.Diagnostics[0].CauseCode ?? "<null>"}.");
        }

        return new BuildFixture(
            new FixtureArtifact(fileName, scenarioBytes, null, null),
            kind, expected, predecessorFile, false, expectCode, expectCauseCode, null, null);
    }

    // ---- Scenario JSON writer -----------------------------------------------------

    private static byte[] WriteScenario(ScenarioInputs inputs)
    {
        var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();

            writer.WritePropertyName("context");
            writer.WriteStartObject();
            WriteInteraction(writer, inputs.ContextInteraction);
            writer.WriteString("subjectDigest", inputs.Context.SubjectDigest);
            writer.WriteString("reviewedHeadSha", inputs.Context.ReviewedHeadSha);
            writer.WriteString("reviewedBaseSha", inputs.Context.ReviewedBaseSha);
            writer.WritePropertyName("changedFiles");
            writer.WriteStartArray();
            foreach (var file in inputs.Context.ChangedFiles)
            {
                writer.WriteStartObject();
                writer.WriteString("path", file.Path);
                if (file.PreviousPath is null)
                {
                    writer.WriteNull("previousPath");
                }
                else
                {
                    writer.WriteString("previousPath", file.PreviousPath);
                }

                writer.WriteString("status", file.Status);
                writer.WriteNumber("additions", file.Additions);
                writer.WriteNumber("deletions", file.Deletions);
                writer.WriteNumber("changes", file.Changes);
                if (file.Patch is null)
                {
                    writer.WriteNull("patch");
                }
                else
                {
                    writer.WritePropertyName("patch");
                    writer.WriteStartObject();
                    writer.WriteString("sha256", file.Patch.Sha256);
                    writer.WriteBoolean("truncated", file.Patch.Truncated);
                    writer.WriteNumber("maxChars", file.Patch.MaxChars);
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();

            writer.WritePropertyName("outcome");
            writer.WriteStartObject();
            WriteInteraction(writer, inputs.OutcomeInteraction);
            writer.WriteString("summary", inputs.Outcome.Summary);
            writer.WritePropertyName("findings");
            writer.WriteStartArray();
            foreach (var finding in inputs.Outcome.Findings)
            {
                writer.WriteStartObject();
                writer.WriteString("severity", finding.Severity);
                writer.WriteString("confidence", finding.Confidence);
                writer.WriteString("category", finding.Category);
                writer.WriteString("title", finding.Title);
                writer.WriteString("body", finding.Body);
                WriteNullableString(writer, "evidence", finding.Evidence);
                WriteNullableString(writer, "path", finding.Path);
                if (finding.StartLine is null)
                {
                    writer.WriteNull("startLine");
                }
                else
                {
                    writer.WriteNumber("startLine", finding.StartLine.Value);
                }

                if (finding.EndLine is null)
                {
                    writer.WriteNull("endLine");
                }
                else
                {
                    writer.WriteNumber("endLine", finding.EndLine.Value);
                }

                WriteNullableString(writer, "suggestedAction", finding.SuggestedAction);
                WriteNullableString(writer, "inlinePreference", finding.InlinePreference);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WritePropertyName("limitations");
            writer.WriteStartArray();
            foreach (var limitation in inputs.Outcome.Limitations)
            {
                writer.WriteStringValue(limitation);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static void WriteInteraction(Utf8JsonWriter writer, InteractionIdentity interaction)
    {
        writer.WritePropertyName("interaction");
        writer.WriteStartObject();
        writer.WriteString("interactionId", interaction.InteractionId);
        writer.WriteNumber("interactionOrdinal", interaction.InteractionOrdinal);
        writer.WriteEndObject();
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string propertyName, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, value);
        }
    }
}

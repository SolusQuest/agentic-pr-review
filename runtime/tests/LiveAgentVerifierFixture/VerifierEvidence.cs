using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Quality;

namespace AgenticPrReview.Runtime.LiveAgentVerifierFixture;

internal sealed record VerifierManifestRow(
    string Id,
    string PhaseOrRoute,
    string? StateExpectation,
    string? StableCode);

internal sealed record VerifierAggregateEvidence(
    VerifierPhaseReceipt MustFind,
    VerifierPhaseReceipt MustNotFind,
    VerifierPhaseReceipt Seed,
    VerifierPhaseReceipt Restore,
    VerifierPhaseReceipt Canary,
    IReadOnlyList<VerifierNegativeReceipt> Negatives,
    VerifierArchitectureReceipt Architecture,
    string NegativeMatrixSha256,
    string CanaryMatrixSha256,
    bool TwoFreshProcesses,
    bool RestoredFirstRequestExact,
    bool RandomPriorFactPrivate,
    bool SingleShotIndependent);

internal static class VerifierEvidence
{
    private const string PhaseKind =
        "apr-r3-live-agent-phase-receipt-v1";
    private const string NegativeKind =
        "apr-r3-live-agent-negative-receipt-v1";
    private const string ArchitectureKind =
        "apr-r3-live-agent-architecture-receipt-v1";

    private static readonly VerifierManifestRow[] ExpectedCanaries =
    [
        new("provider", "authorization_header_only", null, null),
        new("state", "host_crypto_only", null, null),
        new("repository", "untrusted_reviewed_content", null, null),
        new("path", "tracked_path_and_tool_evidence", null, null),
        new("prompt", "untrusted_user_and_tool_content", null, null),
        new("github", "absent", null, null),
        new("actions", "absent", null, null),
        new("workflow", "absent", null, null),
        new("prior", "restored_history_and_terminal_only", null, null),
    ];

    internal static bool TryLoad(
        VerifierCommand command,
        VerifierBuildPair buildPair,
        bool requireReplacement,
        out VerifierAggregateEvidence? evidence,
        out string failure)
    {
        evidence = null;
        failure = "evidence_unknown";
        try
        {
            if (!TryReadNegativeManifest(
                    command.NegativeManifest,
                    out var negativeRows,
                    out var negativeManifestBytes) ||
                !TryReadCanaryManifest(
                    command.CanaryManifest,
                    out var canaryRows,
                    out var canaryManifestBytes))
            {
                failure = "evidence_manifest_invalid";
                return false;
            }

            var receiptRoot = Path.Join(command.Root, "receipts");
            var mustFind = ReadPhase(
                Path.Join(receiptRoot, "must-find.json"));
            var mustNot = ReadPhase(
                Path.Join(receiptRoot, "must-not-find.json"));
            var seed = ReadPhase(Path.Join(receiptRoot, "seed.json"));
            var restore = ReadPhase(Path.Join(receiptRoot, "restore.json"));
            var canary = ReadPhase(Path.Join(receiptRoot, "canary.json"));
            var architecture = ReadArchitecture(
                Path.Join(receiptRoot, "architecture.json"));
            if (mustFind is null ||
                mustNot is null ||
                seed is null ||
                restore is null ||
                canary is null ||
                architecture is null)
            {
                failure = "evidence_receipt_missing_or_malformed";
                return false;
            }

            if (!BuildPairValid(buildPair, mustFind) ||
                !BuildPairValid(buildPair, mustNot) ||
                !BuildPairValid(buildPair, seed) ||
                !BuildPairValid(buildPair, restore) ||
                !BuildPairValid(buildPair, canary) ||
                !BuildPairValid(buildPair, architecture))
            {
                failure = "evidence_build_pair_invalid";
                return false;
            }

            var positiveFailure = PositiveFailure(
                    mustFind,
                    mustNot,
                    seed,
                    restore,
                    canary);
            if (positiveFailure is not null)
            {
                failure = positiveFailure;
                return false;
            }

            if (!ArchitectureValid(buildPair, architecture))
            {
                failure = "evidence_architecture_invalid";
                return false;
            }

            var requiredRows = requireReplacement
                ? negativeRows
                : negativeRows.Where(row => row.Id !=
                    "replacement-write-failed").ToArray();
            var negatives = new List<VerifierNegativeReceipt>();
            foreach (var row in requiredRows)
            {
                var receipt = ReadNegative(Path.Join(
                    receiptRoot,
                    "negative",
                    string.Concat(row.Id, ".json")));
                if (receipt is null ||
                    !BuildPairValid(buildPair, receipt) ||
                    !NegativeValid(row, receipt))
                {
                    failure = string.Concat(
                        "evidence_negative_invalid_",
                        row.Id);
                    return false;
                }

                negatives.Add(receipt);
            }

            var processInstances = new[]
                {
                    mustFind.ProcessInstanceSha256,
                    mustNot.ProcessInstanceSha256,
                    seed.ProcessInstanceSha256,
                    restore.ProcessInstanceSha256,
                    canary.ProcessInstanceSha256,
                    architecture.ProcessInstanceSha256,
                }
                .Concat(negatives.Select(item =>
                    item.ProcessInstanceSha256))
                .ToArray();
            if (processInstances.Any(value =>
                    !LiveAgentFreshProcessDomain.IsSha256(value)) ||
                processInstances.Distinct(StringComparer.Ordinal).Count() !=
                    processInstances.Length)
            {
                failure = "evidence_process_instance_invalid";
                return false;
            }

            var canaryOutcomes = canary.CanaryRoutes!;
            var observedCanaries = canaryRows.Select(row =>
            {
                var observed = row.Id == "prior"
                    ? restore.ExactReplayValidated &&
                        LiveAgentFreshProcessDomain.IsSha256(
                            restore.PriorFactSha256)
                    : canaryOutcomes.SingleOrDefault(item =>
                        StringComparer.Ordinal.Equals(
                            item.Class,
                            row.Id) &&
                        StringComparer.Ordinal.Equals(
                            item.ApprovedRoute,
                            row.PhaseOrRoute))?.Observed == true;
                return string.Join('\t', row.Id, row.PhaseOrRoute, observed);
            }).ToArray();
            if (observedCanaries.Any(line =>
                    line.EndsWith("False", StringComparison.Ordinal)))
            {
                failure = "evidence_canary_outcome_invalid";
                return false;
            }

            var negativeOutcomes = negativeRows.Select(row =>
            {
                var receipt = negatives.SingleOrDefault(item =>
                    StringComparer.Ordinal.Equals(item.Case, row.Id));
                if (receipt is null && !requireReplacement &&
                    row.Id == "replacement-write-failed")
                {
                    return string.Join(
                        '\t',
                        row.Id,
                        row.PhaseOrRoute,
                        row.StateExpectation,
                        row.StableCode,
                        "pending");
                }

                if (receipt is null)
                {
                    throw new InvalidOperationException(
                        "A required negative receipt was not loaded.");
                }

                return string.Join(
                    '\t',
                    row.Id,
                    row.PhaseOrRoute,
                    row.StateExpectation,
                    receipt.ExpectedCode,
                    receipt.ActualCode,
                    receipt.Passed);
            }).ToArray();
            var negativeDigest = MatrixSha256(
                negativeManifestBytes,
                negativeOutcomes);
            var canaryDigest = MatrixSha256(
                canaryManifestBytes,
                observedCanaries);
            var twoProcesses = !StringComparer.Ordinal.Equals(
                seed.ProcessInstanceSha256,
                restore.ProcessInstanceSha256);
            var restoredExact = restore.ExactReplayValidated &&
                restore.ReplayMutationMatrixValidated &&
                LiveAgentFreshProcessDomain.IsSha256(
                    seed.HistoricalMessagesSha256) &&
                StringComparer.Ordinal.Equals(
                    seed.HistoricalMessagesSha256,
                    restore.HistoricalMessagesSha256);
            var factPrivate = restoredExact &&
                LiveAgentFreshProcessDomain.IsSha256(
                    seed.PriorFactSha256) &&
                StringComparer.Ordinal.Equals(
                    seed.PriorFactSha256,
                    restore.PriorFactSha256);
            evidence = new VerifierAggregateEvidence(
                mustFind,
                mustNot,
                seed,
                restore,
                canary,
                negatives,
                architecture,
                negativeDigest,
                canaryDigest,
                twoProcesses,
                restoredExact,
                factPrivate,
                architecture.Passed);
            var valid = twoProcesses && restoredExact && factPrivate;
            failure = valid
                ? string.Empty
                : "evidence_continuation_invalid";
            return valid;
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or
            JsonException or
            InvalidOperationException or
            FormatException)
        {
            failure = string.Concat(
                "evidence_exception_",
                exception.GetType().Name);
            return false;
        }
    }

    internal static bool TryReadCanaryManifest(
        string? path,
        out IReadOnlyList<VerifierManifestRow> rows,
        out byte[] normalizedBytes)
    {
        rows = [];
        normalizedBytes = [];
        if (!TryReadLines(path, out var lines) ||
            lines.Length != ExpectedCanaries.Length + 1 ||
            lines[0] != "class\tapproved_route")
        {
            return false;
        }

        var parsed = lines.Skip(1).Select(line => line.Split('\t'))
            .ToArray();
        if (parsed.Any(parts => parts.Length != 2))
        {
            return false;
        }

        rows = parsed.Select(parts => new VerifierManifestRow(
            parts[0], parts[1], null, null)).ToArray();
        normalizedBytes = Encoding.UTF8.GetBytes(
            string.Concat(string.Join('\n', lines), "\n"));
        return rows.SequenceEqual(ExpectedCanaries);
    }

    private static bool TryReadNegativeManifest(
        string? path,
        out IReadOnlyList<VerifierManifestRow> rows,
        out byte[] normalizedBytes)
    {
        rows = [];
        normalizedBytes = [];
        if (!TryReadLines(path, out var lines) ||
            lines.Length < 2 ||
            lines[0] !=
                "case\tphase\tstate_expectation\tstable_code")
        {
            return false;
        }

        var parsed = lines.Skip(1).Select(line => line.Split('\t'))
            .ToArray();
        if (parsed.Any(parts => parts.Length != 4))
        {
            return false;
        }

        rows = parsed.Select(parts => new VerifierManifestRow(
            parts[0], parts[1], parts[2], parts[3])).ToArray();
        var expected = Enum.GetValues<VerifierScenario>()
            .Where(VerifierScenarioDomain.IsNegative)
            .Select(VerifierScenarioDomain.Negative)
            .Select(item => new VerifierManifestRow(
                item.Id,
                item.Phase,
                item.StateExpectation,
                item.StableCode))
            .Append(new VerifierManifestRow(
                "replacement-write-failed",
                "post_commit",
                "accepted_preserved",
                LiveAgentFreshProcessCodes.OutputFailed))
            .ToArray();
        normalizedBytes = Encoding.UTF8.GetBytes(
            string.Concat(string.Join('\n', lines), "\n"));
        return rows.SequenceEqual(expected) &&
            rows.Select(item => item.Id).Distinct(StringComparer.Ordinal)
                .Count() == rows.Count;
    }

    private static bool TryReadLines(
        string? path,
        out string[] lines)
    {
        lines = [];
        if (path is null || !File.Exists(path))
        {
            return false;
        }

        var text = File.ReadAllText(path)
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!text.EndsWith('\n'))
        {
            return false;
        }

        lines = text[..^1].Split('\n');
        return lines.All(line => line.Length > 0);
    }

    private static string? PositiveFailure(
        VerifierPhaseReceipt mustFind,
        VerifierPhaseReceipt mustNot,
        VerifierPhaseReceipt seed,
        VerifierPhaseReceipt restore,
        VerifierPhaseReceipt canary)
    {
        var values = new[] { mustFind, mustNot, seed, restore, canary };
        if (values.Any(receipt => receipt.Kind != PhaseKind ||
                receipt.Status != "passed" ||
                receipt.ProductCode != R3LiveAgentCodes.Completed ||
                !receipt.WireValid ||
                receipt.WireFailureCode is not null ||
                !receipt.CommitDelegatedOnce ||
                !receipt.HandoffReady ||
                !receipt.AcceptedTupleValidated ||
                !LiveAgentFreshProcessDomain.IsSha256(
                    receipt.InvocationIdentitySha256) ||
                !LiveAgentFreshProcessDomain.IsSha256(
                    receipt.LineageSha256) ||
                !LiveAgentFreshProcessDomain.IsSha256(
                    receipt.AcceptedSessionSha256) ||
                !LiveAgentFreshProcessDomain.IsSha256(
                    receipt.AcceptedEnvelopeSha256) ||
                !LiveAgentFreshProcessDomain.IsSha256(
                    receipt.TerminalSha256)))
        {
            return "evidence_positive_common_invalid";
        }

        if (!PhaseShape(mustFind, "MustFind", 0, "same_head", 3, 3) ||
            !QualityShape(mustFind.Quality, "must-find-read-diff", 1, 2))
        {
            return "evidence_must_find_invalid";
        }

        if (!PhaseShape(mustNot, "MustNotFind", 0, "same_head", 4, 4) ||
            !QualityShape(
                mustNot.Quality,
                "must-not-find-safe-line",
                0,
                3))
        {
            return "evidence_must_not_invalid";
        }

        if (!PhaseShape(seed, "ContinuationSeed", 0, "same_head", 2, 2) ||
            seed.Quality is not null ||
            !LiveAgentFreshProcessDomain.IsSha256(seed.SeedIdentitySha256) ||
            !LiveAgentFreshProcessDomain.IsSha256(
                seed.HistoricalMessagesSha256) ||
            seed.ExactReplayValidated)
        {
            return "evidence_seed_invalid";
        }

        if (!PhaseShape(
                restore,
                "ContinuationRestore",
                1,
                "verified_ahead",
                2,
                2) ||
            !QualityShape(
                restore.Quality,
                "continuation-prior-only",
                0,
                1) ||
            restore.SeedIdentitySha256 is not null ||
            !restore.ExactReplayValidated ||
            !restore.ReplayMutationMatrixValidated ||
            !LiveAgentFreshProcessDomain.IsSha256(
                restore.FirstRequestSha256))
        {
            return "evidence_restore_invalid";
        }

        if (!PhaseShape(canary, "CanaryRouting", 0, "same_head", 2, 2) ||
            !canary.CanaryRoutesValidated ||
            canary.CanaryRoutes is not { Count: 8 } ||
            !canary.CanaryRoutes.All(route => route.Observed) ||
            canary.CanaryRoutes.Select(route => route.Class)
                .Distinct(StringComparer.Ordinal).Count() != 8)
        {
            return "evidence_canary_invalid";
        }

        return null;
    }

    private static bool PhaseShape(
        VerifierPhaseReceipt receipt,
        string scenario,
        long generation,
        string transition,
        int providerRequests,
        int toolCalls) =>
        receipt.Scenario == scenario &&
        receipt.Generation == generation &&
        receipt.Transition == transition &&
        receipt.ModelCalls == providerRequests &&
        receipt.ProviderRequests == providerRequests &&
        receipt.ToolCalls == toolCalls;

    private static bool QualityShape(
        VerifierQualityProjection? quality,
        string caseId,
        int findings,
        int tools) => quality is
    {
        Status: "passed",
        Classification: "quality",
        Code: R3QualityCodes.Passed,
        TerminalPresent: true,
        ExpectedCaseBound: true,
    } &&
        quality.CaseId == caseId &&
        LiveAgentFreshProcessDomain.IsSha256(quality.CaseSha256) &&
        quality.FindingCount == findings &&
        quality.ToolCallCount == tools;

    private static bool NegativeValid(
        VerifierManifestRow row,
        VerifierNegativeReceipt receipt)
    {
        if (receipt.Kind != NegativeKind ||
            receipt.Case != row.Id ||
            receipt.Phase != row.PhaseOrRoute ||
            receipt.StateExpectation != row.StateExpectation ||
            receipt.ExpectedCode != row.StableCode ||
            receipt.ActualCode != row.StableCode ||
            !ReceiptBuildPairShapeValid(
                receipt.ExecutionKind,
                receipt.ExecutionArtifactSha256,
                receipt.ArchitectureAssemblySha256,
                receipt.BuildPairSha256) ||
            !receipt.Passed ||
            receipt.HandoffReady ||
            !LiveAgentFreshProcessDomain.IsSha256(
                receipt.StateBeforeSha256) ||
            !LiveAgentFreshProcessDomain.IsSha256(
                receipt.StateAfterSha256))
        {
            return false;
        }

        if (row.Id == "replacement-write-failed")
        {
            return receipt.AcceptedTruthPreserved &&
                receipt.AcceptedGeneration == 1 &&
                AcceptedIdentityMatches(receipt) &&
                receipt.ActivationCount == 1 &&
                receipt.ProviderRequests == 2 &&
                receipt.CommitDelegationCount == 1 &&
                receipt.StateBeforeSha256 != receipt.StateAfterSha256 &&
                LiveAgentFreshProcessDomain.IsSha256(
                    receipt.LineageBeforeSha256) &&
                LiveAgentFreshProcessDomain.IsSha256(
                    receipt.LineageAfterSha256) &&
                receipt.LineageBeforeSha256 != receipt.LineageAfterSha256 &&
                LiveAgentFreshProcessDomain.IsSha256(
                    receipt.ResultBeforeSha256) &&
                receipt.ResultBeforeSha256 == receipt.ResultAfterSha256 &&
                receipt.ResultPublicationAttempts == 1;
        }

        var scenario = Enum.GetValues<VerifierScenario>()
            .Where(VerifierScenarioDomain.IsNegative)
            .Single(value => VerifierScenarioDomain.Negative(value).Id == row.Id);
        var expectedActivation = scenario is
            VerifierScenario.OuterAuthorizationDenied or
            VerifierScenario.TransitionFromHeadInvalid or
            VerifierScenario.LineageTampered
                ? 0
                : 1;
        var expectedRequests = scenario switch
        {
            VerifierScenario.OuterAuthorizationDenied or
                VerifierScenario.InnerAuthorizationDenied or
                VerifierScenario.TransitionFromHeadInvalid or
                VerifierScenario.LineageTampered => 0,
            VerifierScenario.QualityFailedAfterCommit => 3,
            VerifierScenario.PublicResultCanary => 4,
            _ => 1,
        };
        var unchanged = receipt.StateBeforeSha256 ==
            receipt.StateAfterSha256 &&
            receipt.LineageBeforeSha256 == receipt.LineageAfterSha256;
        var stateValid = row.StateExpectation switch
        {
            "no_advance" or "prior_unchanged" => unchanged &&
                receipt.AcceptedGeneration is null &&
                receipt.AcceptedSessionSha256 is null &&
                receipt.AcceptedEnvelopeSha256 is null &&
                receipt.AcceptedLineageSha256 is null &&
                !receipt.AcceptedTruthPreserved,
            "accepted_preserved" => receipt.AcceptedTruthPreserved &&
                receipt.AcceptedGeneration == 0 &&
                AcceptedIdentityMatches(receipt) &&
                receipt.CommitDelegationCount == 1 &&
                receipt.StateBeforeSha256 != receipt.StateAfterSha256 &&
                LiveAgentFreshProcessDomain.IsSha256(
                    receipt.LineageAfterSha256),
            _ => false,
        };
        return stateValid &&
            receipt.ActivationCount == expectedActivation &&
            receipt.ProviderRequests == expectedRequests &&
            receipt.ResultBeforeSha256 is null &&
            receipt.ResultAfterSha256 is null &&
            receipt.ResultPublicationAttempts == 0;
    }

    internal static bool AcceptedIdentityMatches(
        VerifierNegativeReceipt receipt) =>
        receipt.AcceptedGeneration is >= 0 &&
        receipt.AcceptedGeneration ==
            receipt.CanonicalLineageGeneration &&
        LiveAgentFreshProcessDomain.IsSha256(
            receipt.AcceptedSessionSha256) &&
        StringComparer.Ordinal.Equals(
            receipt.AcceptedSessionSha256,
            receipt.CanonicalLineageSessionSha256) &&
        LiveAgentFreshProcessDomain.IsSha256(
            receipt.AcceptedEnvelopeSha256) &&
        StringComparer.Ordinal.Equals(
            receipt.AcceptedEnvelopeSha256,
            receipt.CanonicalLineageEnvelopeSha256) &&
        LiveAgentFreshProcessDomain.IsSha256(
            receipt.AcceptedLineageSha256) &&
        StringComparer.Ordinal.Equals(
            receipt.AcceptedLineageSha256,
            receipt.CanonicalLineageSha256);

    internal static bool NegativeValidForTesting(
        VerifierNegativeReceipt receipt) => NegativeValid(
        new VerifierManifestRow(
            "quality-failed-after-commit",
            "post_commit",
            "accepted_preserved",
            LiveAgentFreshProcessCodes.TransportProofFailed),
        receipt);

    internal static bool ReplacementNegativeValidForTesting(
        VerifierNegativeReceipt receipt) => NegativeValid(
        new VerifierManifestRow(
            "replacement-write-failed",
            "post_commit",
            "accepted_preserved",
            LiveAgentFreshProcessCodes.OutputFailed),
        receipt);

    private static bool ArchitectureValid(
        VerifierBuildPair buildPair,
        VerifierArchitectureReceipt receipt) =>
        receipt.Kind == ArchitectureKind &&
        receipt.Status == "passed" &&
        LiveAgentFreshProcessDomain.IsSha256(receipt.AssemblySha256) &&
        receipt.AssemblySha256 == buildPair.ArchitectureAssemblySha256 &&
        receipt.ForbiddenReferencesAbsent &&
        receipt.RealTransportCreationCalls == 2 &&
        receipt.TransportFactoryTypes == 1 &&
        receipt.ProfileTypes == 1 &&
        receipt.Passed;

    private static bool BuildPairValid(
        VerifierBuildPair expected,
        VerifierPhaseReceipt receipt) => BuildPairValid(
        expected,
        receipt.ExecutionKind,
        receipt.ExecutionArtifactSha256,
        receipt.ArchitectureAssemblySha256,
        receipt.BuildPairSha256);

    private static bool BuildPairValid(
        VerifierBuildPair expected,
        VerifierNegativeReceipt receipt) => BuildPairValid(
        expected,
        receipt.ExecutionKind,
        receipt.ExecutionArtifactSha256,
        receipt.ArchitectureAssemblySha256,
        receipt.BuildPairSha256);

    private static bool BuildPairValid(
        VerifierBuildPair expected,
        VerifierArchitectureReceipt receipt) => BuildPairValid(
        expected,
        receipt.ExecutionKind,
        receipt.ExecutionArtifactSha256,
        receipt.ArchitectureAssemblySha256,
        receipt.BuildPairSha256);

    private static bool BuildPairValid(
        VerifierBuildPair expected,
        string executionKind,
        string executionArtifactSha256,
        string architectureAssemblySha256,
        string buildPairSha256) =>
        ReceiptBuildPairShapeValid(
            executionKind,
            executionArtifactSha256,
            architectureAssemblySha256,
            buildPairSha256) &&
        executionKind == expected.ExecutionKind &&
        executionArtifactSha256 == expected.ExecutionArtifactSha256 &&
        architectureAssemblySha256 == expected.ArchitectureAssemblySha256 &&
        buildPairSha256 == expected.BuildPairSha256;

    private static bool ReceiptBuildPairShapeValid(
        string executionKind,
        string executionArtifactSha256,
        string architectureAssemblySha256,
        string buildPairSha256) =>
        executionKind is (
            VerifierExecutionKinds.Framework or
            VerifierExecutionKinds.NativeAot) &&
        LiveAgentFreshProcessDomain.IsSha256(executionArtifactSha256) &&
        LiveAgentFreshProcessDomain.IsSha256(architectureAssemblySha256) &&
        LiveAgentFreshProcessDomain.IsSha256(buildPairSha256) &&
        buildPairSha256 == VerifierBuildPairDomain.ComputeSha256(
            executionKind,
            executionArtifactSha256,
            architectureAssemblySha256);

    internal static bool BuildPairValidForTesting(
        VerifierBuildPair expected,
        VerifierNegativeReceipt receipt) => BuildPairValid(expected, receipt);

    private static VerifierPhaseReceipt? ReadPhase(string path)
    {
        using var document = OpenExact(path,
        [
            "kind", "execution_kind", "execution_artifact_sha256",
            "architecture_assembly_sha256", "build_pair_sha256",
            "scenario", "status", "product_code", "generation",
            "transition", "model_calls", "tool_calls", "provider_requests",
            "wire_valid", "wire_failure_code", "commit_delegated_once",
            "handoff_ready", "first_request_sha256", "terminal_sha256",
            "prior_fact_sha256", "invocation_identity_sha256",
            "seed_identity_sha256", "lineage_sha256",
            "accepted_session_sha256", "accepted_envelope_sha256",
            "accepted_tuple_validated",
            "historical_messages_sha256", "exact_replay_validated",
            "replay_mutation_matrix_validated",
            "process_instance_sha256", "canary_routes_validated",
            "canary_routes", "quality",
        ]);
        if (document is null)
        {
            return null;
        }

        var root = document.RootElement;
        var quality = ReadQuality(root.GetProperty("quality"));
        if (root.GetProperty("quality").ValueKind != JsonValueKind.Null &&
            quality is null)
        {
            return null;
        }

        IReadOnlyList<VerifierCanaryRouteOutcome>? routes = null;
        var routeValue = root.GetProperty("canary_routes");
        if (routeValue.ValueKind == JsonValueKind.Array)
        {
            var values = new List<VerifierCanaryRouteOutcome>();
            foreach (var item in routeValue.EnumerateArray())
            {
                if (!ExactProperties(
                        item,
                        ["class", "approved_route", "observed"]))
                {
                    return null;
                }

                values.Add(new VerifierCanaryRouteOutcome(
                    RequiredString(item, "class"),
                    RequiredString(item, "approved_route"),
                    item.GetProperty("observed").GetBoolean()));
            }

            routes = values;
        }
        else if (routeValue.ValueKind != JsonValueKind.Null)
        {
            return null;
        }

        return new VerifierPhaseReceipt(
            RequiredString(root, "scenario"),
            RequiredString(root, "status"),
            RequiredString(root, "product_code"),
            NullableInt64(root, "generation"),
            RequiredString(root, "transition"),
            root.GetProperty("model_calls").GetInt32(),
            root.GetProperty("tool_calls").GetInt32(),
            root.GetProperty("provider_requests").GetInt32(),
            root.GetProperty("wire_valid").GetBoolean(),
            NullableString(root, "wire_failure_code"),
            root.GetProperty("commit_delegated_once").GetBoolean(),
            root.GetProperty("handoff_ready").GetBoolean(),
            NullableString(root, "first_request_sha256"),
            NullableString(root, "terminal_sha256"),
            NullableString(root, "prior_fact_sha256"),
            RequiredString(root, "invocation_identity_sha256"),
            NullableString(root, "seed_identity_sha256"),
            NullableString(root, "lineage_sha256"),
            NullableString(root, "accepted_session_sha256"),
            NullableString(root, "accepted_envelope_sha256"),
            root.GetProperty("accepted_tuple_validated").GetBoolean(),
            NullableString(root, "historical_messages_sha256"),
            root.GetProperty("exact_replay_validated").GetBoolean(),
            root.GetProperty("replay_mutation_matrix_validated").GetBoolean(),
            quality,
            RequiredString(root, "kind"),
            RequiredString(root, "process_instance_sha256"),
            root.GetProperty("canary_routes_validated").GetBoolean(),
            routes,
            RequiredString(root, "execution_kind"),
            RequiredString(root, "execution_artifact_sha256"),
            RequiredString(root, "architecture_assembly_sha256"),
            RequiredString(root, "build_pair_sha256"));
    }

    private static VerifierQualityProjection? ReadQuality(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object ||
            !ExactProperties(value,
            [
                "case_id", "case_sha256", "status", "classification",
                "code", "finding_count", "tool_call_count",
                "terminal_present", "expected_case_bound",
            ]))
        {
            return null;
        }

        return new VerifierQualityProjection(
            RequiredString(value, "case_id"),
            RequiredString(value, "case_sha256"),
            RequiredString(value, "status"),
            RequiredString(value, "classification"),
            RequiredString(value, "code"),
            value.GetProperty("finding_count").GetInt32(),
            value.GetProperty("tool_call_count").GetInt32(),
            value.GetProperty("terminal_present").GetBoolean(),
            value.GetProperty("expected_case_bound").GetBoolean());
    }

    private static VerifierNegativeReceipt? ReadNegative(string path)
    {
        using var document = OpenExact(path,
        [
            "kind", "execution_kind", "execution_artifact_sha256",
            "architecture_assembly_sha256", "build_pair_sha256",
            "case", "phase", "state_expectation", "expected_code",
            "actual_code", "state_before_sha256", "state_after_sha256",
            "lineage_before_sha256", "lineage_after_sha256",
            "accepted_generation", "accepted_session_sha256",
            "accepted_envelope_sha256", "accepted_lineage_sha256",
            "canonical_lineage_generation",
            "canonical_lineage_session_sha256",
            "canonical_lineage_envelope_sha256",
            "canonical_lineage_sha256", "activation_count", "provider_requests",
            "commit_delegation_count", "handoff_ready",
            "accepted_truth_preserved", "passed", "process_instance_sha256",
            "result_before_sha256", "result_after_sha256",
            "result_publication_attempts",
        ]);
        if (document is null)
        {
            return null;
        }

        var root = document.RootElement;
        return new VerifierNegativeReceipt(
            RequiredString(root, "kind"),
            RequiredString(root, "case"),
            RequiredString(root, "phase"),
            RequiredString(root, "state_expectation"),
            RequiredString(root, "expected_code"),
            NullableString(root, "actual_code"),
            RequiredString(root, "state_before_sha256"),
            RequiredString(root, "state_after_sha256"),
            NullableString(root, "lineage_before_sha256"),
            NullableString(root, "lineage_after_sha256"),
            NullableInt64(root, "accepted_generation"),
            NullableString(root, "accepted_session_sha256"),
            NullableString(root, "accepted_envelope_sha256"),
            NullableString(root, "accepted_lineage_sha256"),
            NullableInt64(root, "canonical_lineage_generation"),
            NullableString(root, "canonical_lineage_session_sha256"),
            NullableString(root, "canonical_lineage_envelope_sha256"),
            NullableString(root, "canonical_lineage_sha256"),
            root.GetProperty("activation_count").GetInt32(),
            root.GetProperty("provider_requests").GetInt32(),
            root.GetProperty("commit_delegation_count").GetInt32(),
            root.GetProperty("handoff_ready").GetBoolean(),
            root.GetProperty("accepted_truth_preserved").GetBoolean(),
            root.GetProperty("passed").GetBoolean(),
            RequiredString(root, "process_instance_sha256"),
            NullableString(root, "result_before_sha256"),
            NullableString(root, "result_after_sha256"),
            root.GetProperty("result_publication_attempts").GetInt32(),
            RequiredString(root, "execution_kind"),
            RequiredString(root, "execution_artifact_sha256"),
            RequiredString(root, "architecture_assembly_sha256"),
            RequiredString(root, "build_pair_sha256"));
    }

    private static VerifierArchitectureReceipt? ReadArchitecture(string path)
    {
        using var document = OpenExact(path,
        [
            "kind", "execution_kind", "execution_artifact_sha256",
            "architecture_assembly_sha256", "build_pair_sha256",
            "status", "assembly_sha256",
            "forbidden_references_absent", "real_transport_creation_calls",
            "transport_factory_types", "profile_types", "passed",
            "process_instance_sha256",
        ]);
        if (document is null)
        {
            return null;
        }

        var root = document.RootElement;
        return new VerifierArchitectureReceipt(
            RequiredString(root, "kind"),
            RequiredString(root, "execution_kind"),
            RequiredString(root, "execution_artifact_sha256"),
            RequiredString(root, "architecture_assembly_sha256"),
            RequiredString(root, "build_pair_sha256"),
            RequiredString(root, "status"),
            RequiredString(root, "assembly_sha256"),
            root.GetProperty("forbidden_references_absent").GetBoolean(),
            root.GetProperty("real_transport_creation_calls").GetInt32(),
            root.GetProperty("transport_factory_types").GetInt32(),
            root.GetProperty("profile_types").GetInt32(),
            root.GetProperty("passed").GetBoolean(),
            RequiredString(root, "process_instance_sha256"));
    }

    private static JsonDocument? OpenExact(
        string path,
        IReadOnlyList<string> properties)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var document = JsonDocument.Parse(File.ReadAllBytes(path));
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !ExactProperties(document.RootElement, properties))
        {
            document.Dispose();
            return null;
        }

        return document;
    }

    private static bool ExactProperties(
        JsonElement value,
        IReadOnlyList<string> expected) => value.EnumerateObject()
        .Select(property => property.Name)
        .SequenceEqual(expected, StringComparer.Ordinal);

    private static string RequiredString(JsonElement root, string name) =>
        root.GetProperty(name).GetString() ??
        throw new JsonException(string.Concat(name, " must be a string."));

    private static string? NullableString(JsonElement root, string name) =>
        root.GetProperty(name).ValueKind == JsonValueKind.Null
            ? null
            : RequiredString(root, name);

    private static long? NullableInt64(JsonElement root, string name) =>
        root.GetProperty(name).ValueKind == JsonValueKind.Null
            ? null
            : root.GetProperty(name).GetInt64();

    private static string MatrixSha256(
        ReadOnlySpan<byte> manifest,
        IReadOnlyList<string> outcomes)
    {
        using var stream = new MemoryStream();
        stream.Write(manifest);
        stream.Write("--observed--\n"u8);
        foreach (var outcome in outcomes)
        {
            stream.Write(Encoding.UTF8.GetBytes(outcome));
            stream.WriteByte((byte)'\n');
        }

        return LiveAgentFreshProcessDomain.RawSha256(stream.ToArray());
    }
}

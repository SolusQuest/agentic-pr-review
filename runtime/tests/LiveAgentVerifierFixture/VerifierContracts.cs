using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Quality;

namespace AgenticPrReview.Runtime.LiveAgentVerifierFixture;

internal enum VerifierScenario
{
    MustFind,
    MustNotFind,
    ContinuationSeed,
    ContinuationRestore,
    CanaryRouting,
    OuterAuthorizationDenied,
    InnerAuthorizationDenied,
    ProviderHttpFailure,
    ProviderMalformedResponse,
    ToolArgumentsInvalid,
    TerminalUngrounded,
    TransitionFromHeadInvalid,
    LineageTampered,
    QualityFailedAfterCommit,
    PublicResultCanary,
}

internal static class VerifierCodes
{
    internal const string ArgumentsInvalid = "APR_R3_LIVE_ARGUMENTS_INVALID";
    internal const string FixtureInvalid = "APR_R3_LIVE_FIXTURE_INVALID";
    internal const string PhaseFailed = "APR_R3_LIVE_PHASE_FAILED";
    internal const string PhaseOk = "APR_R3_LIVE_PHASE_OK";
    internal const string AggregateOk = "APR_R3_LIVE_DETERMINISTIC_OK";
}

internal sealed record VerifierCommand(
    string Verb,
    VerifierScenario? Scenario,
    string Root,
    string Corpus,
    string Output,
    string? ExpectedLineageSha256,
    string? ExpectedHistorySha256,
    string? NegativeManifest,
    string? CanaryManifest,
    string? ReplacementTarget);

internal sealed record VerifierCanaryRouteOutcome(
    string Class,
    string ApprovedRoute,
    bool Observed);

internal sealed record VerifierQualityProjection(
    string CaseId,
    string CaseSha256,
    string Status,
    string Classification,
    string Code,
    int FindingCount,
    int ToolCallCount,
    bool TerminalPresent,
    bool ExpectedCaseBound);

internal sealed record VerifierPhaseReceipt(
    string Scenario,
    string Status,
    string ProductCode,
    long? Generation,
    string Transition,
    int ModelCalls,
    int ToolCalls,
    int ProviderRequests,
    bool WireValid,
    string? WireFailureCode,
    bool CommitDelegatedOnce,
    bool HandoffReady,
    string? FirstRequestSha256,
    string? TerminalSha256,
    string? PriorFactSha256,
    string InvocationIdentitySha256,
    string? SeedIdentitySha256,
    string? LineageSha256,
    string? HistoricalMessagesSha256,
    bool ExactReplayValidated,
    bool ReplayMutationMatrixValidated,
    VerifierQualityProjection? Quality,
    string Kind = "apr-r3-live-agent-phase-receipt-v1",
    string ProcessInstanceSha256 = "",
    bool CanaryRoutesValidated = false,
    IReadOnlyList<VerifierCanaryRouteOutcome>? CanaryRoutes = null);

internal sealed record VerifierNegativeReceipt(
    string Kind,
    string Case,
    string Phase,
    string StateExpectation,
    string ExpectedCode,
    string? ActualCode,
    string StateBeforeSha256,
    string StateAfterSha256,
    string? LineageBeforeSha256,
    string? LineageAfterSha256,
    long? AcceptedGeneration,
    int ActivationCount,
    int ProviderRequests,
    int CommitDelegationCount,
    bool HandoffReady,
    bool AcceptedTruthPreserved,
    bool Passed,
    string ProcessInstanceSha256);

internal static class VerifierArguments
{
    internal static bool TryParse(
        IReadOnlyList<string> args,
        out VerifierCommand? command)
    {
        command = null;
        if (args.Count < 1 || args.Count % 2 == 0)
        {
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Count; index += 2)
        {
            if (index + 1 >= args.Count ||
                !args[index].StartsWith("--", StringComparison.Ordinal) ||
                !values.TryAdd(args[index], args[index + 1]))
            {
                return false;
            }
        }

        if (!values.TryGetValue("--root", out var root) ||
            !values.TryGetValue("--corpus", out var corpus) ||
            !values.TryGetValue("--output", out var output) ||
            !Path.IsPathFullyQualified(root) ||
            !Path.IsPathFullyQualified(corpus) ||
            !Path.IsPathFullyQualified(output) ||
            !File.Exists(corpus))
        {
            return false;
        }

        VerifierScenario? scenario = args[0] switch
        {
            "must-find" => VerifierScenario.MustFind,
            "must-not-find" => VerifierScenario.MustNotFind,
            "continuation-seed" => VerifierScenario.ContinuationSeed,
            "continuation-restore" => VerifierScenario.ContinuationRestore,
            "canary-routing" => VerifierScenario.CanaryRouting,
            "negative-outer-authorization-denied" =>
                VerifierScenario.OuterAuthorizationDenied,
            "negative-inner-authorization-denied" =>
                VerifierScenario.InnerAuthorizationDenied,
            "negative-provider-http-failure" =>
                VerifierScenario.ProviderHttpFailure,
            "negative-provider-malformed-response" =>
                VerifierScenario.ProviderMalformedResponse,
            "negative-tool-arguments-invalid" =>
                VerifierScenario.ToolArgumentsInvalid,
            "negative-terminal-ungrounded" =>
                VerifierScenario.TerminalUngrounded,
            "negative-transition-from-head-invalid" =>
                VerifierScenario.TransitionFromHeadInvalid,
            "negative-lineage-authority-tampered" =>
                VerifierScenario.LineageTampered,
            "negative-quality-failed-after-commit" =>
                VerifierScenario.QualityFailedAfterCommit,
            "negative-public-result-canary" =>
                VerifierScenario.PublicResultCanary,
            "aggregate" => null,
            _ => (VerifierScenario?)null,
        };
        if (scenario is null && args[0] is not (
                "aggregate" or
                "architecture" or
                "negative-replacement-write-failed") ||
            values.Keys.Any(key => key is not (
                "--root" or
                "--corpus" or
                "--output" or
                "--expected-lineage-sha256" or
                "--expected-history-sha256" or
                "--negative-manifest" or
                "--canary-manifest" or
                "--replacement-target")))
        {
            return false;
        }

        values.TryGetValue(
            "--expected-lineage-sha256",
            out var expectedLineageSha256);
        if (expectedLineageSha256 is not null &&
            !LiveAgentFreshProcessDomain.IsSha256(expectedLineageSha256))
        {
            return false;
        }

        values.TryGetValue(
            "--expected-history-sha256",
            out var expectedHistorySha256);
        if (expectedHistorySha256 is not null &&
            !LiveAgentFreshProcessDomain.IsSha256(expectedHistorySha256))
        {
            return false;
        }

        values.TryGetValue("--negative-manifest", out var negativeManifest);
        values.TryGetValue("--canary-manifest", out var canaryManifest);
        values.TryGetValue("--replacement-target", out var replacementTarget);
        if (negativeManifest is not null)
        {
            negativeManifest = Path.GetFullPath(negativeManifest);
        }
        if (canaryManifest is not null)
        {
            canaryManifest = Path.GetFullPath(canaryManifest);
        }
        if (replacementTarget is not null)
        {
            replacementTarget = Path.GetFullPath(replacementTarget);
        }
        if (negativeManifest is not null && !File.Exists(negativeManifest) ||
            canaryManifest is not null && !File.Exists(canaryManifest))
        {
            return false;
        }

        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        corpus = Path.GetFullPath(corpus);
        output = Path.GetFullPath(output);
        if (!IsDescendant(root, output) ||
            replacementTarget is not null &&
                !IsDescendant(root, replacementTarget))
        {
            return false;
        }

        command = new VerifierCommand(
            args[0],
            scenario,
            root,
            corpus,
            output,
            expectedLineageSha256,
            expectedHistorySha256,
            negativeManifest,
            canaryManifest,
            replacementTarget);
        return true;
    }

    private static bool IsDescendant(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != "." &&
            relative != ".." &&
            !relative.StartsWith(
                string.Concat("..", Path.DirectorySeparatorChar),
                StringComparison.Ordinal) &&
            !Path.IsPathFullyQualified(relative);
    }
}

internal static class VerifierReceiptCodec
{
    internal static byte[] Write(VerifierPhaseReceipt receipt)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", receipt.Kind);
            writer.WriteString("scenario", receipt.Scenario);
            writer.WriteString("status", receipt.Status);
            writer.WriteString("product_code", receipt.ProductCode);
            if (receipt.Generation is { } generation)
            {
                writer.WriteNumber("generation", generation);
            }
            else
            {
                writer.WriteNull("generation");
            }
            writer.WriteString("transition", receipt.Transition);
            writer.WriteNumber("model_calls", receipt.ModelCalls);
            writer.WriteNumber("tool_calls", receipt.ToolCalls);
            writer.WriteNumber("provider_requests", receipt.ProviderRequests);
            writer.WriteBoolean("wire_valid", receipt.WireValid);
            WriteNullable(writer, "wire_failure_code", receipt.WireFailureCode);
            writer.WriteBoolean(
                "commit_delegated_once",
                receipt.CommitDelegatedOnce);
            writer.WriteBoolean("handoff_ready", receipt.HandoffReady);
            WriteNullable(writer, "first_request_sha256", receipt.FirstRequestSha256);
            WriteNullable(writer, "terminal_sha256", receipt.TerminalSha256);
            WriteNullable(writer, "prior_fact_sha256", receipt.PriorFactSha256);
            writer.WriteString(
                "invocation_identity_sha256",
                receipt.InvocationIdentitySha256);
            WriteNullable(writer, "seed_identity_sha256", receipt.SeedIdentitySha256);
            WriteNullable(writer, "lineage_sha256", receipt.LineageSha256);
            WriteNullable(
                writer,
                "historical_messages_sha256",
                receipt.HistoricalMessagesSha256);
            writer.WriteBoolean(
                "exact_replay_validated",
                receipt.ExactReplayValidated);
            writer.WriteBoolean(
                "replay_mutation_matrix_validated",
                receipt.ReplayMutationMatrixValidated);
            writer.WriteString(
                "process_instance_sha256",
                receipt.ProcessInstanceSha256);
            writer.WriteBoolean(
                "canary_routes_validated",
                receipt.CanaryRoutesValidated);
            writer.WritePropertyName("canary_routes");
            if (receipt.CanaryRoutes is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStartArray();
                foreach (var route in receipt.CanaryRoutes)
                {
                    writer.WriteStartObject();
                    writer.WriteString("class", route.Class);
                    writer.WriteString("approved_route", route.ApprovedRoute);
                    writer.WriteBoolean("observed", route.Observed);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WritePropertyName("quality");
            if (receipt.Quality is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                var quality = receipt.Quality;
                writer.WriteStartObject();
                writer.WriteString("case_id", quality.CaseId);
                writer.WriteString("case_sha256", quality.CaseSha256);
                writer.WriteString("status", quality.Status);
                writer.WriteString("classification", quality.Classification);
                writer.WriteString("code", quality.Code);
                writer.WriteNumber("finding_count", quality.FindingCount);
                writer.WriteNumber("tool_call_count", quality.ToolCallCount);
                writer.WriteBoolean("terminal_present", quality.TerminalPresent);
                writer.WriteBoolean("expected_case_bound", quality.ExpectedCaseBound);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    internal static VerifierQualityProjection Project(
        R3QualityCase testCase,
        R3QualityOutcome outcome) => new(
        outcome.CaseId,
        outcome.CaseSha256,
        outcome.Status,
        outcome.Classification,
        outcome.Code,
        outcome.FindingCount,
        outcome.ToolCallCount,
        outcome.TerminalSha256 is not null,
        StringComparer.Ordinal.Equals(outcome.CaseId, testCase.Id) &&
            StringComparer.Ordinal.Equals(
                outcome.CaseSha256,
                testCase.CaseSha256));

    internal static byte[] Write(VerifierNegativeReceipt receipt)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", receipt.Kind);
            writer.WriteString("case", receipt.Case);
            writer.WriteString("phase", receipt.Phase);
            writer.WriteString(
                "state_expectation",
                receipt.StateExpectation);
            writer.WriteString("expected_code", receipt.ExpectedCode);
            WriteNullable(writer, "actual_code", receipt.ActualCode);
            writer.WriteString(
                "state_before_sha256",
                receipt.StateBeforeSha256);
            writer.WriteString(
                "state_after_sha256",
                receipt.StateAfterSha256);
            WriteNullable(
                writer,
                "lineage_before_sha256",
                receipt.LineageBeforeSha256);
            WriteNullable(
                writer,
                "lineage_after_sha256",
                receipt.LineageAfterSha256);
            if (receipt.AcceptedGeneration is { } generation)
            {
                writer.WriteNumber("accepted_generation", generation);
            }
            else
            {
                writer.WriteNull("accepted_generation");
            }
            writer.WriteNumber("activation_count", receipt.ActivationCount);
            writer.WriteNumber("provider_requests", receipt.ProviderRequests);
            writer.WriteNumber(
                "commit_delegation_count",
                receipt.CommitDelegationCount);
            writer.WriteBoolean("handoff_ready", receipt.HandoffReady);
            writer.WriteBoolean(
                "accepted_truth_preserved",
                receipt.AcceptedTruthPreserved);
            writer.WriteBoolean("passed", receipt.Passed);
            writer.WriteString(
                "process_instance_sha256",
                receipt.ProcessInstanceSha256);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    internal static byte[] Write(VerifierArchitectureReceipt receipt)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", receipt.Kind);
            writer.WriteString("status", receipt.Status);
            writer.WriteString("assembly_sha256", receipt.AssemblySha256);
            writer.WriteBoolean(
                "forbidden_references_absent",
                receipt.ForbiddenReferencesAbsent);
            writer.WriteNumber(
                "real_transport_creation_calls",
                receipt.RealTransportCreationCalls);
            writer.WriteNumber(
                "transport_factory_types",
                receipt.TransportFactoryTypes);
            writer.WriteNumber("profile_types", receipt.ProfileTypes);
            writer.WriteBoolean("passed", receipt.Passed);
            writer.WriteString(
                "process_instance_sha256",
                receipt.ProcessInstanceSha256);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static void WriteNullable(
        Utf8JsonWriter writer,
        string name,
        string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }
}

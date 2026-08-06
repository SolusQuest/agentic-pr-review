using System.Text;
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

internal static class VerifierExecutionKinds
{
    internal const string Framework = "framework";
    internal const string NativeAot = "native-aot";
}

internal sealed record VerifierBuildPair(
    string ExecutionKind,
    string ExecutionArtifactSha256,
    string ArchitectureAssemblySha256,
    string BuildPairSha256);

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
    string? ReplacementTarget,
    string ExecutionKind,
    string ExecutionArtifact,
    string BuildPairManifest,
    string? ArchitectureAssembly);

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
    string? AcceptedSessionSha256,
    string? AcceptedEnvelopeSha256,
    bool AcceptedTupleValidated,
    string? HistoricalMessagesSha256,
    bool ExactReplayValidated,
    bool ReplayMutationMatrixValidated,
    VerifierQualityProjection? Quality,
    string Kind = "apr-r3-live-agent-phase-receipt-v1",
    string ProcessInstanceSha256 = "",
    bool CanaryRoutesValidated = false,
    IReadOnlyList<VerifierCanaryRouteOutcome>? CanaryRoutes = null,
    string ExecutionKind = "",
    string ExecutionArtifactSha256 = "",
    string ArchitectureAssemblySha256 = "",
    string BuildPairSha256 = "");

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
    string? AcceptedSessionSha256,
    string? AcceptedEnvelopeSha256,
    string? AcceptedLineageSha256,
    long? CanonicalLineageGeneration,
    string? CanonicalLineageSessionSha256,
    string? CanonicalLineageEnvelopeSha256,
    string? CanonicalLineageSha256,
    int ActivationCount,
    int ProviderRequests,
    int CommitDelegationCount,
    bool HandoffReady,
    bool AcceptedTruthPreserved,
    bool Passed,
    string ProcessInstanceSha256,
    string? ResultBeforeSha256 = null,
    string? ResultAfterSha256 = null,
    int ResultPublicationAttempts = 0,
    string ExecutionKind = "",
    string ExecutionArtifactSha256 = "",
    string ArchitectureAssemblySha256 = "",
    string BuildPairSha256 = "");

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
            !values.TryGetValue("--execution-kind", out var executionKind) ||
            !values.TryGetValue(
                "--execution-artifact",
                out var executionArtifact) ||
            !values.TryGetValue(
                "--build-pair-manifest",
                out var buildPairManifest) ||
            !Path.IsPathFullyQualified(root) ||
            !Path.IsPathFullyQualified(corpus) ||
            !Path.IsPathFullyQualified(output) ||
            !Path.IsPathFullyQualified(executionArtifact) ||
            !Path.IsPathFullyQualified(buildPairManifest) ||
            executionKind is not (
                VerifierExecutionKinds.Framework or
                VerifierExecutionKinds.NativeAot) ||
            !File.Exists(corpus) ||
            !File.Exists(executionArtifact) ||
            !File.Exists(buildPairManifest))
        {
            return false;
        }

        VerifierScenario? scenario = args[0] switch
        {
            "must-find" => VerifierScenario.MustFind,
            "must-not-find" => VerifierScenario.MustNotFind,
            "continuation-seed" => VerifierScenario.ContinuationSeed,
            "continuation-restore" => VerifierScenario.ContinuationRestore,
            "trusted-must-find" => VerifierScenario.MustFind,
            "trusted-must-not-find" => VerifierScenario.MustNotFind,
            "trusted-continuation-seed" =>
                VerifierScenario.ContinuationSeed,
            "trusted-continuation-restore" =>
                VerifierScenario.ContinuationRestore,
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
                "negative-replacement-write-failed" or
                "live-supervise") ||
            values.Keys.Any(key => key is not (
                "--root" or
                "--corpus" or
                "--output" or
                "--expected-lineage-sha256" or
                "--expected-history-sha256" or
                "--negative-manifest" or
                "--canary-manifest" or
                "--replacement-target" or
                "--execution-kind" or
                "--execution-artifact" or
                "--build-pair-manifest" or
                "--architecture-assembly")))
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
        values.TryGetValue(
            "--architecture-assembly",
            out var architectureAssembly);
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
        if (architectureAssembly is not null)
        {
            architectureAssembly = Path.GetFullPath(architectureAssembly);
        }
        if (negativeManifest is not null && !File.Exists(negativeManifest) ||
            canaryManifest is not null && !File.Exists(canaryManifest) ||
            args[0] == "architecture" &&
                (architectureAssembly is null ||
                    !File.Exists(architectureAssembly)) ||
            args[0] != "architecture" && architectureAssembly is not null)
        {
            return false;
        }

        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        corpus = Path.GetFullPath(corpus);
        output = Path.GetFullPath(output);
        executionArtifact = Path.GetFullPath(executionArtifact);
        buildPairManifest = Path.GetFullPath(buildPairManifest);
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
            replacementTarget,
            executionKind,
            executionArtifact,
            buildPairManifest,
            architectureAssembly);
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

internal static class VerifierBuildPairDomain
{
    internal const string Kind = "apr-r3-live-agent-build-pair-v1";
    private const int MaximumManifestBytes = 4 * 1024;
    private const int MaximumExecutionArtifactBytes = 256 * 1024 * 1024;

    internal static bool TryAdmit(
        VerifierCommand command,
        out VerifierBuildPair? buildPair)
    {
        buildPair = null;
        try
        {
            var manifestInfo = new FileInfo(command.BuildPairManifest);
            if (!manifestInfo.Exists ||
                manifestInfo.Length is <= 0 or > MaximumManifestBytes)
            {
                return false;
            }

            using var document = JsonDocument.Parse(
                File.ReadAllBytes(command.BuildPairManifest));
            var root = document.RootElement;
            var properties = root.ValueKind == JsonValueKind.Object
                ? root.EnumerateObject().Select(property => property.Name)
                : [];
            if (!properties.SequenceEqual(
                [
                    "kind",
                    "execution_kind",
                    "execution_artifact_sha256",
                    "architecture_assembly_sha256",
                    "build_pair_sha256",
                ],
                StringComparer.Ordinal))
            {
                return false;
            }

            var kind = RequiredString(root, "kind");
            var executionKind = RequiredString(root, "execution_kind");
            var executionSha256 = RequiredString(
                root,
                "execution_artifact_sha256");
            var architectureSha256 = RequiredString(
                root,
                "architecture_assembly_sha256");
            var pairSha256 = RequiredString(root, "build_pair_sha256");
            if (kind != Kind ||
                executionKind != command.ExecutionKind ||
                !LiveAgentFreshProcessDomain.IsSha256(executionSha256) ||
                !LiveAgentFreshProcessDomain.IsSha256(architectureSha256) ||
                !LiveAgentFreshProcessDomain.IsSha256(pairSha256) ||
                pairSha256 != ComputeSha256(
                    executionKind,
                    executionSha256,
                    architectureSha256))
            {
                return false;
            }

            var artifactInfo = new FileInfo(command.ExecutionArtifact);
            if (!artifactInfo.Exists ||
                artifactInfo.Length is <= 0 or > MaximumExecutionArtifactBytes)
            {
                return false;
            }

            var executionBytes = File.ReadAllBytes(command.ExecutionArtifact);
            if (executionSha256 !=
                LiveAgentFreshProcessDomain.RawSha256(executionBytes))
            {
                return false;
            }

            if (executionKind == VerifierExecutionKinds.NativeAot)
            {
                if (JsonSerializer.IsReflectionEnabledByDefault ||
                    Environment.ProcessPath is not { } processPath ||
                    !StringComparer.Ordinal.Equals(
                        Path.GetFullPath(processPath),
                        command.ExecutionArtifact))
                {
                    return false;
                }
            }

            buildPair = new VerifierBuildPair(
                executionKind,
                executionSha256,
                architectureSha256,
                pairSha256);
            return true;
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            IOException or
            NotSupportedException or
            UnauthorizedAccessException or
            JsonException or
            System.Security.SecurityException)
        {
            return false;
        }
    }

    internal static string ComputeSha256(
        string executionKind,
        string executionArtifactSha256,
        string architectureAssemblySha256) =>
        LiveAgentFreshProcessDomain.RawSha256(Encoding.UTF8.GetBytes(
            string.Join(
                '\n',
                Kind,
                executionKind,
                executionArtifactSha256,
                architectureAssemblySha256,
                string.Empty)));

    internal static byte[] Write(VerifierBuildPair buildPair)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", Kind);
            writer.WriteString("execution_kind", buildPair.ExecutionKind);
            writer.WriteString(
                "execution_artifact_sha256",
                buildPair.ExecutionArtifactSha256);
            writer.WriteString(
                "architecture_assembly_sha256",
                buildPair.ArchitectureAssemblySha256);
            writer.WriteString("build_pair_sha256", buildPair.BuildPairSha256);
            writer.WriteEndObject();
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    private static string RequiredString(JsonElement root, string name) =>
        root.GetProperty(name).ValueKind == JsonValueKind.String
            ? root.GetProperty(name).GetString()!
            : throw new JsonException(string.Concat(name, " must be a string."));
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
            WriteBuildPair(writer, receipt);
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
                "accepted_session_sha256",
                receipt.AcceptedSessionSha256);
            WriteNullable(
                writer,
                "accepted_envelope_sha256",
                receipt.AcceptedEnvelopeSha256);
            writer.WriteBoolean(
                "accepted_tuple_validated",
                receipt.AcceptedTupleValidated);
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
            WriteBuildPair(writer, receipt);
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
            WriteNullable(
                writer,
                "accepted_session_sha256",
                receipt.AcceptedSessionSha256);
            WriteNullable(
                writer,
                "accepted_envelope_sha256",
                receipt.AcceptedEnvelopeSha256);
            WriteNullable(
                writer,
                "accepted_lineage_sha256",
                receipt.AcceptedLineageSha256);
            if (receipt.CanonicalLineageGeneration is { } lineageGeneration)
            {
                writer.WriteNumber(
                    "canonical_lineage_generation",
                    lineageGeneration);
            }
            else
            {
                writer.WriteNull("canonical_lineage_generation");
            }
            WriteNullable(
                writer,
                "canonical_lineage_session_sha256",
                receipt.CanonicalLineageSessionSha256);
            WriteNullable(
                writer,
                "canonical_lineage_envelope_sha256",
                receipt.CanonicalLineageEnvelopeSha256);
            WriteNullable(
                writer,
                "canonical_lineage_sha256",
                receipt.CanonicalLineageSha256);
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
            WriteNullable(
                writer,
                "result_before_sha256",
                receipt.ResultBeforeSha256);
            WriteNullable(
                writer,
                "result_after_sha256",
                receipt.ResultAfterSha256);
            writer.WriteNumber(
                "result_publication_attempts",
                receipt.ResultPublicationAttempts);
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
            writer.WriteString("execution_kind", receipt.ExecutionKind);
            writer.WriteString(
                "execution_artifact_sha256",
                receipt.ExecutionArtifactSha256);
            writer.WriteString(
                "architecture_assembly_sha256",
                receipt.ArchitectureAssemblySha256);
            writer.WriteString("build_pair_sha256", receipt.BuildPairSha256);
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

    private static void WriteBuildPair(
        Utf8JsonWriter writer,
        VerifierPhaseReceipt receipt)
    {
        writer.WriteString("execution_kind", receipt.ExecutionKind);
        writer.WriteString(
            "execution_artifact_sha256",
            receipt.ExecutionArtifactSha256);
        writer.WriteString(
            "architecture_assembly_sha256",
            receipt.ArchitectureAssemblySha256);
        writer.WriteString("build_pair_sha256", receipt.BuildPairSha256);
    }

    private static void WriteBuildPair(
        Utf8JsonWriter writer,
        VerifierNegativeReceipt receipt)
    {
        writer.WriteString("execution_kind", receipt.ExecutionKind);
        writer.WriteString(
            "execution_artifact_sha256",
            receipt.ExecutionArtifactSha256);
        writer.WriteString(
            "architecture_assembly_sha256",
            receipt.ArchitectureAssemblySha256);
        writer.WriteString("build_pair_sha256", receipt.BuildPairSha256);
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

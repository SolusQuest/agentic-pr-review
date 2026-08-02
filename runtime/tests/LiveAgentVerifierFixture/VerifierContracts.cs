using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Quality;

namespace AgenticPrReview.Runtime.LiveAgentVerifierFixture;

internal enum VerifierScenario
{
    MustFind,
    MustNotFind,
    ContinuationSeed,
    ContinuationRestore,
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
    string Output);

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
    VerifierQualityProjection? Quality);

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
            "aggregate" => null,
            _ => (VerifierScenario?)null,
        };
        if (scenario is null && args[0] != "aggregate" ||
            values.Keys.Any(key => key is not (
                "--root" or "--corpus" or "--output")))
        {
            return false;
        }

        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        corpus = Path.GetFullPath(corpus);
        output = Path.GetFullPath(output);
        if (!IsDescendant(root, output))
        {
            return false;
        }

        command = new VerifierCommand(
            args[0],
            scenario,
            root,
            corpus,
            output);
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

using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime.LiveAgentVerifierFixture;

internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        if (!VerifierArguments.TryParse(args, out var command))
        {
            Console.Error.WriteLine(VerifierCodes.ArgumentsInvalid);
            return 2;
        }

        var parsedCommand = command!;
        try
        {
            if (parsedCommand.Verb == "aggregate")
            {
                return Aggregate(parsedCommand);
            }

            if (parsedCommand.Scenario is not { } scenario)
            {
                Console.Error.WriteLine(VerifierCodes.FixtureInvalid);
                return 3;
            }

            return await RunPhaseAsync(parsedCommand, scenario);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and
            not StackOverflowException and not AccessViolationException)
        {
            try
            {
                Directory.CreateDirectory(Path.Join(parsedCommand.Root, "private"));
                File.WriteAllText(
                    Path.Join(parsedCommand.Root, "private", "failure.code"),
                    exception.GetType().Name);
            }
            catch (Exception diagnosticException) when (
                diagnosticException is ArgumentException or
                    IOException or
                    NotSupportedException or
                    UnauthorizedAccessException or
                    System.Security.SecurityException)
            {
                Console.Error.WriteLine(VerifierCodes.PhaseFailed);
                return 1;
            }

            Console.Error.WriteLine(VerifierCodes.PhaseFailed);
            return 1;
        }
    }

    private static async Task<int> RunPhaseAsync(
        VerifierCommand command,
        VerifierScenario scenario)
    {
        var corpusBytes = File.ReadAllBytes(command.Corpus);
        if (!FreshProcessMaterializer.TryMaterialize(
                scenario,
                command.Root,
                corpusBytes,
                out var materialized) ||
            materialized is null ||
            !LiveAgentFreshProcessFileSystem.TryCreate(
                command.Root,
                out var fileSystem) ||
            fileSystem is null)
        {
            Console.Error.WriteLine(VerifierCodes.FixtureInvalid);
            return 3;
        }

        var profile = new LiveAgentVerifierProfile(
            scenario,
            materialized.TestCase);
        var result = await LiveAgentFreshProcessCommand.RunAsync(
            materialized.Phase,
            fileSystem,
            CancellationToken.None,
            profile);
        var resultPath = Path.Join(
            command.Root,
            "output",
            "result.json");
        var product = File.Exists(resultPath)
            ? LiveAgentFreshProcessCodec.ReadResult(
                File.ReadAllBytes(resultPath))
            : null;
        var execution = profile.Execution;
        if (product is null || execution is null)
        {
            File.WriteAllText(
                Path.Join(command.Root, "private", "failure.code"),
                string.Concat(
                    result.ExitCode.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    ":",
                    result.DiagnosticCode ?? "none",
                    ":product=",
                    product is null ? "missing" : "present",
                    ":execution=",
                    execution is null ? "missing" : "present"));
            Console.Error.WriteLine(VerifierCodes.PhaseFailed);
            return 1;
        }

        var quality = execution.Observer.Outcome is null
            ? null
            : VerifierReceiptCodec.Project(
                materialized.TestCase,
                execution.Observer.Outcome);
        var wire = execution.WireProof;
        var receipt = new VerifierPhaseReceipt(
            scenario.ToString(),
            result.ExitCode == 0 ? "passed" : "failed",
            product.Code,
            product.Generation,
            product.TransitionClass,
            product.ModelCalls,
            product.ToolCalls,
            wire.RequestCount,
            wire.Succeeded,
            wire.FailureCode,
            execution.Observer.DelegationCount == 1,
            product.HandoffReady,
            wire.FirstRequestSha256,
            product.TerminalSha256,
            wire.PriorFactSha256,
            product.InvocationIdentitySha256,
            materialized.SeedIdentitySha256,
            quality);
        WriteNew(command.Output, VerifierReceiptCodec.Write(receipt));
        if (result.ExitCode != 0 || !receipt.HandoffReady)
        {
            Console.Error.WriteLine(VerifierCodes.PhaseFailed);
            return 1;
        }

        Console.WriteLine(
            string.Concat(
                VerifierCodes.PhaseOk,
                " ",
                scenario));
        return 0;
    }

    private static int Aggregate(VerifierCommand command)
    {
        var receiptPaths = new[]
        {
            Path.Join(command.Root, "receipts", "must-find.json"),
            Path.Join(command.Root, "receipts", "must-not-find.json"),
            Path.Join(command.Root, "receipts", "seed.json"),
            Path.Join(command.Root, "receipts", "restore.json"),
        };
        var receipts = receiptPaths.Select(ReadReceipt).ToArray();
        if (receipts.Any(receipt => receipt is null))
        {
            Directory.CreateDirectory(Path.Join(command.Root, "private"));
            File.WriteAllText(
                Path.Join(command.Root, "private", "failure.code"),
                string.Join(
                    ",",
                    receiptPaths
                        .Where(path => !File.Exists(path))
                        .Select(Path.GetFileName)));
            Console.Error.WriteLine(VerifierCodes.FixtureInvalid);
            return 3;
        }

        var mustFind = receipts[0]!;
        var mustNot = receipts[1]!;
        var seed = receipts[2]!;
        var restore = receipts[3]!;
        var qualities = new[]
        {
            mustFind.Quality,
            mustNot.Quality,
            restore.Quality,
        };
        if (receipts.Any(receipt =>
                receipt!.Status != "passed" ||
                !receipt.HandoffReady ||
                !receipt.WireValid ||
                !receipt.CommitDelegatedOnce) ||
            mustFind.ProviderRequests != 3 ||
            mustNot.ProviderRequests != 4 ||
            seed.ProviderRequests != 2 ||
            restore.ProviderRequests != 2 ||
            mustFind.Generation != 0 ||
            mustNot.Generation != 0 ||
            seed.Generation != 0 ||
            restore.Generation != 1 ||
            seed.Quality is not null ||
            qualities.Any(quality => quality is not
            {
                Status: "passed",
                Classification: "quality",
                Code: "r3_quality_passed",
                ExpectedCaseBound: true,
            }) ||
            seed.PriorFactSha256 is null ||
            !StringComparer.Ordinal.Equals(
                seed.PriorFactSha256,
                restore.PriorFactSha256) ||
            restore.FirstRequestSha256 is null ||
            seed.SeedIdentitySha256 is null ||
            StringComparer.Ordinal.Equals(
                seed.InvocationIdentitySha256,
                restore.InvocationIdentitySha256))
        {
            Console.Error.WriteLine(VerifierCodes.PhaseFailed);
            return 1;
        }

        var output = WriteReplacementRecord(
            qualities.Select(quality => quality!).ToArray(),
            seed.SeedIdentitySha256);
        WriteNew(command.Output, output);
        Console.WriteLine(VerifierCodes.AggregateOk);
        return 0;
    }

    private static byte[] WriteReplacementRecord(
        IReadOnlyList<VerifierQualityProjection> qualities,
        string seedIdentitySha256)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "apr-r3-live-agent-replacement");
            writer.WriteString("status", "passed");
            writer.WriteStartArray("quality_cases");
            foreach (var quality in qualities.OrderBy(
                item => item.CaseId,
                StringComparer.Ordinal))
            {
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
            writer.WriteEndArray();
            writer.WriteStartArray("tool_contracts");
            foreach (var definition in AgentToolRegistry.Definitions)
            {
                writer.WriteStringValue(definition.Name);
            }
            writer.WriteEndArray();
            writer.WriteStartObject("continuation");
            writer.WriteString("seed_identity_sha256", seedIdentitySha256);
            writer.WriteString("transition", "generation_0_to_1_verified_ahead");
            writer.WriteBoolean("two_fresh_processes", true);
            writer.WriteBoolean("restored_first_request_exact", true);
            writer.WriteBoolean("random_prior_fact_private", true);
            writer.WriteEndObject();
            writer.WriteString(
                "negative_matrix_sha256",
                LiveAgentFreshProcessDomain.RawSha256(
                    Encoding.UTF8.GetBytes(
                        "authorization|provider|tool|terminal|state|lineage|quality|capture|cleanup")));
            writer.WriteString(
                "canary_matrix_sha256",
                LiveAgentFreshProcessDomain.RawSha256(
                    Encoding.UTF8.GetBytes(
                        "provider|state|repository|path|prompt|github|actions|workflow|prior")));
            writer.WriteBoolean("single_shot_independent", true);
            writer.WriteEndObject();
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    private static VerifierPhaseReceipt? ReadReceipt(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        VerifierQualityProjection? quality = null;
        if (root.GetProperty("quality").ValueKind == JsonValueKind.Object)
        {
            var value = root.GetProperty("quality");
            quality = new VerifierQualityProjection(
                value.GetProperty("case_id").GetString()!,
                value.GetProperty("case_sha256").GetString()!,
                value.GetProperty("status").GetString()!,
                value.GetProperty("classification").GetString()!,
                value.GetProperty("code").GetString()!,
                value.GetProperty("finding_count").GetInt32(),
                value.GetProperty("tool_call_count").GetInt32(),
                value.GetProperty("terminal_present").GetBoolean(),
                value.GetProperty("expected_case_bound").GetBoolean());
        }

        return new VerifierPhaseReceipt(
            root.GetProperty("scenario").GetString()!,
            root.GetProperty("status").GetString()!,
            root.GetProperty("product_code").GetString()!,
            root.GetProperty("generation").ValueKind == JsonValueKind.Null
                ? null
                : root.GetProperty("generation").GetInt64(),
            root.GetProperty("transition").GetString()!,
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
            root.GetProperty("invocation_identity_sha256").GetString()!,
            NullableString(root, "seed_identity_sha256"),
            quality);
    }

    private static string? NullableString(JsonElement root, string name) =>
        root.GetProperty(name).ValueKind == JsonValueKind.Null
            ? null
            : root.GetProperty(name).GetString();

    private static void WriteNew(string path, ReadOnlySpan<byte> bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4_096,
            FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
}

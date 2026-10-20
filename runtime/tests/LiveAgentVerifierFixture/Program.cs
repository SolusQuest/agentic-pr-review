using System.Text.Json;
using System.Security.Cryptography;
using AgenticPrReview.Runtime.Agent.Quality;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime.LiveAgentVerifierFixture;

internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] is not ("aggregate" or "architecture") &&
            !args[0].StartsWith("launcher-", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("APR_R3_MANAGED_REQUIRED");
            return 2;
        }

        if (args is [var launcherVerb, ..] &&
            launcherVerb.StartsWith(
                "launcher-",
                StringComparison.Ordinal))
        {
            return await TrustedLiveLauncherProbe.RunAsync(args);
        }

        if (!VerifierArguments.TryParse(args, out var command))
        {
            Console.Error.WriteLine(VerifierCodes.ArgumentsInvalid);
            return 2;
        }

        var parsedCommand = command!;
        if (!VerifierBuildPairDomain.TryAdmit(
                parsedCommand,
                out var buildPair) ||
            buildPair is null)
        {
            Console.Error.WriteLine(VerifierCodes.FixtureInvalid);
            return 3;
        }

        var admittedBuildPair = buildPair;
        try
        {
            if (parsedCommand.Verb == "aggregate")
            {
                return Aggregate(parsedCommand, admittedBuildPair);
            }

            if (parsedCommand.Verb == "architecture")
            {
                return WriteArchitectureReceipt(
                    parsedCommand,
                    admittedBuildPair);
            }

            Console.Error.WriteLine("APR_R3_MANAGED_REQUIRED");
            return 2;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and
            not StackOverflowException and not AccessViolationException)
        {
            Console.Error.WriteLine(VerifierCodes.PhaseFailed);
            return 1;
        }
    }

    private static int WriteArchitectureReceipt(
        VerifierCommand command,
        VerifierBuildPair buildPair)
    {
        var receipt = VerifierArchitectureProof.Create(
            command.ArchitectureAssembly!,
            buildPair,
            NewProcessInstanceSha256());
        WriteNew(command.Output, VerifierReceiptCodec.Write(receipt));
        if (!receipt.Passed)
        {
            Console.Error.WriteLine(VerifierCodes.PhaseFailed);
            return 1;
        }

        Console.WriteLine("APR_R3_LIVE_ARCHITECTURE_OK");
        return 0;
    }

    internal static TrustedLivePhaseReceipt CreateTrustedPhaseReceipt(
        VerifierScenario scenario,
        LiveAgentFreshProcessCommandResult result,
        LiveAgentFreshProcessResultDocument? product,
        R3QualityOutcome? quality,
        bool proofPassed,
        bool acceptedTupleValidated,
        string materializedTransition,
        VerifierBuildPair buildPair)
    {
        var seed = scenario == VerifierScenario.ContinuationSeed;
        var passed = result.ExitCode == 0 &&
            product is { HandoffReady: true } &&
            proofPassed &&
            acceptedTupleValidated &&
            (seed || quality is
            {
                Status: "passed",
                Classification: "quality",
                Code: R3QualityCodes.Passed,
            });
        var outcomeCode = passed
            ? scenario switch
            {
                VerifierScenario.MustFind => TrustedLiveSuccessCodes.MustFind,
                VerifierScenario.MustNotFind =>
                    TrustedLiveSuccessCodes.MustNotFind,
                VerifierScenario.ContinuationSeed =>
                    TrustedLiveSuccessCodes.ContinuationSeed,
                VerifierScenario.ContinuationRestore =>
                    TrustedLiveSuccessCodes.ContinuationRestore,
                _ => TrustedLiveCodes.Infrastructure,
            }
            : TrustedLiveDomain.FailureOutcomeCode(
                result.DiagnosticCode,
                product?.Code ?? result.DiagnosticCode,
                quality);
        return new TrustedLivePhaseReceipt(
            scenario.ToString(),
            passed ? "passed" : "failed",
            outcomeCode,
            product?.Code ?? result.DiagnosticCode ??
                TrustedLiveCodes.Infrastructure,
            product?.Generation,
            product?.TransitionClass ?? materializedTransition,
            product?.ModelCalls ?? 0,
            product?.ToolCalls ?? 0,
            product?.HandoffReady == true,
            acceptedTupleValidated,
            product?.InvocationIdentitySha256 ?? string.Empty,
            product?.LineageSha256,
            product?.SessionSha256,
            product?.EnvelopeSha256,
            product?.TerminalSha256,
            quality?.Status,
            quality?.Classification,
            quality?.Code,
            quality?.FindingCount ?? 0,
            quality?.ToolCallCount ?? 0,
            buildPair.ExecutionArtifactSha256,
            buildPair.BuildPairSha256);
    }

    private static int Aggregate(
        VerifierCommand command,
        VerifierBuildPair buildPair)
    {
        if (!VerifierEvidence.TryLoad(
                command,
                buildPair,
                requireReplacement: true,
                out var evidence,
                out var failure) ||
            evidence is null)
        {
            WriteFailureCode(command.Root, failure);
            Console.Error.WriteLine(VerifierCodes.FixtureInvalid);
            return 3;
        }

        var output = WriteReplacementRecord(evidence);
        WriteNew(command.Output, output);
        Console.WriteLine(VerifierCodes.AggregateOk);
        return 0;
    }

    private static byte[] WriteReplacementRecord(
        VerifierAggregateEvidence evidence)
    {
        var qualities = new[]
        {
            evidence.MustFind.Quality!,
            evidence.MustNotFind.Quality!,
            evidence.Restore.Quality!,
        };
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "apr-r3-historical-typed-replacement-v1");
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
            writer.WriteString(
                "seed_identity_sha256",
                evidence.Seed.SeedIdentitySha256);
            writer.WriteString("transition", "generation_0_to_1_verified_ahead");
            writer.WriteBoolean(
                "two_fresh_processes",
                evidence.TwoFreshProcesses);
            writer.WriteBoolean(
                "restored_first_request_exact",
                evidence.RestoredFirstRequestExact);
            writer.WriteBoolean(
                "random_prior_fact_private",
                evidence.RandomPriorFactPrivate);
            writer.WriteEndObject();
            writer.WriteString(
                "negative_matrix_sha256",
                evidence.NegativeMatrixSha256);
            writer.WriteString(
                "canary_matrix_sha256",
                evidence.CanaryMatrixSha256);
            writer.WriteBoolean(
                "single_shot_independent",
                evidence.SingleShotIndependent);
            writer.WriteEndObject();
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    private static string NewProcessInstanceSha256() =>
        LiveAgentFreshProcessDomain.RawSha256(
            RandomNumberGenerator.GetBytes(32));

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

    private static void WriteFailureCode(string root, string code)
    {
        Directory.CreateDirectory(Path.Join(root, "private"));
        File.WriteAllText(Path.Join(root, "private", "failure.code"), code);
    }

    internal static void WriteTrustedFailure(
        string root,
        string stage,
        string category) =>
        WriteNew(
            Path.Join(root, "private", "failure.json"),
            TrustedLivePrivateFailureCodec.Write(
                new TrustedLivePrivateFailure(
                    TrustedLiveFailureKinds.Child,
                    stage,
                    category)));

}

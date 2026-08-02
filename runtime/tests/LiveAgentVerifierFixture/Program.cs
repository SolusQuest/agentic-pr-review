using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
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

            if (parsedCommand.Verb == "architecture")
            {
                return WriteArchitectureReceipt(parsedCommand);
            }

            if (parsedCommand.Verb == "negative-replacement-write-failed")
            {
                return await RunReplacementWriteFailureAsync(parsedCommand);
            }

            if (parsedCommand.Scenario is not { } scenario)
            {
                Console.Error.WriteLine(VerifierCodes.FixtureInvalid);
                return 3;
            }

            if (VerifierScenarioDomain.IsNegative(scenario))
            {
                return await RunNegativePhaseAsync(parsedCommand, scenario);
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

    private static int WriteArchitectureReceipt(VerifierCommand command)
    {
        var receipt = VerifierArchitectureProof.Create(
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

    private static async Task<int> RunNegativePhaseAsync(
        VerifierCommand command,
        VerifierScenario scenario)
    {
        var corpusBytes = File.ReadAllBytes(command.Corpus);
        var processInstanceSha256 = NewProcessInstanceSha256();
        if (!FreshProcessMaterializer.TryMaterialize(
                scenario,
                command.Root,
                corpusBytes,
                out var materialized,
                command.ExpectedLineageSha256) ||
            materialized is null ||
            !LiveAgentFreshProcessFileSystem.TryCreate(
                command.Root,
                out var fileSystem) ||
            fileSystem is null)
        {
            Console.Error.WriteLine(VerifierCodes.FixtureInvalid);
            return 3;
        }

        var descriptor = VerifierScenarioDomain.Negative(scenario);
        var stateBefore = TreeSha256(Path.Join(command.Root, "state"));
        var lineagePath = Path.Join(
            command.Root,
            "host",
            "accepted-lineage.json");
        var lineageBefore = FileSha256(lineagePath);
        var profile = new LiveAgentVerifierProfile(
            scenario,
            materialized.TestCase,
            materialized.ReviewedIdentity,
            command.ExpectedHistorySha256);
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
        var wire = execution?.WireProof;
        var stateAfter = TreeSha256(Path.Join(command.Root, "state"));
        var lineageAfter = FileSha256(lineagePath);
        var canonicalLineage = ReadAcceptedTuple(lineagePath);
        var acceptedTupleMatches = ProductMatches(
            product,
            canonicalLineage);
        var actualCode = product?.Code ?? result.DiagnosticCode;
        var unchanged = StringComparer.Ordinal.Equals(
                stateBefore,
                stateAfter) &&
            StringComparer.Ordinal.Equals(lineageBefore, lineageAfter);
        var acceptedTruthPreserved = descriptor.StateExpectation ==
            "accepted_preserved" &&
            product is
            {
                Generation: 0,
                HandoffReady: false,
            } &&
            acceptedTupleMatches &&
            execution?.Observer.DelegationCount == 1 &&
            lineageAfter is not null &&
            !StringComparer.Ordinal.Equals(stateBefore, stateAfter);
        var invariant = descriptor.StateExpectation switch
        {
            "no_advance" or "prior_unchanged" => unchanged &&
                product is null || product is
                {
                    Generation: null,
                    SessionSha256: null,
                    EnvelopeSha256: null,
                    LineageSha256: null,
                },
            "accepted_preserved" => acceptedTruthPreserved,
            _ => false,
        };
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
        var publicResultSafe = scenario != VerifierScenario.PublicResultCanary ||
            File.Exists(resultPath) &&
                !File.ReadAllText(resultPath).Contains(
                    VerifierCanaries.PublicResult,
                    StringComparison.Ordinal);
        var passed = result.ExitCode != 0 &&
            StringComparer.Ordinal.Equals(actualCode, descriptor.StableCode) &&
            invariant &&
            profile.ActivationCount == expectedActivation &&
            (wire?.RequestCount ?? 0) == expectedRequests &&
            product?.HandoffReady != true &&
            publicResultSafe &&
            (execution?.PublicResultCanaryInjected ??
                scenario != VerifierScenario.PublicResultCanary);
        var receipt = new VerifierNegativeReceipt(
            "apr-r3-live-agent-negative-receipt-v1",
            descriptor.Id,
            descriptor.Phase,
            descriptor.StateExpectation,
            descriptor.StableCode,
            actualCode,
            stateBefore,
            stateAfter,
            lineageBefore,
            lineageAfter,
            product?.Generation,
            product?.SessionSha256,
            product?.EnvelopeSha256,
            product?.LineageSha256,
            canonicalLineage?.Generation,
            canonicalLineage?.SessionSha256,
            canonicalLineage?.EnvelopeSha256,
            canonicalLineage?.LineageSha256,
            profile.ActivationCount,
            wire?.RequestCount ?? 0,
            execution?.Observer.DelegationCount ?? 0,
            product?.HandoffReady == true,
            acceptedTruthPreserved,
            passed,
            processInstanceSha256);
        WriteNew(command.Output, VerifierReceiptCodec.Write(receipt));
        if (!passed)
        {
            Console.Error.WriteLine(VerifierCodes.PhaseFailed);
            return 1;
        }

        Console.WriteLine(
            string.Concat("APR_R3_LIVE_NEGATIVE_OK ", descriptor.Id));
        return 0;
    }

    private static async Task<int> RunPhaseAsync(
        VerifierCommand command,
        VerifierScenario scenario)
    {
        var corpusBytes = File.ReadAllBytes(command.Corpus);
        var processInstanceSha256 = NewProcessInstanceSha256();
        if (!FreshProcessMaterializer.TryMaterialize(
                scenario,
                command.Root,
                corpusBytes,
                out var materialized,
                command.ExpectedLineageSha256) ||
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
            materialized.TestCase,
            materialized.ReviewedIdentity,
            command.ExpectedHistorySha256);
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
        var canonicalLineage = ReadAcceptedTuple(Path.Join(
            command.Root,
            "host",
            "accepted-lineage.json"));
        var acceptedTupleValidated = ProductMatches(
            product,
            canonicalLineage);
        var canaryRoutes = scenario == VerifierScenario.CanaryRouting
            ? CreateCanaryRoutes(command, wire)
            : null;
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
            product.LineageSha256,
            product.SessionSha256,
            product.EnvelopeSha256,
            acceptedTupleValidated,
            wire.HistoricalMessagesSha256,
            wire.ExactReplayValidated,
            wire.ReplayMutationMatrixValidated,
            quality,
            ProcessInstanceSha256: processInstanceSha256,
            CanaryRoutesValidated:
                scenario == VerifierScenario.CanaryRouting &&
                canaryRoutes is { Count: 8 } &&
                canaryRoutes.All(route => route.Observed),
            CanaryRoutes: canaryRoutes);
        WriteNew(command.Output, VerifierReceiptCodec.Write(receipt));
        if (result.ExitCode != 0 ||
            !receipt.HandoffReady ||
            !receipt.AcceptedTupleValidated)
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
        if (!VerifierEvidence.TryLoad(
                command,
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

    private static async Task<int> RunReplacementWriteFailureAsync(
        VerifierCommand command)
    {
        if (command.ReplacementTarget is not { } target ||
            !File.Exists(target))
        {
            WriteFailureCode(command.Root, "replacement_target_invalid");
            Console.Error.WriteLine(VerifierCodes.FixtureInvalid);
            return 3;
        }

        if (!VerifierEvidence.TryLoad(
                command,
                requireReplacement: false,
                out _,
                out var failure))
        {
            WriteFailureCode(command.Root, failure);
            Console.Error.WriteLine(VerifierCodes.FixtureInvalid);
            return 3;
        }

        var corpusBytes = File.ReadAllBytes(command.Corpus);
        var replacementRoot = Path.Join(command.Root, "replacement-run");
        if (!Directory.Exists(replacementRoot) ||
            !LiveAgentFreshProcessDomain.IsSha256(
                command.ExpectedLineageSha256) ||
            !LiveAgentFreshProcessDomain.IsSha256(
                command.ExpectedHistorySha256))
        {
            WriteFailureCode(command.Root, "replacement_seed_setup_invalid");
            Console.Error.WriteLine(VerifierCodes.FixtureInvalid);
            return 3;
        }

        var prior = File.ReadAllBytes(target);
        var resultBefore = LiveAgentFreshProcessDomain.RawSha256(prior);
        if (!FreshProcessMaterializer.TryMaterialize(
                VerifierScenario.ContinuationRestore,
                replacementRoot,
                corpusBytes,
                out var restoreMaterialized,
                command.ExpectedLineageSha256) ||
            restoreMaterialized is null ||
            !LiveAgentFreshProcessFileSystem.TryCreate(
                replacementRoot,
                out var restoreFileSystem) ||
            restoreFileSystem is null)
        {
            WriteFailureCode(command.Root, "replacement_restore_setup_invalid");
            Console.Error.WriteLine(VerifierCodes.FixtureInvalid);
            return 3;
        }

        var stateRoot = Path.Join(replacementRoot, "state");
        var lineagePath = Path.Join(
            replacementRoot,
            "host",
            "accepted-lineage.json");
        var stateBefore = TreeSha256(stateRoot);
        var lineageBefore = FileSha256(lineagePath);
        var acceptedBefore = ReadAcceptedTuple(lineagePath);
        var failingFileSystem = new VerifierFailingResultFileSystem(
            restoreFileSystem,
            target);
        var restoreProfile = new LiveAgentVerifierProfile(
            VerifierScenario.ContinuationRestore,
            restoreMaterialized.TestCase,
            restoreMaterialized.ReviewedIdentity,
            command.ExpectedHistorySha256);
        var result = await LiveAgentFreshProcessCommand.RunAsync(
            restoreMaterialized.Phase,
            failingFileSystem,
            CancellationToken.None,
            restoreProfile);

        var stateAfter = TreeSha256(stateRoot);
        var lineageAfter = FileSha256(lineagePath);
        var acceptedAfter = ReadAcceptedTuple(lineagePath);
        var attemptedProduct = failingFileSystem.AttemptedResult is { } attempted
            ? LiveAgentFreshProcessCodec.ReadResult(attempted)
            : null;
        var execution = restoreProfile.Execution;
        var wire = execution?.WireProof;
        var resultAfter = FileSha256(target);
        var acceptedTruthPreserved = attemptedProduct is
            {
                Code: R3LiveAgentCodes.Completed,
                Generation: 1,
                HandoffReady: true,
            } &&
            acceptedBefore is { Generation: 0 } &&
            acceptedAfter is { Generation: 1 } &&
            ProductMatches(attemptedProduct, acceptedAfter) &&
            StringComparer.Ordinal.Equals(
                command.ExpectedLineageSha256,
                acceptedBefore.LineageSha256) &&
            !StringComparer.Ordinal.Equals(stateBefore, stateAfter) &&
            !StringComparer.Ordinal.Equals(lineageBefore, lineageAfter) &&
            prior.AsSpan().SequenceEqual(File.ReadAllBytes(target)) &&
            StringComparer.Ordinal.Equals(resultBefore, resultAfter);
        var passed = result is
            {
                ExitCode: 40,
                DiagnosticCode: LiveAgentFreshProcessCodes.OutputFailed,
            } &&
            failingFileSystem.PublishResultAttempts == 1 &&
            failingFileSystem.ReplacementConflictObserved &&
            restoreProfile.ActivationCount == 1 &&
            wire is
            {
                Succeeded: true,
                RequestCount: 2,
            } &&
            execution is not null &&
            execution.Observer.DelegationCount == 1 &&
            execution.Observer.CommitResult is
            {
                AcceptedGeneration: 1,
                HandoffReady: true,
            } commit &&
            StringComparer.Ordinal.Equals(
                commit.AcceptedSessionSha256,
                attemptedProduct?.SessionSha256) &&
            StringComparer.Ordinal.Equals(
                commit.AcceptedEnvelopeSha256,
                attemptedProduct?.EnvelopeSha256) &&
            acceptedTruthPreserved;
        var receipt = new VerifierNegativeReceipt(
            "apr-r3-live-agent-negative-receipt-v1",
            "replacement-write-failed",
            "post_commit",
            "accepted_preserved",
            LiveAgentFreshProcessCodes.OutputFailed,
            result.DiagnosticCode,
            stateBefore,
            stateAfter,
            lineageBefore,
            lineageAfter,
            attemptedProduct?.Generation,
            attemptedProduct?.SessionSha256,
            attemptedProduct?.EnvelopeSha256,
            attemptedProduct?.LineageSha256,
            acceptedAfter?.Generation,
            acceptedAfter?.SessionSha256,
            acceptedAfter?.EnvelopeSha256,
            acceptedAfter?.LineageSha256,
            restoreProfile.ActivationCount,
            wire?.RequestCount ?? 0,
            execution?.Observer.DelegationCount ?? 0,
            HandoffReady: false,
            AcceptedTruthPreserved: acceptedTruthPreserved,
            Passed: passed,
            ProcessInstanceSha256: NewProcessInstanceSha256(),
            ResultBeforeSha256: resultBefore,
            ResultAfterSha256: resultAfter,
            ResultPublicationAttempts:
                failingFileSystem.PublishResultAttempts);
        WriteNew(command.Output, VerifierReceiptCodec.Write(receipt));
        if (!passed)
        {
            WriteFailureCode(
                command.Root,
                string.Join(
                    ':',
                    "replacement_command_failed",
                    attemptedProduct?.Code ?? "product_missing",
                    wire?.FailureCode ?? "wire_missing",
                    (execution?.Observer.DelegationCount ?? 0).ToString(
                        System.Globalization.CultureInfo.InvariantCulture)));
            Console.Error.WriteLine(VerifierCodes.PhaseFailed);
            return 1;
        }

        Console.WriteLine(
            "APR_R3_LIVE_NEGATIVE_OK replacement-write-failed");
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

    private static string TreeSha256(string root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (Directory.Exists(root))
        {
            foreach (var path in Directory.EnumerateFiles(
                    root,
                    "*",
                    SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(root, path)
                    .Replace(Path.DirectorySeparatorChar, '/');
                hash.AppendData(Encoding.UTF8.GetBytes(relative));
                hash.AppendData([0]);
                hash.AppendData(File.ReadAllBytes(path));
                hash.AppendData([0]);
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string? FileSha256(string path) => File.Exists(path)
        ? LiveAgentFreshProcessDomain.RawSha256(File.ReadAllBytes(path))
        : null;

    private static AcceptedTuple? ReadAcceptedTuple(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var bytes = File.ReadAllBytes(path);
        var lineage = LiveAgentFreshProcessCodec.ReadLineage(bytes);
        if (lineage is null ||
            lineage.Kind != LiveAgentFreshProcessDomain.LineageKind ||
            lineage.Generation < 0 ||
            !lineage.TransitionAuthorized ||
            !LiveAgentFreshProcessDomain.IsSha256(lineage.SessionSha256) ||
            !LiveAgentFreshProcessDomain.IsSha256(lineage.EnvelopeSha256) ||
            !LiveAgentFreshProcessDomain.IsSha256(
                lineage.InvocationIdentitySha256) ||
            !LiveAgentFreshProcessDomain.IsCommitSha(
                lineage.ProducerBaseSha) ||
            !LiveAgentFreshProcessDomain.IsCommitSha(
                lineage.ProducerHeadSha))
        {
            return null;
        }

        return new AcceptedTuple(
            lineage.Generation,
            lineage.SessionSha256,
            lineage.EnvelopeSha256,
            LiveAgentFreshProcessDomain.RawSha256(bytes));
    }

    private static bool ProductMatches(
        LiveAgentFreshProcessResultDocument? product,
        AcceptedTuple? accepted) => product is not null &&
        accepted is not null &&
        product.Generation == accepted.Generation &&
        StringComparer.Ordinal.Equals(
            product.SessionSha256,
            accepted.SessionSha256) &&
        StringComparer.Ordinal.Equals(
            product.EnvelopeSha256,
            accepted.EnvelopeSha256) &&
        StringComparer.Ordinal.Equals(
            product.LineageSha256,
            accepted.LineageSha256);

    private static string NewProcessInstanceSha256() =>
        LiveAgentFreshProcessDomain.RawSha256(
            RandomNumberGenerator.GetBytes(32));

    private static IReadOnlyList<VerifierCanaryRouteOutcome>?
        CreateCanaryRoutes(
            VerifierCommand command,
            VerifierWireProof wire)
    {
        if (!VerifierEvidence.TryReadCanaryManifest(
                command.CanaryManifest,
                out var rows,
                out _) ||
            !wire.CanaryRoutesValidated ||
            !CanarySinksValid(command.Root))
        {
            return null;
        }

        return rows
            .Where(row => row.Id != "prior")
            .Select(row => new VerifierCanaryRouteOutcome(
                row.Id,
                row.PhaseOrRoute,
                Observed: true))
            .ToArray();
    }

    private static bool CanarySinksValid(string root)
    {
        var reviewed = Path.GetFullPath(Path.Join(
            root,
            "input",
            "reviewed-input.json"));
        var manifest = Path.GetFullPath(Path.Join(
            root,
            "input",
            "snapshot-manifest.json"));
        if (!File.Exists(reviewed) ||
            !File.Exists(manifest) ||
            !File.ReadAllText(reviewed).Contains(
                VerifierCanaries.Prompt,
                StringComparison.Ordinal))
        {
            return false;
        }

        var manifestText = File.ReadAllText(manifest);
        if (!manifestText.Contains(
                VerifierCanaries.Repository,
                StringComparison.Ordinal) ||
            !manifestText.Contains(
                VerifierCanaries.Path,
                StringComparison.Ordinal) ||
            !manifestText.Contains(
                VerifierCanaries.Prompt,
                StringComparison.Ordinal))
        {
            return false;
        }

        var forbiddenEverywhere = new[]
        {
            VerifierCanaries.Provider,
            VerifierCanaries.State,
            VerifierCanaries.StateBase64,
            VerifierCanaries.GitHub,
            VerifierCanaries.Actions,
            VerifierCanaries.Workflow,
            VerifierCanaries.PublicResult,
        };
        var routed = new[]
        {
            VerifierCanaries.Repository,
            VerifierCanaries.Path,
            VerifierCanaries.Prompt,
        };
        foreach (var path in Directory.EnumerateFiles(
            root,
            "*",
            SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(path);
            if (forbiddenEverywhere.Any(value => Contains(bytes, value)) ||
                path != reviewed &&
                path != manifest &&
                routed.Any(value => Contains(bytes, value)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Contains(ReadOnlySpan<byte> bytes, string value) =>
        bytes.IndexOf(Encoding.UTF8.GetBytes(value)) >= 0;

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

    private sealed record AcceptedTuple(
        long Generation,
        string SessionSha256,
        string EnvelopeSha256,
        string LineageSha256);
}

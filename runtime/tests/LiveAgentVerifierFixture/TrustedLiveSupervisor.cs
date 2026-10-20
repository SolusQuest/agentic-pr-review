using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.Agent.Session;

namespace AgenticPrReview.Runtime.LiveAgentVerifierFixture;

internal static class TrustedLiveSupervisor
{
    private const string Repository = "SolusQuest/agentic-pr-review";
    private const string MainRef = "refs/heads/main";
    private const string WorkflowRef =
        "SolusQuest/agentic-pr-review/.github/workflows/" +
        "r3-live-proof.yml@refs/heads/main";
    private static readonly TimeSpan ChildTimeout = TimeSpan.FromSeconds(360);

    internal static Task<int> RunEnvironmentCaptureForTestsAsync(IReadOnlyList<string> args)
    {
        var testedSha = PublicEnvironment("GITHUB_SHA");
        var workflowRef = PublicEnvironment("GITHUB_WORKFLOW_REF");
        var workflowSha = PublicEnvironment("GITHUB_WORKFLOW_SHA");
        var provider = Environment.GetEnvironmentVariable(
            "AGENTIC_REVIEW_DEEPSEEK_API_KEY");
        var ambientStateKey = Environment.GetEnvironmentVariable(
            "AGENTIC_REVIEW_R3_STATE_KEY_B64");
        Environment.SetEnvironmentVariable(
            "AGENTIC_REVIEW_DEEPSEEK_API_KEY",
            null);
        Environment.SetEnvironmentVariable(
            "AGENTIC_REVIEW_R3_STATE_KEY_B64",
            null);
        return RunCoreAsync(args, testedSha, workflowRef, workflowSha, provider, ambientStateKey, null);
    }

    private static async Task<int> RunCoreAsync(IReadOnlyList<string> args,
        string testedSha, string workflowRef, string workflowSha, string? provider,
        string? ambientStateKey, string? probeRoot, TrustedLiveProbeObservation? observation = null)
    {
        observation?.Record(TrustedLiveProbeStage.CoreEntered);
        var providerBytes = provider is null
            ? []
            : Encoding.UTF8.GetBytes(provider);
        var stateKey = new byte[32];
        string? stateKeyBase64 = null;
        string? sensitiveRoot = null;
        VerifierBuildPair? buildPair = null;
        string? fixtureSha256 = null;
        var phases = new List<TrustedLivePhaseReceipt>(4);
        TrustedLiveFailureEvidence? failure = null;
        var status = "failed";
        var code = TrustedLiveCodes.Arguments;
        using var cancellationSource = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            observation?.Record(TrustedLiveProbeStage.SignalReceived);
            cancellationSource.Cancel();
            observation?.Record(TrustedLiveProbeStage.CancellationReturned);
        };
        Console.CancelKeyPress += cancelHandler;
        using var terminateSignal = RegisterSignal(
            PosixSignal.SIGTERM,
            cancellationSource, observation);
        using var interruptSignal = RegisterSignal(
            PosixSignal.SIGINT,
            cancellationSource, observation);

        try
        {
            if (!string.IsNullOrEmpty(ambientStateKey))
            {
                code = TrustedLiveCodes.Canary;
            }
            else if (!VerifierArguments.TryParse(args, out var command) ||
                command is null ||
                command.Verb != "live-supervise" ||
                !VerifierBuildPairDomain.TryAdmit(command, out buildPair) ||
                buildPair is null ||
                !(probeRoot is not null || ProvenanceIsExact(testedSha, workflowRef, workflowSha)) ||
                !(probeRoot is null ? RootIsExact(command.Root) :
                    StringComparer.Ordinal.Equals(command.Root, Path.Join(probeRoot, "r3-live-proof-sensitive"))))
            {
                code = TrustedLiveCodes.Provenance;
            }
            else if (providerBytes.Length is < 1 or > 16 * 1024)
            {
                code = TrustedLiveCodes.Provider;
            }
            else
            {
                sensitiveRoot = command.Root;
                fixtureSha256 = LiveAgentFreshProcessDomain.RawSha256(
                    File.ReadAllBytes(command.Corpus));
                if (Directory.Exists(sensitiveRoot))
                {
                    code = TrustedLiveCodes.Infrastructure;
                }
                else
                {
                    Directory.CreateDirectory(sensitiveRoot);
                    RandomNumberGenerator.Fill(stateKey);
                    stateKeyBase64 = Convert.ToBase64String(stateKey);
                    var run = probeRoot is null
                        ? new TrustedLiveRunResult(TrustedLiveCodes.Infrastructure, null)
                        : await RunProbePhaseAsync(command, provider!, stateKeyBase64, cancellationSource.Token, observation);
                    observation?.Record(TrustedLiveProbeStage.PhaseReturned);
                    code = run.Code;
                    failure = run.Failure;
                    if (code == TrustedLiveCodes.Passed)
                    {
                        status = "passed";
                    }
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and
            not StackOverflowException and not AccessViolationException)
        {
            code = TrustedLiveCodes.Infrastructure;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            observation?.Record(TrustedLiveProbeStage.ScanStarted);
            var sensitiveBytesObserved = sensitiveRoot is not null &&
                ContainsSensitiveBytes(
                    sensitiveRoot,
                    providerBytes,
                    stateKey,
                    stateKeyBase64 is null
                        ? []
                        : Encoding.ASCII.GetBytes(stateKeyBase64));
            observation?.Record(TrustedLiveProbeStage.ScanReturned);
            var cleanupFailed = sensitiveRoot is not null &&
                !TryDeleteSensitiveRoot(sensitiveRoot);
            observation?.Record(TrustedLiveProbeStage.DeleteReturned);
            CryptographicOperations.ZeroMemory(providerBytes);
            CryptographicOperations.ZeroMemory(stateKey);
            observation?.Record(TrustedLiveProbeStage.SecretsZeroed);
            provider = null;
            ambientStateKey = null;
            stateKeyBase64 = null;
            var safetyCode = ApplySafetyClassification(
                code,
                sensitiveBytesObserved,
                cleanupFailed);
            if (safetyCode != code)
            {
                status = "failed";
            }
            code = safetyCode;
            if (code == TrustedLiveCodes.Canary)
            {
                phases.Clear();
                failure = null;
            }

            observation?.Record(TrustedLiveProbeStage.CompletionStarted);
            Console.Out.WriteLine(TrustedLiveReceiptCodec.WriteCompletion(
                status,
                code,
                testedSha,
                workflowRef,
                workflowSha,
                fixtureSha256,
                buildPair,
                phases,
                failure));
            observation?.Record(TrustedLiveProbeStage.CompletionWritten);
        }

        observation?.Record(TrustedLiveProbeStage.CoreReturning);
        return status == "passed" ? 0 : 1;
    }

    // Test fixture only: fixed synthetic values and this fixture's own tree
    // probe. No user-selected executable, child argv, SDK or accepted authority.
    internal static async Task<int> RunProbeAsync(string root)
    {
        try
        {
            var prefix = "apr-r3-trusted-live-";
            var normalized = Path.GetFullPath(root);
            var parent = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
            var name = Path.GetFileName(normalized);
            if (!OperatingSystem.IsLinux() || normalized != root ||
                Path.GetDirectoryName(normalized) != parent ||
                !name.StartsWith(prefix, StringComparison.Ordinal) ||
                !Guid.TryParseExact(name[prefix.Length..], "N", out _) ||
                !Directory.Exists(root) ||
                (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0 ||
                Directory.Exists(Path.Join(root, "r3-live-proof-sensitive")))
                return 2;
            var artifact = Path.Join(AppContext.BaseDirectory,
                "AgenticPrReview.Runtime.LiveAgentVerifierFixture");
            var assembly = typeof(Program).Assembly.Location;
            if (!File.Exists(artifact) || !File.Exists(assembly)) return 2;
            var corpus = Path.Join(root, "supervisor-probe-corpus.json");
            var manifest = Path.Join(root, "supervisor-probe-build.json");
            using (var file = new FileStream(corpus, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                file.Write("{}"u8);
            var executionHash = LiveAgentFreshProcessDomain.RawSha256(File.ReadAllBytes(artifact));
            var assemblyHash = LiveAgentFreshProcessDomain.RawSha256(File.ReadAllBytes(assembly));
            var pair = new VerifierBuildPair(VerifierExecutionKinds.Framework, executionHash, assemblyHash,
                VerifierBuildPairDomain.ComputeSha256(VerifierExecutionKinds.Framework, executionHash, assemblyHash));
            using (var file = new FileStream(manifest, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                file.Write(VerifierBuildPairDomain.Write(pair));
            string[] args = ["live-supervise", "--root", Path.Join(root, "r3-live-proof-sensitive"),
                "--corpus", corpus, "--output", Path.Join(root, "r3-live-proof-sensitive", "private", "completion.json"),
                "--execution-kind", VerifierExecutionKinds.Framework, "--execution-artifact", artifact,
                "--build-pair-manifest", manifest];
            using var observation = TrustedLiveProbeObservation.TryCreate(root);
            return await RunCoreAsync(args, new string('a', 40), WorkflowRef, new string('a', 40),
                "supervisor-probe-synthetic", null, root, observation);
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or
            UnauthorizedAccessException or NotSupportedException)
        {
            return 2;
        }
    }

    private static async Task<TrustedLiveRunResult> RunProbePhaseAsync(VerifierCommand command,
        string provider, string stateKeyBase64, CancellationToken token, TrustedLiveProbeObservation? observation)
    {
        var phaseRoot = Path.Join(command.Root, "must-find");
        Directory.CreateDirectory(phaseRoot);
        await TrustedLiveProcessLauncher.RunAsync(command.ExecutionArtifact,
            ["launcher-tree-probe", Path.Join(phaseRoot, "probe.pid")], command.Root,
            provider, stateKeyBase64, ChildTimeout, token, observation);
        // The synthetic lifecycle probe can never emit a passing live receipt.
        return new(TrustedLiveCodes.Infrastructure, null);
    }

    internal static bool TryConsumeAdmittedReceipt(
        string phaseRoot,
        string verb)
    {
        try
        {
            var receiptPath = Path.Join(
                phaseRoot,
                "private",
                string.Concat(verb, ".json"));
            if (!File.Exists(receiptPath))
            {
                return false;
            }

            File.Delete(receiptPath);
            return !File.Exists(receiptPath);
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            IOException or
            NotSupportedException or
            UnauthorizedAccessException or
            System.Security.SecurityException)
        {
            return false;
        }
    }

    internal static bool TryAdmitPhase(
        TrustedLivePhaseExecution execution,
        VerifierScenario scenario,
        VerifierBuildPair buildPair,
        List<TrustedLivePhaseReceipt> phases,
        out string code,
        out TrustedLiveFailureEvidence? failure)
    {
        code = ClassifyFailure(execution);
        failure = null;
        var receipt = execution.Receipt;
        var receiptMatchesInvocation = receipt is not null &&
            TrustedLiveDomain.ReceiptIsAdmitted(receipt) &&
            receipt.Scenario == scenario.ToString() &&
            receipt.ExecutionArtifactSha256 ==
                buildPair.ExecutionArtifactSha256 &&
            receipt.BuildPairSha256 == buildPair.BuildPairSha256;
        if (execution.CanaryDetected ||
            execution.Process is not
            {
                ExitCode: 0,
                TimedOut: false,
                Cancelled: false,
                SensitiveBytesObserved: false,
                OutputLimitExceeded: false,
            } ||
            receipt is not
            {
                Status: "passed",
                HandoffReady: true,
                AcceptedTupleValidated: true,
             } ||
            !receiptMatchesInvocation ||
            !PhaseMatchesScenario(receipt, scenario))
        {
            failure = CreateFailureEvidence(
                execution,
                scenario,
                receiptMatchesInvocation);
            return false;
        }

        phases.Add(receipt);
        code = TrustedLiveCodes.Passed;
        return true;
    }

    internal static TrustedLiveFailureEvidence CreateFailureEvidence(
        TrustedLivePhaseExecution execution,
        VerifierScenario scenario,
        bool receiptMatchesInvocation)
    {
        var receipt = execution.Receipt;
        if (execution.CanaryDetected ||
            execution.Process.SensitiveBytesObserved)
        {
            return new TrustedLiveFailureEvidence(
                scenario.ToString(),
                TrustedLiveFailureKinds.Canary,
                Stage: null,
                Category: null,
                TrustedLiveCodes.Canary,
                execution.Process.ExitCode,
                ModelCalls: 0,
                ToolCalls: 0,
                ProductCode: null,
                OutcomeCode: null,
                QualityClassification: null,
                QualityCode: null);
        }
        if (receipt is null && execution.ReceiptFilePresent ||
            receipt is not null &&
            (!receiptMatchesInvocation || receipt.Status != "failed"))
        {
            return new TrustedLiveFailureEvidence(
                scenario.ToString(),
                TrustedLiveFailureKinds.ReceiptInvalid,
                Stage: null,
                Category: null,
                TrustedLiveDiagnosticCodes.PhaseReceiptInvalid,
                execution.Process.ExitCode,
                ModelCalls: 0,
                ToolCalls: 0,
                ProductCode: null,
                OutcomeCode: null,
                QualityClassification: null,
                QualityCode: null);
        }
        if (receipt is null)
        {
            var privateFailure = execution.PrivateFailure;
            return new TrustedLiveFailureEvidence(
                scenario.ToString(),
                privateFailure is null
                    ? TrustedLiveFailureKinds.ReceiptMissing
                    : TrustedLiveFailureKinds.Child,
                privateFailure?.Stage,
                privateFailure?.Category,
                privateFailure is null
                    ? TrustedLiveDiagnosticCodes.PhaseReceiptMissing
                    : TrustedLiveDiagnosticCodes.PhaseChildFailed,
                execution.Process.ExitCode,
                ModelCalls: 0,
                ToolCalls: 0,
                ProductCode: null,
                OutcomeCode: null,
                QualityClassification: null,
                QualityCode: null);
        }

        return new TrustedLiveFailureEvidence(
            scenario.ToString(),
            TrustedLiveFailureKinds.Application,
            TrustedLiveDomain.ApplicationStage(receipt.OutcomeCode),
            Category: null,
            receipt.OutcomeCode,
            execution.Process.ExitCode,
            receipt.ModelCalls,
            receipt.ToolCalls,
            receipt.ProductCode,
            receipt.OutcomeCode,
            receipt.QualityClassification,
            receipt.QualityCode);
    }

    private static bool PhaseMatchesScenario(
        TrustedLivePhaseReceipt receipt,
        VerifierScenario scenario)
    {
        if (!LiveAgentFreshProcessDomain.IsSha256(
                receipt.InvocationIdentitySha256) ||
            !LiveAgentFreshProcessDomain.IsSha256(receipt.LineageSha256) ||
            !LiveAgentFreshProcessDomain.IsSha256(
                receipt.AcceptedSessionSha256) ||
            !LiveAgentFreshProcessDomain.IsSha256(
                receipt.AcceptedEnvelopeSha256) ||
            !LiveAgentFreshProcessDomain.IsSha256(receipt.TerminalSha256) ||
            receipt.ModelCalls is < 1 or > 8 ||
            receipt.ToolCalls is < 1 or > 16)
        {
            return false;
        }
        var qualityPassed = receipt.QualityStatus == "passed" &&
            receipt.QualityClassification == "quality" &&
            receipt.QualityCode == "r3_quality_passed";
        return scenario switch
        {
            VerifierScenario.MustFind =>
                receipt.Generation == 0 &&
                receipt.Transition == "same_head" &&
                receipt.OutcomeCode == TrustedLiveSuccessCodes.MustFind &&
                qualityPassed,
            VerifierScenario.MustNotFind =>
                receipt.Generation == 0 &&
                receipt.Transition == "same_head" &&
                receipt.OutcomeCode ==
                    TrustedLiveSuccessCodes.MustNotFind &&
                qualityPassed,
            VerifierScenario.ContinuationSeed =>
                receipt.Generation == 0 &&
                receipt.Transition == "same_head" &&
                receipt.OutcomeCode ==
                    TrustedLiveSuccessCodes.ContinuationSeed &&
                receipt.QualityStatus is null &&
                receipt.QualityClassification is null &&
                receipt.QualityCode is null,
            VerifierScenario.ContinuationRestore =>
                receipt.Generation == 1 &&
                receipt.Transition == "verified_ahead" &&
                receipt.OutcomeCode ==
                    TrustedLiveSuccessCodes.ContinuationRestore &&
                qualityPassed,
            _ => false,
        };
    }

    internal static string ClassifyFailure(
        TrustedLivePhaseExecution execution)
    {
        if (execution.CanaryDetected ||
            execution.Process.SensitiveBytesObserved)
        {
            return TrustedLiveCodes.Canary;
        }
        if (execution.Process.TimedOut)
        {
            return TrustedLiveCodes.Timeout;
        }
        var receipt = execution.Receipt;
        if (execution.Process.Cancelled ||
            execution.Process.OutputLimitExceeded ||
            receipt is null)
        {
            return TrustedLiveCodes.Infrastructure;
        }
        var qualityCode = receipt.QualityCode;
        if (receipt.OutcomeCode == qualityCode)
        {
            if (qualityCode is
                "r3_quality_required_tool_missing" or
                "r3_quality_required_tool_wrong")
            {
                return TrustedLiveCodes.MissingTool;
            }
            if (qualityCode == "r3_quality_required_observation_missing")
            {
                return TrustedLiveCodes.Grounding;
            }
            if (qualityCode == "r3_quality_expected_finding_missing")
            {
                return TrustedLiveCodes.MustFind;
            }
            if (qualityCode == "r3_quality_prohibited_finding")
            {
                return TrustedLiveCodes.MustNotFind;
            }
            if (qualityCode is
                "r3_quality_prior_fact_missing" or
                "r3_quality_state_failed")
            {
                return TrustedLiveCodes.Continuation;
            }
            if (receipt.QualityClassification == "provider" ||
                qualityCode == "r3_quality_provider_failed")
            {
                return TrustedLiveCodes.Provider;
            }
        }
        var productCode = receipt.ProductCode;
        if (TrustedLiveDomain.TryClassifyProductFailure(
                productCode,
                out var classification))
        {
            return classification;
        }
        return TrustedLiveCodes.Infrastructure;
    }

    private static bool ProvenanceIsExact(
        string sha,
        string workflowRef,
        string workflowSha) =>
        IsGitSha(sha) &&
        PublicEnvironment("GITHUB_REPOSITORY") == Repository &&
        PublicEnvironment("GITHUB_REF") == MainRef &&
        workflowRef == WorkflowRef &&
        workflowSha == sha;

    private static bool RootIsExact(string root)
    {
        var runnerTemp = PublicEnvironment("RUNNER_TEMP");
        if (!Path.IsPathFullyQualified(runnerTemp))
        {
            return false;
        }
        return StringComparer.Ordinal.Equals(
            root,
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Join(
                runnerTemp,
                "r3-live-proof-sensitive"))));
    }

    private static bool IsGitSha(string value) =>
        value.Length == 40 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static bool ContainsSensitiveBytes(
        string root,
        params byte[][] canaries)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return false;
            }
            foreach (var file in Directory.EnumerateFiles(
                         root,
                         "*",
                         SearchOption.AllDirectories))
            {
                var info = new FileInfo(file);
                if (info.Length > 16 * 1024 * 1024)
                {
                    return true;
                }
                var bytes = File.ReadAllBytes(file);
                if (canaries.Any(canary =>
                        canary.Length != 0 &&
                        bytes.AsSpan().IndexOf(canary) >= 0))
                {
                    return true;
                }
            }
            return false;
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            IOException or
            NotSupportedException or
            UnauthorizedAccessException or
            System.Security.SecurityException)
        {
            return true;
        }
    }

    internal static string ApplySafetyClassification(
        string code,
        bool sensitiveBytesObserved,
        bool cleanupFailed) => sensitiveBytesObserved
            ? TrustedLiveCodes.Canary
            : cleanupFailed
                ? TrustedLiveCodes.Cleanup
                : code;

    private static PosixSignalRegistration? RegisterSignal(
        PosixSignal signal,
        CancellationTokenSource cancellationSource, TrustedLiveProbeObservation? observation)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }
        return PosixSignalRegistration.Create(signal, context =>
        {
            context.Cancel = true;
            observation?.Record(TrustedLiveProbeStage.SignalReceived);
            cancellationSource.Cancel();
            observation?.Record(TrustedLiveProbeStage.CancellationReturned);
        });
    }

    private static bool TryDeleteSensitiveRoot(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
            return !Directory.Exists(root);
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            IOException or
            NotSupportedException or
            UnauthorizedAccessException or
            System.Security.SecurityException)
        {
            return false;
        }
    }

    private static string PublicEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) ?? string.Empty;
}

internal static class TrustedLiveProcessLauncher
{
    private const int MaximumCapturedCharacters = 64 * 1024;

    internal static async Task<TrustedLiveProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string provider,
        string stateKeyBase64,
        TimeSpan timeout,
        CancellationToken cancellationToken, TrustedLiveProbeObservation? observation = null)
    {
        var canaries = new[] { provider, stateKeyBase64 }
            .Where(value => !string.IsNullOrEmpty(value))
            .ToArray();
        if (arguments.Any(argument => canaries.Any(canary =>
                argument.Contains(canary, StringComparison.Ordinal))))
        {
            return new TrustedLiveProcessResult(
                1,
                TimedOut: false,
                Cancelled: false,
                SensitiveBytesObserved: true,
                OutputLimitExceeded: false,
                string.Empty,
                string.Empty);
        }
        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment.Clear();
        start.Environment["AGENTIC_REVIEW_DEEPSEEK_API_KEY"] = provider;
        start.Environment["AGENTIC_REVIEW_R3_STATE_KEY_B64"] = stateKeyBase64;
        var home = Path.Join(workingDirectory, "home");
        var temporary = Path.Join(workingDirectory, "tmp");
        start.Environment["HOME"] = home;
        start.Environment["TMPDIR"] = temporary;
        start.Environment["LANG"] = "C.UTF-8";
        start.Environment["LC_ALL"] = "C.UTF-8";
        start.Environment["TZ"] = "UTC";
        // Darwin normalizes this at startup; never inherit the caller's value.
        if (OperatingSystem.IsMacOS()) start.Environment["__CF_USER_TEXT_ENCODING"] = "0:0:0";
        if (OperatingSystem.IsWindows())
        {
            AddPublicRuntimeVariable(start, "SystemRoot");
            AddPublicRuntimeVariable(start, "WINDIR");
        }
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(temporary);

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
            {
                return new TrustedLiveProcessResult(
                    1,
                    TimedOut: false,
                    Cancelled: false,
                    SensitiveBytesObserved: false,
                    OutputLimitExceeded: false,
                    string.Empty,
                    string.Empty);
            }
        }
        finally
        {
            start.Environment.Clear();
        }
        observation?.Record(TrustedLiveProbeStage.LauncherStarted);
        var output = ReadBoundedAndScanAsync(
            process.StandardOutput,
            canaries);
        var error = ReadBoundedAndScanAsync(
            process.StandardError,
            canaries);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var timedOut = false;
        var cancelled = false;
        try
        {
            observation?.Record(TrustedLiveProbeStage.ExitWaitStarted);
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            observation?.Record(TrustedLiveProbeStage.CancellationObserved);
            cancelled = cancellationToken.IsCancellationRequested;
            timedOut = !cancelled;
            if (!process.HasExited)
            {
                observation?.Record(TrustedLiveProbeStage.KillStarted);
                process.Kill(entireProcessTree: true);
                observation?.Record(TrustedLiveProbeStage.KillReturned);
            }
            await process.WaitForExitAsync(CancellationToken.None);
            observation?.Record(TrustedLiveProbeStage.Reaped);
        }
        observation?.Record(TrustedLiveProbeStage.OutputAwait);
        var standardOutput = await output;
        observation?.Record(TrustedLiveProbeStage.OutputClosed);
        var standardError = await error;
        observation?.Record(TrustedLiveProbeStage.ErrorClosed);
        var sensitiveBytesObserved =
            standardOutput.SensitiveBytesObserved ||
            standardError.SensitiveBytesObserved;
        var outputLimitExceeded = standardOutput.LimitExceeded ||
            standardError.LimitExceeded;
        return new TrustedLiveProcessResult(
            process.ExitCode,
            timedOut,
            cancelled,
            sensitiveBytesObserved,
            outputLimitExceeded,
            sensitiveBytesObserved || outputLimitExceeded
                ? string.Empty
                : standardOutput.Text,
            sensitiveBytesObserved || outputLimitExceeded
                ? string.Empty
                : standardError.Text);
    }

    private static async Task<TrustedLiveBoundedCapture>
        ReadBoundedAndScanAsync(
            StreamReader reader,
            IReadOnlyList<string> canaries)
    {
        var capture = new StringBuilder(MaximumCapturedCharacters);
        var buffer = new char[4096];
        var longestCanary = canaries.Count == 0
            ? 0
            : canaries.Max(value => value.Length);
        var carry = string.Empty;
        var sensitiveBytesObserved = false;
        var limitExceeded = false;
        long totalCharacters = 0;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory());
            if (read == 0)
            {
                break;
            }
            totalCharacters += read;
            var chunk = new string(buffer, 0, read);
            var scan = string.Concat(carry, chunk);
            if (canaries.Any(canary =>
                    scan.Contains(canary, StringComparison.Ordinal)))
            {
                sensitiveBytesObserved = true;
            }
            var carryLength = Math.Min(
                Math.Max(longestCanary - 1, 0),
                scan.Length);
            carry = carryLength == 0
                ? string.Empty
                : scan[^carryLength..];
            if (capture.Length < MaximumCapturedCharacters)
            {
                var remaining = MaximumCapturedCharacters - capture.Length;
                capture.Append(chunk.AsSpan(0, Math.Min(remaining, chunk.Length)));
            }
            limitExceeded = totalCharacters > MaximumCapturedCharacters;
        }
        return new TrustedLiveBoundedCapture(
            capture.ToString(),
            sensitiveBytesObserved,
            limitExceeded);
    }

    private static void AddPublicRuntimeVariable(
        ProcessStartInfo start,
        string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrEmpty(value))
        {
            start.Environment[name] = value;
        }
    }
}

internal sealed record TrustedLiveBoundedCapture(
    string Text,
    bool SensitiveBytesObserved,
    bool LimitExceeded);

internal sealed record TrustedLivePhaseExecution(
    TrustedLiveProcessResult Process,
    TrustedLivePhaseReceipt? Receipt,
    bool CanaryDetected,
    bool ReceiptFilePresent = false,
    TrustedLivePrivateFailure? PrivateFailure = null);

internal sealed record TrustedLiveRunResult(
    string Code,
    TrustedLiveFailureEvidence? Failure);

internal static class TrustedLiveLauncherProbe
{
    internal static async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        if (args is ["launcher-pipe-parent", var pipeMode, var pipePidPath])
            return await RunPipeParentAsync(pipeMode, pipePidPath);
        if (args is ["launcher-pipe-child", "delayed-close"])
        {
            await Task.Delay(TimeSpan.FromMilliseconds(750));
            Console.Out.WriteLine("APR_R3_PIPE_CHILD_OUT");
            Console.Error.WriteLine("APR_R3_PIPE_CHILD_ERROR");
            return 0;
        }
        if (args is ["launcher-pipe-child", "hold"])
        {
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 0;
        }
        if (args is ["launcher-supervisor-probe", var root])
            return await TrustedLiveSupervisor.RunProbeAsync(root);
        if (args is ["launcher-dispatch-probe", ..])
        {
            var runnerTemp = Environment.GetEnvironmentVariable("RUNNER_TEMP");
            var workspace = Environment.GetEnvironmentVariable(
                "GITHUB_WORKSPACE");
            if (Environment.GetEnvironmentVariable(
                    "APR_R3_TRUSTED_LIVE_DISPATCH_PROBE") != "1" ||
                !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(
                    "AGENTIC_REVIEW_DEEPSEEK_API_KEY")) ||
                !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(
                    "AGENTIC_REVIEW_R3_STATE_KEY_B64")) ||
                runnerTemp is null ||
                workspace is null ||
                !Path.IsPathFullyQualified(runnerTemp) ||
                !Path.IsPathFullyQualified(workspace) ||
                !VerifierArguments.TryParse(args.Skip(1).ToArray(), out var command) ||
                command is null ||
                command.Verb != "live-supervise" ||
                command.ExecutionKind != VerifierExecutionKinds.NativeAot ||
                !VerifierBuildPairDomain.TryAdmit(command, out var buildPair) ||
                buildPair is null ||
                !StringComparer.Ordinal.Equals(
                    command.Root,
                    Path.Join(runnerTemp, "r3-live-proof-sensitive")) ||
                !StringComparer.Ordinal.Equals(
                    command.Corpus,
                    Path.Join(
                        workspace,
                        "runtime",
                        "tests",
                        "fixtures",
                        "agent",
                        "r3-quality",
                        "corpus.json")) ||
                !StringComparer.Ordinal.Equals(
                    command.Output,
                    Path.Join(
                        runnerTemp,
                        "r3-live-proof-sensitive",
                        "private",
                        "completion.json")))
            {
                return 2;
            }
            Console.Out.WriteLine("APR_R3_TRUSTED_LIVE_DISPATCH_OK");
            return 0;
        }
        if (args is ["launcher-grandchild"])
        {
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 0;
        }
        if (args is ["launcher-tree-probe", var pidPath])
        {
            var start = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? string.Empty,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("launcher-grandchild");
            using var child = Process.Start(start);
            if (child is null)
            {
                return 2;
            }
            File.WriteAllText(
                pidPath,
                child.Id.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 0;
        }
        if (args is not ["launcher-probe", var delayText, ..] ||
            !int.TryParse(
                delayText,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var delay) ||
            delay is < 0 or > 60_000)
        {
            return 2;
        }
        if (delay != 0)
        {
            await Task.Delay(delay);
        }
        var provider = Environment.GetEnvironmentVariable(
            "AGENTIC_REVIEW_DEEPSEEK_API_KEY");
        var state = Environment.GetEnvironmentVariable(
            "AGENTIC_REVIEW_R3_STATE_KEY_B64");
        if (args.Contains("emit-provider-large") && provider is not null)
        {
            Console.Out.Write(new string('x', 70 * 1024));
            Console.Out.Write(provider);
            return 1;
        }
        if (args.Contains("emit-provider") && provider is not null)
        {
            Console.Error.Write(provider);
            return 1;
        }
        if (args.Contains("write-provider-file") && provider is not null)
        {
            File.WriteAllText(
                Path.Join(Environment.CurrentDirectory, "provider.leak"),
                provider);
            return 1;
        }
        using var stream = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteBoolean(
                "provider_in_arguments",
                provider is not null && args.Any(argument =>
                    argument.Contains(provider, StringComparison.Ordinal)));
            writer.WriteBoolean(
                "state_in_arguments",
                state is not null && args.Any(argument =>
                    argument.Contains(state, StringComparison.Ordinal)));
            writer.WriteStartArray("environment_names");
            foreach (var name in Environment.GetEnvironmentVariables().Keys
                         .OfType<string>()
                         .Order(StringComparer.Ordinal))
            {
                writer.WriteStringValue(name);
            }
            writer.WriteEndArray();
            writer.WriteNumber("process_id", Environment.ProcessId);
            writer.WriteEndObject();
        }
        Console.Out.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
        return 0;
    }

    private static async Task<int> RunPipeParentAsync(string mode, string pidPath)
    {
        if (mode is not ("closed" or "delayed-close" or "parent-exits" or "parent-open")) return 2;
        var normalized = Path.GetFullPath(pidPath);
        var root = Path.GetDirectoryName(normalized)!;
        var name = Path.GetFileName(root);
        const string prefix = "apr-r3-pipe-driver-";
        if (normalized != pidPath || Path.GetFileName(normalized) != "child.pid" ||
            Path.GetDirectoryName(root) != Path.TrimEndingDirectorySeparator(Path.GetTempPath()) ||
            !name.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(name[prefix.Length..], "N", out _) ||
            !Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            return 2;
        Console.Out.WriteLine("APR_R3_PIPE_PARENT_OUT");
        Console.Error.WriteLine("APR_R3_PIPE_PARENT_ERROR");
        if (mode == "closed") return 0;
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        start.ArgumentList.Add("launcher-pipe-child");
        start.ArgumentList.Add(mode == "delayed-close" ? "delayed-close" : "hold");
        using var child = Process.Start(start);
        if (child is null) return 2;
        using (var file = new FileStream(pidPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            file.Write(Encoding.ASCII.GetBytes(child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if (mode == "parent-open") await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }

}

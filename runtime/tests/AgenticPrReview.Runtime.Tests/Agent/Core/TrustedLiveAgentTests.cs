using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.LiveAgentVerifierFixture;

namespace AgenticPrReview.Runtime.Tests.Agent.Core;

public sealed class TrustedLiveAgentTests : IDisposable
{
    private readonly string root = Path.Join(
        Path.GetTempPath(),
        string.Concat("apr-r3-trusted-live-", Guid.NewGuid().ToString("N")));

    [Fact]
    public void VerifierOwnsExactlyTheDeterministicAndTrustedProfiles()
    {
        var profiles = typeof(LiveAgentVerifierProfile).Assembly.GetTypes()
            .Where(type => type is { IsInterface: false, IsAbstract: false } &&
                typeof(ILiveAgentFreshProcessProfile).IsAssignableFrom(type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [typeof(LiveAgentVerifierProfile), typeof(TrustedLiveAgentProfile)],
            profiles);
        Assert.Contains(
            typeof(TrustedLiveAgentExecution).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(R3LiveAgentTransportFactory));
        Assert.DoesNotContain(
            typeof(TrustedLiveAgentExecution).Assembly.GetTypes(),
            type => type != typeof(VerifierTransportFactory) &&
                type is { IsInterface: false, IsAbstract: false } &&
                typeof(IR3LiveAgentTransportFactory).IsAssignableFrom(type));
    }

    [Fact]
    public async Task LauncherUsesPublicArgumentsAndAnExactChildEnvironment()
    {
        Directory.CreateDirectory(root);
        var provider = "provider-launcher-canary";
        var state = Convert.ToBase64String(new byte[32]);
        var result = await RunProbeAsync(
            ["launcher-probe", "0", "public-marker"],
            provider,
            state,
            TimeSpan.FromSeconds(10));

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.False(result.Cancelled);
        Assert.False(result.SensitiveBytesObserved);
        Assert.False(result.OutputLimitExceeded);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var value = document.RootElement;
        Assert.False(value.GetProperty("provider_in_arguments").GetBoolean());
        Assert.False(value.GetProperty("state_in_arguments").GetBoolean());
        var names = value.GetProperty("environment_names")
            .EnumerateArray()
            .Select(item => item.GetString())
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expected = new HashSet<string>(
            [
                "AGENTIC_REVIEW_DEEPSEEK_API_KEY",
                "AGENTIC_REVIEW_R3_STATE_KEY_B64",
                "HOME",
                "TMPDIR",
                "LANG",
                "LC_ALL",
                "TZ",
            ],
            StringComparer.OrdinalIgnoreCase);
        if (OperatingSystem.IsWindows())
        {
            expected.Add("SystemRoot");
            expected.Add("WINDIR");
        }
        Assert.True(
            expected.SetEquals(names),
            string.Join(",", names.Order(StringComparer.OrdinalIgnoreCase)));
        Assert.DoesNotContain("GITHUB_TOKEN", names);
        Assert.DoesNotContain("ACTIONS_RUNTIME_TOKEN", names);
        Assert.DoesNotContain("AWS_SECRET_ACCESS_KEY", names);
        Assert.DoesNotContain("NPM_TOKEN", names);
    }

    [Fact]
    public async Task LauncherSerializesFreshProcessesAndTimesOut()
    {
        Directory.CreateDirectory(root);
        var first = await RunProbeAsync(
            ["launcher-probe", "0", "first"],
            "provider-canary",
            Convert.ToBase64String(new byte[32]),
            TimeSpan.FromSeconds(10));
        var second = await RunProbeAsync(
            ["launcher-probe", "0", "second"],
            "provider-canary",
            Convert.ToBase64String(new byte[32]),
            TimeSpan.FromSeconds(10));
        using var firstDocument = JsonDocument.Parse(first.StandardOutput);
        using var secondDocument = JsonDocument.Parse(second.StandardOutput);
        Assert.NotEqual(
            firstDocument.RootElement.GetProperty("process_id").GetInt32(),
            secondDocument.RootElement.GetProperty("process_id").GetInt32());

        var timed = await RunProbeAsync(
            ["launcher-probe", "30000"],
            "provider-canary",
            Convert.ToBase64String(new byte[32]),
            TimeSpan.FromMilliseconds(100));
        Assert.True(timed.TimedOut);
        Assert.NotEqual(0, timed.ExitCode);
    }

    [Fact]
    public async Task LauncherTimeoutTerminatesTheProcessTree()
    {
        Directory.CreateDirectory(root);
        var pidPath = Path.Join(root, "grandchild.pid");
        var timed = await RunProbeAsync(
            ["launcher-tree-probe", pidPath],
            "provider-canary",
            Convert.ToBase64String(new byte[32]),
            TimeSpan.FromSeconds(2));
        Assert.True(timed.TimedOut);
        Assert.True(File.Exists(pidPath));
        var processId = int.Parse(
            File.ReadAllText(pidPath),
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.False(ProcessIsRunning(processId));
    }

    [Fact]
    public async Task LauncherCancellationTerminatesTheProcessTree()
    {
        Directory.CreateDirectory(root);
        var pidPath = Path.Join(root, "cancelled-grandchild.pid");
        using var cancellationSource = new CancellationTokenSource(
            TimeSpan.FromSeconds(1));
        var result = await RunProbeAsync(
            ["launcher-tree-probe", pidPath],
            "provider-canary",
            Convert.ToBase64String(new byte[32]),
            TimeSpan.FromSeconds(30),
            cancellationSource.Token);

        Assert.True(result.Cancelled);
        Assert.False(result.TimedOut);
        Assert.True(File.Exists(pidPath));
        var processId = int.Parse(
            File.ReadAllText(pidPath),
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.False(ProcessIsRunning(processId));
    }

    [Fact]
    public async Task SupervisorSigtermTerminatesChildAndCleansSensitiveRoot()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        Directory.CreateDirectory(root);
        var sensitiveRoot = Path.Join(root, "r3-live-proof-sensitive");
        var phasePidPath = Path.Join(sensitiveRoot, "must-find", "probe.pid");
        var probe = Path.Join(root, "long-running-probe.sh");
        File.WriteAllText(
            probe,
            "#!/usr/bin/env bash\nset -euo pipefail\nphase_root=\"$3\"\n" +
            "mkdir -p -- \"${phase_root}\"\nsleep 300 &\nchild=$!\n" +
            "printf '%s\\n' \"${child}\" >\"${phase_root}/probe.pid\"\n" +
            "wait \"${child}\"\n");
        File.SetUnixFileMode(
            probe,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute);
        var corpus = Path.Join(root, "corpus.json");
        File.WriteAllText(corpus, "{}\n");
        var manifest = WriteBuildPairManifest(probe);
        var sha = new string('a', 40);
        var start = new ProcessStartInfo
        {
            FileName = VerifierExecutable(),
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
        {
            "live-supervise",
            "--root",
            sensitiveRoot,
            "--corpus",
            corpus,
            "--output",
            Path.Join(sensitiveRoot, "private", "completion.json"),
            "--execution-kind",
            VerifierExecutionKinds.Framework,
            "--execution-artifact",
            probe,
            "--build-pair-manifest",
            manifest,
        })
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment.Clear();
        start.Environment["AGENTIC_REVIEW_DEEPSEEK_API_KEY"] =
            "sigterm-provider-canary";
        start.Environment["GITHUB_REPOSITORY"] =
            "SolusQuest/agentic-pr-review";
        start.Environment["GITHUB_REF"] = "refs/heads/main";
        start.Environment["GITHUB_SHA"] = sha;
        start.Environment["GITHUB_WORKFLOW_SHA"] = sha;
        start.Environment["GITHUB_WORKFLOW_REF"] =
            "SolusQuest/agentic-pr-review/.github/workflows/" +
            "r3-live-proof.yml@refs/heads/main";
        start.Environment["RUNNER_TEMP"] = root;
        using var process = Process.Start(start);
        Assert.NotNull(process);
        await WaitForFileAsync(phasePidPath, TimeSpan.FromSeconds(10));
        var childPid = int.Parse(
            File.ReadAllText(phasePidPath),
            System.Globalization.CultureInfo.InvariantCulture);

        using (var signal = Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/kill",
            UseShellExecute = false,
            ArgumentList = { "-TERM", process.Id.ToString(
                System.Globalization.CultureInfo.InvariantCulture) },
        }))
        {
            Assert.NotNull(signal);
            await signal.WaitForExitAsync();
            Assert.Equal(0, signal.ExitCode);
        }

        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(string.Empty, error);
        Assert.Equal(1, process.ExitCode);
        Assert.False(ProcessIsRunning(childPid));
        Assert.False(Directory.Exists(sensitiveRoot));
        Assert.Single(output.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task LauncherRejectsEmbeddedArgumentAndOversizedOutputLeaks()
    {
        Directory.CreateDirectory(root);
        const string provider = "provider-embedded-canary";
        var state = Convert.ToBase64String(new byte[32]);
        var embedded = await RunProbeAsync(
            ["launcher-probe", "0", $"--credential={provider}"],
            provider,
            state,
            TimeSpan.FromSeconds(10));
        Assert.True(embedded.SensitiveBytesObserved);
        Assert.Equal(1, embedded.ExitCode);
        Assert.Equal(string.Empty, embedded.StandardOutput);

        var oversized = await RunProbeAsync(
            ["launcher-probe", "0", "emit-provider-large"],
            provider,
            state,
            TimeSpan.FromSeconds(10));
        Assert.True(oversized.SensitiveBytesObserved);
        Assert.True(oversized.OutputLimitExceeded);
        Assert.Equal(string.Empty, oversized.StandardOutput);
        Assert.Equal(string.Empty, oversized.StandardError);
    }

    [Fact]
    public async Task FileLeakFromAFailedChildDominatesTheProviderFailure()
    {
        Directory.CreateDirectory(root);
        const string providerText = "provider-file-canary";
        var provider = Encoding.UTF8.GetBytes(providerText);
        var result = await RunProbeAsync(
            ["launcher-probe", "0", "write-provider-file"],
            providerText,
            Convert.ToBase64String(new byte[32]),
            TimeSpan.FromSeconds(10));

        Assert.Equal(1, result.ExitCode);
        Assert.False(result.SensitiveBytesObserved);
        var observed = TrustedLiveSupervisor.ContainsSensitiveBytes(
            root,
            provider);
        Assert.True(observed);
        Assert.Equal(
            TrustedLiveCodes.Canary,
            TrustedLiveSupervisor.ApplySafetyClassification(
                TrustedLiveCodes.Provider,
                observed,
                cleanupFailed: false));
    }

    [Theory]
    [InlineData("agent_chat_failed", null, null, TrustedLiveCodes.Provider)]
    [InlineData("other", "provider", "r3_quality_provider_failed", TrustedLiveCodes.Provider)]
    [InlineData("other", null, "r3_quality_required_tool_missing", TrustedLiveCodes.MissingTool)]
    [InlineData("other", null, "r3_quality_required_observation_missing", TrustedLiveCodes.Grounding)]
    [InlineData("other", null, "r3_quality_expected_finding_missing", TrustedLiveCodes.MustFind)]
    [InlineData("other", null, "r3_quality_prohibited_finding", TrustedLiveCodes.MustNotFind)]
    [InlineData("other", null, "r3_quality_prior_fact_missing", TrustedLiveCodes.Continuation)]
    [InlineData("agent_terminal_invalid", null, null, TrustedLiveCodes.Grounding)]
    [InlineData("unknown", null, "unknown", TrustedLiveCodes.Infrastructure)]
    public void FailureClassificationIsExhaustiveAndStable(
        string productCode,
        string? qualityClassification,
        string? qualityCode,
        string expected)
    {
        var receipt = FailureReceipt(
            productCode,
            qualityClassification,
            qualityCode);
        var execution = new TrustedLivePhaseExecution(
            new TrustedLiveProcessResult(
                1,
                TimedOut: false,
                Cancelled: false,
                SensitiveBytesObserved: false,
                OutputLimitExceeded: false,
                string.Empty,
                string.Empty),
            receipt,
            CanaryDetected: false);

        Assert.Equal(expected, TrustedLiveSupervisor.ClassifyFailure(execution));
    }

    [Fact]
    public void MissingOrCanaryReceiptFailsClosed()
    {
        var process = new TrustedLiveProcessResult(
            1,
            TimedOut: false,
            Cancelled: false,
            SensitiveBytesObserved: false,
            OutputLimitExceeded: false,
            string.Empty,
            string.Empty);
        Assert.Equal(
            TrustedLiveCodes.Infrastructure,
            TrustedLiveSupervisor.ClassifyFailure(
                new TrustedLivePhaseExecution(process, null, false)));
        Assert.Equal(
            TrustedLiveCodes.Canary,
            TrustedLiveSupervisor.ClassifyFailure(
                new TrustedLivePhaseExecution(process, null, true)));
    }

    [Fact]
    public void FailedPhaseIsRetainedWithSanitizedEvidence()
    {
        var receipt = FailureReceipt(
            R3LiveAgentCodes.CompositionFailed,
            qualityClassification: null,
            qualityCode: null) with
        {
            OutcomeCode = R3LiveAgentDiagnosticCodes.TransportFailed,
        };
        var buildPair = new VerifierBuildPair(
            VerifierExecutionKinds.Framework,
            receipt.ExecutionArtifactSha256,
            new string('3', 64),
            receipt.BuildPairSha256);
        var execution = new TrustedLivePhaseExecution(
            new TrustedLiveProcessResult(
                1,
                TimedOut: false,
                Cancelled: false,
                SensitiveBytesObserved: false,
                OutputLimitExceeded: false,
                string.Empty,
                string.Empty),
            receipt,
            CanaryDetected: false,
            DiagnosticCode: receipt.OutcomeCode);
        var phases = new List<TrustedLivePhaseReceipt>();

        Assert.False(TrustedLiveSupervisor.TryAdmitPhase(
            execution,
            VerifierScenario.MustFind,
            buildPair,
            phases,
            out var code,
            out var failure));

        Assert.Equal(TrustedLiveCodes.Infrastructure, code);
        Assert.Equal(receipt, Assert.Single(phases));
        Assert.NotNull(failure);
        Assert.Equal(TrustedLiveDiagnosticCodes.PhaseReceipt, failure.Kind);
        Assert.Equal(
            R3LiveAgentDiagnosticCodes.TransportFailed,
            failure.DiagnosticCode);
        Assert.Equal(R3LiveAgentCodes.CompositionFailed, failure.ProductCode);
    }

    [Fact]
    public void CanaryFailureSuppressesAllReceiptFields()
    {
        var receipt = FailureReceipt(
            "provider-canary",
            qualityClassification: null,
            qualityCode: null);
        var buildPair = new VerifierBuildPair(
            VerifierExecutionKinds.Framework,
            receipt.ExecutionArtifactSha256,
            new string('3', 64),
            receipt.BuildPairSha256);
        var execution = new TrustedLivePhaseExecution(
            new TrustedLiveProcessResult(
                1,
                TimedOut: false,
                Cancelled: false,
                SensitiveBytesObserved: true,
                OutputLimitExceeded: false,
                string.Empty,
                string.Empty),
            receipt,
            CanaryDetected: true,
            DiagnosticCode: receipt.OutcomeCode);
        var phases = new List<TrustedLivePhaseReceipt>();

        Assert.False(TrustedLiveSupervisor.TryAdmitPhase(
            execution,
            VerifierScenario.MustFind,
            buildPair,
            phases,
            out var code,
            out var failure));

        Assert.Equal(TrustedLiveCodes.Canary, code);
        Assert.Empty(phases);
        Assert.NotNull(failure);
        Assert.Equal(TrustedLiveDiagnosticCodes.PhaseCanary, failure.Kind);
        Assert.Null(failure.ProductCode);
        Assert.Null(failure.OutcomeCode);
        Assert.Null(failure.QualityClassification);
        Assert.Null(failure.QualityCode);
    }

    [Theory]
    [InlineData("ArgumentNullException", "phase_exception_argument")]
    [InlineData("IOException", "phase_exception_io")]
    [InlineData("UnauthorizedAccessException", "phase_exception_access")]
    [InlineData("PlatformNotSupportedException", "phase_exception_unsupported")]
    [InlineData("CryptographicException", "phase_exception_cryptography")]
    [InlineData("JsonException", "phase_exception_json")]
    [InlineData("Win32Exception", "phase_exception_process")]
    [InlineData("InvalidOperationException", "phase_exception_other")]
    public void RawExceptionTypesMapToStablePublicDiagnostics(
        string exceptionType,
        string expected)
    {
        Assert.Equal(
            expected,
            TrustedLiveSupervisor.ClassifyException(exceptionType));
    }

    [Theory]
    [InlineData(false, TrustedLiveCodes.Provenance)]
    [InlineData(true, TrustedLiveCodes.Canary)]
    public async Task SupervisorEmitsOneSanitizedFailureRecord(
        bool injectAmbientStateKey,
        string expectedCode)
    {
        Directory.CreateDirectory(root);
        const string provider = "supervisor-provider-canary";
        const string ambientState = "supervisor-state-canary";
        var start = new ProcessStartInfo
        {
            FileName = VerifierExecutable(),
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("live-supervise");
        start.Environment.Clear();
        start.Environment["AGENTIC_REVIEW_DEEPSEEK_API_KEY"] = provider;
        if (injectAmbientStateKey)
        {
            start.Environment["AGENTIC_REVIEW_R3_STATE_KEY_B64"] = ambientState;
        }
        if (OperatingSystem.IsWindows())
        {
            start.Environment["SystemRoot"] =
                Environment.GetEnvironmentVariable("SystemRoot")!;
            start.Environment["WINDIR"] =
                Environment.GetEnvironmentVariable("WINDIR")!;
        }
        using var process = Process.Start(start);
        Assert.NotNull(process);
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(1, process.ExitCode);
        Assert.Equal(string.Empty, error);
        Assert.DoesNotContain(provider, output);
        Assert.DoesNotContain(ambientState, output);
        Assert.Single(output.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        using var document = JsonDocument.Parse(output);
        Assert.Equal("failed", document.RootElement.GetProperty("status").GetString());
        Assert.Equal(expectedCode, document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public void CompletionRecordIsBoundedAndDoesNotContainSecrets()
    {
        var buildPair = new VerifierBuildPair(
            VerifierExecutionKinds.Framework,
            new string('1', 64),
            new string('2', 64),
            new string('3', 64));
        var phase = new TrustedLivePhaseReceipt(
            VerifierScenario.MustFind.ToString(),
            "passed",
            "APR_R3_TRUSTED_LIVE_MUST_FIND_OK",
            R3LiveAgentCodes.Completed,
            0,
            "same_head",
            2,
            2,
            true,
            true,
            new string('4', 64),
            new string('5', 64),
            new string('6', 64),
            new string('7', 64),
            new string('8', 64),
            "passed",
            "quality",
            "r3_quality_passed",
            1,
            2,
            buildPair.ExecutionArtifactSha256,
            buildPair.BuildPairSha256);

        var value = TrustedLiveReceiptCodec.WriteCompletion(
            "passed",
            TrustedLiveCodes.Passed,
            new string('a', 40),
            "SolusQuest/agentic-pr-review/.github/workflows/" +
                "r3-live-proof.yml@refs/heads/main",
            new string('a', 40),
            new string('9', 64),
            buildPair,
            [phase],
            new TrustedLiveFailureEvidence(
                phase.Scenario,
                TrustedLiveDiagnosticCodes.PhaseReceipt,
                R3LiveAgentDiagnosticCodes.TransportFailed,
                1,
                R3LiveAgentCodes.CompositionFailed,
                R3LiveAgentDiagnosticCodes.TransportFailed,
                QualityClassification: null,
                QualityCode: null));

        Assert.DoesNotContain("provider-launcher-canary", value);
        Assert.DoesNotContain("reasoning_content", value);
        Assert.DoesNotContain("Authorization", value);
        using var document = JsonDocument.Parse(value);
        Assert.Equal(1, document.RootElement.GetProperty("phase_count").GetInt32());
        var failure = document.RootElement.GetProperty("failure");
        Assert.Equal(
            R3LiveAgentDiagnosticCodes.TransportFailed,
            failure.GetProperty("diagnostic_code").GetString());
        Assert.DoesNotContain("InvalidOperationException", value);
        Assert.True(value.Length < 8 * 1024);
    }

    [Fact]
    public void ReceiptRejectsAnOversizedPublicDiagnostic()
    {
        Directory.CreateDirectory(root);
        var path = Path.Join(root, "oversized-receipt.json");
        var receipt = FailureReceipt(
            R3LiveAgentCodes.CompositionFailed,
            qualityClassification: null,
            qualityCode: null) with
        {
            OutcomeCode = new string('x', 129),
        };
        File.WriteAllBytes(path, TrustedLiveReceiptCodec.Write(receipt));

        Assert.Null(TrustedLiveReceiptCodec.Read(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private async Task<TrustedLiveProcessResult> RunProbeAsync(
        IReadOnlyList<string> probeArguments,
        string provider,
        string state,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var arguments = new List<string>();
        arguments.AddRange(probeArguments);
        return await TrustedLiveProcessLauncher.RunAsync(
            VerifierExecutable(),
            arguments,
            root,
            provider,
            state,
            timeout,
            cancellationToken);
    }

    private static TrustedLivePhaseReceipt FailureReceipt(
        string productCode,
        string? qualityClassification,
        string? qualityCode) => new(
            VerifierScenario.MustFind.ToString(),
            "failed",
            qualityCode ?? productCode,
            productCode,
            null,
            "same_head",
            0,
            0,
            false,
            false,
            string.Empty,
            null,
            null,
            null,
            null,
            qualityCode is null ? null : "failed",
            qualityClassification,
            qualityCode,
            0,
            0,
            new string('1', 64),
            new string('2', 64));

    private string WriteBuildPairManifest(string artifact)
    {
        var executionSha = LiveAgentFreshProcessDomain.RawSha256(
            File.ReadAllBytes(artifact));
        var architectureSha = executionSha;
        var pairSha = VerifierBuildPairDomain.ComputeSha256(
            VerifierExecutionKinds.Framework,
            executionSha,
            architectureSha);
        var manifest = Path.Join(root, "build-pair.json");
        File.WriteAllText(
            manifest,
            $"{{\"kind\":\"{VerifierBuildPairDomain.Kind}\"," +
            $"\"execution_kind\":\"{VerifierExecutionKinds.Framework}\"," +
            $"\"execution_artifact_sha256\":\"{executionSha}\"," +
            $"\"architecture_assembly_sha256\":\"{architectureSha}\"," +
            $"\"build_pair_sha256\":\"{pairSha}\"}}\n");
        return manifest;
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!File.Exists(path) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }
        Assert.True(File.Exists(path), path);
    }

    private static string VerifierExecutable()
    {
        var assembly = typeof(Program).Assembly.Location;
        var candidate = Path.Join(
            Path.GetDirectoryName(assembly)!,
            OperatingSystem.IsWindows()
                ? "AgenticPrReview.Runtime.LiveAgentVerifierFixture.exe"
                : "AgenticPrReview.Runtime.LiveAgentVerifierFixture");
        Assert.True(File.Exists(candidate), candidate);
        return candidate;
    }

    private static bool ProcessIsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

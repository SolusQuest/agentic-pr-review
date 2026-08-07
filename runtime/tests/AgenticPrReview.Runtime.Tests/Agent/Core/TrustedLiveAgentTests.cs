using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Quality;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.LiveAgentVerifierFixture;

namespace AgenticPrReview.Runtime.Tests.Agent.Core;

public sealed class TrustedLiveAgentTests : IDisposable
{
    private readonly string root = Path.Join(
        Path.GetTempPath(),
        string.Concat("apr-r3-trusted-live-", Guid.NewGuid().ToString("N")));

    public static TheoryData<Exception, string> ExceptionCategories => new()
    {
        { new ArgumentNullException("value"), TrustedLiveFailureCategories.Argument },
        { new IOException(), TrustedLiveFailureCategories.Io },
        { new UnauthorizedAccessException(), TrustedLiveFailureCategories.Access },
        { new PlatformNotSupportedException(), TrustedLiveFailureCategories.Unsupported },
        { new CryptographicException(), TrustedLiveFailureCategories.Cryptography },
        { new JsonException(), TrustedLiveFailureCategories.Json },
        { new Win32Exception(), TrustedLiveFailureCategories.Process },
        { new OperationCanceledException(), TrustedLiveFailureCategories.Cancelled },
        { new InvalidOperationException(), TrustedLiveFailureCategories.Other },
    };

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
    [InlineData(AgentFailureCodes.ChatFailed, null, null, TrustedLiveCodes.Provider)]
    [InlineData(R3LiveAgentCodes.Completed, "provider", R3QualityCodes.ProviderFailed, TrustedLiveCodes.Provider)]
    [InlineData(R3LiveAgentCodes.Completed, "quality", R3QualityCodes.RequiredToolMissing, TrustedLiveCodes.MissingTool)]
    [InlineData(R3LiveAgentCodes.Completed, "quality", R3QualityCodes.RequiredObservationMissing, TrustedLiveCodes.Grounding)]
    [InlineData(R3LiveAgentCodes.Completed, "quality", R3QualityCodes.ExpectedFindingMissing, TrustedLiveCodes.MustFind)]
    [InlineData(R3LiveAgentCodes.Completed, "quality", R3QualityCodes.ProhibitedFinding, TrustedLiveCodes.MustNotFind)]
    [InlineData(R3LiveAgentCodes.Completed, "quality", R3QualityCodes.PriorFactMissing, TrustedLiveCodes.Continuation)]
    [InlineData(AgentFailureCodes.TerminalInvalid, null, null, TrustedLiveCodes.Grounding)]
    [InlineData(AgentSessionCodes.ContinuationInvalid, null, null, TrustedLiveCodes.Continuation)]
    [InlineData(AgentSessionCodes.TransitionRejected, null, null, TrustedLiveCodes.Continuation)]
    [InlineData(AgentSessionCodes.RecordInvalid, null, null, TrustedLiveCodes.Grounding)]
    [InlineData(AgentSessionCodes.CurrentMalformed, null, null, TrustedLiveCodes.Infrastructure)]
    [InlineData(R3LiveAgentCodes.HandoffCleanupFailed, null, null, TrustedLiveCodes.Cleanup)]
    [InlineData(RestrictedStateCodes.CleanupFailed, null, null, TrustedLiveCodes.Cleanup)]
    [InlineData(RestrictedStateCodes.LineageMismatch, null, null, TrustedLiveCodes.Continuation)]
    [InlineData(LiveAgentFreshProcessCodes.ProcessIdentityReused, null, null, TrustedLiveCodes.Continuation)]
    [InlineData(RestrictedStateCodes.ReplayRejected, null, null, TrustedLiveCodes.Continuation)]
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
            CanaryDetected: false,
            ReceiptFilePresent: true);

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
    public void FailedPhaseIsSeparatedFromPassingPhaseEvidence()
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
            ReceiptFilePresent: true);
        var phases = new List<TrustedLivePhaseReceipt>();

        Assert.False(TrustedLiveSupervisor.TryAdmitPhase(
            execution,
            VerifierScenario.MustFind,
            buildPair,
            phases,
            out var code,
            out var failure));

        Assert.Equal(TrustedLiveCodes.Infrastructure, code);
        Assert.Empty(phases);
        Assert.NotNull(failure);
        Assert.Equal(TrustedLiveFailureKinds.Application, failure.Kind);
        Assert.Equal(
            R3LiveAgentDiagnosticCodes.TransportFailed,
            failure.Stage);
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
            CanaryDetected: true);
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
        Assert.Equal(TrustedLiveFailureKinds.Canary, failure.Kind);
        Assert.Null(failure.ProductCode);
        Assert.Null(failure.OutcomeCode);
        Assert.Null(failure.QualityClassification);
        Assert.Null(failure.QualityCode);
    }

    [Theory]
    [MemberData(nameof(ExceptionCategories))]
    public void ExceptionsMapDirectlyToClosedPrivateCategories(
        Exception exception,
        string expected)
    {
        Assert.Equal(
            expected,
            TrustedLiveFailureCategories.FromException(exception));
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
            "failed",
            TrustedLiveCodes.Infrastructure,
            new string('a', 40),
            "SolusQuest/agentic-pr-review/.github/workflows/" +
                "r3-live-proof.yml@refs/heads/main",
            new string('a', 40),
            new string('9', 64),
            buildPair,
            [phase],
            new TrustedLiveFailureEvidence(
                phase.Scenario,
                TrustedLiveFailureKinds.Application,
                R3LiveAgentDiagnosticCodes.TransportFailed,
                Category: null,
                R3LiveAgentDiagnosticCodes.TransportFailed,
                1,
                ModelCalls: 0,
                ToolCalls: 0,
                R3LiveAgentCodes.CompositionFailed,
                R3LiveAgentDiagnosticCodes.TransportFailed,
                QualityClassification: null,
                QualityCode: null));

        Assert.DoesNotContain("provider-launcher-canary", value);
        Assert.DoesNotContain("reasoning_content", value);
        Assert.DoesNotContain("Authorization", value);
        using var document = JsonDocument.Parse(value);
        Assert.Equal(1, document.RootElement.GetProperty("phase_count").GetInt32());
        Assert.Equal(
            2,
            document.RootElement.GetProperty("attempted_phase_count").GetInt32());
        Assert.Equal(
            [
                "scenario",
                "outcome_code",
                "generation",
                "transition",
                "model_calls",
                "tool_calls",
                "terminal_sha256",
                "lineage_sha256",
            ],
            document.RootElement.GetProperty("phases")[0]
                .EnumerateObject()
                .Select(property => property.Name));
        var failure = document.RootElement.GetProperty("failure");
        Assert.Equal(
            R3LiveAgentDiagnosticCodes.TransportFailed,
            failure.GetProperty("diagnostic_code").GetString());
        Assert.DoesNotContain("InvalidOperationException", value);
        Assert.True(value.Length < 8 * 1024);
    }

    [Theory]
    [InlineData("upstream_response_invalid")]
    [InlineData("request_body_invalid")]
    [InlineData("InvalidOperationException")]
    [InlineData("C:\\private\\state.key")]
    public void ReceiptRejectsUnownedOrSensitiveLookingDiagnostics(
        string diagnostic)
    {
        var receipt = FailureReceipt(
            R3LiveAgentCodes.CompositionFailed,
            qualityClassification: null,
            qualityCode: null) with
        {
            OutcomeCode = diagnostic,
        };

        Assert.Null(WriteAndReadReceipt(receipt));
    }

    [Fact]
    public void ReceiptRejectsOverlongAndInconsistentCombinations()
    {
        var overlong = FailureReceipt(
            R3LiveAgentCodes.CompositionFailed,
            qualityClassification: null,
            qualityCode: null) with
        {
            OutcomeCode = new string('x', 129),
        };
        var resetAsFailure = FailureReceipt(
            AgentSessionCodes.ResetExplicit,
            qualityClassification: null,
            qualityCode: null);
        var wrongApplicationProduct = FailureReceipt(
            R3LiveAgentCodes.InputInvalid,
            qualityClassification: null,
            qualityCode: null) with
        {
            OutcomeCode = R3LiveAgentDiagnosticCodes.TransportFailed,
        };

        Assert.Null(WriteAndReadReceipt(overlong));
        Assert.Null(WriteAndReadReceipt(resetAsFailure));
        Assert.Null(WriteAndReadReceipt(wrongApplicationProduct));
    }

    [Fact]
    public void FailedReceiptNeverPromotesACompletedQualityOutcome()
    {
        var qualityPassed = FailureReceipt(
            AgentSessionCodes.RecordInvalid,
            "quality",
            R3QualityCodes.Passed) with
        {
            OutcomeCode = AgentSessionCodes.RecordInvalid,
            QualityStatus = "passed",
            FindingCount = 1,
            QualityToolCallCount = 1,
            TerminalSha256 = new string('f', 64),
        };
        var applicationFailed = qualityPassed with
        {
            OutcomeCode = R3LiveAgentDiagnosticCodes.StateCommitFailed,
            ProductCode = R3LiveAgentCodes.CompositionFailed,
        };
        var staleQualityPrimary = qualityPassed with
        {
            OutcomeCode = R3QualityCodes.Passed,
        };

        Assert.NotNull(WriteAndReadReceipt(qualityPassed));
        Assert.NotNull(WriteAndReadReceipt(applicationFailed));
        Assert.Null(WriteAndReadReceipt(staleQualityPrimary));
        Assert.Equal(
            LiveAgentFreshProcessCodes.AuthorizationInvalid,
            TrustedLiveDomain.FailureOutcomeCode(
                LiveAgentFreshProcessCodes.AuthorizationInvalid,
                LiveAgentFreshProcessCodes.AuthorizationInvalid,
                quality: null));
        Assert.Equal(
            R3LiveAgentDiagnosticCodes.StateCommitFailed,
            TrustedLiveDomain.FailureOutcomeCode(
                R3LiveAgentDiagnosticCodes.StateCommitFailed,
                AgentSessionCodes.RecordInvalid,
                quality: null));
    }

    [Theory]
    [InlineData(AgentSessionCodes.ContinuationInvalid, TrustedLiveCodes.Continuation)]
    [InlineData(AgentSessionCodes.TransitionRejected, TrustedLiveCodes.Continuation)]
    [InlineData(AgentSessionCodes.RecordInvalid, TrustedLiveCodes.Grounding)]
    [InlineData(AgentSessionCodes.CurrentMalformed, TrustedLiveCodes.Infrastructure)]
    public void AdmittedSessionFailuresRetainTheirExactClassification(
        string productCode,
        string expected)
    {
        var receipt = FailureReceipt(
            productCode,
            qualityClassification: null,
            qualityCode: null);
        var admitted = WriteAndReadReceipt(receipt);

        Assert.NotNull(admitted);
        Assert.Equal(
            expected,
            TrustedLiveSupervisor.ClassifyFailure(
                new TrustedLivePhaseExecution(
                    FailedProcess(),
                    admitted,
                    CanaryDetected: false)));
    }

    [Theory]
    [InlineData(R3LiveAgentCodes.HandoffCleanupFailed, TrustedLiveCodes.Cleanup)]
    [InlineData(RestrictedStateCodes.CleanupFailed, TrustedLiveCodes.Cleanup)]
    [InlineData(LiveAgentFreshProcessCodes.LineageInvalid, TrustedLiveCodes.Continuation)]
    [InlineData(LiveAgentFreshProcessCodes.ProcessIdentityReused, TrustedLiveCodes.Continuation)]
    [InlineData(RestrictedStateCodes.CurrentMissing, TrustedLiveCodes.Continuation)]
    [InlineData(RestrictedStateCodes.Expired, TrustedLiveCodes.Continuation)]
    [InlineData(RestrictedStateCodes.ExplicitMissing, TrustedLiveCodes.Continuation)]
    [InlineData(RestrictedStateCodes.ReplayRejected, TrustedLiveCodes.Continuation)]
    public void CleanupAndContinuationFailuresRemainClassifiedThroughAdmission(
        string productCode,
        string expected)
    {
        var receipt = FailureReceipt(
            productCode,
            qualityClassification: null,
            qualityCode: null);
        var buildPair = new VerifierBuildPair(
            VerifierExecutionKinds.Framework,
            receipt.ExecutionArtifactSha256,
            new string('3', 64),
            receipt.BuildPairSha256);
        var phases = new List<TrustedLivePhaseReceipt>();

        Assert.False(TrustedLiveSupervisor.TryAdmitPhase(
            new TrustedLivePhaseExecution(
                FailedProcess(),
                receipt,
                CanaryDetected: false,
                ReceiptFilePresent: true),
            VerifierScenario.MustFind,
            buildPair,
            phases,
            out var code,
            out var failure));

        Assert.Equal(expected, code);
        Assert.Empty(phases);
        Assert.NotNull(failure);
        Assert.Equal(productCode, failure.ProductCode);
        Assert.Equal(productCode, failure.OutcomeCode);
    }

    [Fact]
    public void UnknownSessionCodeBecomesNonReflectiveInvalidReceiptEvidence()
    {
        const string unknown = "session_future_response_failure";
        var receipt = FailureReceipt(
            unknown,
            qualityClassification: null,
            qualityCode: null);
        var buildPair = new VerifierBuildPair(
            VerifierExecutionKinds.Framework,
            receipt.ExecutionArtifactSha256,
            new string('3', 64),
            receipt.BuildPairSha256);
        var phases = new List<TrustedLivePhaseReceipt>();

        Assert.False(TrustedLiveSupervisor.TryAdmitPhase(
            new TrustedLivePhaseExecution(
                FailedProcess(),
                receipt,
                CanaryDetected: false),
            VerifierScenario.MustFind,
            buildPair,
            phases,
            out var code,
            out var failure));

        Assert.Equal(TrustedLiveCodes.Infrastructure, code);
        Assert.Empty(phases);
        Assert.NotNull(failure);
        Assert.Equal(TrustedLiveFailureKinds.ReceiptInvalid, failure.Kind);
        Assert.Equal(
            TrustedLiveDiagnosticCodes.PhaseReceiptInvalid,
            failure.DiagnosticCode);
        Assert.Null(failure.ProductCode);
        Assert.Null(failure.OutcomeCode);
        Assert.DoesNotContain(
            unknown,
            TrustedLiveReceiptCodec.WriteCompletion(
                "failed",
                code,
                new string('a', 40),
                "workflow-ref",
                new string('a', 40),
                fixtureSha256: null,
                buildPair,
                phases,
                failure));
    }

    [Fact]
    public void ProductCodeInventoryExactlyCoversCurrentSourceFamilies()
    {
        var sourceCodes = new[]
            {
                typeof(R3LiveAgentCodes),
                typeof(LiveAgentFreshProcessCodes),
                typeof(AgentFailureCodes),
                typeof(RestrictedStateCodes),
                typeof(AgentSessionCodes),
            }
            .SelectMany(type => type.GetFields(
                BindingFlags.Static |
                BindingFlags.Public |
                BindingFlags.NonPublic))
            .Where(field => field.IsLiteral &&
                !field.IsInitOnly &&
                field.FieldType == typeof(string))
            .Select(field => Assert.IsType<string>(field.GetRawConstantValue()))
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(
            sourceCodes.SetEquals(TrustedLiveDomain.ProductCodes),
            string.Join(",", sourceCodes
                .Except(TrustedLiveDomain.ProductCodes)
                .Concat(TrustedLiveDomain.ProductCodes.Except(sourceCodes))));

        var applicationCodes = typeof(R3LiveAgentDiagnosticCodes)
            .GetFields(
                BindingFlags.Static |
                BindingFlags.Public |
                BindingFlags.NonPublic)
            .Where(field => field.IsLiteral &&
                !field.IsInitOnly &&
                field.FieldType == typeof(string))
            .Select(field => Assert.IsType<string>(field.GetRawConstantValue()))
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(applicationCodes.SetEquals(
            TrustedLiveDomain.ApplicationDiagnostics));

        var classifiedFailures = TrustedLiveDomain.ProductCodes
            .Where(TrustedLiveDomain.ProductFailureCodeIsAdmitted)
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(classifiedFailures.SetEquals(
            TrustedLiveDomain.ProductFailureClassifications.Keys));
        Assert.All(classifiedFailures, code =>
            Assert.True(TrustedLiveDomain.TryClassifyProductFailure(
                code,
                out _)));
    }

    [Fact]
    public void EveryChildStageProducesOnlyClosedPrivateFailureEvidence()
    {
        Directory.CreateDirectory(root);
        foreach (var stage in TrustedLiveChildStages.All)
        {
            foreach (var category in TrustedLiveFailureCategories.All)
            {
                var marker = new TrustedLivePrivateFailure(
                    TrustedLiveFailureKinds.Child,
                    stage,
                    category);
                var path = Path.Join(root, $"{stage}-{category}.json");
                File.WriteAllBytes(
                    path,
                    TrustedLivePrivateFailureCodec.Write(marker));
                var read = TrustedLivePrivateFailureCodec.Read(path);
                Assert.Equal(marker, read);

                var evidence = TrustedLiveSupervisor.CreateFailureEvidence(
                    new TrustedLivePhaseExecution(
                        FailedProcess(),
                        Receipt: null,
                        CanaryDetected: false,
                        ReceiptFilePresent: false,
                        PrivateFailure: read),
                    VerifierScenario.MustFind,
                    receiptMatchesInvocation: false);
                Assert.Equal(TrustedLiveFailureKinds.Child, evidence.Kind);
                Assert.Equal(stage, evidence.Stage);
                Assert.Equal(category, evidence.Category);
                Assert.Equal(
                    TrustedLiveDiagnosticCodes.PhaseChildFailed,
                    evidence.DiagnosticCode);
            }
        }
    }

    [Fact]
    public void PrivateFailureCodecRejectsUnknownDuplicateAndRawValues()
    {
        Directory.CreateDirectory(root);
        var values = new[]
        {
            "{\"kind\":\"child\",\"stage\":\"unknown\",\"category\":\"other\"}",
            "{\"kind\":\"child\",\"stage\":\"command_execution\",\"category\":\"InvalidOperationException\"}",
            "{\"stage\":\"command_execution\",\"kind\":\"child\",\"category\":\"other\"}",
            "{\"kind\":\"child\",\"stage\":\"command_execution\",\"stage\":\"lineage_read\",\"category\":\"other\"}",
            "{\"kind\":\"child\",\"stage\":\"command_execution\",\"category\":\"other\",\"path\":\"C:\\\\private\"}",
        };
        for (var index = 0; index < values.Length; index++)
        {
            var path = Path.Join(root, $"invalid-{index}.json");
            File.WriteAllText(path, values[index]);
            Assert.Null(TrustedLivePrivateFailureCodec.Read(path));
        }
    }

    [Fact]
    public void PrivateFailureMarkerUsesCreateNewSemantics()
    {
        Directory.CreateDirectory(root);
        AgenticPrReview.Runtime.LiveAgentVerifierFixture.Program
            .WriteTrustedFailure(
            root,
            TrustedLiveChildStages.CommandExecution,
            TrustedLiveFailureCategories.Process);

        Assert.Throws<IOException>(() =>
            AgenticPrReview.Runtime.LiveAgentVerifierFixture.Program
                .WriteTrustedFailure(
                    root,
                    TrustedLiveChildStages.CommandExecution,
                    TrustedLiveFailureCategories.Process));
    }

    [Fact]
    public async Task TrustedChildWritesAClosedMarkerForMaterializationFailure()
    {
        Directory.CreateDirectory(root);
        var phaseRoot = Path.Join(root, "phase");
        var corpus = Path.Join(root, "invalid-corpus.json");
        File.WriteAllText(corpus, "{}\n");
        var executable = VerifierExecutable();
        var manifest = WriteBuildPairManifest(executable);
        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
        {
            "trusted-must-find",
            "--root",
            phaseRoot,
            "--corpus",
            corpus,
            "--output",
            Path.Join(phaseRoot, "private", "trusted-must-find.json"),
            "--execution-kind",
            VerifierExecutionKinds.Framework,
            "--execution-artifact",
            executable,
            "--build-pair-manifest",
            manifest,
        })
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start);
        Assert.NotNull(process);
        await process.WaitForExitAsync();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();

        Assert.Equal(3, process.ExitCode);
        Assert.Equal(string.Empty, output);
        Assert.Contains(VerifierCodes.FixtureInvalid, error);
        Assert.False(File.Exists(Path.Join(phaseRoot, "private", "failure.code")));
        var marker = TrustedLivePrivateFailureCodec.Read(Path.Join(
            phaseRoot,
            "private",
            "failure.json"));
        Assert.NotNull(marker);
        Assert.Equal(
            TrustedLiveChildStages.FixtureMaterialization,
            marker.Stage);
        Assert.Equal(TrustedLiveFailureCategories.Invalid, marker.Category);
    }

    [Fact]
    public void CanaryCompletionClearsAllPhaseDerivedEvidence()
    {
        var phase = FailureReceipt(
            AgentSessionCodes.RecordInvalid,
            qualityClassification: null,
            qualityCode: null);
        var failure = new TrustedLiveFailureEvidence(
            phase.Scenario,
            TrustedLiveFailureKinds.Canary,
            Stage: null,
            Category: null,
            TrustedLiveCodes.Canary,
            1,
            ModelCalls: 0,
            ToolCalls: 0,
            ProductCode: null,
            OutcomeCode: null,
            QualityClassification: null,
            QualityCode: null);
        var completion = TrustedLiveReceiptCodec.WriteCompletion(
            "failed",
            TrustedLiveCodes.Canary,
            new string('a', 40),
            "workflow-ref",
            new string('a', 40),
            fixtureSha256: null,
            buildPair: null,
            [phase],
            failure);

        Assert.DoesNotContain(AgentSessionCodes.RecordInvalid, completion);
        using var document = JsonDocument.Parse(completion);
        Assert.Equal(0, document.RootElement.GetProperty("phase_count").GetInt32());
        Assert.Equal(
            0,
            document.RootElement.GetProperty("attempted_phase_count").GetInt32());
        Assert.Empty(document.RootElement.GetProperty("phases").EnumerateArray());
        Assert.Equal(
            JsonValueKind.Null,
            document.RootElement.GetProperty("failure").ValueKind);
    }

    [Fact]
    public void CanonicalProductResultBytesRemainDiagnosticFree()
    {
        var value = new LiveAgentFreshProcessResultDocument(
            "apr-r3-live-agent-result",
            R3LiveAgentCodes.CompositionFailed,
            Generation: null,
            "same_head",
            0,
            0,
            StablePlanSha256: null,
            TerminalSha256: null,
            SessionSha256: null,
            EnvelopeSha256: null,
            LineageSha256: null,
            SecondRequestSha256: null,
            new string('a', 64),
            HandoffReady: false);
        var bytes = LiveAgentFreshProcessCodec.Write(value);
        var json = Encoding.UTF8.GetString(bytes);

        Assert.Equal(
            "{\"kind\":\"apr-r3-live-agent-result\"," +
            "\"code\":\"r3_live_composition_failed\"," +
            "\"generation\":null,\"transition_class\":\"same_head\"," +
            "\"model_calls\":0,\"tool_calls\":0," +
            "\"stable_plan_sha256\":null,\"terminal_sha256\":null," +
            "\"session_sha256\":null,\"envelope_sha256\":null," +
            "\"lineage_sha256\":null,\"second_request_sha256\":null," +
            $"\"invocation_identity_sha256\":\"{new string('a', 64)}\"," +
            "\"handoff_ready\":false}",
            json);
        Assert.DoesNotContain("diagnostic", json);
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

    private TrustedLivePhaseReceipt? WriteAndReadReceipt(
        TrustedLivePhaseReceipt receipt)
    {
        Directory.CreateDirectory(root);
        var path = Path.Join(root, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllBytes(path, TrustedLiveReceiptCodec.Write(receipt));
        return TrustedLiveReceiptCodec.Read(path);
    }

    private static TrustedLiveProcessResult FailedProcess() =>
        new(
            1,
            TimedOut: false,
            Cancelled: false,
            SensitiveBytesObserved: false,
            OutputLimitExceeded: false,
            string.Empty,
            string.Empty);

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

using System.Diagnostics;
using System.Reflection;
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
            [phase]);

        Assert.DoesNotContain("provider-launcher-canary", value);
        Assert.DoesNotContain("reasoning_content", value);
        Assert.DoesNotContain("Authorization", value);
        using var document = JsonDocument.Parse(value);
        Assert.Equal(1, document.RootElement.GetProperty("phase_count").GetInt32());
        Assert.True(value.Length < 8 * 1024);
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
        TimeSpan timeout)
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
            CancellationToken.None);
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

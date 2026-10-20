using System.Diagnostics;
using System.Runtime.InteropServices;
using AgenticPrReview.Runtime.LiveAgentVerifierFixture;

namespace AgenticPrReview.Runtime.Tests.Agent.Core;

public sealed class TrustedLiveSupervisorDriverTests
{
    [Theory]
    [InlineData("closed")]
    [InlineData("delayed-close")]
    public async Task ParentExitAndDelayedPipeClosureAreBothObserved(string mode)
    {
        await WithProbeAsync(mode, async (process, driver, _) =>
        {
            await TrustedLiveSupervisorDriver.WaitForExitOnlyAsync(process, TimeSpan.FromSeconds(5));
            var result = await driver.CollectAsync(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5))
                .WaitAsync(TimeSpan.FromSeconds(12));
            Assert.Equal(0, process.ExitCode);
            Assert.Contains("APR_R3_PIPE_PARENT_OUT", result.Output);
            Assert.Contains("APR_R3_PIPE_PARENT_ERROR", result.Error);
            if (mode == "delayed-close")
            {
                Assert.Contains("APR_R3_PIPE_CHILD_OUT", result.Output);
                Assert.Contains("APR_R3_PIPE_CHILD_ERROR", result.Error);
            }
        });
    }

    [Fact]
    public async Task OpenDescendantPipesFailBoundedlyAfterParentExitBeforeCleanup()
    {
        await WithProbeAsync("parent-exits", async (process, driver, child) =>
        {
            await TrustedLiveSupervisorDriver.WaitForExitOnlyAsync(process, TimeSpan.FromSeconds(5));
            var failure = await Assert.ThrowsAsync<TimeoutException>(() =>
                driver.CollectAsync(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(200)))
                .WaitAsync(TimeSpan.FromSeconds(7));
            Assert.Equal("APR_R3_PROBE_OUTPUT_TIMEOUT", failure.Message);
            Assert.NotNull(child);
            Assert.False(child.HasExited); // Observe the failure before finally can reap it.
        });
    }

    [Fact]
    public async Task OpenParentFailsBoundedlyBeforeCleanupCanHideIt()
    {
        await WithProbeAsync("parent-open", async (process, driver, child) =>
        {
            var failure = await Assert.ThrowsAsync<TimeoutException>(() =>
                driver.CollectAsync(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200)))
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("APR_R3_PROBE_EXIT_TIMEOUT", failure.Message);
            Assert.False(process.HasExited);
            Assert.NotNull(child);
            Assert.False(child.HasExited);
        });
    }

    private static async Task WithProbeAsync(string mode,
        Func<Process, TrustedLiveSupervisorDriver, Process?, Task> assertion)
    {
        var root = Path.Join(Path.GetTempPath(), "apr-r3-pipe-driver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var pidPath = Path.Join(root, "child.pid");
        var executable = Path.Join(Path.GetDirectoryName(typeof(AgenticPrReview.Runtime.LiveAgentVerifierFixture.Program).Assembly.Location)!,
            OperatingSystem.IsWindows() ? "AgenticPrReview.Runtime.LiveAgentVerifierFixture.exe" :
                "AgenticPrReview.Runtime.LiveAgentVerifierFixture");
        var start = new ProcessStartInfo(executable)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
        start.ArgumentList.Add("launcher-pipe-parent"); start.ArgumentList.Add(mode); start.ArgumentList.Add(pidPath);
        start.Environment.Clear();
        var publicTemp = Path.GetTempPath();
        start.Environment["TMPDIR"] = publicTemp; start.Environment["TMP"] = publicTemp; start.Environment["TEMP"] = publicTemp;
        start.Environment["DOTNET_ROOT"] = Path.GetFullPath(Path.Join(RuntimeEnvironment.GetRuntimeDirectory(), "../../.."));
        if (OperatingSystem.IsWindows())
        {
            start.Environment["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot");
            start.Environment["WINDIR"] = Environment.GetEnvironmentVariable("WINDIR");
        }
        using var process = Process.Start(start)!;
        var driver = new TrustedLiveSupervisorDriver(process);
        Process? child = null;
        try
        {
            if (mode != "closed")
            {
                var timer = Stopwatch.StartNew();
                while (!File.Exists(pidPath) && timer.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
                Assert.True(File.Exists(pidPath));
                var pid = int.Parse(File.ReadAllText(pidPath), System.Globalization.CultureInfo.InvariantCulture);
                try { child = Process.GetProcessById(pid); }
                catch (ArgumentException) when (mode == "delayed-close") { }
            }
            await assertion(process, driver, child);
        }
        finally
        {
            try
            {
                try { await TrustedLiveSupervisorDriver.ReapOwnedAsync(process); }
                finally { if (child is not null) await TrustedLiveSupervisorDriver.ReapOwnedAsync(child); }
            }
            finally
            {
                child?.Dispose();
                Directory.Delete(root, recursive: true);
            }
        }
    }
}

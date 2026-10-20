using System.Diagnostics;
using System.Runtime.InteropServices;
using AgenticPrReview.Runtime.LiveAgentVerifierFixture;

namespace AgenticPrReview.Runtime.Tests.Agent.Core;

public sealed class TrustedLiveScanTests
{
    [Fact]
    public async Task FifoIsRefusedWithoutWaitingForAWriter()
    {
        if (OperatingSystem.IsWindows()) return;
        await WithRootAsync(async root =>
        {
            using var create = Process.Start(new ProcessStartInfo("/usr/bin/mkfifo")
            { UseShellExecute = false, ArgumentList = { Path.Join(root, "fifo") } });
            Assert.NotNull(create);
            await create.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, create.ExitCode);
            await AssertScanAsync(root, 1);
        });
    }

    [Theory]
    [InlineData("safe", 0)]
    [InlineData("canary", 1)]
    [InlineData("empty", 0)]
    [InlineData("exact-limit", 0)]
    [InlineData("over-limit", 1)]
    [InlineData("leaf-link", 1)]
    public async Task RegularFilesRetainCanaryAndSizeChecksAndLinksAreRefused(string mode, int expected)
    {
        if (mode == "leaf-link" && OperatingSystem.IsWindows()) return;
        await WithRootAsync(async root =>
        {
            var path = Path.Join(root, "regular");
            if (mode == "canary") File.WriteAllText(path, "prefix fixed-scan-canary suffix");
            else if (mode == "exact-limit" || mode == "over-limit")
            {
                using var file = File.Create(path);
                file.SetLength(16 * 1024 * 1024 + (mode == "over-limit" ? 1 : 0));
            }
            else File.WriteAllText(path, mode == "empty" ? "" : "safe bytes");
            if (mode == "leaf-link") File.CreateSymbolicLink(Path.Join(root, "link"), path);
            await AssertScanAsync(root, expected);
        });
    }

    [Theory]
    [InlineData("truncate")]
    [InlineData("grow")]
    [InlineData("replace-path")]
    public async Task DescriptorReadRefusesLengthChangesAndDoesNotReopenAReplacementPath(string mutation)
    {
        if (OperatingSystem.IsWindows()) return;
        await WithRootAsync(root =>
        {
            var scanRoot = Path.Join(root, "scanned");
            Directory.CreateDirectory(scanRoot);
            var path = Path.Join(scanRoot, "regular");
            File.WriteAllText(path, mutation == "replace-path" ? "fixed-scan-canary" : "safe bytes");
            var mutated = false;
            Assert.True(TrustedLiveSupervisor.ContainsSensitiveBytesForTests(scanRoot, () =>
            {
                if (mutation == "replace-path")
                {
                    File.Move(path, Path.Join(root, "original"));
                    File.WriteAllText(path, "replacement safe bytes");
                }
                else
                {
                    using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                    writer.SetLength(mutation == "truncate" ? 0 : writer.Length + 1);
                }
                mutated = true;
            }, "fixed-scan-canary"u8.ToArray()));
            Assert.True(mutated);
            if (mutation == "truncate") Assert.Equal(0, new FileInfo(path).Length);
            if (mutation == "grow") Assert.Equal(11, new FileInfo(path).Length);
            if (mutation == "replace-path") Assert.Equal("replacement safe bytes", File.ReadAllText(path));
            return Task.CompletedTask;
        });
    }

    private static async Task WithRootAsync(Func<string, Task> assertion)
    {
        var root = Path.Join(Path.GetTempPath(), "apr-r3-scan-driver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { await assertion(root); }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task AssertScanAsync(string root, int expected)
    {
        var executable = Path.Join(Path.GetDirectoryName(typeof(AgenticPrReview.Runtime.LiveAgentVerifierFixture.Program).Assembly.Location)!,
            OperatingSystem.IsWindows() ? "AgenticPrReview.Runtime.LiveAgentVerifierFixture.exe" :
                "AgenticPrReview.Runtime.LiveAgentVerifierFixture");
        var start = new ProcessStartInfo(executable)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
        start.ArgumentList.Add("launcher-scan-probe"); start.ArgumentList.Add(root);
        start.Environment.Clear();
        var temp = Path.GetTempPath();
        start.Environment["TMPDIR"] = temp; start.Environment["TMP"] = temp; start.Environment["TEMP"] = temp;
        start.Environment["DOTNET_ROOT"] = Path.GetFullPath(Path.Join(RuntimeEnvironment.GetRuntimeDirectory(), "../../.."));
        if (OperatingSystem.IsWindows())
        {
            start.Environment["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot");
            start.Environment["WINDIR"] = Environment.GetEnvironmentVariable("WINDIR");
        }
        using var process = Process.Start(start)!;
        var driver = new TrustedLiveSupervisorDriver(process);
        try
        {
            var result = await driver.CollectAsync(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
            Assert.Equal(expected, process.ExitCode);
            Assert.Equal(string.Empty, result.Output);
            Assert.Equal(string.Empty, result.Error);
        }
        finally { await TrustedLiveSupervisorDriver.ReapOwnedAsync(process); }
    }
}

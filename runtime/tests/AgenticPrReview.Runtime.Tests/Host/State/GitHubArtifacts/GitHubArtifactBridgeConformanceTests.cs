using System.Diagnostics;
using AgenticPrReview.Runtime.Host.State.GitHubArtifacts;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;

namespace AgenticPrReview.Runtime.Tests.Host.State.GitHubArtifacts;

public sealed class GitHubArtifactBridgeConformanceTests
{
    [Fact]
    public async Task SharedConformanceTraversesRealNodeBridge()
    {
        var repositoryRoot = FindRepositoryRoot();
        var testRoot = Path.Join(
            Path.GetTempPath(),
            $"apr-artifact-bridge-test-{Guid.NewGuid():N}");
        var stagingRoot = Path.Join(testRoot, "staging");
        var controlRoot = Path.Join(testRoot, "control");
        var bundlePath = Path.Join(testRoot, "fixture-server.mjs");
        Directory.CreateDirectory(stagingRoot);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                stagingRoot,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
        }
        Directory.CreateDirectory(controlRoot);
        Process? server = null;
        try
        {
            await BundleFixtureAsync(repositoryRoot, bundlePath);
            var endpoint = OperatingSystem.IsWindows()
                ? $@"\\.\pipe\apr-artifact-{Guid.NewGuid():N}"
                : Path.Join(testRoot, "bridge.sock");
            const string build = "issue152-test-build";
            server = StartNode(
                bundlePath,
                endpoint,
                build,
                stagingRoot,
                controlRoot);
            var ready = await server.StandardOutput.ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(20));
            if (!StringComparer.Ordinal.Equals(ready, "READY"))
            {
                var error = await server.StandardError.ReadToEndAsync();
                Assert.Fail($"Synthetic Node bridge failed to start: {error}");
            }

            var store = new GitHubArtifactRestrictedStateStore(
                endpoint,
                build,
                stagingRoot);
            await RestrictedStateStoreConformanceHarness.VerifyAsync(
                async action =>
                {
                    await action(new RestrictedStateStoreConformanceDriver(
                        store,
                        async expected =>
                        {
                            SetFlag(controlRoot, "duplicate-list");
                            return await store.ListExactAsync(
                                new OpaqueStoreListRequest(
                                    expected.Reference.Name,
                                    OpaqueStoreLimits.MaximumObjects),
                                CancellationToken.None);
                        },
                        expected =>
                        {
                            SetFlag(controlRoot, "missing-readback");
                            return store.ReadBackExactAsync(
                                new OpaqueStoreReadBackRequest(expected),
                                CancellationToken.None);
                        },
                        async expected =>
                        {
                            var nowPath = Path.Join(controlRoot, "now");
                            File.WriteAllText(
                                nowPath,
                                expected.ExpiresAtUnixSeconds.ToString(
                                    System.Globalization.CultureInfo
                                        .InvariantCulture));
                            try
                            {
                                return await store.DownloadAsync(
                                    new OpaqueStoreDownloadRequest(
                                        expected,
                                        checked((int)expected.Size)),
                                    CancellationToken.None);
                            }
                            finally
                            {
                                File.Delete(nowPath);
                            }
                        },
                        () => SetFlag(controlRoot, "may-commit-upload"),
                        expected =>
                        {
                            SetFlag(controlRoot, "unknown-delete");
                            return store.DeleteExactAsync(
                                new OpaqueStoreDeleteRequest(expected),
                                CancellationToken.None);
                        }));
                });

            SetFlag(controlRoot, "stall-list");
            using var cancellation = new CancellationTokenSource(
                TimeSpan.FromMilliseconds(250));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                store.ListExactAsync(
                    new OpaqueStoreListRequest(
                        new OpaqueStoreName("cancel-cross-process"),
                        8),
                    cancellation.Token));
        }
        finally
        {
            if (server is { HasExited: false })
            {
                server.Kill(entireProcessTree: true);
                await server.WaitForExitAsync();
            }
            server?.Dispose();
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static async Task BundleFixtureAsync(
        string repositoryRoot,
        string outputPath)
    {
        var entry = Path.Join(
            repositoryRoot,
            "src",
            "action-wrapper",
            "artifact-bridge",
            "synthetic-fixture-server.ts");
        var esbuild = Path.Join(
            repositoryRoot,
            "node_modules",
            "esbuild",
            "bin",
            "esbuild");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows()
                    ? "node"
                    : esbuild,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            },
        };
        if (OperatingSystem.IsWindows())
        {
            process.StartInfo.ArgumentList.Add(esbuild);
        }
        process.StartInfo.ArgumentList.Add(entry);
        process.StartInfo.ArgumentList.Add("--bundle");
        process.StartInfo.ArgumentList.Add("--platform=node");
        process.StartInfo.ArgumentList.Add("--format=esm");
        process.StartInfo.ArgumentList.Add("--target=node20");
        process.StartInfo.ArgumentList.Add($"--outfile={outputPath}");
        Assert.True(process.Start());
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync()
                .ConfigureAwait(false);
            Assert.Fail($"Unable to bundle synthetic bridge fixture: {error}");
        }
    }

    private static Process StartNode(
        string bundlePath,
        string endpoint,
        string build,
        string stagingRoot,
        string controlRoot)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "node",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            },
        };
        process.StartInfo.ArgumentList.Add(bundlePath);
        process.StartInfo.ArgumentList.Add(endpoint);
        process.StartInfo.ArgumentList.Add(build);
        process.StartInfo.ArgumentList.Add(stagingRoot);
        process.StartInfo.ArgumentList.Add(controlRoot);
        Assert.True(process.Start());
        return process;
    }

    private static void SetFlag(string controlRoot, string name) =>
        File.WriteAllBytes(Path.Join(controlRoot, name), []);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Join(directory.FullName, "package.json")) &&
                Directory.Exists(Path.Join(directory.FullName, "runtime")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("repository_root_not_found");
    }
}

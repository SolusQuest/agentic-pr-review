using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Host.State.GitHubArtifacts;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;

namespace AgenticPrReview.Runtime.Tests.Host.State.GitHubArtifacts;

internal sealed class HybridArtifactFixture : IAsyncDisposable
{
    private Process? server;
    private int port;
    private double milliseconds;
    internal string Root { get; } = Path.Join(Path.GetTempPath(), "apr-t1-" + Guid.NewGuid().ToString("N"));
    internal string Control => Path.Join(Root, "control");
    internal string Staging => Path.Join(Root, "artifact-staging");
    internal string Endpoint => Path.Join(Root, "bridge.sock");
    internal ActionHostLaunchContract Launch { get; private set; } = null!;
    internal long Now { get; set; }
    internal List<(string Host, string Path, string Method)> Requests { get; } = [];
    internal ArtifactRestClock Clock => new()
    {
        Milliseconds = () => milliseconds, UnixSeconds = () => Now,
        Delay = (duration, token) => { token.ThrowIfCancellationRequested(); milliseconds += duration.TotalMilliseconds; return Task.CompletedTask; },
    };
    internal Func<HttpMessageHandler> Handlers => () => new LoopbackHandler(this);

    internal static async Task<HybridArtifactFixture> StartAsync(ActionHostLaunchContract launch, long? now = null)
    {
        var fixture = new HybridArtifactFixture { Now = now ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
        try
        {
            Directory.CreateDirectory(fixture.Control);
            Directory.CreateDirectory(fixture.Staging);
            if (!OperatingSystem.IsWindows())
                foreach (var directory in new[] { fixture.Root, fixture.Control, fixture.Staging })
                    File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Assert.True(ActionHostLaunchContract.TryCreate(launch.Inputs, launch.EventJsonPath, launch.EventJsonSha256,
                launch.RepositoryName, launch.RepositoryId, launch.RunId, launch.RunAttempt, launch.WorkflowPath,
                launch.WorkflowRef, launch.WorkflowSha, launch.ActionSourceSha, launch.PayloadSha256, launch.BuildDiscriminator,
                launch.Cancellation, fixture.Endpoint, out var bound));
            fixture.Launch = bound!;
            var root = RepositoryRoot();
            var bundle = Path.Join(fixture.Root, "fixture.cjs");
            var esbuild = Path.Join(root, "node_modules", "esbuild", "bin", "esbuild");
            var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "node" : esbuild)
            { WorkingDirectory = root, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            if (OperatingSystem.IsWindows()) start.ArgumentList.Add(esbuild);
            foreach (var argument in new[] { Path.Join(root, "src/action-wrapper/artifact-bridge/hybrid-fixture-server.ts"),
                "--bundle", "--platform=node", "--format=cjs", "--target=node24", "--outfile=" + bundle }) start.ArgumentList.Add(argument);
            // The service uses top-level await. Bundle an async ESM entry with
            // require available for the pinned SDK's CommonJS dependencies.
            start.ArgumentList.Remove("--format=cjs");
            start.ArgumentList.Add("--format=esm");
            start.ArgumentList.Add("--banner:js=import { createRequire } from 'node:module'; const require = createRequire(import.meta.url);");
            bundle = Path.Join(fixture.Root, "fixture.mjs");
            for (var i = 0; i < start.ArgumentList.Count; i++)
                if (start.ArgumentList[i].StartsWith("--outfile=", StringComparison.Ordinal)) start.ArgumentList[i] = "--outfile=" + bundle;
            using (var compiler = Process.Start(start)!)
            {
                var errors = compiler.StandardError.ReadToEndAsync();
                await compiler.WaitForExitAsync();
                Assert.True(compiler.ExitCode == 0, await errors);
            }
            var node = new ProcessStartInfo("node")
            { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in new[] { bundle, fixture.Endpoint, launch.BuildDiscriminator, fixture.Staging,
                fixture.Control, launch.RunId.ToString(CultureInfo.InvariantCulture), launch.RunAttempt.ToString(CultureInfo.InvariantCulture),
                fixture.Now.ToString(CultureInfo.InvariantCulture) }) node.ArgumentList.Add(argument);
            fixture.server = Process.Start(node)!;
            var line = await fixture.server.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
            if (line is null || !line.StartsWith("READY ", StringComparison.Ordinal))
                Assert.Fail("Hybrid Node service did not start: " + await fixture.server.StandardError.ReadToEndAsync());
            fixture.port = int.Parse(line![6..], CultureInfo.InvariantCulture);
            return fixture;
        }
        catch { await fixture.DisposeAsync(); throw; }
    }

    internal GitHubArtifactRestrictedStateStore Store(ArtifactRestBudgetPolicy? policy = null) =>
        new(Launch, new GitHubArtifactRestClient(Launch, Handlers, policy ?? new(null, null, 0), Clock), Staging);

    internal void Flag(string name) => File.WriteAllText(Path.Join(Control, name), "1");
    internal int Count(string name)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Join(Control, "counts.json")));
        return document.RootElement.GetProperty(name).GetInt32();
    }
    internal static OpaqueStoreUploadRequest Upload(string name = "opaque", byte[]? bytes = null, long? expiry = null)
    {
        bytes ??= [0, 7, 255, 0, 9];
        return new(new(name), new(Guid.NewGuid().ToString("N")), bytes, new(OpaqueStoreHash.Sha256(bytes)),
            expiry ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600);
    }

    private sealed class LoopbackHandler(HybridArtifactFixture fixture) : HttpMessageHandler
    {
        private readonly HttpClient client = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false });
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            var authenticated = uri.Host == "api.github.com";
            Assert.Equal(authenticated ? fixture.Launch.Inputs.GitHubToken!.ExportForPrivateLaunch() : null,
                request.Headers.Authorization?.Parameter);
            Assert.Equal(authenticated ? "Bearer" : null, request.Headers.Authorization?.Scheme);
            Assert.True(authenticated || GitHubArtifactRestClient.SafeArchiveLocation(uri));
            fixture.Requests.Add((uri.Host, uri.PathAndQuery, request.Method.Method));
            using var forwarded = new HttpRequestMessage(request.Method, $"http://127.0.0.1:{fixture.port}" + uri.PathAndQuery);
            foreach (var header in request.Headers) forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
            return await client.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        }
        protected override void Dispose(bool disposing) { if (disposing) client.Dispose(); base.Dispose(disposing); }
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Join(directory.FullName, "package.json")) && Directory.Exists(Path.Join(directory.FullName, "src/action-wrapper")))
                return directory.FullName;
        throw new DirectoryNotFoundException("repository_root_missing");
    }
    public async ValueTask DisposeAsync()
    {
        if (server is { HasExited: false }) { server.Kill(entireProcessTree: true); await server.WaitForExitAsync(); }
        server?.Dispose(); server = null;
        if (Directory.Exists(Root))
        {
            if (!OperatingSystem.IsWindows())
                foreach (var directory in Directory.GetDirectories(Root, "*", SearchOption.AllDirectories).Reverse())
                    File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(Root, true);
        }
    }
}

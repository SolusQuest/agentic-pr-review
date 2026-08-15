using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace AgenticPrReview.Runtime.ActionHostVerifierFixture;

internal sealed class SyntheticOfficialPlatform : IAsyncDisposable
{
    private sealed class Artifact(
        long id,
        string name,
        byte[] archive,
        string digest,
        DateTimeOffset expiresAt,
        long producingRunId,
        int producingRunAttempt)
    {
        internal long Id { get; } = id;
        internal string Name { get; } = name;
        internal byte[] Archive { get; } = archive;
        internal string Digest { get; } = digest;
        internal DateTimeOffset ExpiresAt { get; } = expiresAt;
        internal long ProducingRunId { get; } = producingRunId;
        internal int ProducingRunAttempt { get; } = producingRunAttempt;
    }

    private readonly HttpListener listener = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly Dictionary<long, Artifact> artifacts = [];
    private readonly Dictionary<string, byte[]> blocks =
        new(StringComparer.Ordinal);
    private readonly List<string> artifactNames = [];
    private readonly object gate = new();
    private readonly string evidenceRoot;
    private Task? pump;
    private string activeMode = "sticky";
    private int inFlight;
    private string pendingName = "";
    private DateTimeOffset pendingExpiry;
    private long nextId = 1000;

    private SyntheticOfficialPlatform(string evidenceRoot, int port)
    {
        this.evidenceRoot = evidenceRoot;
        BaseUrl = $"http://127.0.0.1:{port}";
        listener.Prefixes.Add(BaseUrl + "/");
    }

    internal string BaseUrl { get; }

    internal int InFlight => Volatile.Read(ref inFlight);

    internal void BeginScenario(string mode)
    {
        lock (gate)
        {
            activeMode = mode;
        }
    }

    internal static SyntheticOfficialPlatform Start(string evidenceRoot)
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        var platform = new SyntheticOfficialPlatform(evidenceRoot, port);
        platform.listener.Start();
        platform.pump = platform.PumpAsync();
        return platform;
    }

    internal IReadOnlyList<string> ArtifactNames
    {
        get
        {
            lock (gate)
            {
                return artifactNames
                    .Order(StringComparer.Ordinal).ToArray();
            }
        }
    }

    internal void ResetArtifacts()
    {
        lock (gate)
        {
            artifacts.Clear();
            blocks.Clear();
            pendingName = "";
            pendingExpiry = default;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await shutdown.CancelAsync().ConfigureAwait(false);
        listener.Stop();
        if (pump is not null)
        {
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (HttpListenerException) when (shutdown.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException) when (shutdown.IsCancellationRequested)
            {
            }
        }

        listener.Close();
        shutdown.Dispose();
    }

    private async Task PumpAsync()
    {
        while (!shutdown.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync()
                    .WaitAsync(shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                break;
            }

            _ = Task.Run(() => HandleAsync(context), CancellationToken.None);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        Interlocked.Increment(ref inFlight);
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "";
            if (path.StartsWith("/twirp/", StringComparison.Ordinal))
            {
                await HandleTwirpAsync(context).ConfigureAwait(false);
                return;
            }

            if (path == "/blob/upload")
            {
                await HandleUploadAsync(context).ConfigureAwait(false);
                return;
            }

            if (path.StartsWith("/blob/download/", StringComparison.Ordinal))
            {
                await HandleSignedDownloadAsync(context).ConfigureAwait(false);
                return;
            }

            if (!HasGitHubAuthorization(
                    context.Request,
                    FrameworkCanaries.GitHubToken))
            {
                await WriteJsonAsync(context.Response,
                    HttpStatusCode.Unauthorized, "{}").ConfigureAwait(false);
                return;
            }

            await HandleRestAsync(context).ConfigureAwait(false);
        }
        catch (Exception error) when (IsNonFatal(error))
        {
            Directory.CreateDirectory(evidenceRoot);
            await File.AppendAllTextAsync(
                Path.Join(evidenceRoot, "platform-errors.txt"),
                error.GetType().Name + "\n").ConfigureAwait(false);
            if (context.Response.OutputStream.CanWrite)
            {
                await WriteJsonAsync(context.Response,
                    HttpStatusCode.InternalServerError, "{}")
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Decrement(ref inFlight);
        }
    }

    private async Task HandleTwirpAsync(HttpListenerContext context)
    {
        Increment("official-twirp-count");
        RecordObservation("actions-runtime-jwt", "results.authorization");
        if (!HasBearer(context.Request, FrameworkSupervisor.RuntimeToken))
        {
            await WriteJsonAsync(context.Response,
                HttpStatusCode.Unauthorized, "{}").ConfigureAwait(false);
            return;
        }

        var body = await ReadBodyAsync(context.Request).ConfigureAwait(false);
        if (!body.Contains(FrameworkCanaries.RunBackendId,
                StringComparison.Ordinal) ||
            !body.Contains(FrameworkCanaries.JobBackendId,
                StringComparison.Ordinal))
        {
            await WriteJsonAsync(context.Response,
                HttpStatusCode.BadRequest, "{}").ConfigureAwait(false);
            return;
        }

        if (context.Request.Url!.AbsolutePath.EndsWith(
                "/CreateArtifact", StringComparison.Ordinal))
        {
            using var document = JsonDocument.Parse(body);
            pendingName = PropertyString(document.RootElement, "name") ?? "";
            pendingExpiry = ParseExpiry(document.RootElement) ??
                DateTimeOffset.FromUnixTimeSeconds(1_900_000_000);
            lock (gate) blocks.Clear();
            var signed = BaseUrl + "/blob/upload?sig=" +
                Uri.EscapeDataString(FrameworkCanaries.SignedUrl);
            await WriteJsonAsync(context.Response, HttpStatusCode.OK,
                JsonSerializer.Serialize(new
                {
                    ok = true,
                    signedUploadUrl = signed,
                })).ConfigureAwait(false);
            return;
        }

        if (context.Request.Url.AbsolutePath.EndsWith(
                "/FinalizeArtifact", StringComparison.Ordinal))
        {
            byte[] archive;
            lock (gate)
            {
                archive = blocks.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .SelectMany(pair => pair.Value).ToArray();
            }

            if (archive.Length == 0 || string.IsNullOrEmpty(pendingName))
            {
                await WriteJsonAsync(context.Response,
                    HttpStatusCode.BadRequest, "{}").ConfigureAwait(false);
                return;
            }

            if (!TryEnvelopeIdentity(archive, out var producingRunId,
                    out var producingRunAttempt))
            {
                await WriteJsonAsync(context.Response,
                    HttpStatusCode.BadRequest, "{}").ConfigureAwait(false);
                return;
            }

            var digest = Convert.ToHexString(SHA256.HashData(archive))
                .ToLowerInvariant();
            var id = Interlocked.Increment(ref nextId);
            lock (gate)
            {
                artifacts[id] = new Artifact(
                    id,
                    pendingName,
                    archive,
                    digest,
                    pendingExpiry,
                    producingRunId,
                    producingRunAttempt);
                artifactNames.Add(pendingName);
            }

            if (!CanariesAbsent(archive))
            {
                File.WriteAllText(
                    Path.Join(evidenceRoot, "canary-route-violation"),
                    "plaintext_in_artifact_archive");
            }
            RecordObservation("artifact-ciphertext", "artifact.archive");

            Increment("official-finalize-count");
            if (Mode() == "artifact-upload-outcome-unknown")
            {
                File.WriteAllText(
                    Path.Join(evidenceRoot, "upload-outcome-unknown-committed"),
                    id.ToString(CultureInfo.InvariantCulture));
                await WriteJsonAsync(context.Response,
                    HttpStatusCode.InternalServerError,
                    "{\"ok\":true}").ConfigureAwait(false);
                return;
            }
            await WriteJsonAsync(context.Response, HttpStatusCode.OK,
                JsonSerializer.Serialize(new
                {
                    ok = true,
                    artifactId = id.ToString(CultureInfo.InvariantCulture),
                })).ConfigureAwait(false);
            return;
        }

        await WriteJsonAsync(context.Response, HttpStatusCode.NotFound, "{}")
            .ConfigureAwait(false);
    }

    private async Task HandleUploadAsync(HttpListenerContext context)
    {
        Increment("official-blob-count");
        RecordObservation("signed-url-sig", "blob.query");
        if (context.Request.QueryString["sig"] != FrameworkCanaries.SignedUrl ||
            context.Request.Headers["Authorization"] is not null)
        {
            context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            context.Response.Close();
            return;
        }

        var component = context.Request.QueryString["comp"];
        if (context.Request.HttpMethod == "PUT" && component == "block")
        {
            var id = context.Request.QueryString["blockid"] ?? "";
            var bytes = await ReadBytesAsync(context.Request).ConfigureAwait(false);
            lock (gate) blocks[id] = bytes;
            context.Response.StatusCode = (int)HttpStatusCode.Created;
            context.Response.Close();
            return;
        }

        if (context.Request.HttpMethod == "PUT" && component == "blocklist")
        {
            var xml = await ReadBodyAsync(context.Request).ConfigureAwait(false);
            var order = XDocument.Parse(xml).Descendants()
                .Where(node => node.Name.LocalName is "Latest" or "Uncommitted")
                .Select(node => node.Value).ToArray();
            lock (gate)
            {
                if (order.Length > 0)
                {
                    var ordered = order.Select((key, index) =>
                            new KeyValuePair<string, byte[]>(
                                index.ToString("D8", CultureInfo.InvariantCulture),
                                blocks[key]))
                        .ToArray();
                    blocks.Clear();
                    foreach (var pair in ordered) blocks[pair.Key] = pair.Value;
                }
            }

            context.Response.StatusCode = (int)HttpStatusCode.Created;
            context.Response.Close();
            return;
        }

        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
        context.Response.Close();
    }

    private async Task HandleSignedDownloadAsync(HttpListenerContext context)
    {
        Increment("official-signed-download-count");
        RecordObservation("signed-url-sig", "blob.query");
        if (context.Request.QueryString["sig"] != FrameworkCanaries.SignedUrl ||
            context.Request.Headers["Authorization"] is not null ||
            !long.TryParse(
                context.Request.Url!.AbsolutePath["/blob/download/".Length..],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var id) ||
            !TryArtifact(id, out var artifact))
        {
            context.Response.StatusCode = (int)HttpStatusCode.NotFound;
            context.Response.Close();
            return;
        }

        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = "application/zip";
        context.Response.ContentLength64 = artifact!.Archive.Length;
        await context.Response.OutputStream.WriteAsync(artifact.Archive)
            .ConfigureAwait(false);
        context.Response.Close();
    }

    private async Task HandleRestAsync(HttpListenerContext context)
    {
        Increment("official-rest-count");
        RecordObservation("github-token", "artifact-rest.authorization");
        var path = context.Request.Url!.AbsolutePath;
        var prefix = "/repos/" + FrameworkCanaries.Repository +
            "/actions/artifacts";
        if (context.Request.HttpMethod == "GET" && path == prefix)
        {
            var name = context.Request.QueryString["name"] ?? "";
            var page = ParsePositive(context.Request.QueryString["page"], 1);
            Artifact[] values;
            lock (gate)
            {
                values = artifacts.Values
                    .Where(value => value.Name == name)
                    .OrderBy(value => value.Id)
                    .ToArray();
            }

            var mode = Mode();
            if (values.Length > 0 && mode == "artifact-list-duplicate")
            {
                var duplicate = MetadataDocument(values[0]);
                await WriteJsonAsync(context.Response, HttpStatusCode.OK,
                    JsonSerializer.Serialize(new
                    {
                        total_count = 2,
                        artifacts = new[] { duplicate, duplicate },
                    })).ConfigureAwait(false);
                return;
            }

            if (values.Length > 0 && mode is
                "artifact-pagination-changed" or "artifact-pagination-late")
            {
                if (page == 1)
                {
                    var firstPage = Enumerable.Range(0, 100)
                        .Select(index => MetadataDocument(values[0],
                            10_000 + index)).ToArray();
                    await WriteJsonAsync(context.Response, HttpStatusCode.OK,
                        JsonSerializer.Serialize(new
                        {
                            total_count = 101,
                            artifacts = firstPage,
                        })).ConfigureAwait(false);
                    return;
                }

                if (mode == "artifact-pagination-late")
                {
                    await WriteJsonAsync(context.Response,
                        HttpStatusCode.InternalServerError, "{}")
                        .ConfigureAwait(false);
                    return;
                }

                await WriteJsonAsync(context.Response, HttpStatusCode.OK,
                    JsonSerializer.Serialize(new
                    {
                        total_count = 102,
                        artifacts = new[]
                        {
                            MetadataDocument(values[0], 10_100),
                        },
                    })).ConfigureAwait(false);
                return;
            }

            var selected = values.Skip((page - 1) * 100).Take(100)
                .Select(value => MetadataDocument(value,
                    overrideDigest: mode == "artifact-digest-mismatch",
                    overrideExpiry: mode == "artifact-expired"))
                .ToArray();
            await WriteJsonAsync(context.Response, HttpStatusCode.OK,
                JsonSerializer.Serialize(new
                {
                    total_count = values.Length,
                    artifacts = selected,
                })).ConfigureAwait(false);
            return;
        }

        var archivePrefix = prefix + "/";
        if (context.Request.HttpMethod == "GET" &&
            path.EndsWith("/zip", StringComparison.Ordinal))
        {
            var idText = path[archivePrefix.Length..^4];
            if (!long.TryParse(idText, NumberStyles.None,
                    CultureInfo.InvariantCulture, out var archiveId) ||
                !TryArtifact(archiveId, out _))
            {
                await WriteJsonAsync(context.Response, HttpStatusCode.NotFound,
                    "{}").ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = (int)HttpStatusCode.Redirect;
            context.Response.RedirectLocation = BaseUrl + "/blob/download/" +
                archiveId.ToString(CultureInfo.InvariantCulture) + "?sig=" +
                Uri.EscapeDataString(FrameworkCanaries.SignedUrl);
            context.Response.Close();
            return;
        }

        if (path.StartsWith(prefix + "/", StringComparison.Ordinal) &&
            TryTrailingId(path, prefix + "/", out var id))
        {
            if (context.Request.HttpMethod == "DELETE")
            {
                int before;
                int after;
                lock (gate)
                {
                    before = artifacts.Count;
                    artifacts.Remove(id);
                    after = artifacts.Count;
                }
                Increment("official-delete-count");
                if (Mode() == "delete-exact" &&
                    before > 1 && after == before - 1 &&
                    !TryArtifact(id, out _))
                {
                    File.WriteAllText(
                        Path.Join(evidenceRoot, "exact-delete-proof"),
                        before.ToString(CultureInfo.InvariantCulture) + "\t" +
                        after.ToString(CultureInfo.InvariantCulture));
                }
                if (Mode() == "artifact-delete-outcome-unknown")
                {
                    File.WriteAllText(
                        Path.Join(evidenceRoot,
                            "delete-outcome-unknown-committed"),
                        id.ToString(CultureInfo.InvariantCulture));
                    await WriteJsonAsync(context.Response,
                        HttpStatusCode.InternalServerError, "{}")
                        .ConfigureAwait(false);
                    return;
                }
                context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                context.Response.Close();
                return;
            }

            if (context.Request.HttpMethod == "GET" &&
                TryArtifact(id, out var artifact))
            {
                var mode = Mode();
                await WriteJsonAsync(context.Response, HttpStatusCode.OK,
                    JsonSerializer.Serialize(MetadataDocument(artifact!,
                        overrideDigest: mode == "artifact-digest-mismatch",
                        overrideExpiry: mode == "artifact-expired")))
                    .ConfigureAwait(false);
                return;
            }

            await WriteJsonAsync(context.Response, HttpStatusCode.NotFound,
                "{\"message\":\"Not Found\"}").ConfigureAwait(false);
            return;
        }

        var attemptPrefix = "/repos/" + FrameworkCanaries.Repository +
            "/actions/runs/";
        if (context.Request.HttpMethod == "GET" &&
            path.StartsWith(attemptPrefix, StringComparison.Ordinal) &&
            TryRunAttempt(path[attemptPrefix.Length..], out var runId,
                out var runAttempt))
        {
            await WriteJsonAsync(context.Response, HttpStatusCode.OK,
                JsonSerializer.Serialize(new
                {
                    id = runId,
                    run_attempt = runAttempt,
                })).ConfigureAwait(false);
            return;
        }

        await WriteJsonAsync(context.Response, HttpStatusCode.NotFound, "{}")
            .ConfigureAwait(false);
    }

    private static object MetadataDocument(
        Artifact artifact,
        long? overrideId = null,
        bool overrideDigest = false,
        bool overrideExpiry = false) => new
    {
        id = overrideId ?? artifact.Id,
        name = artifact.Name,
        size_in_bytes = artifact.Archive.Length,
        expired = overrideExpiry,
        expires_at = (overrideExpiry
                ? DateTimeOffset.FromUnixTimeSeconds(1_700_000_000)
                : artifact.ExpiresAt).UtcDateTime.ToString(
            "O", CultureInfo.InvariantCulture),
        digest = "sha256:" + (overrideDigest
            ? new string('0', 64)
            : artifact.Digest),
        workflow_run = new { id = artifact.ProducingRunId },
    };

    private static bool TryEnvelopeIdentity(
        byte[] archive,
        out long runId,
        out int runAttempt)
    {
        runId = 0;
        runAttempt = 0;
        try
        {
            using var stream = new MemoryStream(archive, writable: false);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            var entry = zip.GetEntry("artifact-envelope.json");
            if (entry is null) return false;
            using var entryStream = entry.Open();
            using var document = JsonDocument.Parse(entryStream);
            var root = document.RootElement;
            return long.TryParse(PropertyString(root, "producing_run_id"),
                    NumberStyles.None, CultureInfo.InvariantCulture,
                    out runId) && runId > 0 &&
                int.TryParse(PropertyString(root, "producing_run_attempt"),
                    NumberStyles.None, CultureInfo.InvariantCulture,
                    out runAttempt) && runAttempt > 0;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryRunAttempt(
        string suffix,
        out long runId,
        out int runAttempt)
    {
        runId = 0;
        runAttempt = 0;
        var parts = suffix.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 3 && parts[1] == "attempts" &&
            long.TryParse(parts[0], NumberStyles.None,
                CultureInfo.InvariantCulture, out runId) && runId > 0 &&
            int.TryParse(parts[2], NumberStyles.None,
                CultureInfo.InvariantCulture, out runAttempt) &&
            runAttempt > 0;
    }

    private bool TryArtifact(long id, out Artifact? artifact)
    {
        lock (gate) return artifacts.TryGetValue(id, out artifact);
    }

    private static bool TryTrailingId(
        string path,
        string prefix,
        out long id) => long.TryParse(
            path[prefix.Length..],
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out id);

    private static int ParsePositive(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture,
            out var parsed) && parsed > 0 ? parsed : fallback;

    private static string? PropertyString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? ParseExpiry(JsonElement root)
    {
        if (!root.TryGetProperty("expiresAt", out var expires)) return null;
        if (expires.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(expires.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var direct))
        {
            return direct;
        }

        if (expires.ValueKind == JsonValueKind.Object &&
            expires.TryGetProperty("seconds", out var seconds) &&
            long.TryParse(seconds.ToString(), NumberStyles.None,
                CultureInfo.InvariantCulture, out var unix))
        {
            return DateTimeOffset.FromUnixTimeSeconds(unix);
        }

        return null;
    }

    private static bool HasBearer(HttpListenerRequest request, string value) =>
        request.Headers["Authorization"] == "Bearer " + value;

    private static bool HasGitHubAuthorization(
        HttpListenerRequest request,
        string value) => request.Headers["Authorization"] is { } header &&
        (header == "Bearer " + value || header == "token " + value);

    private static async Task<string> ReadBodyAsync(
        HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream,
            request.ContentEncoding, false, leaveOpen: true);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadBytesAsync(
        HttpListenerRequest request)
    {
        using var stream = new MemoryStream();
        await request.InputStream.CopyToAsync(stream).ConfigureAwait(false);
        return stream.ToArray();
    }

    private static async Task WriteJsonAsync(
        HttpListenerResponse response,
        HttpStatusCode status,
        string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        response.StatusCode = (int)status;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        response.Close();
    }

    private void Increment(string name)
    {
        lock (gate)
        {
            var path = Path.Join(evidenceRoot, name);
            var value = File.Exists(path) && int.TryParse(
                File.ReadAllText(path), NumberStyles.None,
                CultureInfo.InvariantCulture, out var parsed)
                ? parsed + 1
                : 1;
            File.WriteAllText(path,
                value.ToString(CultureInfo.InvariantCulture));
        }
    }

    private void RecordObservation(string canaryClass, string sink)
    {
        lock (gate)
        {
            File.AppendAllText(
                Path.Join(evidenceRoot, "canary-observations.tsv"),
                canaryClass + "\t" + sink + "\n");
        }
    }

    private string Mode()
    {
        lock (gate) return activeMode;
    }

    private static bool CanariesAbsent(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        string[] forbidden =
        [
            FrameworkCanaries.ProviderKey,
            FrameworkCanaries.GitHubToken,
            FrameworkCanaries.StateKey,
            FrameworkCanaries.PreviousStateKey,
            FrameworkCanaries.Prompt,
            FrameworkCanaries.ToolData,
            FrameworkCanaries.ContinuationMarker,
            FrameworkCanaries.PublicResult,
        ];
        return forbidden.All(value =>
            !text.Contains(value, StringComparison.Ordinal) &&
            !text.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(value)),
                StringComparison.Ordinal) &&
            !text.Contains(Uri.EscapeDataString(value),
                StringComparison.Ordinal));
    }

    private static bool IsNonFatal(Exception error) =>
        error is not OutOfMemoryException and
        not StackOverflowException and
        not AccessViolationException;
}

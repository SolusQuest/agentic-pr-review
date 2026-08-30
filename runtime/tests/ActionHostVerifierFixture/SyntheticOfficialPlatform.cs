using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace AgenticPrReview.Runtime.ActionHostVerifierFixture;

internal sealed class SyntheticOfficialPlatform : IAsyncDisposable
{
    private sealed class Artifact(
        long id,
        string name,
        byte[] archive,
        string digest,
        string envelopeDigest,
        DateTimeOffset expiresAt,
        long producingRunId,
        int producingRunAttempt)
    {
        internal long Id { get; } = id;
        internal string Name { get; } = name;
        internal byte[] Archive { get; } = archive;
        internal string Digest { get; } = digest;
        internal string EnvelopeDigest { get; } = envelopeDigest;
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
    private readonly List<RecordedRequest> requestEvents = [];
    private readonly Dictionary<string, List<RecordedRequest>> scenarioRequestEvents =
        new(StringComparer.Ordinal);
    private readonly List<Task> handlers = [];
    private readonly object gate = new();
    private readonly string evidenceRoot;
    private readonly Func<long> epochSeconds;
    private Task? pump;
    private string activeMode = "sticky";
    private string activeScenarioRoot;
    private string activePayloadSha256 = new('f', 64);
    private int inFlight;
    private string pendingName = "";
    private DateTimeOffset pendingExpiry;
    private long nextId = 1000;

    private SyntheticOfficialPlatform(string evidenceRoot, int port,
        Func<long>? epochSeconds = null)
    {
        this.evidenceRoot = evidenceRoot;
        this.epochSeconds = epochSeconds ??
            (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        activeScenarioRoot = evidenceRoot;
        scenarioRequestEvents[evidenceRoot] = [];
        File.WriteAllLines(Path.Join(evidenceRoot,
            "trusted-proof-request-domains.tsv"),
        [
            "node_artifact_rest\t0",
            "host_head_source_rest\t0",
            "host_other_github_rest\t0",
            "trusted_control_rest\t0",
            "actions_results_service\t0",
            "anonymous_transfers\t0",
        ]);
        BaseUrl = $"http://127.0.0.1:{port}";
        listener.Prefixes.Add(BaseUrl + "/");
    }

    internal string BaseUrl { get; }

    internal int InFlight => Volatile.Read(ref inFlight);

    internal void BeginScenario(
        string mode,
        string scenarioRoot,
        string payloadSha256)
    {
        lock (gate)
        {
            activeMode = mode;
            activeScenarioRoot = scenarioRoot;
            activePayloadSha256 = payloadSha256;
            scenarioRequestEvents[scenarioRoot] = [];
            File.WriteAllLines(Path.Join(scenarioRoot,
                "trusted-proof-request-domains.tsv"),
            [
                "node_artifact_rest\t0",
                "host_head_source_rest\t0",
                "host_other_github_rest\t0",
                "trusted_control_rest\t0",
                "actions_results_service\t0",
                "anonymous_transfers\t0",
            ]);
        }
    }

    internal static SyntheticOfficialPlatform Start(string evidenceRoot,
        Func<long>? epochSeconds = null)
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        var platform = new SyntheticOfficialPlatform(evidenceRoot, port, epochSeconds);
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
                Debug.Assert(shutdown.IsCancellationRequested);
            }
            catch (ObjectDisposedException) when (shutdown.IsCancellationRequested)
            {
                Debug.Assert(shutdown.IsCancellationRequested);
            }
        }

        Task[] pending;
        lock (gate)
        {
            pending = [.. handlers];
        }
        await Task.WhenAll(pending).ConfigureAwait(false);

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

            Interlocked.Increment(ref inFlight);
            var handler = Task.Run(() => HandleAsync(context),
                CancellationToken.None);
            lock (gate)
            {
                handlers.Add(handler);
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var dispatchTimestamp = Stopwatch.GetTimestamp();
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "";
            if (path.StartsWith("/twirp/", StringComparison.Ordinal))
            {
                await HandleTwirpAsync(context).ConfigureAwait(false);
                RecordDispatch("actions_results_service", "actions_results_twirp", context,
                    dispatchTimestamp);
                return;
            }

            if (path == "/blob/upload")
            {
                await HandleUploadAsync(context).ConfigureAwait(false);
                RecordDispatch("anonymous_transfers", "actions_results_signed_upload", context,
                    dispatchTimestamp);
                return;
            }

            if (path.StartsWith("/blob/download/", StringComparison.Ordinal))
            {
                await HandleSignedDownloadAsync(context).ConfigureAwait(false);
                RecordDispatch("anonymous_transfers", "actions_results_signed_download", context,
                    dispatchTimestamp);
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

            var nodeCounter = BeginNodeArtifactRestCounter(context.Request);
            var responseClass = await HandleRestAsync(context).ConfigureAwait(false);
            ReconcileNodeArtifactRestCounter(nodeCounter,
                context.Response.StatusCode);
            RecordDispatch(ClassifyRestDomain(context.Request), "github_rest", context,
                dispatchTimestamp, responseClass);
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
        FrameworkCanaryCapture.CaptureAll(evidenceRoot,
            "results.authorization", context.Request.Headers["Authorization"]);
        if (!HasBearer(context.Request, FrameworkSupervisor.RuntimeToken))
        {
            await WriteJsonAsync(context.Response,
                HttpStatusCode.Unauthorized, "{}").ConfigureAwait(false);
            return;
        }

        var body = await ReadBodyAsync(context.Request).ConfigureAwait(false);
        FrameworkCanaryCapture.CaptureAll(evidenceRoot,
            "artifact.metadata", body);
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
            Increment("official-create-count");
            FrameworkCanaryCapture.CaptureAll(evidenceRoot,
                "results.create-response", signed);
            await WriteJsonAsync(context.Response, HttpStatusCode.OK,
                FrameworkJson.Serialize(FrameworkJson.Object(
                    ("ok", true),
                    ("signedUploadUrl", signed)))).ConfigureAwait(false);
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
                    out var producingRunAttempt, out var envelopeDigest))
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
                    envelopeDigest,
                    pendingExpiry,
                    producingRunId,
                    producingRunAttempt);
                artifactNames.Add(pendingName);
            }

            if (!FrameworkCanaryCapture.ArchiveHasNoPrivateCanary(
                    evidenceRoot, archive, "artifact.archive") ||
                !FrameworkCanaryCapture.ObserveCiphertextArchive(
                    evidenceRoot, archive, out _))
            {
                File.AppendAllText(
                    Path.Join(evidenceRoot, "canary-route-violation"),
                    "artifact-ciphertext\tartifact.archive\t" +
                    "plaintext_in_artifact_archive\n");
            }

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
                FrameworkJson.Serialize(FrameworkJson.Object(
                    ("ok", true),
                    ("artifactId", id.ToString(
                        CultureInfo.InvariantCulture))))).ConfigureAwait(false);
            return;
        }

        await WriteJsonAsync(context.Response, HttpStatusCode.NotFound, "{}")
            .ConfigureAwait(false);
    }

    private async Task HandleUploadAsync(HttpListenerContext context)
    {
        Increment("official-blob-count");
        FrameworkCanaryCapture.CaptureAll(evidenceRoot,
            "blob.query", context.Request.Url?.Query);
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
        FrameworkCanaryCapture.CaptureAll(evidenceRoot,
            "blob.query", context.Request.Url?.Query);
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

    private async Task<string?> HandleRestAsync(HttpListenerContext context)
    {
        FrameworkCanaryCapture.CaptureAll(evidenceRoot,
            "artifact-rest.authorization",
            context.Request.Headers["Authorization"]);
        FrameworkCanaryCapture.CaptureAll(evidenceRoot,
            "artifact.metadata", context.Request.Url?.Query);
        var path = context.Request.Url!.AbsolutePath;
        var prefix = "/repos/" + FrameworkCanaries.Repository +
            "/actions/artifacts";
        var attemptPrefix = "/repos/" + FrameworkCanaries.Repository +
            "/actions/runs/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal) &&
            !path.StartsWith(attemptPrefix, StringComparison.Ordinal))
        {
            return await ForwardGitHubAsync(context).ConfigureAwait(false);
        }

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
                await WriteEtaggedJsonAsync(context, HttpStatusCode.OK,
                    ArtifactList(2,
                    [
                        duplicate,
                        (JsonObject)duplicate.DeepClone(),
                    ])).ConfigureAwait(false);
                return null;
            }

            if (values.Length > 0 && mode is
                "artifact-pagination-changed" or "artifact-pagination-late")
            {
                if (page == 1)
                {
                    var firstPage = Enumerable.Range(0, 100)
                        .Select(index => MetadataDocument(values[0],
                            10_000 + index)).ToArray();
                    await WriteEtaggedJsonAsync(context, HttpStatusCode.OK,
                        ArtifactList(101, firstPage)).ConfigureAwait(false);
                    return null;
                }

                if (mode == "artifact-pagination-late")
                {
                    await WriteJsonAsync(context.Response,
                        HttpStatusCode.InternalServerError, "{}")
                        .ConfigureAwait(false);
                    return null;
                }

                await WriteEtaggedJsonAsync(context, HttpStatusCode.OK,
                    ArtifactList(102,
                    [
                        MetadataDocument(values[0], 10_100),
                    ])).ConfigureAwait(false);
                return null;
            }

            var selected = values.Skip((page - 1) * 100).Take(100)
                .Select(value => MetadataDocument(value,
                    overrideDigest: mode == "artifact-digest-mismatch",
                    overrideExpiry: mode == "artifact-expired"))
                .ToArray();
            await WriteEtaggedJsonAsync(context, HttpStatusCode.OK,
                ArtifactList(values.Length, selected)).ConfigureAwait(false);
            return null;
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
                return null;
            }

            context.Response.StatusCode = (int)HttpStatusCode.Redirect;
            context.Response.RedirectLocation = BaseUrl + "/blob/download/" +
                archiveId.ToString(CultureInfo.InvariantCulture) + "?sig=" +
                Uri.EscapeDataString(FrameworkCanaries.SignedUrl);
            context.Response.Close();
            return null;
        }

        if (path.StartsWith(prefix + "/", StringComparison.Ordinal) &&
            TryTrailingId(path, prefix + "/", out var id))
        {
            if (context.Request.HttpMethod == "DELETE")
            {
                ArtifactIdentity[] before;
                ArtifactIdentity[] after;
                ArtifactIdentity? target;
                bool removed;
                lock (gate)
                {
                    before = SnapshotArtifacts();
                    target = before.SingleOrDefault(value => value.Id == id);
                    removed = artifacts.Remove(id);
                    after = SnapshotArtifacts();
                }
                Increment("official-delete-count");
                if (Mode() == "delete-exact" &&
                    removed && target is not null && before.Length > 1 &&
                    after.Length == before.Length - 1 &&
                    after.All(value => value.Id != id) &&
                    after.SequenceEqual(before.Where(value => value.Id != id)))
                {
                    var preDigest = IdentityDigest(before);
                    var postDigest = IdentityDigest(after);
                    var targetDigest = IdentityDigest([target]);
                    File.WriteAllText(
                        Path.Join(evidenceRoot, "exact-delete-proof"),
                        FrameworkJson.SerializeIndented(FrameworkJson.Object(
                            ("requested_id", id),
                            ("target", ArtifactIdentityDocument(target)),
                            ("target_identity_digest", targetDigest),
                            ("complete_pre_map", ArtifactIdentities(before)),
                            ("complete_pre_map_digest", preDigest),
                            ("complete_post_map", ArtifactIdentities(after)),
                            ("complete_post_map_digest", postDigest),
                            ("preserved_non_target_count", after.Length),
                            ("target_absent", true),
                            ("non_targets_byte_identical", true),
                            ("operation_response", "no_content"))));
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
                    return null;
                }
                context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                context.Response.Close();
                return null;
            }

            if (context.Request.HttpMethod == "GET" &&
                TryArtifact(id, out var artifact))
            {
                var mode = Mode();
                await WriteEtaggedJsonAsync(context, HttpStatusCode.OK,
                    FrameworkJson.Serialize(MetadataDocument(artifact!,
                        overrideDigest: mode == "artifact-digest-mismatch",
                        overrideExpiry: mode == "artifact-expired")))
                    .ConfigureAwait(false);
                return null;
            }

            await WriteJsonAsync(context.Response, HttpStatusCode.NotFound,
                "{\"message\":\"Not Found\"}").ConfigureAwait(false);
            return null;
        }

        if (context.Request.HttpMethod == "GET" &&
            path.StartsWith(attemptPrefix, StringComparison.Ordinal) &&
            TryRunAttempt(path[attemptPrefix.Length..], out var runId,
                out var runAttempt))
        {
            await WriteEtaggedJsonAsync(context, HttpStatusCode.OK,
                FrameworkJson.Serialize(FrameworkJson.Object(
                    ("id", runId),
                    ("run_attempt", runAttempt)))).ConfigureAwait(false);
            return null;
        }

        await WriteJsonAsync(context.Response, HttpStatusCode.NotFound, "{}")
            .ConfigureAwait(false);
        return null;
    }

    private async Task<string> ForwardGitHubAsync(HttpListenerContext context)
    {
        string scenarioRoot;
        string payloadSha256;
        lock (gate)
        {
            scenarioRoot = activeScenarioRoot;
            payloadSha256 = activePayloadSha256;
        }

        using var handler = new FrameworkGitHubHandler(
            scenarioRoot,
            payloadSha256);
        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(
            new HttpMethod(context.Request.HttpMethod),
            new Uri("https://api.github.com" +
                context.Request.Url!.PathAndQuery));
        foreach (var key in context.Request.Headers.AllKeys)
        {
            var values = key is null
                ? null
                : context.Request.Headers.GetValues(key);
            if (key is not null && values is not null &&
                !request.Headers.TryAddWithoutValidation(
                    key,
                    values))
            {
                request.Content ??= new ByteArrayContent([]);
                request.Content.Headers.TryAddWithoutValidation(
                    key,
                    values);
            }
        }

        if (context.Request.HasEntityBody)
        {
            request.Content = new ByteArrayContent(
                await ReadBytesAsync(context.Request).ConfigureAwait(false));
            if (!string.IsNullOrEmpty(context.Request.ContentType))
            {
                request.Content.Headers.TryAddWithoutValidation(
                    "Content-Type",
                    context.Request.ContentType);
            }
        }

        using var response = await invoker.SendAsync(
            request,
            shutdown.Token).ConfigureAwait(false);
        var responseClass = TrustedProofOperationRequestAccounting.WitnessResponseClass(
            TrustedProofOperationRequestAccounting.ResponseClassify(
                response, epochSeconds()));
        context.Response.StatusCode = (int)response.StatusCode;
        foreach (var name in new[] { "x-ratelimit-limit", "x-ratelimit-remaining", "x-ratelimit-reset", "retry-after" })
        {
            if (response.Headers.TryGetValues(name, out var values))
            {
                context.Response.Headers[name] = string.Join(",", values);
            }
        }
        if (response.StatusCode != HttpStatusCode.NoContent &&
            response.Content is not null)
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(
                shutdown.Token).ConfigureAwait(false);
            context.Response.ContentLength64 = bytes.Length;
            if (response.Content.Headers.ContentType is not null)
            {
                context.Response.ContentType =
                    response.Content.Headers.ContentType.ToString();
            }

            if (bytes.Length > 0)
            {
                await context.Response.OutputStream.WriteAsync(bytes)
                    .ConfigureAwait(false);
            }
        }

        context.Response.Close();
        return responseClass;
    }

    private static JsonObject MetadataDocument(
        Artifact artifact,
        long? overrideId = null,
        bool overrideDigest = false,
        bool overrideExpiry = false) => FrameworkJson.Object(
            ("id", overrideId ?? artifact.Id),
            ("name", artifact.Name),
            ("size_in_bytes", artifact.Archive.Length),
            ("expired", overrideExpiry),
            ("expires_at", (overrideExpiry
                    ? DateTimeOffset.FromUnixTimeSeconds(1_700_000_000)
                    : artifact.ExpiresAt).UtcDateTime.ToString(
                "O", CultureInfo.InvariantCulture)),
            ("digest", "sha256:" + (overrideDigest
                ? new string('0', 64)
                : artifact.Digest)),
            ("workflow_run", FrameworkJson.Object(
                ("id", artifact.ProducingRunId))));

    private static string ArtifactList(
        int totalCount,
        IEnumerable<JsonObject> artifacts) => FrameworkJson.Serialize(
            FrameworkJson.Object(
                ("total_count", totalCount),
                ("artifacts", FrameworkJson.Array(artifacts))));

    private static bool TryEnvelopeIdentity(
        byte[] archive,
        out long runId,
        out int runAttempt,
        out string envelopeDigest)
    {
        runId = 0;
        runAttempt = 0;
        envelopeDigest = "";
        try
        {
            using var stream = new MemoryStream(archive, writable: false);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            var entry = zip.GetEntry("artifact-envelope.json");
            if (entry is null) return false;
            using var entryStream = entry.Open();
            using var content = new MemoryStream();
            entryStream.CopyTo(content);
            var envelopeBytes = content.ToArray();
            using var document = JsonDocument.Parse(envelopeBytes);
            var root = document.RootElement;
            envelopeDigest = Convert.ToHexString(SHA256.HashData(envelopeBytes))
                .ToLowerInvariant();
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

    private ArtifactIdentity[] SnapshotArtifacts() => artifacts.Values
        .OrderBy(value => value.Id)
        .Select(value => new ArtifactIdentity(
            value.Id,
            value.Name,
            value.Digest,
            value.EnvelopeDigest,
            value.ExpiresAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            value.ProducingRunId,
            value.ProducingRunAttempt))
        .ToArray();

    private static string IdentityDigest(
        IReadOnlyCollection<ArtifactIdentity> values) => Convert.ToHexString(
            SHA256.HashData(FrameworkJson.SerializeToUtf8Bytes(
                ArtifactIdentities(values))))
        .ToLowerInvariant();

    private static JsonArray ArtifactIdentities(
        IEnumerable<ArtifactIdentity> values) => FrameworkJson.Array(
            values.Select(ArtifactIdentityDocument));

    private static JsonObject ArtifactIdentityDocument(ArtifactIdentity value) =>
        FrameworkJson.Object(
            ("Id", value.Id),
            ("Name", value.Name),
            ("ArchiveDigest", value.ArchiveDigest),
            ("EnvelopeDigest", value.EnvelopeDigest),
            ("ExpiresAt", value.ExpiresAt),
            ("ProducingRunId", value.ProducingRunId),
            ("ProducingRunAttempt", value.ProducingRunAttempt));

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

    private async Task WriteEtaggedJsonAsync(
        HttpListenerContext context,
        HttpStatusCode status,
        string body)
    {
        var etag = "\"" + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(body))).ToLowerInvariant() + "\"";
        context.Response.Headers[HttpResponseHeader.ETag] = etag;
        if (MatchesIfNoneMatch(context.Request.Headers["If-None-Match"],
                etag))
        {
            context.Response.StatusCode = (int)HttpStatusCode.NotModified;
            context.Response.Close();
            return;
        }

        await WriteJsonAsync(context.Response, status, body).ConfigureAwait(false);
    }

    private static bool MatchesIfNoneMatch(string? header, string etag)
    {
        if (string.IsNullOrWhiteSpace(header)) return false;
        foreach (var value in header.Split(',', StringSplitOptions.TrimEntries))
        {
            if (value == "*" || value == etag ||
                (value.StartsWith("W/", StringComparison.Ordinal) &&
                    value[2..] == etag))
            {
                return true;
            }
        }

        return false;
    }

    private void Increment(string name)
    {
        Add(name, 1);
    }

    private void Add(string name, int amount)
    {
        if (amount <= 0) return;
        lock (gate)
        {
            var path = Path.Join(evidenceRoot, name);
            var value = File.Exists(path) && int.TryParse(
                File.ReadAllText(path), NumberStyles.None,
                CultureInfo.InvariantCulture, out var parsed)
                ? checked(parsed + amount)
                : amount;
            File.WriteAllText(path,
                value.ToString(CultureInfo.InvariantCulture));
        }
    }

    private void Decrement(string name)
    {
        lock (gate)
        {
            var path = Path.Join(evidenceRoot, name);
            var value = File.Exists(path) && int.TryParse(
                File.ReadAllText(path), NumberStyles.None,
                CultureInfo.InvariantCulture, out var parsed)
                ? Math.Max(0, parsed - 1)
                : 0;
            File.WriteAllText(path,
                value.ToString(CultureInfo.InvariantCulture));
        }
    }

    // The fixture persists only normalized routing facts. In particular it
    // never retains a token, ETag, query string, or request body. This is the
    // independent witness for the payload/control receipts.
    private void RecordDispatch(
        string domain,
        string route,
        HttpListenerContext context,
        long timestamp,
        string? normalizedResponse = null)
    {
        var request = context.Request;
        var statusCode = context.Response.StatusCode;
        var isRead = request.HttpMethod is "GET" or "HEAD" or "OPTIONS";
        var response = normalizedResponse ?? ClassifySyntheticResponse(statusCode);
        var item = new RecordedRequest(domain, route, request.HttpMethod,
            isRead ? 1 : 5, timestamp, response);
        lock (gate)
        {
            requestEvents.Add(item);
            if (!scenarioRequestEvents.TryGetValue(activeScenarioRoot,
                    out var scenarioEvents))
            {
                throw new InvalidOperationException("synthetic_scenario_not_started");
            }
            scenarioEvents.Add(item);
            File.WriteAllLines(Path.Join(evidenceRoot,
                    "trusted-proof-request-events.tsv"),
                requestEvents.Select(value => value.ToTsv()));
            var scenarioPath = activeScenarioRoot == evidenceRoot
                ? null
                : Path.Join(activeScenarioRoot,
                    "trusted-proof-request-events.tsv");
            if (scenarioPath is not null)
            {
                File.AppendAllText(scenarioPath, item.ToTsv() + "\n");
                File.WriteAllLines(Path.Join(activeScenarioRoot,
                    "trusted-proof-request-domains.tsv"),
                [
                    "node_artifact_rest\t" + CountScenario("node_artifact_rest"),
                    "host_head_source_rest\t" + CountScenario("host_head_source_rest"),
                    "host_other_github_rest\t" + CountScenario("host_other_github_rest"),
                    "trusted_control_rest\t" + CountScenario("trusted_control_rest"),
                    "actions_results_service\t" + CountScenario("actions_results_service"),
                    "anonymous_transfers\t" + CountScenario("anonymous_transfers"),
                ]);
            }
        }

        // Compatibility counters remain scoped to the Node artifact transport;
        // forwarded Host/control traffic must not contaminate its receipt.
        if (domain == "anonymous_transfers")
        {
            // This is the Actions Results signed-transfer subdomain.  Archive
            // codeload is intentionally not sent through this listener and is
            // joined separately to the Host receipt by FrameworkGitHubHandler's
            // exact anonymous-codeload counter.
            Increment(route == "actions_results_signed_upload"
                ? "actions-results-signed-upload-count"
                : "actions-results-signed-download-count");
        }
    }

    private NodeArtifactRestCounter BeginNodeArtifactRestCounter(
        HttpListenerRequest request)
    {
        if (ClassifyRestDomain(request) != "node_artifact_rest")
        {
            return NodeArtifactRestCounter.None;
        }

        Increment("official-rest-count");
        Add("official-rest-secondary-points",
            SecondaryPoints(request.HttpMethod));
        var counter = string.IsNullOrWhiteSpace(
            request.Headers["If-None-Match"])
            ? NodeArtifactRestCounter.Primary
            : NodeArtifactRestCounter.NotModified;
        Increment(counter == NodeArtifactRestCounter.Primary
            ? "official-rest-primary-count"
            : "official-rest-not-modified-count");
        return counter;
    }

    private void ReconcileNodeArtifactRestCounter(
        NodeArtifactRestCounter predicted,
        int statusCode)
    {
        if (predicted == NodeArtifactRestCounter.None)
        {
            return;
        }

        var actual = statusCode == (int)HttpStatusCode.NotModified
            ? NodeArtifactRestCounter.NotModified
            : NodeArtifactRestCounter.Primary;
        if (actual == predicted) return;
        Decrement(predicted == NodeArtifactRestCounter.Primary
            ? "official-rest-primary-count"
            : "official-rest-not-modified-count");
        Increment(actual == NodeArtifactRestCounter.Primary
            ? "official-rest-primary-count"
            : "official-rest-not-modified-count");
    }

    private static string ClassifySyntheticResponse(int statusCode)
    {
        return statusCode switch
        {
            304 => "not_modified",
            >= 200 and < 300 => "success",
            403 => "permission_denied",
            _ => "other_failure",
        };
    }

    private int CountScenario(string domain) => scenarioRequestEvents[activeScenarioRoot]
        .Count(value => value.Domain == domain);

    private static string ClassifyRestDomain(HttpListenerRequest request)
    {
        var path = request.Url?.AbsolutePath ?? "";
        var repository = "/repos/" + FrameworkCanaries.Repository;
        if (path.StartsWith(repository + "/actions/artifacts", StringComparison.Ordinal) ||
            path.StartsWith(repository + "/actions/runs/", StringComparison.Ordinal))
        {
            return "node_artifact_rest";
        }

        if (string.Equals(request.UserAgent, "agentic-pr-review-r4-e2p",
                StringComparison.Ordinal))
        {
            return "trusted_control_rest";
        }

        return IsExactHeadSourcePath(path)
            ? "host_head_source_rest"
            : "host_other_github_rest";
    }

    private static bool IsExactHeadSourcePath(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var candidate = parts.Length switch
        {
            6 when parts[0] == "repos" && parts[3] == "git" &&
                parts[4] is "commits" or "trees" => parts[5],
            5 when parts[0] == "repos" && parts[3] == "tarball" => parts[4],
            _ => null,
        };
        return candidate is not null && candidate.Length == 40 &&
            candidate.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private string Mode()
    {
        lock (gate) return activeMode;
    }

    private static bool IsNonFatal(Exception error) =>
        error is not OutOfMemoryException and
        not StackOverflowException and
        not AccessViolationException;

    private static int SecondaryPoints(string method) =>
        method is "GET" or "HEAD" or "OPTIONS" ? 1 : 5;

    private enum NodeArtifactRestCounter
    {
        None,
        Primary,
        NotModified,
    }

    private sealed record ArtifactIdentity(
        long Id,
        string Name,
        string ArchiveDigest,
        string EnvelopeDigest,
        string ExpiresAt,
        long ProducingRunId,
        int ProducingRunAttempt);

    private sealed record RecordedRequest(
        string Domain,
        string Route,
        string Method,
        int SecondaryPoints,
        long Timestamp,
        string ResponseClass)
    {
        internal string ToTsv() => Domain + "\t" + Route + "\t" + Method + "\t" +
            SecondaryPoints.ToString(CultureInfo.InvariantCulture) + "\t" +
            Timestamp.ToString(CultureInfo.InvariantCulture) + "\t" +
            ResponseClass;
    }
}

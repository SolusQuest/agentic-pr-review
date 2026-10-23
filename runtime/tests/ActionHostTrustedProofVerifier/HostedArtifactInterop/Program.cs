using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.Host.State.GitHubArtifacts;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;

// Repository-only companion. The ordinary CLI never selects this linkedRunner.
internal static class Program
{
    private const string Repository = "SolusQuest/agentic-pr-review";
    private const string Workflow = ".github/workflows/cr2-hosted-artifact-proof.yml";
    private const string ActionSource = "f45b6c09a166eb0f4c1683d93d1766c607c3f4b9";
    private const string Digest = "15d392b30989f7b08f94615c1c57202a47a95408e35abdf286662a4ab8c3ce86";
    private static JsonElement config;
    private static readonly Observations observed = new();
    private static OpaqueStoreObjectMetadata? admitted;
    private static string outcome = "refused";
    private static string mutationState = "not_dispatched";

    public static async Task<int> Main()
    {
        try
        {
            var path = Environment.GetEnvironmentVariable("APR_CR2_H1_CONFIG");
            Require(path is not null && Path.IsPathFullyQualified(path));
            var bytes = await File.ReadAllBytesAsync(path!);
            Require(bytes.Length is > 0 and <= 32768);
            using var document = ArtifactTransportFormat.ParseJson(bytes);
            config = document.RootElement.Clone();
            Require(Text("mode") is "rehearsal" or "hosted");
            if (Text("mode") == "hosted") Require(!config.TryGetProperty("faults", out _));
            if (Text("mode") == "rehearsal")
            {
                var origin = new Uri(Text("origin"));
                Require(origin.Scheme == "http" && origin.Host == "127.0.0.1" && origin.Port > 0 && origin.AbsolutePath == "/");
            }
            Require(Text("phase") is "produce" or "restore");
            Require(Text("source").Length == 40 && Text("actorId") == "16307884");
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
            using var term = OperatingSystem.IsWindows() ? null : System.Runtime.InteropServices.PosixSignalRegistration.Create(
                System.Runtime.InteropServices.PosixSignal.SIGTERM, context => { context.Cancel = true; deadline.Cancel(); });
            using var interrupt = OperatingSystem.IsWindows() ? null : System.Runtime.InteropServices.PosixSignalRegistration.Create(
                System.Runtime.InteropServices.PosixSignal.SIGINT, context => { context.Cancel = true; deadline.Cancel(); });
            return await RuntimeApplication.RunActionHostAsync(Console.OpenStandardInput(),
                Console.OpenStandardOutput(), Console.Error, deadline.Token, RunAsync);
        }
        catch
        {
            Console.Error.WriteLine("CR2_H1_COMPANION_REFUSED");
            return 1;
        }
        finally
        {
            try { await WriteReceiptAsync(); }
            catch { /* No private error text or speculative successful receipt. */ }
        }
    }

    private static async Task<ActionHostCompletion> RunAsync(ActionHostLaunchContract launch, CancellationToken token)
    {
        using var proofCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        token = proofCancellation.Token;
        if (Fault("cancelBefore")) proofCancellation.Cancel();
        Require(launch.RepositoryName == Repository && launch.RepositoryId == 1263978368 && launch.RunAttempt == 1);
        Require(launch.RunId.ToString() == Text("runId") && launch.WorkflowSha == Text("source"));
        Require(launch.ActionSourceSha == ActionSource && launch.WorkflowPath == Workflow);
        Require(launch.WorkflowRef == Repository + "/" + Workflow + "@refs/heads/main");
        Require(launch.BuildDiscriminator == "r7-d0" && launch.Inputs.GitHubToken is not null);
        var eventBytes = await File.ReadAllBytesAsync(launch.EventJsonPath, token);
        Require(eventBytes.Length <= 65536 && OpaqueStoreHash.Sha256(eventBytes) == launch.EventJsonSha256);
        using (var eventDoc = ArtifactTransportFormat.ParseJson(eventBytes))
        {
            Require(eventDoc.RootElement.GetProperty("repository").GetProperty("id").GetInt64() == 1263978368);
            Require(eventDoc.RootElement.GetProperty("sender").GetProperty("id").GetInt64() == 16307884);
            Require(eventDoc.RootElement.GetProperty("sender").GetProperty("login").GetString() == "Yuee98");
        }
        CryptographicOperations.ZeroMemory(eventBytes);
        var credential = launch.Inputs.GitHubToken!.ExportForPrivateLaunch();
        if (Text("mode") == "rehearsal") Require(credential == "cr2-h1-rest-token-canary");
        using var rest = new GitHubArtifactRestClient(launch, () => new LimitedHandler(credential),
            new ArtifactRestBudgetPolicy(16, 16, 0));
        using var store = new GitHubArtifactRestrictedStateStore(launch, rest);
        var name = new OpaqueStoreName("cr2-h1-" + Text("runA"));
        if (Text("phase") == "produce")
        {
            Require(Text("runA") == Text("runId"));
            var empty = await store.ListExactAsync(new(name, 2, 4096), token);
            Require(empty.Succeeded && empty.Complete && empty.Objects.Length == 0);
            Require(observed.LastCensus is >= 0 and <= 99);
            Require(observed.Api <= 5); // Reserve capacity before the one SDK dispatch.
            token.ThrowIfCancellationRequested();
            var encrypted = Fixture();
            try
            {
                observed.Uploads++;
                mutationState = "upload_possible";
                var uploaded = await store.UploadImmutableAsync(new(name, new("cr2-h1-one-upload"), encrypted,
                    new(Digest), DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600), token);
                admitted = uploaded.Metadata;
                mutationState = uploaded.MutationState switch
                {
                    OpaqueStoreMutationState.Committed => "upload_committed",
                    OpaqueStoreMutationState.OutcomeUnknown => "upload_unknown",
                    _ => "not_committed",
                };
                Require(uploaded.Succeeded && uploaded.MutationState == OpaqueStoreMutationState.Committed && admitted is not null);
                if (Fault("cancelAfterUpload")) proofCancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }
            finally { CryptographicOperations.ZeroMemory(encrypted); }
            ValidateArtifact(admitted!, Text("runId"));
            var listed = await store.ListExactAsync(new(name, 2, 4096), token);
            Require(listed.Succeeded && listed.Complete && listed.Objects.Length == 1 && listed.Objects[0] == admitted!.Reference);
            var readback = await store.ReadBackExactAsync(new(admitted!), token);
            Require(readback.Succeeded && readback.Metadata == admitted);
            Require(observed.Lists == 2 && observed.Uploads == 1 && observed.Deletes == 0);
        }
        else
        {
            Require(Text("runA") != Text("runId"));
            var candidateBytes = await File.ReadAllBytesAsync(Text("receiptIn"), token);
            Require(candidateBytes.Length is > 0 and <= 16384);
            using var candidateDoc = ArtifactTransportFormat.ParseJson(candidateBytes);
            var candidate = candidateDoc.RootElement;
            Require(candidate.GetProperty("format").GetString() == "apr.cr2.hosted-artifact-proof.v1");
            Require(candidate.GetProperty("outcome").GetString() == "passed" && candidate.GetProperty("phase").GetString() == "produce");
            Require(candidate.GetProperty("mutationState").GetString() == "upload_committed");
            Require(candidate.GetProperty("source").GetString() == Text("source") && candidate.GetProperty("runId").GetString() == Text("runA"));
            Require(candidate.GetProperty("actionSource").GetString() == ActionSource);
            Require(candidate.GetProperty("repositoryId").GetString() == "1263978368" && candidate.GetProperty("actorId").GetString() == "16307884");
            Require(candidate.GetProperty("mode").GetString() == Text("mode"));
            var artifact = candidate.GetProperty("artifact");
            var id = artifact.GetProperty("id").GetString()!;
            Require(ArtifactTransportFormat.PositiveDecimal(id, out _));
            using (var operation = new ArtifactRestOperation(token, rest.Clock))
            using (var provenance = await rest.GetAsync(rest.AttemptPath(Text("runA"), 1), operation))
                ValidateProducer(provenance.RootElement);
            var listed = await store.ListExactAsync(new(name, 2, 4096), token);
            Require(listed.Succeeded && listed.Complete && listed.Objects.Length == 1);
            Require(listed.Objects[0] == new OpaqueStoreObjectReference(name, new(id)));
            var metadata = await store.ReadMetadataAsync(new(listed.Objects[0], 4096), token);
            Require(metadata.Succeeded && metadata.Metadata is not null);
            admitted = metadata.Metadata;
            ValidateArtifact(admitted!, Text("runA"));
            Require(admitted!.ArchiveDigest.Sha256 == artifact.GetProperty("archiveDigest").GetString());
            Require(admitted.ExpiresAtUnixSeconds == artifact.GetProperty("expires").GetInt64());
            Require(artifact.GetProperty("name").GetString() == name.Value && artifact.GetProperty("physicalName").GetString() == ArtifactTransportFormat.PhysicalName(name.Value, Digest));
            Require(artifact.GetProperty("producer").GetString() == Text("runA") && artifact.GetProperty("attempt").GetInt32() == 1);
            Require(artifact.GetProperty("encryptedDigest").GetString() == Digest && artifact.GetProperty("size").GetInt64() == 4096);
            var restored = await store.DownloadAsync(new(admitted, 4096), token);
            Require(restored.Succeeded && restored.Metadata == admitted);
            try { Authenticate(restored.EncryptedBytes.Span); }
            finally { if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(restored.EncryptedBytes, out var array)) Array.Clear(array.Array!); }
            // Only this independently restored exact object is deletion authority.
            observed.DeleteAdmitted = admitted.Reference.ObjectId.Value;
            mutationState = "delete_possible";
            var deleted = await store.DeleteExactAsync(new(admitted), token);
            mutationState = deleted.MutationState switch
            {
                OpaqueStoreMutationState.Committed => "delete_committed",
                OpaqueStoreMutationState.OutcomeUnknown => "delete_unknown",
                _ => "not_committed",
            };
            Require(deleted.Succeeded && deleted.MutationState == OpaqueStoreMutationState.Committed);
            Require(observed.Lists == 1 && observed.Uploads == 0 && observed.Deletes == 1);
        }
        // Product review was not run. Transport acceptance lives only in the separate receipt.
        Require(ActionHostStepSummary.TryCreate(null, null, null, ActionHostStateDisposition.NotAccessed, out var summary));
        Require(ActionHostCompletion.TryCreate("r7-d0", ActionHostStatus.SkippedUntrustedEvent,
            summary, [], out var completion, ActionHostAccounting.NoProvider, ActionHostTerminationReason.NotStarted));
        outcome = "passed";
        return completion!;
    }

    private static void ValidateProducer(JsonElement run)
    {
        Require(run.GetProperty("id").GetInt64().ToString() == Text("runA") && run.GetProperty("run_attempt").GetInt32() == 1);
        Require(run.GetProperty("head_sha").GetString() == Text("source") && run.GetProperty("path").GetString() == Workflow);
        Require(run.GetProperty("event").GetString() == "workflow_dispatch" && run.GetProperty("status").GetString() == "completed" && run.GetProperty("conclusion").GetString() == "success");
        Require(run.GetProperty("repository").GetProperty("id").GetInt64() == 1263978368);
        foreach (var field in new[] { "actor", "triggering_actor" })
            Require(run.GetProperty(field).GetProperty("id").GetInt64() == 16307884 && run.GetProperty(field).GetProperty("login").GetString() == "Yuee98");
        var created = DateTimeOffset.Parse(run.GetProperty("created_at").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        Require(created <= DateTimeOffset.UtcNow && DateTimeOffset.UtcNow - created <= TimeSpan.FromMinutes(30));
    }

    private static void ValidateArtifact(OpaqueStoreObjectMetadata value, string producer)
    {
        Require(value.Reference.Name.Value == "cr2-h1-" + Text("runA") && value.ProducingRun.Identity == producer && value.ProducingRun.Attempt == 1);
        Require(value.Size == 4096 && value.EncryptedObjectDigest.Sha256 == Digest);
        Require(value.ExpiresAtUnixSeconds >= DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 1800 && value.ExpiresAtUnixSeconds <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 90000);
    }

    private static byte[] Fixture()
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes("apr.cr2.h1.public-test-key.v1"));
        var nonce = SHA256.HashData(Encoding.UTF8.GetBytes("apr.cr2.h1.public-test-nonce.v1"))[..12];
        var plain = Enumerable.Repeat((byte)0x5a, 4068).ToArray();
        Encoding.UTF8.GetBytes("CR2-H1-SYNTHETIC-OPAQUE-V1").CopyTo(plain, 0);
        var result = new byte[4096]; nonce.CopyTo(result, 0);
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, result.AsSpan(12, 4068), result.AsSpan(4080, 16), Encoding.UTF8.GetBytes("apr.cr2.h1.synthetic-only.v1"));
        CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plain);
        Require(OpaqueStoreHash.Sha256(result) == Digest);
        return result;
    }

    private static void Authenticate(ReadOnlySpan<byte> bytes)
    {
        var expected = Fixture();
        try { Require(bytes.SequenceEqual(expected)); }
        finally { CryptographicOperations.ZeroMemory(expected); }
        var key = SHA256.HashData(Encoding.UTF8.GetBytes("apr.cr2.h1.public-test-key.v1"));
        var plain = new byte[4068];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(bytes[..12], bytes.Slice(12, 4068), bytes.Slice(4080, 16), plain, Encoding.UTF8.GetBytes("apr.cr2.h1.synthetic-only.v1"));
            Require(plain.AsSpan().StartsWith(Encoding.UTF8.GetBytes("CR2-H1-SYNTHETIC-OPAQUE-V1")));
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plain); }
    }

    private static async Task WriteReceiptAsync()
    {
        if (config.ValueKind != JsonValueKind.Object) return;
        var artifact = admitted is null ? null : new
        {
            id = admitted.Reference.ObjectId.Value, name = admitted.Reference.Name.Value,
            physicalName = ArtifactTransportFormat.PhysicalName(admitted.Reference.Name.Value, Digest),
            encryptedDigest = admitted.EncryptedObjectDigest.Sha256, archiveDigest = admitted.ArchiveDigest.Sha256,
            expires = admitted.ExpiresAtUnixSeconds, size = admitted.Size,
            producer = admitted.ProducingRun.Identity, attempt = admitted.ProducingRun.Attempt,
        };
        var receipt = new
        {
            format = "apr.cr2.hosted-artifact-proof.v1", mode = Text("mode"), phase = Text("phase"),
            source = Text("source"), actionSource = ActionSource, repositoryId = "1263978368", actorId = "16307884",
            runId = Text("runId"), runA = Text("runA"), artifact,
            counts = new { api = observed.Api, anonymous = observed.Anonymous, metadataBytes = observed.MetadataBytes,
                archiveBytes = observed.ArchiveBytes, lists = observed.Lists, uploads = observed.Uploads, deletes = observed.Deletes },
            outcome, mutationState, syntheticMap = true, companionEntry = true, publicDevelopmentProfile = true,
        };
        await File.WriteAllTextAsync(Text("receiptOut"), JsonSerializer.Serialize(receipt));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Text("receiptOut"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private sealed class Observations
    {
        internal int Api, Anonymous, MetadataBytes, ArchiveBytes, Lists, Uploads, Deletes;
        internal int LastCensus = -1;
        internal string? DeleteAdmitted;
    }

    private sealed class LimitedHandler(string credential) : HttpMessageHandler
    {
        private readonly HttpClient inner = new(ActionHostGitHubAuthorizationTransport.CreateHandler(TimeSpan.FromSeconds(10))) { Timeout = Timeout.InfiniteTimeSpan };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            Require(uri.Scheme == "https" && uri.Port == 443 && uri.UserInfo.Length == 0);
            var api = uri.Host == "api.github.com";
            Require(api || GitHubArtifactRestClient.SafeArchiveLocation(uri));
            Require(api ? request.Headers.Authorization?.Parameter == credential : request.Headers.Authorization is null);
            if (api)
            {
                Require(observed.Api < 16 && uri.AbsolutePath.StartsWith("/repos/" + Repository + "/actions/", StringComparison.Ordinal));
                observed.Api++;
                if (uri.AbsolutePath.EndsWith("/artifacts", StringComparison.Ordinal))
                {
                    Require(request.Method == HttpMethod.Get && uri.Query == "?per_page=100&page=1");
                    Require(observed.Lists < (Text("phase") == "produce" ? 2 : 1));
                    observed.Lists++;
                }
                if (request.Method == HttpMethod.Delete)
                {
                    Require(Text("phase") == "restore" && observed.DeleteAdmitted is not null);
                    Require(uri.AbsolutePath == "/repos/" + Repository + "/actions/artifacts/" + observed.DeleteAdmitted && observed.Deletes == 0);
                    observed.Deletes++;
                }
                else Require(request.Method == HttpMethod.Get);
            }
            else { Require(request.Method == HttpMethod.Get && observed.Anonymous < 2); observed.Anonymous++; }
            using var forwarded = new HttpRequestMessage(request.Method, Text("mode") == "rehearsal"
                ? new Uri(new Uri(Text("origin")), uri.PathAndQuery) : uri);
            foreach (var header in request.Headers) forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
            using var response = await inner.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, token);
            var maximum = api ? 524288 - observed.MetadataBytes : Math.Min(16384, 32768 - observed.ArchiveBytes);
            Require(response.Content.Headers.ContentLength is not { } declared || declared <= maximum);
            byte[] body = [];
            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.InternalServerError)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(token);
                using var output = new MemoryStream();
                var buffer = new byte[8192];
                for (;;)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maximum + 1 - (int)output.Length)), token);
                    if (read == 0) break;
                    Require(output.Length + read <= maximum && (api || output.Length + read <= 16384));
                    output.Write(buffer, 0, read);
                }
                body = output.ToArray();
                if (api) observed.MetadataBytes += body.Length; else observed.ArchiveBytes += body.Length;
            }
            if (api && response.StatusCode == HttpStatusCode.OK && uri.AbsolutePath.EndsWith("/artifacts", StringComparison.Ordinal))
            {
                using var census = ArtifactTransportFormat.ParseJson(body);
                observed.LastCensus = census.RootElement.GetProperty("total_count").GetInt32();
                Require(observed.LastCensus >= 0 && observed.LastCensus <= (Text("phase") == "produce" && observed.Uploads == 0 ? 99 : 100));
            }
            var result = new HttpResponseMessage(response.StatusCode) { Content = new ByteArrayContent(body) };
            foreach (var header in response.Headers) result.Headers.TryAddWithoutValidation(header.Key, header.Value);
            foreach (var header in response.Content.Headers.Where(header => header.Key != "Content-Length"))
                result.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            return result;
        }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    private static string Text(string name) => config.GetProperty(name).GetString() ?? throw new IOException("cr2_h1_refused");
    private static bool Fault(string name) => Text("mode") == "rehearsal" && config.TryGetProperty("faults", out var faults) &&
        faults.ValueKind == JsonValueKind.Object && faults.TryGetProperty(name, out var flag) && flag.ValueKind == JsonValueKind.True;
    private static void Require(bool value) { if (!value) throw new IOException("cr2_h1_refused"); }
}

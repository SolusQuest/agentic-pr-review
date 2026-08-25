using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHostTrustedProofCapture;
using AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceContracts;
using AgenticPrReview.Runtime.Host.State.GitHubArtifacts;

namespace AgenticPrReview.Runtime.Tests.Host.Action.TrustedProof;

public sealed class TrustedProofEvidenceBoundaryTests : IDisposable
{
    private readonly List<string> roots = [];

    [Fact]
    public void EvidenceLimitsMatchProductionArtifactBridge()
    {
        Assert.Equal(ArtifactBridgeLimits.MaximumNameBytes, EvidenceLimits.MaximumNameBytes);
        Assert.Equal(ArtifactBridgeLimits.MaximumCorrelationBytes, EvidenceLimits.MaximumCorrelationBytes);
        Assert.Equal(ArtifactBridgeLimits.MaximumRelativePathBytes, EvidenceLimits.MaximumRelativePathBytes);
        Assert.Equal(ArtifactBridgeLimits.MaximumEncryptedObjectBytes, EvidenceLimits.MaximumEncryptedObjectBytes);
        Assert.Equal(ArtifactBridgeLimits.MaximumStagingFileBytes, EvidenceLimits.MaximumArchiveBytes);
        Assert.Equal(ArtifactBridgeLimits.MaximumDocumentBytes, EvidenceLimits.MaximumDocumentBytes);
        Assert.Equal(ArtifactBridgeLimits.RecordsPerPage, EvidenceLimits.RecordsPerPage);
        Assert.Equal(ArtifactBridgeLimits.MaximumPages, EvidenceLimits.MaximumPages);
        Assert.Equal(ArtifactBridgeLimits.MaximumRecords, EvidenceLimits.MaximumRecords);
        Assert.Equal(ArtifactBridgeLimits.RequestTimeout, EvidenceLimits.RequestTimeout);
        Assert.Equal(ArtifactBridgeLimits.LogicalOperationTimeout, EvidenceLimits.LogicalOperationTimeout);
    }

    [Fact]
    public void CaptureAndOracleHaveDisjointProtectedCapabilities()
    {
        var capture = typeof(TrustedProofCaptureClient).Assembly;
        var oracle = Assembly.Load("AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceOracle");
        Assert.DoesNotContain(
            capture.GetReferencedAssemblies(),
            item => item.Name == "AgenticPrReview.Runtime");
        Assert.Contains(
            oracle.GetReferencedAssemblies(),
            item => item.Name == "AgenticPrReview.Runtime");
        Assert.DoesNotContain(
            oracle.GetReferencedAssemblies(),
            item => item.Name == "System.Net.Http");

        var captureStrings = Encoding.UTF8.GetString(File.ReadAllBytes(capture.Location));
        var oracleStrings = Encoding.UTF8.GetString(File.ReadAllBytes(oracle.Location));
        Assert.DoesNotContain("--current-state-key-file", captureStrings, StringComparison.Ordinal);
        Assert.DoesNotContain("--previous-state-key-file", captureStrings, StringComparison.Ordinal);
        Assert.DoesNotContain("--github-token-file", oracleStrings, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpClient", oracleStrings, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://evidence.blob.core.windows.net/container/file?sig=private", true)]
    [InlineData("https://a.b.blob.core.windows.net/container/file", true)]
    [InlineData("https://blob.core.windows.net/container/file", false)]
    [InlineData("http://evidence.blob.core.windows.net/container/file", false)]
    [InlineData("https://evidence.blob.core.windows.net.evil.example/file", false)]
    [InlineData("https://user@evidence.blob.core.windows.net/file", false)]
    [InlineData("https://evidence.blob.core.windows.net/file#fragment", false)]
    public void ArtifactRedirectPolicyIsExact(string value, bool expected)
    {
        Assert.Equal(expected, TrustedProofCaptureClient.ValidArtifactRedirect(new Uri(value)));
    }

    [Fact]
    public void AdmitsExactSingleEntryArtifactEnvelope()
    {
        var encrypted = Encoding.UTF8.GetBytes("synthetic-encrypted-object");
        var archive = CreateArchive(encrypted);
        var admitted = ArtifactArchiveAdmission.Admit(
            archive,
            CanonicalEvidence.Sha256(archive),
            "9001",
            "1");
        try
        {
            Assert.Equal(encrypted, admitted.EncryptedObject);
            Assert.Equal(CanonicalEvidence.Sha256(encrypted), admitted.EncryptedObjectSha256);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(admitted.EncryptedObject);
        }
    }

    [Theory]
    [InlineData("../artifact-envelope.json")]
    [InlineData("/artifact-envelope.json")]
    [InlineData("artifact\\envelope.json")]
    [InlineData("ARTIFACT-ENVELOPE.JSON")]
    public void RejectsUnsafeArchiveEntryName(string name)
    {
        var encrypted = Encoding.UTF8.GetBytes("synthetic-encrypted-object");
        var archive = CreateArchive(encrypted, name);
        Assert.Throws<InvalidDataException>(() => ArtifactArchiveAdmission.Admit(
            archive,
            CanonicalEvidence.Sha256(archive),
            "9001",
            "1"));
    }

    [Fact]
    public void RejectsExtraArchiveEntryAndDigestMismatch()
    {
        var encrypted = Encoding.UTF8.GetBytes("synthetic-encrypted-object");
        var archive = CreateArchive(encrypted, extraEntry: true);
        Assert.Throws<InvalidDataException>(() => ArtifactArchiveAdmission.Admit(
            archive,
            CanonicalEvidence.Sha256(archive),
            "9001",
            "1"));
        Assert.Throws<InvalidDataException>(() => ArtifactArchiveAdmission.Admit(
            CreateArchive(encrypted),
            new string('0', 64),
            "9001",
            "1"));
    }

    [Theory]
    [InlineData("artifact-envelope.json")]
    [InlineData("ARTIFACT-ENVELOPE.JSON")]
    public void RejectsDuplicateAndCaseCollidingArchiveEntries(string extraName)
    {
        var encrypted = Encoding.UTF8.GetBytes("synthetic-encrypted-object");
        var archive = CreateArchive(encrypted, extraEntry: true, extraEntryName: extraName);
        Assert.Throws<InvalidDataException>(() => ArtifactArchiveAdmission.Admit(
            archive,
            CanonicalEvidence.Sha256(archive),
            "9001",
            "1"));
    }

    [Theory]
    [InlineData((0xa000 | 0x1ff) << 16)]
    [InlineData((0x8000 | 0x040) << 16)]
    public void RejectsLinkAndExecutableArchiveEntries(int externalAttributes)
    {
        var encrypted = Encoding.UTF8.GetBytes("synthetic-encrypted-object");
        var archive = CreateArchive(encrypted, externalAttributes: externalAttributes);
        Assert.Throws<InvalidDataException>(() => ArtifactArchiveAdmission.Admit(
            archive,
            CanonicalEvidence.Sha256(archive),
            "9001",
            "1"));
    }

    [Fact]
    public void RejectsCompressionBombAndTruncatedArchive()
    {
        var compressed = CreateArchive(new byte[256 * 1024]);
        Assert.Throws<InvalidDataException>(() => ArtifactArchiveAdmission.Admit(
            compressed,
            CanonicalEvidence.Sha256(compressed),
            "9001",
            "1"));

        var exact = CreateArchive(Encoding.UTF8.GetBytes("synthetic-encrypted-object"));
        var truncated = exact[..^8];
        Assert.ThrowsAny<Exception>(() => ArtifactArchiveAdmission.Admit(
            truncated,
            CanonicalEvidence.Sha256(truncated),
            "9001",
            "1"));
    }

    [Fact]
    public void RestrictedRootRequiresMarkerAndCanonicalCredentialFiles()
    {
        var root = CreateRestrictedRoot();
        File.WriteAllText(Path.Join(root.Path, "token"), "synthetic-token", new UTF8Encoding(false));
        File.WriteAllText(
            Path.Join(root.Path, "current-key"),
            Convert.ToBase64String(new byte[32]),
            new UTF8Encoding(false));
        var token = root.ReadCredentialFile("token", base64Key: false);
        var key = root.ReadCredentialFile("current-key", base64Key: true);
        try
        {
            Assert.Equal("synthetic-token", Encoding.UTF8.GetString(token));
            Assert.Equal(32, key.Length);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(token);
            CryptographicOperations.ZeroMemory(key);
        }

        File.WriteAllText(Path.Join(root.Path, "bad-token"), "synthetic-token\n");
        Assert.Throws<InvalidDataException>(() =>
            root.ReadCredentialFile("bad-token", base64Key: false));
        Assert.Throws<InvalidDataException>(() =>
            root.ResolveExistingFile("../outside", EvidenceLimits.MaximumDocumentBytes));
        Assert.Throws<InvalidDataException>(() =>
            root.ResolveExistingFile(
                $"{Path.DirectorySeparatorChar}outside",
                EvidenceLimits.MaximumDocumentBytes));
    }

    [Fact]
    public void PackageWriterAdmitsBeforePersistenceAndFinalizesOnce()
    {
        var root = CreateRestrictedRoot();
        var writer = new CapturePackageWriter(root, "operation");
        var sourceBody = Encoding.UTF8.GetBytes("{}\n");
        writer.AddSource(
            "runs:page:1",
            new SafeResponseCapture(
                "/repos/SolusQuest/agentic-pr-review/actions/runs?per_page=100",
                1,
                200,
                CanonicalEvidence.Sha256(sourceBody),
                sourceBody.Length,
                new string('4', 64),
                1,
                2,
                null),
            sourceBody);
        var malformed = Encoding.UTF8.GetBytes("not-a-zip");
        Assert.ThrowsAny<Exception>(() => writer.AddArtifact(
            "1",
            "root",
            "repository-locator-root",
            "repository",
            "agentic-pr-review-state-root-v1",
            malformed,
            CanonicalEvidence.Sha256(malformed),
            "9001",
            "1",
            DownloadCapture(malformed)));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(Path.Join(root.Path, "operation")),
            path => Path.GetFileName(path).StartsWith("artifact-", StringComparison.Ordinal));

        var encrypted = Encoding.UTF8.GetBytes("synthetic-encrypted-object");
        var archive = CreateArchive(encrypted);
        writer.AddArtifact(
            "1",
            "root",
            "repository-locator-root",
            "repository",
            "agentic-pr-review-state-root-v1",
            archive,
            CanonicalEvidence.Sha256(archive),
            "9001",
            "1",
            DownloadCapture(archive));
        var finalized = writer.Finalize(
            "42",
            "SolusQuest/agentic-pr-review",
            [new string('6', 64), new string('8', 64)],
            new string('7', 64));
        Assert.True(File.Exists(finalized.Path));
        Assert.Equal(finalized.Sha256, CanonicalEvidence.Sha256(File.ReadAllBytes(finalized.Path)));
        Assert.Throws<InvalidDataException>(() => writer.Finalize(
            "42",
            "SolusQuest/agentic-pr-review",
            [new string('6', 64), new string('8', 64)],
            new string('7', 64)));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../escape")]
    [InlineData("nested/path")]
    [InlineData("nested\\path")]
    [InlineData("C:\\escape")]
    [InlineData("ambiguous.")]
    [InlineData(" padded")]
    [InlineData("padded ")]
    [InlineData("control\u0001character")]
    public void PackageWriterRejectsEscapingOrAmbiguousPackageNames(string packageName)
    {
        var root = CreateRestrictedRoot();

        Assert.Throws<InvalidDataException>(() =>
            new CapturePackageWriter(root, packageName));
    }

    [Fact]
    public void CredentialCopyRemovalIsBoundedToTheApprovedRoot()
    {
        var root = CreateRestrictedRoot();
        var path = Path.Join(root.Path, "operation-token");
        File.WriteAllText(path, "synthetic-token", new UTF8Encoding(false));

        root.RemoveCredentialFile("operation-token");

        Assert.False(File.Exists(path));
        Assert.Throws<InvalidDataException>(() => root.RemoveCredentialFile("../outside-token"));
    }

    [Fact]
    public async Task CollectorPaginatesAndKeepsCredentialAndSignedRedirectOutOfCaptures()
    {
        var encrypted = Encoding.UTF8.GetBytes("synthetic-encrypted-object");
        var archive = CreateArchive(encrypted);
        var apiResponses = new Queue<HttpResponseMessage>();
        var pageOne = JsonResponse("{\"page\":1}");
        pageOne.Headers.TryAddWithoutValidation(
            "Link",
            "<https://api.github.com/repos/SolusQuest/agentic-pr-review/actions/runs?per_page=100&page=2>; rel=\"next\"");
        apiResponses.Enqueue(pageOne);
        apiResponses.Enqueue(JsonResponse("{\"page\":2}"));
        using var redirectResponse = new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("https://evidence.blob.core.windows.net/container/file?sig=private") },
        };
        apiResponses.Enqueue(redirectResponse);
        var apiHandler = new RecordingHandler(request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("synthetic-token", request.Headers.Authorization?.Parameter);
            Assert.Equal(TrustedProofCaptureClient.ApiVersion, request.Headers.GetValues("X-GitHub-Api-Version").Single());
            return apiResponses.Dequeue();
        });
        var artifactHandler = new RecordingHandler(request =>
        {
            Assert.Null(request.Headers.Authorization);
            Assert.Contains("sig=private", request.RequestUri!.Query, StringComparison.Ordinal);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(archive),
            };
        });
        var token = Encoding.UTF8.GetBytes("synthetic-token");
        using var client = new TrustedProofCaptureClient(token, apiHandler, artifactHandler);

        var pages = await client.GetPaginatedAsync(
            "/repos/SolusQuest/agentic-pr-review/actions/runs?per_page=100",
            CancellationToken.None);
        var artifact = await client.DownloadArtifactAsync(
            "/repos/SolusQuest/agentic-pr-review/actions/artifacts/1001/zip",
            CancellationToken.None);
        try
        {
            Assert.Equal(2, pages.Captures.Length);
            Assert.Equal(2, pages.Bodies.Length);
            Assert.Equal(
                "/repos/SolusQuest/agentic-pr-review/actions/runs?per_page=100&page=2",
                pages.Captures[0].NextRoute);
            Assert.Equal(archive, artifact.Archive);
            var retained = JsonSerializer.Serialize(new { pages.Captures, artifact.Capture });
            Assert.DoesNotContain("synthetic-token", retained, StringComparison.Ordinal);
            Assert.DoesNotContain("sig=private", retained, StringComparison.Ordinal);
            Assert.DoesNotContain("blob.core.windows.net", retained, StringComparison.Ordinal);
        }
        finally
        {
            foreach (var body in pages.Bodies)
            {
                CryptographicOperations.ZeroMemory(body);
            }
            CryptographicOperations.ZeroMemory(artifact.Archive);
            CryptographicOperations.ZeroMemory(token);
        }
    }

    public void Dispose()
    {
        foreach (var root in roots.Where(root =>
            Directory.Exists(root) &&
            RestrictedEvidenceRoot.IsWithin(root, Directory.GetCurrentDirectory())))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private RestrictedEvidenceRoot CreateRestrictedRoot()
    {
        var path = Path.Join(
            Directory.GetCurrentDirectory(),
            $".apr-r4-e3-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        roots.Add(path);
        var identity = new string('5', 64);
        var marker = new RestrictedRootMarker(
            RestrictedEvidenceRoot.MarkerKind,
            identity);
        File.WriteAllBytes(
            Path.Join(path, RestrictedEvidenceRoot.MarkerName),
            CanonicalEvidence.Encode(marker, EvidenceJson.Options));
        return RestrictedEvidenceRoot.Open(path, identity, []);
    }

    private static byte[] CreateArchive(
        byte[] encrypted,
        string entryName = ArtifactArchiveAdmission.EntryName,
        bool extraEntry = false,
        string extraEntryName = "extra",
        int? externalAttributes = null)
    {
        var document = new Dictionary<string, string>
        {
            ["discriminator"] = ArtifactArchiveAdmission.Discriminator,
            ["producing_run_id"] = "9001",
            ["producing_run_attempt"] = "1",
            ["encrypted_object_digest"] = CanonicalEvidence.Sha256(encrypted),
            ["encrypted_object_size"] = encrypted.Length.ToString(),
            ["encrypted_object_base64"] = Convert.ToBase64String(encrypted),
        };
        var envelope = JsonSerializer.SerializeToUtf8Bytes(document);
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            if (externalAttributes is not null)
            {
                entry.ExternalAttributes = externalAttributes.Value;
            }
            using (var stream = entry.Open())
            {
                stream.Write(envelope);
            }

            if (extraEntry)
            {
                var extra = archive.CreateEntry(extraEntryName);
                using var stream = extra.Open();
                stream.WriteByte(1);
            }
        }

        return memory.ToArray();
    }

    private static HttpResponseMessage JsonResponse(string value)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(value)),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return response;
    }

    private static SafeResponseCapture DownloadCapture(byte[] archive) =>
        new(
            "/repos/SolusQuest/agentic-pr-review/actions/artifacts/1001/zip",
            1,
            200,
            CanonicalEvidence.Sha256(archive),
            archive.Length,
            new string('9', 64),
            1,
            2,
            null);

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) :
        HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}

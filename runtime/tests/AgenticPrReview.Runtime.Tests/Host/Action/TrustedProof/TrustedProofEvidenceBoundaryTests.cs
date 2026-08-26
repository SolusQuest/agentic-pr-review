using System.IO.Compression;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHostTrustedProofCapture;
using AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceContracts;
using AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceAssembler;
using AgenticPrReview.Runtime.ActionHostTrustedProofOracleBuild;
using AgenticPrReview.Runtime.Host.State.GitHubArtifacts;
using EvidenceAssemblerProgram = AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceAssembler.Program;
using OracleBuildProgram = AgenticPrReview.Runtime.ActionHostTrustedProofOracleBuild.Program;

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
    public void CaptureOracleAndAssemblerHaveDisjointProtectedCapabilities()
    {
        var capture = typeof(TrustedProofCaptureClient).Assembly;
        var oracle = Assembly.Load("AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceOracle");
        var oracleBuild = Assembly.Load("AgenticPrReview.Runtime.ActionHostTrustedProofOracleBuild");
        var assembler = Assembly.Load("AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceAssembler");
        Assert.DoesNotContain(
            capture.GetReferencedAssemblies(),
            item => item.Name == "AgenticPrReview.Runtime");
        Assert.Contains(
            oracle.GetReferencedAssemblies(),
            item => item.Name == "AgenticPrReview.Runtime");
        Assert.DoesNotContain(
            oracle.GetReferencedAssemblies(),
            item => item.Name == "System.Net.Http");
        Assert.DoesNotContain(
            assembler.GetReferencedAssemblies(),
            item => item.Name == "AgenticPrReview.Runtime");
        Assert.DoesNotContain(
            assembler.GetReferencedAssemblies(),
            item => item.Name == "System.Net.Http");

        var captureStrings = Encoding.UTF8.GetString(File.ReadAllBytes(capture.Location));
        var oracleStrings = Encoding.UTF8.GetString(File.ReadAllBytes(oracle.Location));
        Assert.DoesNotContain("--current-state-key-file", captureStrings, StringComparison.Ordinal);
        Assert.DoesNotContain("--previous-state-key-file", captureStrings, StringComparison.Ordinal);
        Assert.DoesNotContain("--github-token-file", oracleStrings, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpClient", oracleStrings, StringComparison.Ordinal);
        Assert.DoesNotContain(
            oracle.GetCustomAttributes<AssemblyMetadataAttribute>(),
            item => item.Key == "TrustedProofOracleBuildReceiptArgument");
        Assert.Equal(
            "--source-root",
            oracleBuild.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(item => item.Key == "TrustedProofOracleBuildSourceRootArgument").Value);
        Assert.Equal(
            "HEAD^{tree}",
            oracleBuild.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(item => item.Key == "TrustedProofOracleBuildSourceTreeCommand").Value);
        Assert.Equal(
            "scripts/assemble-r4-trusted-proof-evidence.mjs",
            assembler.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(item => item.Key == "TrustedProofAssemblerNodeEntryPoint").Value);
        Assert.Equal(
            "3333333333333333333333333333333333333333",
            oracle.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(item => item.Key == "TrustedProofOracleSourceSha").Value);
        Assert.Equal(
            "4444444444444444444444444444444444444444",
            oracle.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(item => item.Key == "TrustedProofOracleSourceTree").Value);
    }

    [Fact]
    public void OracleBuildSnapshotUsesOnlyAuthorizedGitBlobsAndStaysStable()
    {
        var repository = CreateGitRepository();
        var restricted = CreateRestrictedRoot();
        var git = FindExecutable("git");
        var commit = RunProcess(git, repository, "rev-parse", "HEAD");
        var tree = RunProcess(git, repository, "rev-parse", "HEAD^{tree}");
        File.WriteAllText(Path.Join(repository, ".git", "info", "exclude"), "Directory.Build.targets\n");
        File.WriteAllText(
            Path.Join(repository, "Directory.Build.targets"),
            "<Project><Target Name=\"Injected\" BeforeTargets=\"Build\" /></Project>");

        var destination = Path.Join(restricted.Path, "authorized-source");
        using var snapshot = AuthorizedGitSnapshot.Materialize(
            git,
            repository,
            commit,
            tree,
            destination);

        Assert.Equal("authorized\n", File.ReadAllText(Path.Join(snapshot.Root, "source.txt")));
        Assert.False(File.Exists(Path.Join(snapshot.Root, "Directory.Build.targets")));
        File.WriteAllText(Path.Join(repository, "source.txt"), "replaced-during-build\n");
        Assert.ThrowsAny<Exception>(() => File.WriteAllText(
            Path.Join(snapshot.Root, "source.txt"),
            "snapshot-injection\n"));
        Assert.ThrowsAny<Exception>(() => File.WriteAllText(
            Path.Join(snapshot.Root, "injected.cs"),
            "namespace Injected;"));
        snapshot.Validate();
        Assert.Equal("authorized\n", File.ReadAllText(Path.Join(snapshot.Root, "source.txt")));
    }

    [Fact]
    public void OracleBuildSnapshotRejectsPrepopulatedDestination()
    {
        var repository = CreateGitRepository();
        var restricted = CreateRestrictedRoot();
        var git = FindExecutable("git");
        var commit = RunProcess(git, repository, "rev-parse", "HEAD");
        var tree = RunProcess(git, repository, "rev-parse", "HEAD^{tree}");
        var destination = Path.Join(restricted.Path, "stale-source");
        Directory.CreateDirectory(destination);

        Assert.Throws<InvalidDataException>(() => AuthorizedGitSnapshot.Materialize(
            git,
            repository,
            commit,
            tree,
            destination));
    }

    [Fact]
    public void OracleBuildRejectsPrepopulatedIntermediateAndOutputDirectories()
    {
        var root = CreatePlainRoot("oracle-build-fresh");
        var staleIntermediate = Path.Join(root, "intermediate");
        var staleOutput = Path.Join(root, "output");
        Directory.CreateDirectory(staleIntermediate);
        File.WriteAllText(staleOutput, "stale-output");

        Assert.Throws<InvalidDataException>(() =>
            OracleBuildProgram.CreateFreshBuildDirectory(staleIntermediate));
        Assert.Throws<InvalidDataException>(() =>
            OracleBuildProgram.CreateFreshBuildDirectory(staleOutput));
    }

    [Theory]
    [InlineData("Directory.Build.targets:payload")]
    [InlineData("CON/source.cs")]
    [InlineData("nested/trailing. ")]
    [InlineData("nested/../escape.cs")]
    public void OracleBuildRejectsPlatformAliasingGitPaths(string relative)
    {
        Assert.False(AuthorizedGitSnapshot.IsSafeRelativePath(relative));
    }

    [Fact]
    public void PublicCorpusRejectsPostScanAdditionAndReplacement()
    {
        var repository = CreatePlainRoot("corpus-repository");
        var worktree = CreatePlainRoot("corpus-worktree");
        var logs = CreatePlainRoot("corpus-logs");
        File.WriteAllText(Path.Join(repository, "tracked.txt"), "safe-repository");
        File.WriteAllText(Path.Join(worktree, "public.txt"), "safe-worktree");
        File.WriteAllText(Path.Join(logs, "run.log"), "safe-log");
        using var corpus = PublicSurfaceCorpusLease.Open(repository, worktree, logs);

        File.WriteAllText(Path.Join(worktree, "late.txt"), "late-protected-value");
        Assert.Throws<InvalidDataException>(() => corpus.ValidateComplete(null, []));

        File.Delete(Path.Join(worktree, "late.txt"));
        try
        {
            File.WriteAllText(Path.Join(logs, "run.log"), "replaced-log");
        }
        catch (IOException) when (OperatingSystem.IsWindows())
        {
            return;
        }
        Assert.Throws<InvalidDataException>(() => corpus.ValidateComplete(null, []));
    }

    [Fact]
    public void PublicCorpusScansEveryCategoryAndProtectedSubstring()
    {
        var repository = CreatePlainRoot("scan-repository");
        var logs = CreatePlainRoot("scan-logs");
        var canary = Encoding.UTF8.GetBytes("APR222-PROTECTED-CANARY-WITH-PARTIAL-WINDOW");
        File.WriteAllText(Path.Join(repository, "safe.txt"), "safe");
        File.WriteAllText(Path.Join(logs, "run.log"), Encoding.UTF8.GetString(canary.AsSpan(8, 16)));
        using var corpus = PublicSurfaceCorpusLease.Open(repository, repository, logs);
        var categories = new Dictionary<string, IReadOnlyList<byte[]>>(StringComparer.Ordinal)
        {
            ["authorization"] = [canary],
            ["state_keys"] = [Encoding.UTF8.GetBytes("APR222-STATE-KEY-CANARY-00000001")],
            ["session_plaintext"] = [Encoding.UTF8.GetBytes("APR222-SESSION-CANARY-0000000001")],
            ["provider_content"] = [Encoding.UTF8.GetBytes("APR222-PROVIDER-CANARY-000000001")],
            ["tool_data"] = [Encoding.UTF8.GetBytes("APR222-TOOL-CANARY-0000000000001")],
            ["host_evidence"] = [Encoding.UTF8.GetBytes("APR222-HOST-CANARY-0000000000001")],
        };

        Assert.Throws<InvalidDataException>(() => corpus.AssertAbsent(categories, []));
    }

    [Fact]
    public void PublicCorpusAllowsOnlyTheBoundFinalOutputAddition()
    {
        var repository = CreatePlainRoot("final-repository");
        var worktree = CreatePlainRoot("final-worktree");
        var logs = CreatePlainRoot("final-logs");
        File.WriteAllText(Path.Join(repository, "source.txt"), "safe");
        using var corpus = PublicSurfaceCorpusLease.Open(repository, worktree, logs);
        var output = CanonicalEvidence.Encode(new { kind = "public" }, EvidenceJson.Options);
        var outputPath = Path.Join(worktree, "evidence.json");
        EvidenceAssemblerProgram.WritePublicCreateNew(outputPath, output);
        Assert.Throws<IOException>(() => EvidenceAssemblerProgram.WritePublicCreateNew(outputPath, output));

        corpus.ValidateComplete(outputPath, output);
        File.WriteAllText(outputPath, "replaced");
        Assert.Throws<InvalidDataException>(() => corpus.ValidateComplete(outputPath, output));
    }

    [Fact]
    public void FailedPublicOutputCleanupRemovesOnlyAnExpectedPrefix()
    {
        var root = CreatePlainRoot("failed-public-output");
        var expected = Encoding.UTF8.GetBytes("expected-complete-output");
        var partialPath = Path.Join(root, "partial.json");
        File.WriteAllBytes(partialPath, expected[..8]);
        EvidenceAssemblerProgram.DeleteFailedPublicOutput(partialPath, expected);
        Assert.False(File.Exists(partialPath));

        var unrelatedPath = Path.Join(root, "unrelated.json");
        File.WriteAllText(unrelatedPath, "attacker-owned");
        EvidenceAssemblerProgram.DeleteFailedPublicOutput(unrelatedPath, expected);
        Assert.True(File.Exists(unrelatedPath));
    }

    [Fact]
    public void ProtectedScanInputIsCanonicalBoundedAndNeverPersisted()
    {
        static string Value(char value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(new string(value, 32)));
        var input = CanonicalEvidence.Encode(
            new
            {
                kind = "apr-r4-e3-public-scan-memory-input-v1",
                categories = new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["authorization"] = [Value('a')],
                    ["state_keys"] = [Value('b'), Value('c')],
                    ["session_plaintext"] = [Value('d')],
                    ["provider_content"] = [Value('e')],
                    ["tool_data"] = [Value('f')],
                },
            },
            EvidenceJson.Options);
        using var stream = new MemoryStream(input);
        var values = EvidenceAssemblerProgram.ReadProtectedScanInput(stream);
        try
        {
            Assert.Equal(5, values.Count);
            Assert.All(values.Values, category => Assert.All(category, value => Assert.Equal(32, value.Length)));
        }
        finally
        {
            foreach (var category in values.Values)
            {
                foreach (var value in category)
                {
                    CryptographicOperations.ZeroMemory(value);
                }
            }
            CryptographicOperations.ZeroMemory(input);
        }
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
        WriteRestrictedText(Path.Join(root.Path, "token"), "synthetic-token");
        WriteRestrictedText(
            Path.Join(root.Path, "current-key"),
            Convert.ToBase64String(new byte[32]));
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

        WriteRestrictedText(Path.Join(root.Path, "bad-token"), "synthetic-token\n");
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
    public void RestrictedRootRejectsEitherDirectionOfProhibitedOverlap()
    {
        var root = CreateRestrictedRoot();
        var identity = root.DestinationIdentitySha256;

        Assert.Throws<InvalidDataException>(() => RestrictedEvidenceRoot.Open(
            root.Path,
            identity,
            [Directory.GetParent(root.Path)!.FullName]));
        Assert.Throws<InvalidDataException>(() => RestrictedEvidenceRoot.Open(
            root.Path,
            identity,
            [Path.Join(root.Path, "nested-prohibited-root")]));
    }

    [Fact]
    public void RestrictedRootRejectsHardLinkedEvidenceFiles()
    {
        var root = CreateRestrictedRoot();
        var original = Path.Join(root.Path, "hard-linked-source");
        var alias = Path.Join(root.Path, "hard-linked-alias");
        WriteRestrictedText(original, "synthetic-source");
        HardLinkTestPlatform.Create(alias, original);

        Assert.Throws<InvalidDataException>(() =>
            root.ReadPinnedFile("hard-linked-source", EvidenceLimits.MaximumDocumentBytes));
    }

    [Fact]
    public void RestrictedRootWritesCreateNewAndReopensTheSameBytes()
    {
        var root = CreateRestrictedRoot();
        var bytes = Encoding.UTF8.GetBytes("{\"kind\":\"synthetic\"}\n");
        var identity = root.WritePinnedFileCreateNew("assembled.json", bytes);
        var readback = root.ReadPinnedFile("assembled.json", EvidenceLimits.MaximumDocumentBytes);
        try
        {
            Assert.Equal(bytes, readback.Bytes);
            Assert.Equal(identity, readback.Identity);
            Assert.Throws<InvalidDataException>(() =>
                root.WritePinnedFileCreateNew("assembled.json", bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(readback.Bytes);
        }
    }

    [Fact]
    public void PackageFinalizationRejectsSameBytesAtAReplacementIdentity()
    {
        var root = CreateRestrictedRoot();
        var writer = new CapturePackageWriter(root, "identity-replacement");
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
        var encrypted = Encoding.UTF8.GetBytes("synthetic-encrypted-object");
        var archive = CreateArchive(encrypted);
        writer.AddArtifact(
            "1",
            "root",
            "runs:page:1",
            CanonicalEvidence.Sha256(sourceBody),
            archive,
            CanonicalEvidence.Sha256(archive),
            "9001",
            "1",
            DownloadCapture(archive));
        var sourcePath = Path.Join(root.Path, "identity-replacement", "source-0001.json");
        var displacedPath = Path.Join(root.Path, "identity-replacement", "source-0001.displaced");
        File.Move(sourcePath, displacedPath);
        CanonicalEvidence.WriteCreateNew(sourcePath, sourceBody);

        Assert.Throws<InvalidDataException>(() => writer.Finalize(
            "42",
            "SolusQuest/agentic-pr-review",
            [new string('6', 64), new string('8', 64)],
            OperationRuns(),
            new string('7', 64)));
    }

    [Fact]
    public void WindowsRestrictedRootRejectsBroadMutationAcl()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateRestrictedRoot();
        var directory = new DirectoryInfo(root.Path);
        var security = directory.GetAccessControl(AccessControlSections.Access);
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        directory.SetAccessControl(security);

        Assert.Throws<InvalidDataException>(() => RestrictedEvidenceRoot.Open(
            root.Path,
            root.DestinationIdentitySha256,
            []));
    }

    [Fact]
    public void WindowsRestrictedRootRejectsBroadReadAclOnPinnedFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateRestrictedRoot();
        var path = Path.Join(root.Path, "broad-read");
        WriteRestrictedText(path, "synthetic-source");
        var file = new FileInfo(path);
        var security = file.GetAccessControl(AccessControlSections.Access);
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.ReadData,
            AccessControlType.Allow));
        file.SetAccessControl(security);

        Assert.Throws<InvalidDataException>(() =>
            root.ReadPinnedFile("broad-read", EvidenceLimits.MaximumDocumentBytes));
    }

    [Fact]
    public void WindowsPinnedLeaseDeniesReplacementForTheAssemblyWindow()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateRestrictedRoot();
        var path = Path.Join(root.Path, "leased-source");
        WriteRestrictedText(path, "synthetic-source");
        using var lease = root.AcquirePinnedFile(
            "leased-source",
            EvidenceLimits.MaximumDocumentBytes);

        Assert.Throws<IOException>(() =>
            File.Move(path, Path.Join(root.Path, "replacement-target")));
        lease.Validate();
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
            "runs",
            CanonicalEvidence.Sha256(sourceBody),
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
            "runs",
            CanonicalEvidence.Sha256(sourceBody),
            archive,
            CanonicalEvidence.Sha256(archive),
            "9001",
            "1",
            DownloadCapture(archive));
        var finalized = writer.Finalize(
            "42",
            "SolusQuest/agentic-pr-review",
            [new string('6', 64), new string('8', 64)],
            OperationRuns(),
            new string('7', 64));
        Assert.True(File.Exists(finalized.Path));
        Assert.Equal(finalized.Sha256, CanonicalEvidence.Sha256(File.ReadAllBytes(finalized.Path)));
        Assert.Throws<InvalidDataException>(() => writer.Finalize(
            "42",
            "SolusQuest/agentic-pr-review",
            [new string('6', 64), new string('8', 64)],
            OperationRuns(),
            new string('7', 64)));
    }

    [Fact]
    public void PackageWriterCreatesItsPackageDirectoryExclusively()
    {
        var root = CreateRestrictedRoot();
        _ = new CapturePackageWriter(root, "exclusive-operation");

        Assert.Throws<InvalidDataException>(() =>
            new CapturePackageWriter(root, "exclusive-operation"));
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
        WriteRestrictedText(path, "synthetic-token");

        root.RemoveCredentialFile("operation-token");

        Assert.False(File.Exists(path));
        Assert.Throws<InvalidDataException>(() => root.RemoveCredentialFile("../outside-token"));
    }

    [Fact]
    public async Task CollectorRejectsPaginationOutsideTheOriginalEndpointFamily()
    {
        var page = JsonResponse("{\"page\":1}");
        page.Headers.TryAddWithoutValidation(
            "Link",
            "<https://api.github.com/repos/SolusQuest/agentic-pr-review/issues?per_page=100&page=2>; rel=\"next\"");
        var calls = 0;
        var apiHandler = new RecordingHandler(_ =>
        {
            calls++;
            return page;
        });
        var token = Encoding.UTF8.GetBytes("synthetic-token");
        using var client = new TrustedProofCaptureClient(
            token,
            apiHandler,
            new RecordingHandler(_ => throw new InvalidOperationException()));

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetPaginatedAsync(
            "/repos/SolusQuest/agentic-pr-review/actions/runs?per_page=100",
            CancellationToken.None));
        Assert.Equal(1, calls);
        CryptographicOperations.ZeroMemory(token);
    }

    [Theory]
    [InlineData("not-a-link")]
    [InlineData("<https://api.github.com/repos/SolusQuest/agentic-pr-review/actions/runs?page=2>; rel=\"next\"; type=\"application/json\"")]
    [InlineData("<https://api.github.com/repos/SolusQuest/agentic-pr-review/actions/runs?page=2>; rel=next")]
    public async Task CollectorRejectsMalformedOrExtendedPaginationLinks(string link)
    {
        var page = JsonResponse("{\"page\":1}");
        page.Headers.TryAddWithoutValidation("Link", link);
        var token = Encoding.UTF8.GetBytes("synthetic-token");
        using var client = new TrustedProofCaptureClient(
            token,
            new RecordingHandler(_ => page),
            new RecordingHandler(_ => throw new InvalidOperationException()));

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetPaginatedAsync(
            "/repos/SolusQuest/agentic-pr-review/actions/runs?per_page=100",
            CancellationToken.None));
        CryptographicOperations.ZeroMemory(token);
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
            if (OperatingSystem.IsWindows())
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(directory, FileAttributes.Directory);
                }
            }
            else
            {
                foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
                {
                    File.SetUnixFileMode(
                        directory,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
                File.SetUnixFileMode(
                    root,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            Directory.Delete(root, recursive: true);
        }
    }

    private RestrictedEvidenceRoot CreateRestrictedRoot()
    {
        var path = Path.Join(
            Directory.GetCurrentDirectory(),
            $".apr-r4-e3-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsWindows())
        {
            var current = WindowsIdentity.GetCurrent().User!;
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                current,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        roots.Add(path);
        var identity = new string('5', 64);
        var marker = new RestrictedRootMarker(
            RestrictedEvidenceRoot.MarkerKind,
            identity);
        File.WriteAllBytes(
            Path.Join(path, RestrictedEvidenceRoot.MarkerName),
            CanonicalEvidence.Encode(marker, EvidenceJson.Options));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                Path.Join(path, RestrictedEvidenceRoot.MarkerName),
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        return RestrictedEvidenceRoot.Open(path, identity, []);
    }

    private string CreateGitRepository()
    {
        var path = Path.Join(
            Directory.GetCurrentDirectory(),
            $".apr-r4-e3-git-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        roots.Add(path);
        var git = FindExecutable("git");
        _ = RunProcess(git, path, "init", "--quiet");
        File.WriteAllText(Path.Join(path, "source.txt"), "authorized\n", new UTF8Encoding(false));
        _ = RunProcess(git, path, "add", "source.txt");
        _ = RunProcess(
            git,
            path,
            "-c",
            "user.name=APR Test",
            "-c",
            "user.email=apr-test@example.invalid",
            "commit",
            "--quiet",
            "-m",
            "authorized source");
        return path;
    }

    private string CreatePlainRoot(string label)
    {
        var path = Path.Join(
            Directory.GetCurrentDirectory(),
            $".apr-r4-e3-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        roots.Add(path);
        return path;
    }

    private static string FindExecutable(string name)
    {
        var locator = OperatingSystem.IsWindows() ? "where.exe" : "which";
        return RunProcess(locator, Directory.GetCurrentDirectory(), name)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0];
    }

    private static string RunProcess(
        string executable,
        string workingDirectory,
        params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("test_process_start_failed");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"test_process_failed:{error}");
        }
        return output.TrimEnd('\r', '\n');
    }

    private static void WriteRestrictedText(string path, string value)
    {
        File.WriteAllText(path, value, new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
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

    private static CaptureManifestOperationRun[] OperationRuns() =>
    [
        new(new string('6', 64), "normal", "9001", "1"),
        new(new string('6', 64), "normal", "9002", "1"),
        new(new string('8', 64), "stale", "9003", "1"),
        new(new string('8', 64), "stale", "9004", "1"),
    ];

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

internal static partial class HardLinkTestPlatform
{
    internal static void Create(string linkPath, string existingPath)
    {
        var created = OperatingSystem.IsWindows()
            ? CreateHardLink(linkPath, existingPath, 0)
            : Link(existingPath, linkPath) == 0;
        if (!created)
        {
            throw new IOException($"hard_link_test_setup_failed:{Marshal.GetLastPInvokeError()}");
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLink(
        string fileName,
        string existingFileName,
        nint securityAttributes);

    [LibraryImport("libc", EntryPoint = "link", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Link(string existingPath, string linkPath);
}

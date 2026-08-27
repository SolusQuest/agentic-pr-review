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
using System.Text.Json.Nodes;
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
        Assert.Equal(
            EvidenceLimits.LogicalOperationTimeout + EvidenceLimits.RequestTimeout,
            EvidenceLimits.CaptureCredentialHandoffTimeout);
        Assert.Equal(EvidenceLimits.RequestTimeout, EvidenceLimits.OracleCredentialHandoffTimeout);
        Assert.Equal(EvidenceLimits.RequestTimeout, EvidenceLimits.CredentialConnectedSessionTimeout);
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
    public void AssemblerDeletesCredentialsBeforeCorpusAndStdinWork()
    {
        var source = File.ReadAllText(Path.Join(
            FindRepositoryRoot(),
            "runtime",
            "tests",
            "ActionHostTrustedProofEvidenceAssembler",
            "Program.cs"));
        var acquire = source.IndexOf(
            "AcquireAndDeleteLeasedCredentials(",
            StringComparison.Ordinal);
        var corpus = source.IndexOf(
            "PublicSurfaceCorpusLease.Open(",
            StringComparison.Ordinal);
        var stdin = source.IndexOf(
            "ReadProtectedScanInput(",
            StringComparison.Ordinal);
        var append = source.IndexOf(
            "AppendLeasedCredentialRepresentations(",
            StringComparison.Ordinal);

        Assert.True(acquire >= 0);
        Assert.True(acquire < corpus);
        Assert.True(corpus < stdin);
        Assert.True(stdin < append);
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
    public void PublicCorpusScansEveryCategoryForExactOperationBoundValues()
    {
        var repository = CreatePlainRoot("scan-repository");
        var logs = CreatePlainRoot("scan-logs");
        var canary = Encoding.UTF8.GetBytes("APR222-PROTECTED-CANARY-WITH-PARTIAL-WINDOW");
        File.WriteAllText(Path.Join(repository, "safe.txt"), "safe");
        File.WriteAllText(Path.Join(logs, "run.log"), Encoding.UTF8.GetString(canary));
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
        using var receipt = EvidenceAssemblerProgram.WritePublicCreateNew(outputPath, output);
        Assert.Throws<InvalidDataException>(() =>
            EvidenceAssemblerProgram.WritePublicCreateNew(outputPath, output));

        corpus.ValidateComplete(outputPath, output);
        receipt.Dispose();
        File.WriteAllText(outputPath, "replaced");
        Assert.Throws<InvalidDataException>(() => corpus.ValidateComplete(outputPath, output));
    }

    [Fact]
    public void FailedPublicOutputCleanupRequiresTheCreatedPhysicalIdentity()
    {
        var root = CreatePlainRoot("failed-public-output");
        var expected = Encoding.UTF8.GetBytes("expected-complete-output");
        var partialPath = Path.Join(root, "partial.json");
        Assert.Throws<IOException>(() =>
            EvidenceAssemblerProgram.WritePublicCreateNew(partialPath, expected, failAfterBytesForTest: 8));
        Assert.False(File.Exists(partialPath));

        var competingPath = Path.Join(root, "competing.json");
        File.WriteAllBytes(competingPath, expected[..8]);
        Assert.Throws<InvalidDataException>(() =>
            EvidenceAssemblerProgram.WritePublicCreateNew(competingPath, expected));
        Assert.Equal(expected[..8], File.ReadAllBytes(competingPath));

        var replacePath = Path.Join(root, "replace.json");
        using var receipt = EvidenceAssemblerProgram.WritePublicCreateNew(replacePath, expected);
        File.Delete(replacePath);
        File.WriteAllBytes(replacePath, expected[..8]);
        receipt.DeleteIfOwned();
        Assert.Equal(expected[..8], File.ReadAllBytes(replacePath));
    }

    [Fact]
    public void AtomicPublicationNeverDeletesAReplacementAfterStagingValidation()
    {
        var root = CreatePlainRoot("staging-replacement");
        var outputPath = Path.Join(root, "public.json");
        var expected = Encoding.UTF8.GetBytes("expected-complete-output");
        var competing = Encoding.UTF8.GetBytes("competitor-owned-staging-entry");

        Assert.Throws<InvalidDataException>(() => EvidenceAssemblerProgram.WritePublicCreateNew(
            outputPath,
            expected,
            beforePublishForTest: stagingPath =>
            {
                File.Move(stagingPath, Path.Join(root, "displaced-created-identity"));
                File.WriteAllBytes(stagingPath, competing);
            }));

        Assert.False(File.Exists(outputPath));
        Assert.Contains(
            Directory.EnumerateFiles(root, "candidate", SearchOption.AllDirectories),
            path => File.ReadAllBytes(path).SequenceEqual(competing));
    }

    [Fact]
    public void AtomicPublicationRejectsSameByteReplacementBeforeSuccess()
    {
        var root = CreatePlainRoot("same-byte-final-replacement");
        var outputPath = Path.Join(root, "public.json");
        var displacedPath = Path.Join(root, "displaced-created-identity");
        var expected = Encoding.UTF8.GetBytes("expected-complete-output");

        Assert.Throws<InvalidDataException>(() => EvidenceAssemblerProgram.WritePublicCreateNew(
            outputPath,
            expected,
            afterPublishForTest: publishedPath =>
            {
                File.Move(publishedPath, displacedPath);
                File.WriteAllBytes(publishedPath, expected);
            }));

        Assert.Equal(expected, File.ReadAllBytes(outputPath));
        Assert.Equal(expected, File.ReadAllBytes(displacedPath));
    }

    [Fact]
    public void AtomicPublicationRejectsHardLinkReplacementBeforeSuccess()
    {
        var root = CreatePlainRoot("hard-link-final-replacement");
        var outputPath = Path.Join(root, "public.json");
        var displacedPath = Path.Join(root, "displaced-created-identity");
        var competitorPath = Path.Join(root, "competitor");
        var expected = Encoding.UTF8.GetBytes("expected-complete-output");
        File.WriteAllBytes(competitorPath, expected);

        Assert.Throws<InvalidDataException>(() => EvidenceAssemblerProgram.WritePublicCreateNew(
            outputPath,
            expected,
            afterPublishForTest: publishedPath =>
            {
                File.Move(publishedPath, displacedPath);
                HardLinkTestPlatform.Create(publishedPath, competitorPath);
            }));

        Assert.Equal(expected, File.ReadAllBytes(outputPath));
        Assert.Equal(2, new FileInfo(competitorPath).LinkTarget is null
            ? Directory.EnumerateFiles(root).Count(path =>
                File.ReadAllBytes(path).SequenceEqual(expected) &&
                !StringComparer.Ordinal.Equals(path, displacedPath))
            : 0);
    }

    [Fact]
    public void ProtectedScanInputIsCanonicalBoundedAndNeverPersisted()
    {
        const string repository = "SolusQuest/agentic-pr-review";
        var operations = new[] { new string('1', 64), new string('2', 64) };
        var operation = operations[0];
        static string Value(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        byte[] Encode(string toolData) => CanonicalEvidence.Encode(
            new
            {
                kind = "apr-r4-e3-public-scan-memory-input-v2",
                repository,
                operation_ids = operations,
                categories = new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["authorization"] =
                    [
                        Value($"APR_R4_E4_AUTHORIZATION_{operation}"),
                        Value($"Bearer APR_R4_E4_AUTHORIZATION_{operation}"),
                    ],
                    ["state_keys"] =
                    [
                        Value($"APR_R4_E4_STATE_KEY_{operation}"),
                        Value(Convert.ToBase64String(Encoding.UTF8.GetBytes(
                            $"APR_R4_E4_STATE_KEY_{operation}"))),
                    ],
                    ["session_plaintext"] = [Value($"APR_R4_E4_SESSION_PLAINTEXT_{operation}")],
                    ["provider_content"] = [Value($"APR_R4_E4_PROVIDER_CONTENT_{operation}")],
                    ["tool_data"] = [Value(toolData)],
                    ["host_evidence"] = [Value($"APR_R4_E4_HOST_EVIDENCE_{operation}")],
                },
            },
            EvidenceJson.Options);
        var input = Encode($"APR_R4_E4_TOOL_DATA_{operation}");
        using var stream = new MemoryStream(input);
        var values = EvidenceAssemblerProgram.ReadProtectedScanInput(
            stream,
            expectedDigestForTest: CanonicalEvidence.Sha256(input),
            expectedRepositoryForTest: repository,
            expectedOperationsForTest: operations);
        try
        {
            Assert.Equal(6, values.Count);
            var substituted = Encode($"APR_R4_E4_TOOL_DATA_SUBSTITUTED_{operation}");
            try
            {
                using var substitutedStream = new MemoryStream(substituted);
                Assert.Throws<InvalidDataException>(() => EvidenceAssemblerProgram.ReadProtectedScanInput(
                    substitutedStream,
                    expectedDigestForTest: CanonicalEvidence.Sha256(substituted),
                    expectedRepositoryForTest: repository,
                    expectedOperationsForTest: operations));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(substituted);
            }
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
    [InlineData("authorization-raw")]
    [InlineData("authorization-bearer")]
    [InlineData("state-key-raw")]
    [InlineData("state-key-base64")]
    [InlineData("substituted-state-key-raw")]
    public void ActualCredentialRepresentationsAreScannedBeforeCopiesAreRemoved(string leakKind)
    {
        var restricted = CreateRestrictedRoot();
        var repository = CreatePlainRoot($"credential-scan-repository-{leakKind}");
        var worktree = CreatePlainRoot($"credential-scan-worktree-{leakKind}");
        var logs = CreatePlainRoot($"credential-scan-logs-{leakKind}");
        var token = Encoding.UTF8.GetBytes("synthetic-github-token-value-used-only-by-this-test");
        var current = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        var previous = Enumerable.Range(65, 32).Select(value => (byte)value).ToArray();
        if (leakKind == "substituted-state-key-raw")
        {
            current = Enumerable.Range(129, 32).Select(value => (byte)value).ToArray();
        }
        WriteRestrictedText(Path.Join(restricted.Path, "github-token"), Encoding.UTF8.GetString(token));
        WriteRestrictedText(Path.Join(restricted.Path, "current-state-key"), Convert.ToBase64String(current));
        WriteRestrictedText(Path.Join(restricted.Path, "previous-state-key"), Convert.ToBase64String(previous));
        var values = SyntheticProtectedCategories();
        var credentialPaths = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["--github-token-file"] = "github-token",
            ["--current-state-key-file"] = "current-state-key",
            ["--previous-state-key-file"] = "previous-state-key",
        };
        try
        {
            EvidenceAssemblerProgram.AppendActualCredentialValues(
                values,
                restricted,
                options,
                CredentialIdentities(restricted),
                credentialPaths);
            Assert.Equal(4, values["authorization"].Count);
            Assert.Equal(6, values["state_keys"].Count);
            var leak = leakKind switch
            {
                "authorization-raw" => values["authorization"][2],
                "authorization-bearer" => values["authorization"][3],
                "state-key-raw" or "substituted-state-key-raw" => values["state_keys"][2],
                _ => values["state_keys"][3],
            };
            File.WriteAllBytes(Path.Join(logs, "public.log"), leak);
            using var corpus = PublicSurfaceCorpusLease.Open(repository, worktree, logs);
            Assert.Throws<InvalidDataException>(() => corpus.AssertAbsent(values, []));
            var privateLeak = leak.ToArray();
            var safeHost = Encoding.UTF8.GetBytes("safe-host-document");
            try
            {
                Assert.Throws<InvalidDataException>(() =>
                    EvidenceAssemblerProgram.AssertProtectedValuesAbsent(
                        values,
                        safeHost,
                        [privateLeak]));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(privateLeak);
                CryptographicOperations.ZeroMemory(safeHost);
            }

            foreach (var path in credentialPaths)
            {
                restricted.RemoveCredentialFile(path);
                Assert.False(File.Exists(Path.Join(restricted.Path, path)));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(token);
            CryptographicOperations.ZeroMemory(current);
            CryptographicOperations.ZeroMemory(previous);
            foreach (var category in values.Values)
            {
                foreach (var value in category)
                {
                    CryptographicOperations.ZeroMemory(value);
                }
            }
        }
    }

    [Fact]
    public void ActualCredentialScanRejectsDuplicateOrIncompleteKeyRepresentations()
    {
        var restricted = CreateRestrictedRoot();
        WriteRestrictedText(Path.Join(restricted.Path, "github-token"),
            "synthetic-github-token-value-used-only-by-this-test");
        var duplicate = Convert.ToBase64String(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
        WriteRestrictedText(Path.Join(restricted.Path, "current-state-key"), duplicate);
        WriteRestrictedText(Path.Join(restricted.Path, "previous-state-key"), duplicate);
        var values = SyntheticProtectedCategories();
        try
        {
            Assert.Throws<InvalidDataException>(() =>
                EvidenceAssemblerProgram.AppendActualCredentialValues(
                    values,
                    restricted,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["--github-token-file"] = "github-token",
                        ["--current-state-key-file"] = "current-state-key",
                        ["--previous-state-key-file"] = "previous-state-key",
                    },
                    CredentialIdentities(restricted),
                    new List<string>()));
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
        }
    }

    [Fact]
    public void ActualCredentialScanRejectsAuthorizedFileIdentityReplacement()
    {
        var restricted = CreateRestrictedRoot();
        WriteRestrictedText(Path.Join(restricted.Path, "github-token"),
            "synthetic-github-token-value-used-only-by-this-test");
        WriteRestrictedText(Path.Join(restricted.Path, "current-state-key"),
            Convert.ToBase64String(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()));
        WriteRestrictedText(Path.Join(restricted.Path, "previous-state-key"),
            Convert.ToBase64String(Enumerable.Range(65, 32).Select(value => (byte)value).ToArray()));
        var authorized = CredentialIdentities(restricted);
        restricted.RemoveCredentialFile("github-token");
        WriteRestrictedText(Path.Join(restricted.Path, "github-token"),
            "replacement-github-token-value-used-only-by-this-test");
        var values = SyntheticProtectedCategories();
        try
        {
            Assert.Throws<InvalidDataException>(() =>
                EvidenceAssemblerProgram.AppendActualCredentialValues(
                    values,
                    restricted,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["--github-token-file"] = "github-token",
                        ["--current-state-key-file"] = "current-state-key",
                        ["--previous-state-key-file"] = "previous-state-key",
                    },
                    authorized,
                    new List<string>()));
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
        }
    }

    [Fact]
    public void AssemblerPublishesActualCandidateAcrossRealCorpusAndFullAdmittedInventory()
    {
        var repository = FindRepositoryRoot();
        var worktree = CreatePlainRoot("successful-assembler-worktree");
        var logs = CreatePlainRoot("successful-assembler-logs");
        File.WriteAllText(Path.Join(logs, "run.log"), "ordinary-public-log");
        var publicBytes = File.ReadAllBytes(Path.Join(
            repository,
            "runtime",
            "tests",
            "fixtures",
            "action-host",
            "trusted-proof",
            "templates",
            "public-safe-evidence.json"));
        var hostTemplate = File.ReadAllText(Path.Join(
            repository,
            "runtime",
            "tests",
            "fixtures",
            "action-host",
            "trusted-proof",
            "templates",
            "host-restricted-evidence.json"));
        var hostBytes = Encoding.UTF8.GetBytes(hostTemplate.Replace(
            "\"api_version\":\"2026-03-10\"",
            "\"api_version\":\"2026-03-11\"",
            StringComparison.Ordinal));
        Assert.DoesNotContain("\"api_version\":\"2026-03-10\"", Encoding.UTF8.GetString(hostBytes));
        var categories = new Dictionary<string, IReadOnlyList<byte[]>>(StringComparer.Ordinal)
        {
            ["authorization"] = [RandomNumberGenerator.GetBytes(32)],
            ["state_keys"] = [RandomNumberGenerator.GetBytes(32)],
            ["session_plaintext"] = [RandomNumberGenerator.GetBytes(32)],
            ["provider_content"] = [RandomNumberGenerator.GetBytes(32)],
            ["tool_data"] = [RandomNumberGenerator.GetBytes(32)],
            ["host_evidence"] = [RandomNumberGenerator.GetBytes(32)],
        };
        var admittedDocuments = new List<byte[]>();
        try
        {
            for (var index = 0; index < 15; index++)
            {
                var encrypted = Encoding.UTF8.GetBytes($"production-shaped-encrypted-object-{index:D2}");
                var archive = CreateArchive(encrypted);
                var admitted = ArtifactArchiveAdmission.Admit(
                    archive,
                    CanonicalEvidence.Sha256(archive),
                    "9001",
                    "1");
                Assert.Equal(encrypted, admitted.EncryptedObject);
                admittedDocuments.Add(archive);
                admittedDocuments.Add(admitted.EncryptedObject);
            }
            Assert.Equal(30, admittedDocuments.Count);

            using var corpus = PublicSurfaceCorpusLease.Open(repository, worktree, logs);
            var outputPath = Path.Join(worktree, "trusted-proof-public.json");
            var receipt = EvidenceAssemblerProgram.EnforcePublicProjectionBoundary(
                corpus,
                categories,
                publicBytes,
                hostBytes,
                admittedDocuments,
                outputPath);

            Assert.NotNull(receipt);
            receipt.Dispose();
            Assert.Equal(publicBytes, File.ReadAllBytes(outputPath));
            Assert.Equal(15, JsonDocument.Parse(hostBytes).RootElement
                .GetProperty("inventories").GetProperty("observed_cleanup")
                .GetArrayLength());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(publicBytes);
            CryptographicOperations.ZeroMemory(hostBytes);
            foreach (var category in categories.Values)
            {
                foreach (var value in category)
                {
                    CryptographicOperations.ZeroMemory(value);
                }
            }
            foreach (var document in admittedDocuments)
            {
                CryptographicOperations.ZeroMemory(document);
            }
        }
    }

    [Fact]
    public void RecoveryOnlyAssemblerAdmitsAuthenticatedExtraWithoutPublicProjection()
    {
        var repository = FindRepositoryRoot();
        var worktree = CreatePlainRoot("recovery-assembler-worktree");
        var logs = CreatePlainRoot("recovery-assembler-logs");
        File.WriteAllText(Path.Join(logs, "run.log"), "ordinary-public-log");
        var templatePath = Path.Join(
            repository,
            "runtime",
            "tests",
            "fixtures",
            "action-host",
            "trusted-proof",
            "templates",
            "host-restricted-evidence.json");
        var host = JsonNode.Parse(File.ReadAllText(templatePath))!.AsObject();
        var observed = host["inventories"]!["observed_cleanup"]!.AsArray();
        var extra = observed[^1]!.DeepClone().AsObject();
        extra["artifact_id"] = "1016";
        extra["artifact_name"] = "apr-r4-recovery-only-1016";
        extra["disposition"] = "recovery-only";
        observed.Add(extra);
        var hostBytes = CanonicalEvidence.Encode(host, EvidenceJson.Options);
        var categories = new Dictionary<string, IReadOnlyList<byte[]>>(StringComparer.Ordinal)
        {
            ["authorization"] = [RandomNumberGenerator.GetBytes(32)],
            ["state_keys"] = [RandomNumberGenerator.GetBytes(32)],
            ["session_plaintext"] = [RandomNumberGenerator.GetBytes(32)],
            ["provider_content"] = [RandomNumberGenerator.GetBytes(32)],
            ["tool_data"] = [RandomNumberGenerator.GetBytes(32)],
            ["host_evidence"] = [RandomNumberGenerator.GetBytes(32)],
        };
        try
        {
            using var corpus = PublicSurfaceCorpusLease.Open(repository, worktree, logs);
            var receipt = EvidenceAssemblerProgram.EnforcePublicProjectionBoundary(
                corpus,
                categories,
                [],
                hostBytes,
                [],
                null);

            Assert.Null(receipt);
            Assert.Empty(Directory.EnumerateFiles(worktree));
            Assert.Equal(16, observed.Count);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hostBytes);
            foreach (var category in categories.Values)
            {
                foreach (var value in category)
                {
                    CryptographicOperations.ZeroMemory(value);
                }
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FinalCorpusBarrierRejectsPostScanAdditionsForSuccessAndRecovery(bool publish)
    {
        var repository = CreatePlainRoot("final-corpus-repository");
        var worktree = CreatePlainRoot("final-corpus-worktree");
        var logs = CreatePlainRoot("final-corpus-logs");
        File.WriteAllText(Path.Join(repository, "tracked.txt"), "tracked");
        var hostBytes = ProjectionHostBytes(publish ? 15 : 16);
        var publicBytes = publish ? Encoding.UTF8.GetBytes("{\"kind\":\"public\"}\n") : [];
        var categories = SyntheticProtectedCategories();
        var outputPath = publish ? Path.Join(worktree, "public.json") : null;
        try
        {
            using var corpus = PublicSurfaceCorpusLease.Open(repository, worktree, logs);
            Assert.Throws<InvalidDataException>(() =>
                EvidenceAssemblerProgram.EnforcePublicProjectionBoundary(
                    corpus,
                    categories,
                    publicBytes,
                    hostBytes,
                    [],
                    outputPath,
                    () => File.WriteAllText(Path.Join(logs, "late.log"), "late")));
            if (outputPath is not null)
            {
                Assert.False(File.Exists(outputPath));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hostBytes);
            CryptographicOperations.ZeroMemory(publicBytes);
            ZeroProtectedCategories(categories);
        }
    }

    [Fact]
    public void FinalCorpusBarrierRejectsSameBytePathReplacement()
    {
        var repository = CreatePlainRoot("final-same-byte-repository");
        var worktree = CreatePlainRoot("final-same-byte-worktree");
        var logs = CreatePlainRoot("final-same-byte-logs");
        var trackedPath = Path.Join(repository, "tracked.txt");
        File.WriteAllText(trackedPath, "same bytes");
        var hostBytes = ProjectionHostBytes(15);
        var publicBytes = Encoding.UTF8.GetBytes("{\"kind\":\"public\"}\n");
        var categories = SyntheticProtectedCategories();
        var outputPath = Path.Join(worktree, "public.json");
        try
        {
            using var corpus = PublicSurfaceCorpusLease.Open(repository, worktree, logs);
            Assert.Throws<InvalidDataException>(() =>
                EvidenceAssemblerProgram.EnforcePublicProjectionBoundary(
                    corpus,
                    categories,
                    publicBytes,
                    hostBytes,
                    [],
                    outputPath,
                    () =>
                    {
                        File.Delete(trackedPath);
                        File.WriteAllText(trackedPath, "same bytes");
                    }));
            Assert.False(File.Exists(outputPath));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hostBytes);
            CryptographicOperations.ZeroMemory(publicBytes);
            ZeroProtectedCategories(categories);
        }
    }

    [Fact]
    public void FinalPublicationBarrierNeverRetractsSameByteCompetitor()
    {
        var repository = CreatePlainRoot("final-competitor-repository");
        var worktree = CreatePlainRoot("final-competitor-worktree");
        var logs = CreatePlainRoot("final-competitor-logs");
        var hostBytes = ProjectionHostBytes(15);
        var publicBytes = Encoding.UTF8.GetBytes("{\"kind\":\"public\"}\n");
        var categories = SyntheticProtectedCategories();
        var outputPath = Path.Join(worktree, "public.json");
        try
        {
            using var corpus = PublicSurfaceCorpusLease.Open(repository, worktree, logs);
            Assert.Throws<InvalidDataException>(() =>
                EvidenceAssemblerProgram.EnforcePublicProjectionBoundary(
                    corpus,
                    categories,
                    publicBytes,
                    hostBytes,
                    [],
                    outputPath,
                    afterPublicationForTest: path =>
                    {
                        File.Delete(path);
                        File.WriteAllBytes(path, publicBytes);
                    }));
            Assert.True(File.Exists(outputPath));
            Assert.Equal(publicBytes, File.ReadAllBytes(outputPath));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hostBytes);
            CryptographicOperations.ZeroMemory(publicBytes);
            ZeroProtectedCategories(categories);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactIdentityRetractionPreservesCompetitorInjectedAfterLastValidation(
        bool directoryCompetitor)
    {
        var root = CreatePlainRoot("exact-retraction-competitor");
        var outputPath = Path.Join(root, "public.json");
        var displacedPath = Path.Join(root, "displaced-public.json");
        var expected = Encoding.UTF8.GetBytes("expected-public-output");
        var competitor = Encoding.UTF8.GetBytes("competitor-public-output");
        using var receipt = EvidenceAssemblerProgram.WritePublicCreateNew(outputPath, expected);

        receipt.RetractIfOwned(path =>
        {
            File.Move(path, displacedPath);
            if (directoryCompetitor)
            {
                Directory.CreateDirectory(path);
            }
            else
            {
                File.WriteAllBytes(path, competitor);
            }
        });

        if (directoryCompetitor)
        {
            Assert.True(Directory.Exists(outputPath));
        }
        else
        {
            Assert.Equal(competitor, File.ReadAllBytes(outputPath));
        }
        if (File.Exists(displacedPath))
        {
            Assert.Equal(expected, File.ReadAllBytes(displacedPath));
        }
        Assert.Empty(Directory.EnumerateDirectories(root, ".apr-r4-disposition-*"));
    }

    [Fact]
    public void PinnedCredentialLeaseRejectsSameIdentityContentDrift()
    {
        var restricted = CreateRestrictedRoot();
        var path = Path.Join(restricted.Path, "current-state-key");
        var original = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var replacement = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        WriteRestrictedText(path, original);
        using var lease = restricted.AcquirePinnedFile(
            "current-state-key",
            EvidenceLimits.MaximumCredentialBytes);
        if (OperatingSystem.IsWindows())
        {
            Assert.ThrowsAny<IOException>(() => File.WriteAllText(path, replacement));
            lease.ValidateExactBytes();
        }
        else
        {
            File.WriteAllText(path, replacement);
            Assert.Throws<InvalidDataException>(() =>
            {
                lease.ValidateExactBytes();
            });
        }
    }

    [Theory]
    [InlineData("github-token", false)]
    [InlineData("github-token", true)]
    [InlineData(CredentialLeaseAuthorityClient.GitHubDescriptorName, false)]
    [InlineData(CredentialLeaseAuthorityClient.GitHubDescriptorName, true)]
    public void ExactIdentityDispositionPreservesCredentialAndDescriptorCompetitors(
        string relativePath,
        bool directoryCompetitor)
    {
        var restricted = CreateRestrictedRoot();
        var path = Path.Join(restricted.Path, relativePath);
        var displacedPath = Path.Join(restricted.Path, $"displaced-{relativePath}");
        var expected = Encoding.UTF8.GetBytes("expected-retained-identity");
        var competitor = Encoding.UTF8.GetBytes("competitor-retained-identity");
        WriteRestrictedText(path, Encoding.UTF8.GetString(expected));
        using var lease = restricted.AcquirePinnedFile(
            relativePath,
            EvidenceLimits.MaximumDocumentBytes);

        Assert.Throws<InvalidDataException>(() => lease.DeleteExactIdentity(currentPath =>
        {
            File.Move(currentPath, displacedPath);
            if (directoryCompetitor)
            {
                Directory.CreateDirectory(currentPath);
            }
            else
            {
                File.WriteAllBytes(currentPath, competitor);
            }
        }));

        if (directoryCompetitor)
        {
            Assert.True(Directory.Exists(path));
        }
        else
        {
            Assert.Equal(competitor, File.ReadAllBytes(path));
        }
        if (File.Exists(displacedPath))
        {
            Assert.Equal(expected, File.ReadAllBytes(displacedPath));
        }
        Assert.Empty(Directory.EnumerateDirectories(
            restricted.Path,
            ".apr-r4-disposition-*"));
    }

    [Fact]
    public void ExactIdentityDispositionRejectsNewHardLinkAfterLastValidation()
    {
        var restricted = CreateRestrictedRoot();
        const string relativePath = "github-token";
        var path = Path.Join(restricted.Path, relativePath);
        var hardLinkPath = Path.Join(restricted.Path, "credential-hard-link");
        var expected = Encoding.UTF8.GetBytes("expected-retained-identity");
        WriteRestrictedText(path, Encoding.UTF8.GetString(expected));
        using var lease = restricted.AcquirePinnedFile(
            relativePath,
            EvidenceLimits.MaximumCredentialBytes);

        Assert.Throws<InvalidDataException>(() => lease.DeleteExactIdentity(currentPath =>
            HardLinkTestPlatform.Create(hardLinkPath, currentPath)));

        lease.Dispose();
        Assert.Equal(expected, File.ReadAllBytes(path));
        Assert.Equal(expected, File.ReadAllBytes(hardLinkPath));
    }

    [Fact]
    public void LinuxExactIdentityDispositionRestoresSameInodeDriftAfterLastValidation()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var restricted = CreateRestrictedRoot();
        const string relativePath = "github-token";
        var path = Path.Join(restricted.Path, relativePath);
        var expected = Encoding.UTF8.GetBytes("expected-retained-identity");
        var changed = Encoding.UTF8.GetBytes("modified-retained-identity");
        Assert.Equal(expected.Length, changed.Length);
        WriteRestrictedText(path, Encoding.UTF8.GetString(expected));
        using var lease = restricted.AcquirePinnedFile(
            relativePath,
            EvidenceLimits.MaximumCredentialBytes);

        Assert.Throws<InvalidDataException>(() => lease.DeleteExactIdentity(currentPath =>
            File.WriteAllBytes(currentPath, changed)));

        Assert.Equal(changed, File.ReadAllBytes(path));
        Assert.Empty(Directory.EnumerateDirectories(
            restricted.Path,
            ".apr-r4-disposition-*"));
    }

    [Fact]
    public void CredentialGuardianServesUsedBytesAndDeletesOriginalIdentity()
    {
        var restricted = CreateRestrictedRoot();
        const string credentialName = "github-token";
        var credentialPath = Path.Join(restricted.Path, credentialName);
        var expected = Encoding.UTF8.GetBytes("synthetic-guardian-token-used-only-by-this-test");
        WriteRestrictedText(credentialPath, Encoding.UTF8.GetString(expected));
        using var representations = restricted.ReadCredentialFileRepresentations(
            credentialName,
            base64Key: false);
        CredentialLeaseAuthorityClient.LaunchCurrentProcess(
            restricted,
            restricted.DestinationIdentitySha256,
            [CreatePlainRoot("guardian-excluded")],
            CredentialLeaseAuthorityClient.GitHubDescriptorName,
            [new CredentialLeaseSpec(credentialName, Base64Key: false)],
            [representations],
            typeof(CapturePlan).Assembly.Location);

        using var client = CredentialLeaseAuthorityClient.Open(
            restricted,
            CredentialLeaseAuthorityClient.GitHubDescriptorName,
            [new CredentialLeaseSpec(credentialName, Base64Key: false)]);
        var values = client.ReadValues();
        try
        {
            var value = Assert.Single(values);
            Assert.Equal(expected, value.FileBytes);
            Assert.Equal(credentialName, value.RelativePath);
            client.DeleteCredentialFiles();
            Assert.True(SpinWait.SpinUntil(
                () => !File.Exists(credentialPath) &&
                    !File.Exists(Path.Join(
                        restricted.Path,
                        CredentialLeaseAuthorityClient.GitHubDescriptorName)),
                TimeSpan.FromSeconds(5)));
        }
        finally
        {
            foreach (var value in values) value.Dispose();
            CryptographicOperations.ZeroMemory(expected);
        }
    }

    [Theory]
    [InlineData(CredentialLeaseAuthorityClient.GitHubDescriptorName, "github-token", false)]
    [InlineData(CredentialLeaseAuthorityClient.StateKeyDescriptorName, "current-state-key", true)]
    public void CredentialGuardianHandoffClockStartsOnlyWhenProducerTransfersAuthority(
        string descriptorName,
        string credentialName,
        bool base64Key)
    {
        var (restricted, expected, representations, specs) = CreateGuardianTimingFixture(
            credentialName,
            base64Key);
        using (representations)
        {
            Thread.Sleep(2_500);
            LaunchGuardian(
                restricted,
                descriptorName,
                specs,
                representations,
                handoffMilliseconds: 2_000,
                connectedSessionMilliseconds: 2_000);
            CompleteGuardianDeletion(restricted, descriptorName, specs, expected);
        }
        CryptographicOperations.ZeroMemory(expected);
    }

    [Theory]
    [InlineData(CredentialLeaseAuthorityClient.GitHubDescriptorName, "github-token", false)]
    [InlineData(CredentialLeaseAuthorityClient.StateKeyDescriptorName, "current-state-key", true)]
    public void CredentialGuardianStartsFreshConnectedSessionAfterDelayedHandoff(
        string descriptorName,
        string credentialName,
        bool base64Key)
    {
        var (restricted, expected, representations, specs) = CreateGuardianTimingFixture(
            credentialName,
            base64Key);
        using (representations)
        {
            LaunchGuardian(
                restricted,
                descriptorName,
                specs,
                representations,
                handoffMilliseconds: 6_000,
                connectedSessionMilliseconds: 2_000);
            Thread.Sleep(2_500);
            CompleteGuardianDeletion(restricted, descriptorName, specs, expected, delayMilliseconds: 50);
        }
        CryptographicOperations.ZeroMemory(expected);
    }

    [Theory]
    [InlineData(CredentialLeaseAuthorityClient.GitHubDescriptorName, "github-token", false)]
    [InlineData(CredentialLeaseAuthorityClient.StateKeyDescriptorName, "current-state-key", true)]
    public void CredentialGuardianHandoffExpiryRemovesTheExactOriginalIdentity(
        string descriptorName,
        string credentialName,
        bool base64Key)
    {
        var (restricted, expected, representations, specs) = CreateGuardianTimingFixture(
            credentialName,
            base64Key);
        using (representations)
        {
            LaunchGuardian(
                restricted,
                descriptorName,
                specs,
                representations,
                handoffMilliseconds: 150,
                connectedSessionMilliseconds: 1_000);
            Assert.True(SpinWait.SpinUntil(
                () => !EvidenceFileHandle.PathEntryExists(Path.Join(restricted.Path, credentialName)) &&
                    !EvidenceFileHandle.PathEntryExists(Path.Join(restricted.Path, descriptorName)),
                TimeSpan.FromSeconds(5)));
        }
        CryptographicOperations.ZeroMemory(expected);
    }

    [Theory]
    [InlineData(CredentialLeaseAuthorityClient.GitHubDescriptorName, "github-token", false)]
    [InlineData(CredentialLeaseAuthorityClient.StateKeyDescriptorName, "current-state-key", true)]
    public void CredentialGuardianConnectedSessionExpiryFailsClosedForBothCredentialFamilies(
        string descriptorName,
        string credentialName,
        bool base64Key)
    {
        var (restricted, expected, representations, specs) = CreateGuardianTimingFixture(
            credentialName,
            base64Key);
        using (representations)
        {
            LaunchGuardian(
                restricted,
                descriptorName,
                specs,
                representations,
                handoffMilliseconds: 2_000,
                connectedSessionMilliseconds: 150);
            using var client = CredentialLeaseAuthorityClient.Open(restricted, descriptorName, specs);
            var values = client.ReadValues();
            try
            {
                Assert.Equal(expected, Assert.Single(values).FileBytes);
                Assert.True(SpinWait.SpinUntil(
                    () => !EvidenceFileHandle.PathEntryExists(Path.Join(restricted.Path, credentialName)) &&
                        !EvidenceFileHandle.PathEntryExists(Path.Join(restricted.Path, descriptorName)),
                    TimeSpan.FromSeconds(5)));
                Assert.ThrowsAny<IOException>(() => client.DeleteCredentialFiles());
            }
            finally
            {
                foreach (var value in values) value.Dispose();
            }
        }
        CryptographicOperations.ZeroMemory(expected);
    }

    [Theory]
    [InlineData(CredentialLeaseAuthorityClient.GitHubDescriptorName, "github-token", false)]
    [InlineData(CredentialLeaseAuthorityClient.StateKeyDescriptorName, "current-state-key", true)]
    public void LinuxCredentialGuardianSessionExpiryPreservesReplacementPathname(
        string descriptorName,
        string credentialName,
        bool base64Key)
    {
        if (!OperatingSystem.IsLinux()) return;

        var (restricted, expected, representations, specs) = CreateGuardianTimingFixture(
            credentialName,
            base64Key);
        var credentialPath = Path.Join(restricted.Path, credentialName);
        var competitor = Enumerable.Repeat((byte)'Z', expected.Length).ToArray();
        using (representations)
        {
            LaunchGuardian(
                restricted,
                descriptorName,
                specs,
                representations,
                handoffMilliseconds: 2_000,
                connectedSessionMilliseconds: 150);
            using var client = CredentialLeaseAuthorityClient.Open(restricted, descriptorName, specs);
            var values = client.ReadValues();
            try
            {
                File.Move(credentialPath, Path.Join(restricted.Path, $"displaced-{credentialName}"));
                File.WriteAllBytes(credentialPath, competitor);
                Assert.True(SpinWait.SpinUntil(
                    () => !EvidenceFileHandle.PathEntryExists(Path.Join(restricted.Path, descriptorName)),
                    TimeSpan.FromSeconds(5)));
                Assert.Equal(competitor, File.ReadAllBytes(credentialPath));
                Assert.ThrowsAny<IOException>(() => client.DeleteCredentialFiles());
            }
            finally
            {
                foreach (var value in values) value.Dispose();
            }
        }
        CryptographicOperations.ZeroMemory(expected);
        CryptographicOperations.ZeroMemory(competitor);
    }

    [Fact]
    public void CredentialGuardianLaunchFailureDisposesTheRetainedInputIdentity()
    {
        var restricted = CreateRestrictedRoot();
        const string credentialName = "github-token";
        var credentialPath = Path.Join(restricted.Path, credentialName);
        WriteRestrictedText(
            credentialPath,
            "synthetic-failed-guardian-token-used-only-by-this-test");
        using var representations = restricted.ReadCredentialFileRepresentations(
            credentialName,
            base64Key: false);

        Assert.Throws<InvalidDataException>(() =>
            CredentialLeaseAuthorityClient.LaunchCurrentProcess(
                restricted,
                restricted.DestinationIdentitySha256,
                [CreatePlainRoot("guardian-launch-failure-excluded")],
                CredentialLeaseAuthorityClient.GitHubDescriptorName,
                [new CredentialLeaseSpec(credentialName, Base64Key: false)],
                [representations],
                Path.Join(restricted.Path, "missing-guardian.dll")));

        Assert.False(EvidenceFileHandle.PathEntryExists(credentialPath));
        Assert.False(EvidenceFileHandle.PathEntryExists(Path.Join(
            restricted.Path,
            CredentialLeaseAuthorityClient.GitHubDescriptorName)));
    }

    [Fact]
    public void CredentialGuardianInvalidTimeoutDisposesTheRetainedInputIdentity()
    {
        var restricted = CreateRestrictedRoot();
        const string credentialName = "github-token";
        var credentialPath = Path.Join(restricted.Path, credentialName);
        WriteRestrictedText(
            credentialPath,
            "synthetic-invalid-timeout-token-used-only-by-this-test");
        using var representations = restricted.ReadCredentialFileRepresentations(
            credentialName,
            base64Key: false);

        Assert.Throws<InvalidDataException>(() =>
            CredentialLeaseAuthorityClient.LaunchCurrentProcess(
                restricted,
                restricted.DestinationIdentitySha256,
                [CreatePlainRoot("guardian-invalid-timeout-excluded")],
                CredentialLeaseAuthorityClient.GitHubDescriptorName,
                [new CredentialLeaseSpec(credentialName, Base64Key: false)],
                [representations],
                typeof(CapturePlan).Assembly.Location,
                timeouts: new CredentialLeaseAuthorityTimeouts(
                    TimeSpan.Zero,
                    EvidenceLimits.CredentialConnectedSessionTimeout)));

        Assert.False(EvidenceFileHandle.PathEntryExists(credentialPath));
        Assert.False(EvidenceFileHandle.PathEntryExists(Path.Join(
            restricted.Path,
            CredentialLeaseAuthorityClient.GitHubDescriptorName)));
    }

    [Fact]
    public void CredentialGuardianProcessStartFailureDisposesTheRetainedInputIdentity()
    {
        var restricted = CreateRestrictedRoot();
        const string credentialName = "github-token";
        var credentialPath = Path.Join(restricted.Path, credentialName);
        WriteRestrictedText(
            credentialPath,
            "synthetic-unstartable-guardian-token-used-only-by-this-test");
        using var representations = restricted.ReadCredentialFileRepresentations(
            credentialName,
            base64Key: false);

        Assert.Throws<InvalidDataException>(() =>
            CredentialLeaseAuthorityClient.LaunchCurrentProcess(
                restricted,
                restricted.DestinationIdentitySha256,
                [CreatePlainRoot("guardian-process-start-failure-excluded")],
                CredentialLeaseAuthorityClient.GitHubDescriptorName,
                [new CredentialLeaseSpec(credentialName, Base64Key: false)],
                [representations],
                typeof(CapturePlan).Assembly.Location,
                Path.Join(restricted.Path, "missing-guardian-executable")));

        Assert.False(EvidenceFileHandle.PathEntryExists(credentialPath));
        Assert.False(EvidenceFileHandle.PathEntryExists(Path.Join(
            restricted.Path,
            CredentialLeaseAuthorityClient.GitHubDescriptorName)));
    }

    [Fact]
    public void CredentialGuardiansCarryActualUsedBytesAcrossOverwriteAndRestore()
    {
        var restricted = CreateRestrictedRoot();
        const string tokenName = "github-token";
        const string keyName = "current-state-key";
        var token = Encoding.UTF8.GetBytes("synthetic-used-token-guardian-continuity");
        var decodedKey = RandomNumberGenerator.GetBytes(32);
        var encodedKey = Encoding.UTF8.GetBytes(Convert.ToBase64String(decodedKey));
        var tokenPath = Path.Join(restricted.Path, tokenName);
        var keyPath = Path.Join(restricted.Path, keyName);
        WriteRestrictedText(tokenPath, Encoding.UTF8.GetString(token));
        WriteRestrictedText(keyPath, Encoding.UTF8.GetString(encodedKey));
        using var tokenRepresentations = restricted.ReadCredentialFileRepresentations(
            tokenName,
            base64Key: false);
        using var keyRepresentations = restricted.ReadCredentialFileRepresentations(
            keyName,
            base64Key: true);
        CredentialLeaseAuthorityClient.LaunchCurrentProcess(
            restricted,
            restricted.DestinationIdentitySha256,
            [CreatePlainRoot("guardian-continuity-excluded")],
            CredentialLeaseAuthorityClient.GitHubDescriptorName,
            [new CredentialLeaseSpec(tokenName, Base64Key: false)],
            [tokenRepresentations],
            typeof(CapturePlan).Assembly.Location);
        CredentialLeaseAuthorityClient.LaunchCurrentProcess(
            restricted,
            restricted.DestinationIdentitySha256,
            [CreatePlainRoot("guardian-continuity-excluded-keys")],
            CredentialLeaseAuthorityClient.StateKeyDescriptorName,
            [new CredentialLeaseSpec(keyName, Base64Key: true)],
            [keyRepresentations],
            typeof(CapturePlan).Assembly.Location);

        if (!OperatingSystem.IsWindows())
        {
            File.WriteAllBytes(tokenPath, Enumerable.Repeat((byte)'x', token.Length).ToArray());
            File.WriteAllBytes(tokenPath, token);
            File.WriteAllBytes(keyPath, Enumerable.Repeat((byte)'A', encodedKey.Length).ToArray());
            File.WriteAllBytes(keyPath, encodedKey);
        }

        var categories = SyntheticProtectedCategories();
        var paths = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["--github-token-file"] = tokenName,
            ["--current-state-key-file"] = keyName,
        };
        EvidenceAssemblerProgram.AcquireAndDeleteLeasedCredentials(
            restricted,
            options,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [tokenName] = tokenRepresentations.PhysicalIdentitySha256,
                [keyName] = keyRepresentations.PhysicalIdentitySha256,
            },
            paths,
            out var githubAuthority,
            out var stateKeyAuthority,
            out var leasedValues);
        try
        {
            EvidenceAssemblerProgram.AppendLeasedCredentialRepresentations(
                categories,
                options,
                leasedValues);
            Assert.Contains(categories["authorization"], value => value.AsSpan().SequenceEqual(token));
            Assert.Contains(categories["authorization"], value =>
                value.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(
                    "Bearer " + Encoding.UTF8.GetString(token))));
            Assert.Contains(categories["state_keys"], value => value.AsSpan().SequenceEqual(decodedKey));
            Assert.Contains(categories["state_keys"], value => value.AsSpan().SequenceEqual(encodedKey));
            var leasedToken = leasedValues.Single(value => value.RelativePath == tokenName);
            var leasedKey = leasedValues.Single(value => value.RelativePath == keyName);
            Assert.Contains(categories["authorization"], value => ReferenceEquals(value, leasedToken.FileBytes));
            Assert.Contains(categories["state_keys"], value => ReferenceEquals(value, leasedKey.FileBytes));
            Assert.Contains(categories["state_keys"], value => ReferenceEquals(value, leasedKey.DecodedKey));
            Assert.True(githubAuthority.CredentialsDeleted);
            Assert.True(stateKeyAuthority.CredentialsDeleted);
            Assert.All(paths, path => Assert.False(File.Exists(Path.Join(restricted.Path, path))));
        }
        finally
        {
            githubAuthority.Dispose();
            stateKeyAuthority.Dispose();
            foreach (var value in leasedValues) value.Dispose();
            ZeroProtectedCategories(categories);
            CryptographicOperations.ZeroMemory(token);
            CryptographicOperations.ZeroMemory(decodedKey);
            CryptographicOperations.ZeroMemory(encodedKey);
        }
    }

    [Fact]
    public void CredentialCoordinatorDeletesBeforeSlowSuccessorWorkAndAppendsFromMemory()
    {
        var restricted = CreateRestrictedRoot();
        const string tokenName = "github-token";
        const string keyName = "current-state-key";
        var token = Encoding.UTF8.GetBytes("synthetic-coordinator-token-retained-in-memory");
        var decodedKey = RandomNumberGenerator.GetBytes(32);
        var encodedKey = Encoding.UTF8.GetBytes(Convert.ToBase64String(decodedKey));
        WriteRestrictedText(Path.Join(restricted.Path, tokenName), Encoding.UTF8.GetString(token));
        WriteRestrictedText(Path.Join(restricted.Path, keyName), Encoding.UTF8.GetString(encodedKey));
        using var tokenRepresentations = restricted.ReadCredentialFileRepresentations(
            tokenName,
            base64Key: false);
        using var keyRepresentations = restricted.ReadCredentialFileRepresentations(
            keyName,
            base64Key: true);
        var shortPhases = new CredentialLeaseAuthorityTimeouts(
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(2));
        CredentialLeaseAuthorityClient.LaunchCurrentProcess(
            restricted,
            restricted.DestinationIdentitySha256,
            [CreatePlainRoot("guardian-coordinator-excluded")],
            CredentialLeaseAuthorityClient.GitHubDescriptorName,
            [new CredentialLeaseSpec(tokenName, Base64Key: false)],
            [tokenRepresentations],
            typeof(CapturePlan).Assembly.Location,
            timeouts: shortPhases);
        CredentialLeaseAuthorityClient.LaunchCurrentProcess(
            restricted,
            restricted.DestinationIdentitySha256,
            [CreatePlainRoot("guardian-coordinator-excluded-keys")],
            CredentialLeaseAuthorityClient.StateKeyDescriptorName,
            [new CredentialLeaseSpec(keyName, Base64Key: true)],
            [keyRepresentations],
            typeof(CapturePlan).Assembly.Location,
            timeouts: shortPhases);

        var options = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["--github-token-file"] = tokenName,
            ["--current-state-key-file"] = keyName,
        };
        var categories = SyntheticProtectedCategories();
        var paths = new List<string>();
        EvidenceAssemblerProgram.AcquireAndDeleteLeasedCredentials(
            restricted,
            options,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [tokenName] = tokenRepresentations.PhysicalIdentitySha256,
                [keyName] = keyRepresentations.PhysicalIdentitySha256,
            },
            paths,
            out var githubAuthority,
            out var stateKeyAuthority,
            out var leasedValues);
        try
        {
            Assert.True(githubAuthority.CredentialsDeleted);
            Assert.True(stateKeyAuthority.CredentialsDeleted);
            Assert.All(paths, path => Assert.False(EvidenceFileHandle.PathEntryExists(
                Path.Join(restricted.Path, path))));

            Thread.Sleep(5_500);
            EvidenceAssemblerProgram.AppendLeasedCredentialRepresentations(
                categories,
                options,
                leasedValues);

            Assert.Contains(categories["authorization"], value => value.AsSpan().SequenceEqual(token));
            Assert.Contains(categories["state_keys"], value => value.AsSpan().SequenceEqual(decodedKey));
            Assert.Contains(categories["state_keys"], value => value.AsSpan().SequenceEqual(encodedKey));
        }
        finally
        {
            githubAuthority.Dispose();
            stateKeyAuthority.Dispose();
            foreach (var value in leasedValues) value.Dispose();
            ZeroProtectedCategories(categories);
            CryptographicOperations.ZeroMemory(token);
            CryptographicOperations.ZeroMemory(decodedKey);
            CryptographicOperations.ZeroMemory(encodedKey);
        }
    }

    [Fact]
    public void CredentialGuardianRejectsRestoredPathIdentityAndPreservesCompetitor()
    {
        var restricted = CreateRestrictedRoot();
        const string credentialName = "github-token";
        var credentialPath = Path.Join(restricted.Path, credentialName);
        var displacedPath = Path.Join(restricted.Path, "displaced-github-token");
        var expected = Enumerable.Repeat((byte)'A', 48).ToArray();
        var competitor = Enumerable.Repeat((byte)'B', 48).ToArray();
        WriteRestrictedText(credentialPath, Encoding.UTF8.GetString(expected));
        using var representations = restricted.ReadCredentialFileRepresentations(
            credentialName,
            base64Key: false);
        var specs = new[] { new CredentialLeaseSpec(credentialName, Base64Key: false) };
        CredentialLeaseAuthorityClient.LaunchCurrentProcess(
            restricted,
            restricted.DestinationIdentitySha256,
            [CreatePlainRoot("guardian-replacement-excluded")],
            CredentialLeaseAuthorityClient.GitHubDescriptorName,
            specs,
            [representations],
            typeof(CapturePlan).Assembly.Location);

        using var client = CredentialLeaseAuthorityClient.Open(
            restricted,
            CredentialLeaseAuthorityClient.GitHubDescriptorName,
            specs);
        var values = client.ReadValues();
        try
        {
            Assert.Equal(expected, Assert.Single(values).FileBytes);
            File.Move(credentialPath, displacedPath);
            File.WriteAllBytes(credentialPath, competitor);
            Assert.Throws<InvalidDataException>(() => client.DeleteCredentialFiles());
            Assert.Equal(competitor, File.ReadAllBytes(credentialPath));
            Assert.True(SpinWait.SpinUntil(
                () => OriginalIdentityIsDeletedOrReadable(displacedPath, expected),
                TimeSpan.FromSeconds(5)));
            Assert.True(SpinWait.SpinUntil(
                () => !EvidenceFileHandle.PathEntryExists(Path.Join(
                    restricted.Path,
                    CredentialLeaseAuthorityClient.GitHubDescriptorName)),
                TimeSpan.FromSeconds(5)));
        }
        finally
        {
            foreach (var value in values) value.Dispose();
            CryptographicOperations.ZeroMemory(expected);
            CryptographicOperations.ZeroMemory(competitor);
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
    public void WindowsPinnedLeaseRetainsIdentityAcrossPathReplacement()
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
        var displacedPath = Path.Join(root.Path, "replacement-target");

        File.Move(path, displacedPath);
        File.WriteAllText(path, "synthetic-competitor");

        Assert.Throws<InvalidDataException>(() => lease.Validate());
        lease.Dispose();
        Assert.Equal("synthetic-source", File.ReadAllText(displacedPath));
        Assert.Equal("synthetic-competitor", File.ReadAllText(path));
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
    public async Task PostCleanupPlanAdmitsAllTwentyThreeConcreteSourcesAndExecutesEveryRoute()
    {
        var root = CreateRestrictedRoot();
        var plan = CreatePostCleanupPlan();
        var bytes = CanonicalEvidence.Encode(plan, EvidenceJson.Options);
        try
        {
            root.WritePinnedFileCreateNew("post-cleanup-plan.json", bytes);
            var accepted = CapturePlan.Read(root, "post-cleanup-plan.json");
            Assert.Equal(23, accepted.Sources.Length);

            var calls = new List<string>();
            var token = Encoding.UTF8.GetBytes("synthetic-token-value-with-at-least-thirty-two-bytes");
            try
            {
                using var client = new TrustedProofCaptureClient(
                    token,
                    new RecordingHandler(request =>
                    {
                        calls.Add(request.RequestUri!.PathAndQuery);
                        return JsonResponse("{}");
                    }),
                    new RecordingHandler(_ => throw new InvalidOperationException()));
                foreach (var source in accepted.Sources)
                {
                    var pages = await client.GetPaginatedAsync(
                        source.Route,
                        source.EndpointFamily,
                        CancellationToken.None);
                    foreach (var body in pages.Bodies)
                    {
                        CryptographicOperations.ZeroMemory(body);
                    }
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(token);
            }
            Assert.Equal(23, calls.Count);
            Assert.Equal(17, calls.Distinct(StringComparer.Ordinal).Count());

            AssertInvalidPostCleanupPlan(root, "missing.json", plan with
            {
                Sources = plan.Sources[..^1],
            });
            AssertInvalidPostCleanupPlan(root, "duplicate.json", plan with
            {
                Sources = plan.Sources.Select((item, index) => index == 1 ? plan.Sources[0] : item).ToArray(),
            });
            AssertInvalidPostCleanupPlan(root, "cross-run.json", plan with
            {
                Sources = plan.Sources.Select(item =>
                    item.SourceId == "post-cleanup-final-run-9004"
                        ? item with
                        {
                            SourceId = "post-cleanup-final-run-9999",
                            EndpointFamily = "/repos/SolusQuest/agentic-pr-review/actions/runs/9999",
                            Route = "/repos/SolusQuest/agentic-pr-review/actions/runs/9999",
                        }
                        : item).ToArray(),
            });
            AssertInvalidPostCleanupPlan(root, "extra-repeated-family.json", plan with
            {
                Sources = [.. plan.Sources, plan.Sources[0] with
                {
                    SourceId = "post-cleanup-control-comments-normal-pr-1003",
                    EndpointFamily = "/repos/SolusQuest/agentic-pr-review/issues/1003/comments",
                    Route = "/repos/SolusQuest/agentic-pr-review/issues/1003/comments",
                }],
            });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
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

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
            !File.Exists(Path.Join(directory.FullName, "package.json")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ??
            throw new InvalidOperationException("Repository root not found.");
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

    private static bool OriginalIdentityIsDeletedOrReadable(
        string path,
        ReadOnlySpan<byte> expected)
    {
        if (!File.Exists(path))
        {
            return true;
        }
        try
        {
            return File.ReadAllBytes(path).AsSpan().SequenceEqual(expected);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
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

    private (RestrictedEvidenceRoot Root, byte[] Expected, CredentialFileRepresentations Representations,
        CredentialLeaseSpec[] Specs) CreateGuardianTimingFixture(string credentialName, bool base64Key)
    {
        var restricted = CreateRestrictedRoot();
        byte[] expected;
        if (base64Key)
        {
            var key = RandomNumberGenerator.GetBytes(32);
            try
            {
                expected = Encoding.UTF8.GetBytes(Convert.ToBase64String(key));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
        else
        {
            expected = Encoding.UTF8.GetBytes("synthetic-guardian-timing-credential");
        }
        WriteRestrictedText(
            Path.Join(restricted.Path, credentialName),
            Encoding.UTF8.GetString(expected));
        return (
            restricted,
            expected,
            restricted.ReadCredentialFileRepresentations(credentialName, base64Key),
            [new CredentialLeaseSpec(credentialName, base64Key)]);
    }

    private void LaunchGuardian(
        RestrictedEvidenceRoot restricted,
        string descriptorName,
        CredentialLeaseSpec[] specs,
        CredentialFileRepresentations representations,
        int handoffMilliseconds,
        int connectedSessionMilliseconds)
    {
        CredentialLeaseAuthorityClient.LaunchCurrentProcess(
            restricted,
            restricted.DestinationIdentitySha256,
            [CreatePlainRoot("guardian-timing-excluded")],
            descriptorName,
            specs,
            [representations],
            typeof(CapturePlan).Assembly.Location,
            timeouts: new CredentialLeaseAuthorityTimeouts(
                TimeSpan.FromMilliseconds(handoffMilliseconds),
                TimeSpan.FromMilliseconds(connectedSessionMilliseconds)));
    }

    private static void CompleteGuardianDeletion(
        RestrictedEvidenceRoot restricted,
        string descriptorName,
        CredentialLeaseSpec[] specs,
        byte[] expected,
        int delayMilliseconds = 0)
    {
        using var client = CredentialLeaseAuthorityClient.Open(restricted, descriptorName, specs);
        var values = client.ReadValues();
        try
        {
            if (delayMilliseconds != 0) Thread.Sleep(delayMilliseconds);
            Assert.Equal(expected, Assert.Single(values).FileBytes);
            client.DeleteCredentialFiles();
            Assert.True(SpinWait.SpinUntil(
                () => !EvidenceFileHandle.PathEntryExists(Path.Join(
                        restricted.Path,
                        specs[0].RelativePath)) &&
                    !EvidenceFileHandle.PathEntryExists(Path.Join(restricted.Path, descriptorName)),
                TimeSpan.FromSeconds(5)));
        }
        finally
        {
            foreach (var value in values) value.Dispose();
        }
    }

    private static CaptureManifestOperationRun[] OperationRuns() =>
    [
        new(new string('6', 64), "normal", "9001", "1"),
        new(new string('6', 64), "normal", "9002", "1"),
        new(new string('8', 64), "stale", "9003", "1"),
        new(new string('8', 64), "stale", "9004", "1"),
    ];

    private static Dictionary<string, IReadOnlyList<byte[]>> SyntheticProtectedCategories() =>
        new(StringComparer.Ordinal)
        {
            ["authorization"] =
            [
                Encoding.UTF8.GetBytes("APR222-SYNTHETIC-AUTHORIZATION-RAW"),
                Encoding.UTF8.GetBytes("APR222-SYNTHETIC-AUTHORIZATION-BEARER"),
            ],
            ["state_keys"] =
            [
                Encoding.UTF8.GetBytes("APR222-SYNTHETIC-STATE-KEY-RAW"),
                Encoding.UTF8.GetBytes("APR222-SYNTHETIC-STATE-KEY-BASE64"),
            ],
            ["session_plaintext"] = [Encoding.UTF8.GetBytes("APR222-SYNTHETIC-SESSION-PLAINTEXT")],
            ["provider_content"] = [Encoding.UTF8.GetBytes("APR222-SYNTHETIC-PROVIDER-CONTENT")],
            ["tool_data"] = [Encoding.UTF8.GetBytes("APR222-SYNTHETIC-TOOL-DATA-VALUE")],
            ["host_evidence"] = [Encoding.UTF8.GetBytes("APR222-SYNTHETIC-HOST-EVIDENCE")],
        };

    private static byte[] ProjectionHostBytes(int cleanupCount)
    {
        var template = JsonNode.Parse(File.ReadAllText(Path.Join(
            FindRepositoryRoot(),
            "runtime",
            "tests",
            "fixtures",
            "action-host",
            "trusted-proof",
            "templates",
            "host-restricted-evidence.json")))!.AsObject();
        var observed = template["inventories"]!["observed_cleanup"]!.AsArray();
        while (observed.Count > cleanupCount) observed.RemoveAt(observed.Count - 1);
        while (observed.Count < cleanupCount)
        {
            var extra = observed[^1]!.DeepClone().AsObject();
            extra["artifact_id"] = (1000 + observed.Count + 1).ToString();
            extra["artifact_name"] = $"apr-r4-recovery-only-{observed.Count + 1}";
            extra["disposition"] = "recovery-only";
            observed.Add(extra);
        }
        return CanonicalEvidence.Encode(template, EvidenceJson.Options);
    }

    private static void ZeroProtectedCategories(
        IReadOnlyDictionary<string, IReadOnlyList<byte[]>> categories)
    {
        foreach (var category in categories.Values)
        {
            foreach (var value in category) CryptographicOperations.ZeroMemory(value);
        }
    }

    private static IReadOnlyDictionary<string, string> CredentialIdentities(
        RestrictedEvidenceRoot root)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in new[] { "github-token", "current-state-key", "previous-state-key" })
        {
            using var lease = root.AcquirePinnedFile(name, EvidenceLimits.MaximumCredentialBytes);
            result.Add(name, lease.Identity);
        }
        return result;
    }

    private static CapturePlanDocument CreatePostCleanupPlan()
    {
        const string repository = "SolusQuest/agentic-pr-review";
        const string root = "/repos/SolusQuest/agentic-pr-review";
        var operationIds = new[] { new string('6', 64), new string('8', 64) };
        var runs = new[] { "9001", "9002", "9003", "9004" };
        static CapturePlanSource Source(string id, string route, string pagination = "none") =>
            new(id, route, route, pagination);
        var sources = new List<CapturePlanSource>
        {
            Source("post-cleanup-control-comments-normal-pr-1001", $"{root}/issues/1001/comments", "complete-cursor"),
            Source("post-cleanup-control-comments-stale-pr-1002", $"{root}/issues/1002/comments", "complete-cursor"),
            Source("post-cleanup-sticky-comments-normal-pr-1001", $"{root}/issues/1001/comments", "complete-cursor"),
            Source("post-cleanup-sticky-comments-stale-pr-1002", $"{root}/issues/1002/comments", "complete-cursor"),
            Source("post-cleanup-variables", $"{root}/actions/variables", "complete-cursor"),
            Source("post-cleanup-secrets", $"{root}/actions/secrets", "complete-cursor"),
            Source("post-cleanup-environment", $"{root}/environments/r4-trusted-proof"),
            Source("post-cleanup-ref-normal", $"{root}/git/matching-refs/heads/r4-trusted-proof/{operationIds[0]}", "complete-cursor"),
            Source("post-cleanup-ref-stale", $"{root}/git/matching-refs/heads/r4-trusted-proof/{operationIds[1]}", "complete-cursor"),
            Source("post-cleanup-pr-normal-1001", $"{root}/pulls/1001"),
            Source("post-cleanup-pr-stale-1002", $"{root}/pulls/1002"),
        };
        foreach (var run in runs)
        {
            sources.Add(Source($"post-cleanup-state-delete-run-{run}", $"{root}/actions/runs/{run}/artifacts", "complete-cursor"));
            sources.Add(Source($"post-cleanup-state-empty-run-{run}", $"{root}/actions/runs/{run}/artifacts", "complete-cursor"));
            sources.Add(Source($"post-cleanup-final-run-{run}", $"{root}/actions/runs/{run}"));
        }
        return new CapturePlanDocument(
            "apr-r4-e3-post-cleanup-capture-plan-v1",
            "42",
            repository,
            operationIds,
            [
                new(operationIds[0], "normal", runs[0], "1"),
                new(operationIds[0], "normal", runs[1], "1"),
                new(operationIds[1], "stale", runs[2], "1"),
                new(operationIds[1], "stale", runs[3], "1"),
            ],
            CapturePlan.CheckedSourceMapSha256,
            "post-cleanup-capture",
            sources.ToArray(),
            []);
    }

    private static void AssertInvalidPostCleanupPlan(
        RestrictedEvidenceRoot root,
        string relativePath,
        CapturePlanDocument plan)
    {
        var bytes = CanonicalEvidence.Encode(plan, EvidenceJson.Options);
        try
        {
            root.WritePinnedFileCreateNew(relativePath, bytes);
            Assert.Throws<InvalidDataException>(() => CapturePlan.Read(root, relativePath));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
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

using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceContracts;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofCapture;

internal static class CorrectionGateMaterializer
{
    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] == "correction-gate-check";

    public static int Run(string[] args)
    {
        byte[] token = [];
        try
        {
            var options = Parse(args.Skip(1).ToArray());
            var root = RestrictedEvidenceRoot.Open(
                options["--restricted-root"],
                options["--destination-identity"],
                [options["--repository-root"], options["--worktree-root"]]);
            using var authorizationLease = root.AcquirePinnedFile(
                options["--execution-authorization"],
                EvidenceLimits.MaximumDocumentBytes);
            var authorizationSha256 = CanonicalEvidence.Sha256(authorizationLease.Bytes);
            if (authorizationSha256 != options["--execution-authorization-sha256"])
            {
                throw new InvalidDataException("correction_gate_authorization_invalid");
            }
            using var authorization = JsonDocument.Parse(authorizationLease.Bytes);
            var execution = authorization.RootElement;
            var gate = execution.GetProperty("correction_gate");
            var gateBytes = CanonicalEvidence.Encode(gate, EvidenceJson.Options);
            string gateSha256;
            try
            {
                gateSha256 = CanonicalEvidence.Sha256(gateBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(gateBytes);
            }
            var expected = ReadExpected(execution, gate, options["--destination-identity"]);
            var worktree = System.IO.Path.GetFullPath(options["--worktree-root"]);
            var worktreeIdentity = WorktreeIdentity(worktree);
            var commit = Git(worktree, "rev-parse", "HEAD");
            var tree = Git(worktree, "rev-parse", "HEAD^{tree}");
            var clean = IsWorktreeClean(worktree);
            if (!clean || commit != expected.Commit || tree != expected.Tree ||
                worktreeIdentity != expected.WorktreeIdentitySha256)
            {
                throw new InvalidDataException("correction_gate_git_invalid");
            }
            var gateAssemblySha256 = DigestFile(Assembly.GetExecutingAssembly().Location);
            VerifyLocalIdentities(options, expected, gateAssemblySha256);

            token = ReadToken(Console.OpenStandardInput());
            using var client = TrustedProofCaptureClient.CreateProduction(token);
            using var timeout = new CancellationTokenSource(EvidenceLimits.LogicalOperationTimeout);
            var pr = client.GetPaginatedAsync(
                $"/repos/{expected.Repository}/pulls/{expected.PullRequestNumber}",
                $"/repos/{expected.Repository}/pulls/{expected.PullRequestNumber}",
                timeout.Token).GetAwaiter().GetResult();
            var remoteCommit = client.GetPaginatedAsync(
                $"/repos/{expected.Repository}/git/commits/{expected.Commit}",
                $"/repos/{expected.Repository}/git/commits/{expected.Commit}",
                timeout.Token).GetAwaiter().GetResult();
            var authorizationComment = client.GetPaginatedAsync(
                $"/repos/{expected.Repository}/issues/comments/{expected.AuthorizationCommentId}",
                $"/repos/{expected.Repository}/issues/comments/{expected.AuthorizationCommentId}",
                timeout.Token).GetAwaiter().GetResult();
            CapturePageSet? permission = null;
            try
            {
                if (pr.Bodies.Length != 1 || remoteCommit.Bodies.Length != 1 ||
                    authorizationComment.Bodies.Length != 1)
                {
                    throw new InvalidDataException("correction_gate_remote_invalid");
                }
                using var prJson = JsonDocument.Parse(pr.Bodies[0]);
                using var commitJson = JsonDocument.Parse(remoteCommit.Bodies[0]);
                using var commentJson = JsonDocument.Parse(authorizationComment.Bodies[0]);
                if (prJson.RootElement.GetProperty("number").GetRawText() != expected.PullRequestNumber ||
                    prJson.RootElement.GetProperty("state").GetString() != "open" ||
                    prJson.RootElement.GetProperty("head").GetProperty("ref").GetString() != expected.Branch ||
                    prJson.RootElement.GetProperty("head").GetProperty("sha").GetString() != expected.Commit ||
                    commitJson.RootElement.GetProperty("sha").GetString() != expected.Commit ||
                    commitJson.RootElement.GetProperty("tree").GetProperty("sha").GetString() != expected.Tree)
                {
                    throw new InvalidDataException("correction_gate_remote_invalid");
                }
                var authorLogin = ValidateAuthorizationComment(
                    execution,
                    authorizationComment.Bodies[0],
                    commentJson.RootElement,
                    expected);
                permission = client.GetPaginatedAsync(
                    $"/repos/{expected.Repository}/collaborators/{authorLogin}/permission",
                    $"/repos/{expected.Repository}/collaborators/{authorLogin}/permission",
                    timeout.Token).GetAwaiter().GetResult();
                if (permission.Bodies.Length != 1)
                {
                    throw new InvalidDataException("correction_gate_authorization_invalid");
                }
                using var permissionJson = JsonDocument.Parse(permission.Bodies[0]);
                ValidateAuthorizationPermission(permissionJson.RootElement, authorLogin, expected);
                var readbacks = new[]
                {
                    WriteReadback(root, "correction-gate-pr", "correction-gate-pr.json", pr, 0),
                    WriteReadback(root, "correction-gate-commit", "correction-gate-commit.json", remoteCommit, 0),
                    WriteReadback(root, "correction-gate-authorization-comment",
                        "correction-gate-authorization-comment.json", authorizationComment, 0),
                    WriteReadback(root, "correction-gate-authorization-permission",
                        "correction-gate-authorization-permission.json", permission, 0),
                };
                var receipt = CorrectionGateReceipt.MaterializeCreateNew(
                    root,
                    options["--correction-gate-receipt-output"],
                    new CorrectionGateReceiptDocument(
                        CorrectionGateReceipt.Kind,
                        options["--destination-identity"],
                        authorizationSha256,
                        gateSha256,
                        expected.Repository,
                        expected.PullRequestNumber,
                        expected.Branch,
                        expected.Commit,
                        expected.Tree,
                        worktreeIdentity,
                        gateAssemblySha256,
                        readbacks,
                        expected.AuthorityIdentities,
                        expected.ContractDigests,
                        WorktreeClean: true,
                        Finalized: true));
                Console.Out.WriteLine(
                    $"APR_R4_E3_CORRECTION_GATE_OK {receipt.Sha256} {receipt.PhysicalIdentitySha256}");
                return 0;
            }
            finally
            {
                foreach (var body in pr.Bodies.Concat(remoteCommit.Bodies).Concat(
                    authorizationComment.Bodies).Concat(permission?.Bodies ?? []))
                {
                    CryptographicOperations.ZeroMemory(body);
                }
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or
            UnauthorizedAccessException or CryptographicException or JsonException or
            HttpRequestException or OperationCanceledException or ArgumentException or
            KeyNotFoundException or FormatException)
        {
            Console.Error.WriteLine("APR_R4_E3_CORRECTION_GATE_INVALID");
            return 1;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(token);
        }
    }

    private static CorrectionGateReadback WriteReadback(
        RestrictedEvidenceRoot root,
        string sourceId,
        string path,
        CapturePageSet pages,
        int index)
    {
        var identity = root.WritePinnedFileCreateNew(path, pages.Bodies[index]);
        return new(
            sourceId,
            path,
            pages.Captures[index].BodySha256,
            identity,
            pages.Captures[index].RequestStartedUnixMilliseconds,
            pages.Captures[index].ResponseReceivedUnixMilliseconds);
    }

    internal static string ValidateAuthorizationComment(
        JsonElement localExecution,
        byte[] commentResponseBytes,
        JsonElement response,
        ExpectedGate expected)
    {
        if (response.GetProperty("id").GetRawText() != expected.AuthorizationCommentId ||
            response.GetProperty("user").GetProperty("id").GetRawText() !=
                expected.AuthorizationAuthorId ||
            response.GetProperty("body").GetString() is not { } body ||
            response.GetProperty("user").GetProperty("login").GetString() is not { Length: > 0 } login)
        {
            throw new InvalidDataException("correction_gate_authorization_invalid");
        }
        const string prefix = "<!-- apr-r4-e3-authorization ";
        const string suffix = " -->";
        if (!body.StartsWith(prefix, StringComparison.Ordinal) ||
            !body.EndsWith(suffix, StringComparison.Ordinal))
        {
            throw new InvalidDataException("correction_gate_authorization_invalid");
        }
        using var marker = JsonDocument.Parse(body[prefix.Length..^suffix.Length]);
        var markerRoot = marker.RootElement;
        if (markerRoot.GetProperty("contract").GetString() !=
                "apr-r4-e3-maintainer-authorization-v1" ||
            markerRoot.GetProperty("phase").GetString() != "execution" ||
            markerRoot.GetProperty("repository").GetString() != expected.Repository ||
            markerRoot.GetProperty("issue_number").GetRawText() != expected.AuthorizationIssueNumber)
        {
            throw new InvalidDataException("correction_gate_authorization_invalid");
        }
        var localWithoutSource = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in localExecution.EnumerateObject())
        {
            if (property.Name != "source") localWithoutSource.Add(property.Name, property.Value.Clone());
        }
        var localCanonical = CanonicalEvidence.Encode(localWithoutSource, EvidenceJson.Options);
        var markerCanonical = CanonicalEvidence.Encode(
            markerRoot.GetProperty("authorization"),
            EvidenceJson.Options);
        try
        {
            if (!localCanonical.AsSpan().SequenceEqual(markerCanonical))
            {
                throw new InvalidDataException("correction_gate_authorization_invalid");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(localCanonical);
            CryptographicOperations.ZeroMemory(markerCanonical);
        }
        var source = localExecution.GetProperty("source");
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        try
        {
            if (source.GetProperty("comment_id").GetString() != expected.AuthorizationCommentId ||
                source.GetProperty("author_id").GetString() != expected.AuthorizationAuthorId ||
                source.GetProperty("author_permission").GetString() != expected.AuthorizationPermission ||
                source.GetProperty("body_sha256").GetString() != CanonicalEvidence.Sha256(bodyBytes) ||
                source.GetProperty("readback_sha256").GetString() != CanonicalEvidence.Sha256(bodyBytes) ||
                source.GetProperty("capture_body_sha256").GetString() !=
                    CanonicalEvidence.Sha256(commentResponseBytes))
            {
                throw new InvalidDataException("correction_gate_authorization_invalid");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bodyBytes);
        }
        return login;
    }

    internal static void ValidateAuthorizationPermission(
        JsonElement permission,
        string authorLogin,
        ExpectedGate expected)
    {
        if (permission.GetProperty("permission").GetString() is not ("write" or "admin") ||
            permission.GetProperty("permission").GetString() != expected.AuthorizationPermission ||
            permission.GetProperty("user").GetProperty("id").GetRawText() !=
                expected.AuthorizationAuthorId ||
            permission.GetProperty("user").GetProperty("login").GetString() != authorLogin)
        {
            throw new InvalidDataException("correction_gate_authorization_invalid");
        }
    }

    internal static string WorktreeIdentity(string path)
    {
        var worktree = System.IO.Path.GetFullPath(path);
        return CanonicalEvidence.Sha256(Encoding.UTF8.GetBytes(worktree.Replace('\\', '/')));
    }

    private static void VerifyLocalIdentities(
        IReadOnlyDictionary<string, string> options,
        ExpectedGate expected,
        string gateAssemblySha256)
    {
        var repositoryRoot = System.IO.Path.GetFullPath(options["--repository-root"]);
        var sourceGroups = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["capture"] = ["runtime/tests/ActionHostTrustedProofCapture/Program.cs", "runtime/tests/ActionHostTrustedProofCapture/CapturePlan.cs", "runtime/tests/ActionHostTrustedProofCapture/CapturePackageWriter.cs", "runtime/tests/ActionHostTrustedProofCapture/CorrectionGateMaterializer.cs", "runtime/tests/ActionHostTrustedProofCapture/CleanupAuthorizationMaterializer.cs", "runtime/tests/ActionHostTrustedProofCapture/TrustedProofCaptureClient.cs", "runtime/tests/ActionHostTrustedProofEvidenceContracts/CredentialAdmissionReceipt.cs"],
            ["credential-materializer"] = ["runtime/tests/ActionHostTrustedProofCapture/CredentialMaterializer.cs", "runtime/tests/ActionHostTrustedProofEvidenceContracts/CredentialAdmissionReceipt.cs", "runtime/tests/ActionHostTrustedProofEvidenceContracts/CredentialLeaseAuthority.cs"],
            ["producer-journal-materializer"] = ["runtime/tests/ActionHostTrustedProofCapture/ProducerJournalMaterializer.cs", "runtime/tests/ActionHostTrustedProofEvidenceContracts/ProducerOutcomeJournal.cs", "runtime/tests/ActionHostTrustedProofEvidenceContracts/PhaseFragmentJournal.cs", "runtime/tests/ActionHostTrustedProofEvidenceContracts/CaptureManifest.cs"],
            ["phase-fragment-materializer"] = ["runtime/tests/ActionHostTrustedProofCapture/PhaseFragmentMaterializer.cs", "runtime/tests/ActionHostTrustedProofCapture/EnrollmentObservationMaterializer.cs", "runtime/tests/ActionHostTrustedProofEvidenceContracts/PhaseFragmentJournal.cs"],
            ["oracle"] = ["runtime/tests/ActionHostTrustedProofEvidenceOracle/Program.cs", "runtime/tests/ActionHostTrustedProofEvidenceOracle/OracleCaptureAdmission.cs"],
            ["assembler"] = ["runtime/tests/ActionHostTrustedProofEvidenceAssembler/Program.cs", "scripts/assemble-r4-trusted-proof-evidence.mjs", "scripts/r4-trusted-proof-contract.mjs"],
        };
        var assemblyByComponent = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["oracle"] = options["--oracle-assembly"],
            ["assembler"] = options["--assembler-assembly"],
        };
        foreach (var identity in expected.AuthorityIdentities)
        {
            var actualBuild = assemblyByComponent.TryGetValue(identity.Component, out var assemblyPath)
                ? DigestFile(assemblyPath)
                : gateAssemblySha256;
            if (identity.SourceSha256 != DigestSourceGroup(repositoryRoot, sourceGroups[identity.Component]) ||
                identity.BuildSha256 != actualBuild)
            {
                throw new InvalidDataException("correction_gate_local_identity_invalid");
            }
        }
        var contracts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["cleanup-generator"] = "scripts/generate-r4-trusted-proof-cleanup-plan.mjs",
            ["projector"] = "scripts/project-r4-trusted-proof-evidence.mjs",
            ["static-checker"] = "scripts/check-r4-trusted-proof.mjs",
            ["source-map"] = "runtime/tests/fixtures/action-host/trusted-proof/source-map.json",
            ["host-schema"] = "runtime/tests/fixtures/action-host/trusted-proof/schemas/host-restricted-evidence.schema.json",
            ["private-package-schema"] = "runtime/tests/fixtures/action-host/trusted-proof/schemas/private-package-manifest.schema.json",
            ["public-schema"] = "runtime/tests/fixtures/action-host/trusted-proof/schemas/public-safe-evidence.schema.json",
            ["authorization-grammar"] = "runtime/tests/fixtures/action-host/trusted-proof/authorization-environment-contract.json",
        };
        foreach (var contract in expected.ContractDigests)
        {
            if (contract.Sha256 != DigestFile(System.IO.Path.Join(repositoryRoot, contracts[contract.Component])))
            {
                throw new InvalidDataException("correction_gate_contract_invalid");
            }
        }
    }

    private static string DigestSourceGroup(string root, IEnumerable<string> paths)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var relative in paths.Order(StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(relative));
            hash.AppendData([0]);
            using var stream = File.OpenRead(System.IO.Path.Join(root, relative));
            var bytes = SHA256.HashData(stream);
            hash.AppendData(bytes);
            CryptographicOperations.ZeroMemory(bytes);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string DigestFile(string path)
    {
        using var stream = File.OpenRead(System.IO.Path.GetFullPath(path));
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static ExpectedGate ReadExpected(JsonElement execution, JsonElement gate, string destination)
    {
        if (execution.GetProperty("kind").GetString() != "apr-r4-e3-execution-authorization-v1" ||
            execution.GetProperty("destinations").GetProperty("private")
                .GetProperty("identity_sha256").GetString() != destination)
        {
            throw new InvalidDataException("correction_gate_authorization_invalid");
        }
        var identities = gate.GetProperty("authority_identities").EnumerateArray().Select(item => new CorrectionGateIdentity(
            item.GetProperty("component").GetString() ?? "",
            item.GetProperty("source_sha256").GetString() ?? "",
            item.GetProperty("build_sha256").GetString() ?? "")).ToArray();
        var contracts = gate.GetProperty("contract_digests").EnumerateArray().Select(item => new CorrectionGateContract(
            item.GetProperty("component").GetString() ?? "",
            item.GetProperty("sha256").GetString() ?? "")).ToArray();
        var source = execution.GetProperty("source");
        return new(
            gate.GetProperty("repository").GetString() ?? "",
            gate.GetProperty("pull_request_number").GetString() ?? "",
            gate.GetProperty("branch").GetString() ?? "",
            gate.GetProperty("commit").GetString() ?? "",
            gate.GetProperty("tree").GetString() ?? "",
            execution.GetProperty("destinations").GetProperty("public")
                .GetProperty("worktree_identity_sha256").GetString() ?? "",
            source.GetProperty("issue_number").GetString() ?? "",
            source.GetProperty("comment_id").GetString() ?? "",
            source.GetProperty("author_id").GetString() ?? "",
            source.GetProperty("author_permission").GetString() ?? "",
            identities,
            contracts);
    }

    private static string Git(string worktree, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = worktree,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidDataException("correction_gate_git_invalid");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidDataException("correction_gate_git_invalid");
        return output.Trim();
    }

    internal static bool IsWorktreeClean(string worktree) =>
        Git(worktree, "status", "--porcelain=v1", "--untracked-files=all").Length == 0;

    private static byte[] ReadToken(Stream stream)
    {
        Span<byte> prefix = stackalloc byte[4];
        stream.ReadExactly(prefix);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is < 1 or > EvidenceLimits.MaximumCredentialBytes)
        {
            throw new InvalidDataException("correction_gate_token_invalid");
        }
        var token = new byte[length];
        stream.ReadExactly(token);
        if (stream.ReadByte() != -1) throw new InvalidDataException("correction_gate_token_invalid");
        return token;
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var names = new[]
        {
            "--restricted-root", "--destination-identity", "--repository-root", "--worktree-root",
            "--execution-authorization", "--execution-authorization-sha256",
            "--oracle-assembly", "--assembler-assembly", "--correction-gate-receipt-output",
        };
        if (args.Length != names.Length * 2) throw new InvalidDataException("arguments_invalid");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (!names.Contains(args[index], StringComparer.Ordinal) ||
                !result.TryAdd(args[index], args[index + 1])) throw new InvalidDataException("arguments_invalid");
        }
        if (names.Any(name => !result.ContainsKey(name))) throw new InvalidDataException("arguments_invalid");
        return result;
    }

    internal sealed record ExpectedGate(
        string Repository,
        string PullRequestNumber,
        string Branch,
        string Commit,
        string Tree,
        string WorktreeIdentitySha256,
        string AuthorizationIssueNumber,
        string AuthorizationCommentId,
        string AuthorizationAuthorId,
        string AuthorizationPermission,
        CorrectionGateIdentity[] AuthorityIdentities,
        CorrectionGateContract[] ContractDigests);
}

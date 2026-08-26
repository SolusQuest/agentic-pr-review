using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceContracts;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceAssembler;

internal static partial class Program
{
    private static int Main(string[] args)
    {
        var leases = new List<PinnedEvidenceLease>();
        var protectedScanValues = new Dictionary<string, IReadOnlyList<byte[]>>(StringComparer.Ordinal);
        var leasesByOption = new Dictionary<string, PinnedEvidenceLease>(StringComparer.Ordinal);
        var protectedArtifactLeases = new List<PinnedEvidenceLease>();
        PinnedEvidenceLease? hostLease = null;
        PinnedEvidenceLease? manifestLease = null;
        PinnedEvidenceLease? publicScanLease = null;
        PinnedEvidenceLease? publicCandidateLease = null;
        PublicSurfaceCorpusLease? publicCorpus = null;
        byte[] publicBytes = [];
        string? createdPublicPath = null;
        var completed = false;
        try
        {
            var options = ParseArgs(args);
            var repositoryRoot = ExactRoot(options["--repository-root"]);
            var worktreeRoot = ExactRoot(options["--worktree-root"]);
            var publicLogRoot = ExactRoot(options["--public-log-root"]);
            var root = RestrictedEvidenceRoot.Open(
                options["--restricted-root"],
                options["--destination-identity"],
                [repositoryRoot, worktreeRoot, publicLogRoot]);
            AssertCredentialCopiesAbsent(root.Path);
            protectedScanValues = ReadProtectedScanInput(Console.OpenStandardInput());
            publicCorpus = PublicSurfaceCorpusLease.Open(repositoryRoot, worktreeRoot, publicLogRoot);

            foreach (var option in new[]
            {
                "--source-bundle",
                "--capture-manifest",
                "--post-cleanup-capture-manifest",
                "--oracle-result",
                "--oracle-build-receipt",
                "--ui-attestation",
                "--cleanup-plan",
                "--public-leak-scan",
                "--restricted-package-readback",
                "--oracle-assembly",
                "--production-assembly",
            })
            {
                var maximum = option is "--oracle-assembly" or "--production-assembly"
                    ? EvidenceLimits.MaximumArchiveBytes
                    : EvidenceLimits.MaximumDocumentBytes;
                var lease = root.AcquirePinnedFile(options[option], maximum);
                leases.Add(lease);
                leasesByOption.Add(option, lease);
            }

            var captureLease = leasesByOption["--capture-manifest"];
            var capture = ParseCanonical<CaptureManifestDocument>(captureLease.Bytes);
            if (capture is null || !capture.Finalized ||
                !StringComparer.Ordinal.Equals(
                    capture.DestinationIdentitySha256,
                    root.DestinationIdentitySha256))
            {
                throw new InvalidDataException("assembly_capture_invalid");
            }
            foreach (var source in capture.Sources)
            {
                leases.Add(AcquireExpected(
                    root,
                    source.BodyPath,
                    EvidenceLimits.MaximumDocumentBytes,
                    source.BodySize,
                    source.BodySha256,
                    source.BodyFileIdentity));
            }
            foreach (var artifact in capture.Artifacts)
            {
                var archiveLease = AcquireExpected(
                    root,
                    artifact.ArchivePath,
                    EvidenceLimits.MaximumArchiveBytes,
                    artifact.ArchiveSize,
                    artifact.ArchiveSha256,
                    artifact.ArchiveFileIdentity);
                var encryptedObjectLease = AcquireExpected(
                    root,
                    artifact.EncryptedObjectPath,
                    EvidenceLimits.MaximumEncryptedObjectBytes,
                    artifact.EncryptedObjectSize,
                    artifact.EncryptedObjectSha256,
                    artifact.EncryptedObjectFileIdentity);
                leases.Add(archiveLease);
                leases.Add(encryptedObjectLease);
                protectedArtifactLeases.Add(archiveLease);
                protectedArtifactLeases.Add(encryptedObjectLease);
            }
            var postCleanupCaptureLease = leasesByOption["--post-cleanup-capture-manifest"];
            var postCleanupCapture = ParseCanonical<CaptureManifestDocument>(
                postCleanupCaptureLease.Bytes);
            if (postCleanupCapture is null || !postCleanupCapture.Finalized ||
                postCleanupCapture.Artifacts.Length != 0 ||
                !StringComparer.Ordinal.Equals(
                    postCleanupCapture.DestinationIdentitySha256,
                    root.DestinationIdentitySha256))
            {
                throw new InvalidDataException("assembly_post_cleanup_capture_invalid");
            }
            foreach (var source in postCleanupCapture.Sources)
            {
                leases.Add(AcquireExpected(
                    root,
                    source.BodyPath,
                    EvidenceLimits.MaximumDocumentBytes,
                    source.BodySize,
                    source.BodySha256,
                    source.BodyFileIdentity));
            }

            var result = RunNode(options, repositoryRoot);
            foreach (var lease in leases)
            {
                lease.Validate();
            }
            AssertCredentialCopiesAbsent(root.Path);

            hostLease = root.AcquirePinnedFile(options["--host-output"], EvidenceLimits.MaximumDocumentBytes);
            manifestLease = root.AcquirePinnedFile(
                options["--package-manifest-output"],
                EvidenceLimits.MaximumDocumentBytes);
            publicScanLease = root.AcquirePinnedFile(
                options["--public-scan-output"],
                EvidenceLimits.MaximumDocumentBytes);
            AssertCanonical(hostLease.Bytes);
            AssertCanonical(manifestLease.Bytes);
            AssertCanonical(publicScanLease.Bytes);
            var hostSha256 = CanonicalEvidence.Sha256(hostLease.Bytes);

            var success = SuccessOutput().Match(result);
            var recovery = RecoveryOutput().Match(result);
            var scanCandidateSha256 = ValidatePublicScanManifest(
                publicScanLease.Bytes,
                publicCorpus.Files);
            string? publicPath = null;
            ValidatePrivateManifest(
                manifestLease.Bytes,
                root.DestinationIdentitySha256,
                hostSha256,
                CanonicalEvidence.Sha256(leasesByOption["--source-bundle"].Bytes),
                CanonicalEvidence.Sha256(leasesByOption["--capture-manifest"].Bytes),
                CanonicalEvidence.Sha256(leasesByOption["--post-cleanup-capture-manifest"].Bytes),
                CanonicalEvidence.Sha256(leasesByOption["--oracle-result"].Bytes),
                CanonicalEvidence.Sha256(leasesByOption["--cleanup-plan"].Bytes),
                CanonicalEvidence.Sha256(leasesByOption["--oracle-build-receipt"].Bytes),
                CanonicalEvidence.Sha256(leasesByOption["--oracle-assembly"].Bytes),
                CanonicalEvidence.Sha256(leasesByOption["--production-assembly"].Bytes),
                CanonicalEvidence.Sha256(publicScanLease.Bytes),
                scanCandidateSha256,
                success.Success);
            if (success.Success)
            {
                if (!StringComparer.Ordinal.Equals(success.Groups[1].Value, hostSha256))
                {
                    throw new InvalidDataException("assembly_output_invalid");
                }
                publicCandidateLease = root.AcquirePinnedFile(
                    options["--public-candidate-output"],
                    EvidenceLimits.MaximumDocumentBytes);
                publicBytes = publicCandidateLease.Bytes;
                AssertCanonical(publicBytes);
                var publicSha256 = CanonicalEvidence.Sha256(publicBytes);
                if (!StringComparer.Ordinal.Equals(success.Groups[2].Value, publicSha256) ||
                    !StringComparer.Ordinal.Equals(scanCandidateSha256, publicSha256))
                {
                    throw new InvalidDataException("assembly_output_invalid");
                }
                publicPath = ResolveNewOutput(worktreeRoot, options["--public-output"]);
            }
            else if (!recovery.Success ||
                !StringComparer.Ordinal.Equals(recovery.Groups[1].Value, hostSha256) ||
                File.Exists(ResolveCandidate(worktreeRoot, options["--public-output"])))
            {
                throw new InvalidDataException("assembly_output_invalid");
            }

            var hostOperations = HostOperationIds(hostLease.Bytes);
            protectedScanValues.Add(
                "host_evidence",
                hostOperations.Select(Encoding.UTF8.GetBytes)
                    .Concat(protectedArtifactLeases.Select(lease => lease.Bytes))
                    .ToArray());
            publicCorpus.AssertAbsent(protectedScanValues, publicBytes);
            publicCorpus.AssertExactDocumentAbsent(hostLease.Bytes, publicBytes);
            foreach (var lease in leases)
            {
                publicCorpus.AssertExactDocumentAbsent(lease.Bytes, publicBytes);
            }
            publicCorpus.ValidateComplete(null, []);
            if (publicPath is not null)
            {
                createdPublicPath = publicPath;
                WritePublicCreateNew(publicPath, publicBytes);
                var readback = CanonicalEvidence.ReadPinnedAbsolute(
                    publicPath,
                    EvidenceLimits.MaximumDocumentBytes);
                try
                {
                    if (!readback.Bytes.AsSpan().SequenceEqual(publicBytes))
                    {
                        throw new InvalidDataException("assembly_output_invalid");
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(readback.Bytes);
                }
            }
            publicCorpus.ValidateComplete(publicPath, publicBytes);

            hostLease.Validate();
            manifestLease.Validate();
            publicScanLease.Validate();
            publicCandidateLease?.Validate();
            completed = true;
            Console.Out.Write(result);
            return 0;
        }
        catch (InvalidDataException)
        {
            return Invalid();
        }
        catch (JsonException)
        {
            return Invalid();
        }
        catch (CryptographicException)
        {
            return Invalid();
        }
        catch (IOException)
        {
            return Invalid();
        }
        catch (UnauthorizedAccessException)
        {
            return Invalid();
        }
        catch (Win32Exception)
        {
            return Invalid();
        }
        catch (ArgumentException)
        {
            return Invalid();
        }
        finally
        {
            manifestLease?.Dispose();
            publicScanLease?.Dispose();
            publicCorpus?.Dispose();
            if (!completed && createdPublicPath is not null)
            {
                DeleteFailedPublicOutput(createdPublicPath, publicBytes);
            }
            publicCandidateLease?.Dispose();
            CryptographicOperations.ZeroMemory(publicBytes);
            foreach (var values in protectedScanValues.Values)
            {
                foreach (var value in values)
                {
                    CryptographicOperations.ZeroMemory(value);
                }
            }
            hostLease?.Dispose();
            foreach (var lease in leases)
            {
                lease.Dispose();
            }
        }
    }

    private static int Invalid()
    {
        Console.Error.WriteLine("APR_R4_E3_ASSEMBLY_INVALID");
        return 1;
    }

    internal static Dictionary<string, IReadOnlyList<byte[]>> ReadProtectedScanInput(Stream input)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[4096];
        byte[] bytes = [];
        var result = new Dictionary<string, IReadOnlyList<byte[]>>(StringComparer.Ordinal);
        try
        {
            while (true)
            {
                var read = input.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }
                memory.Write(buffer, 0, read);
                if (memory.Length > 16 * 1024)
                {
                    throw new InvalidDataException("public_scan_input_invalid");
                }
            }
            bytes = memory.ToArray();
            AssertCanonical(bytes);
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            ExactProperties(root, ["kind", "categories"]);
            if (root.GetProperty("kind").GetString() != "apr-r4-e3-public-scan-memory-input-v1")
            {
                throw new InvalidDataException("public_scan_input_invalid");
            }
            var categories = root.GetProperty("categories");
            var names = new[]
            {
                "authorization",
                "state_keys",
                "session_plaintext",
                "provider_content",
                "tool_data",
            };
            ExactProperties(categories, names);
            foreach (var name in names)
            {
                var values = categories.GetProperty(name).EnumerateArray().ToArray();
                if (values.Length is < 1 or > 8)
                {
                    throw new InvalidDataException("public_scan_input_invalid");
                }
                var decoded = new List<byte[]>(values.Length);
                try
                {
                    foreach (var value in values)
                    {
                        if (value.ValueKind != JsonValueKind.String)
                        {
                            throw new InvalidDataException("public_scan_input_invalid");
                        }
                        var candidate = value.GetBytesFromBase64();
                        if (candidate.Length is < 16 or > EvidenceLimits.MaximumCredentialBytes)
                        {
                            CryptographicOperations.ZeroMemory(candidate);
                            throw new InvalidDataException("public_scan_input_invalid");
                        }
                        decoded.Add(candidate);
                    }
                    result.Add(name, decoded);
                }
                catch
                {
                    foreach (var value in decoded)
                    {
                        CryptographicOperations.ZeroMemory(value);
                    }
                    throw;
                }
            }
            return result;
        }
        catch (FormatException)
        {
            ZeroProtectedValues(result);
            throw new InvalidDataException("public_scan_input_invalid");
        }
        catch
        {
            ZeroProtectedValues(result);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            CryptographicOperations.ZeroMemory(bytes);
            CryptographicOperations.ZeroMemory(memory.GetBuffer().AsSpan(0, checked((int)memory.Length)));
        }
    }

    private static void ZeroProtectedValues(
        IReadOnlyDictionary<string, IReadOnlyList<byte[]>> values)
    {
        foreach (var category in values.Values)
        {
            foreach (var value in category)
            {
                CryptographicOperations.ZeroMemory(value);
            }
        }
    }

    private static PinnedEvidenceLease AcquireExpected(
        RestrictedEvidenceRoot root,
        string path,
        int maximumBytes,
        string expectedSize,
        string expectedSha256,
        string expectedIdentity)
    {
        var lease = root.AcquirePinnedFile(path, maximumBytes);
        if (!StringComparer.Ordinal.Equals(expectedSize, lease.Bytes.Length.ToString()) ||
            !StringComparer.Ordinal.Equals(expectedSha256, CanonicalEvidence.Sha256(lease.Bytes)) ||
            !StringComparer.Ordinal.Equals(expectedIdentity, lease.Identity))
        {
            lease.Dispose();
            throw new InvalidDataException("assembly_capture_invalid");
        }
        return lease;
    }

    private static string RunNode(IReadOnlyDictionary<string, string> options, string repositoryRoot)
    {
        var executable = System.IO.Path.GetFullPath(options["--node-executable"]);
        var executableInfo = new FileInfo(executable);
        if (!System.IO.Path.IsPathFullyQualified(options["--node-executable"]) ||
            !executableInfo.Exists ||
            executableInfo.LinkTarget is not null ||
            (executableInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("assembly_node_invalid");
        }
        var script = System.IO.Path.Join(
            repositoryRoot,
            "scripts",
            "assemble-r4-trusted-proof-evidence.mjs");
        var scriptInfo = new FileInfo(script);
        if (!scriptInfo.Exists || scriptInfo.LinkTarget is not null ||
            (scriptInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("assembly_node_invalid");
        }

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            WorkingDirectory = repositoryRoot,
        };
        start.ArgumentList.Add(script);
        foreach (var name in NodeArgumentNames)
        {
            start.ArgumentList.Add(name);
            start.ArgumentList.Add(options[name]);
        }
        start.Environment.Clear();
        if (OperatingSystem.IsWindows())
        {
            start.Environment["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        }

        using var process = Process.Start(start) ??
            throw new InvalidDataException("assembly_node_invalid");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new InvalidDataException("assembly_node_timeout");
        }
        var output = stdout.GetAwaiter().GetResult();
        var error = stderr.GetAwaiter().GetResult();
        if (process.ExitCode != 0 || error.Length != 0 || output.Length > 512)
        {
            throw new InvalidDataException("assembly_node_invalid");
        }
        return output;
    }

    private static T? ParseCanonical<T>(byte[] bytes)
    {
        AssertCanonical(bytes);
        return JsonSerializer.Deserialize<T>(bytes, EvidenceJson.Options);
    }

    private static void AssertCanonical(byte[] bytes)
    {
        if (bytes.Length < 2 || bytes[^1] != (byte)'\n' || bytes.AsSpan().Contains((byte)'\r'))
        {
            throw new InvalidDataException("assembly_document_invalid");
        }
        using var document = JsonDocument.Parse(bytes);
        var canonical = CanonicalEvidence.Encode(document.RootElement, EvidenceJson.Options);
        try
        {
            if (!bytes.AsSpan().SequenceEqual(canonical))
            {
                throw new InvalidDataException("assembly_document_invalid");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
        }
    }

    private static string[] HostOperationIds(byte[] hostBytes)
    {
        using var document = JsonDocument.Parse(hostBytes);
        var values = document.RootElement.GetProperty("identities").GetProperty("operation_ids")
            .EnumerateArray().Select(item => item.GetString() ?? "").ToArray();
        if (values.Length != 2 || values.Any(value => !Regex.IsMatch(value, "^[0-9a-f]{64}$")))
        {
            throw new InvalidDataException("assembly_host_invalid");
        }
        return values;
    }

    private static string ValidatePublicScanManifest(
        byte[] bytes,
        IReadOnlyList<CorpusFileLease> corpus)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        ExactProperties(root, ["kind", "candidate_sha256", "corpus", "results"]);
        var candidate = root.GetProperty("candidate_sha256").GetString() ?? "";
        if (root.GetProperty("kind").GetString() != "apr-r4-e3-public-candidate-scan-v1" ||
            !Regex.IsMatch(candidate, "^[0-9a-f]{64}$"))
        {
            throw new InvalidDataException("assembly_public_scan_invalid");
        }
        var remaining = corpus.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var observed = root.GetProperty("corpus").EnumerateArray().ToArray();
        if (observed.Length != corpus.Count + 1)
        {
            throw new InvalidDataException("assembly_public_scan_invalid");
        }
        var candidateSeen = false;
        foreach (var entry in observed)
        {
            ExactProperties(entry, ["surface_id", "sha256", "size"]);
            var id = entry.GetProperty("surface_id").GetString() ?? "";
            var digest = entry.GetProperty("sha256").GetString() ?? "";
            var size = entry.GetProperty("size").GetString() ?? "";
            if (id == "public-candidate")
            {
                candidateSeen = !candidateSeen && digest == candidate &&
                    long.TryParse(size, out var candidateSize) && candidateSize > 0;
                if (!candidateSeen)
                {
                    throw new InvalidDataException("assembly_public_scan_invalid");
                }
            }
            else if (!remaining.Remove(id, out var file) ||
                digest != CanonicalEvidence.Sha256(file.Bytes) ||
                size != file.Bytes.Length.ToString())
            {
                throw new InvalidDataException("assembly_public_scan_invalid");
            }
        }
        var results = root.GetProperty("results");
        ExactProperties(results, [
            "authorization",
            "state_keys",
            "session_plaintext",
            "provider_content",
            "tool_data",
            "host_evidence",
        ]);
        if (!candidateSeen || remaining.Count != 0 ||
            results.EnumerateObject().Any(item => item.Value.GetString() != "absent"))
        {
            throw new InvalidDataException("assembly_public_scan_invalid");
        }
        return candidate;
    }

    private static void ValidatePrivateManifest(
        byte[] bytes,
        string destinationIdentitySha256,
        string hostSha256,
        string sourceBundleSha256,
        string captureManifestSha256,
        string postCleanupCaptureManifestSha256,
        string oracleResultSha256,
        string cleanupPlanSha256,
        string oracleBuildReceiptSha256,
        string oracleAssemblySha256,
        string productionAssemblySha256,
        string publicScanManifestSha256,
        string publicCandidateSha256,
        bool projectionEligible)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        ExactProperties(root, [
            "kind",
            "destination_identity_sha256",
            "host_evidence_sha256",
            "source_bundle_sha256",
            "capture_manifest_sha256",
            "post_cleanup_capture_manifest_sha256",
            "oracle_result_sha256",
            "oracle_build_receipt_sha256",
            "oracle_assembly_sha256",
            "production_assembly_sha256",
            "public_candidate_sha256",
            "public_scan_manifest_sha256",
            "cleanup_plan_sha256",
            "credential_absence",
            "projection_eligible",
            "finalized",
        ]);
        var credentials = root.GetProperty("credential_absence");
        ExactProperties(credentials, ["github_token", "current_state_key", "previous_state_key"]);
        if (root.GetProperty("kind").GetString() != "apr-r4-e3-private-package-manifest-v1" ||
            root.GetProperty("destination_identity_sha256").GetString() != destinationIdentitySha256 ||
            root.GetProperty("host_evidence_sha256").GetString() != hostSha256 ||
            root.GetProperty("source_bundle_sha256").GetString() != sourceBundleSha256 ||
            root.GetProperty("capture_manifest_sha256").GetString() != captureManifestSha256 ||
            root.GetProperty("post_cleanup_capture_manifest_sha256").GetString() !=
                postCleanupCaptureManifestSha256 ||
            root.GetProperty("oracle_result_sha256").GetString() != oracleResultSha256 ||
            root.GetProperty("oracle_build_receipt_sha256").GetString() != oracleBuildReceiptSha256 ||
            root.GetProperty("oracle_assembly_sha256").GetString() != oracleAssemblySha256 ||
            root.GetProperty("production_assembly_sha256").GetString() != productionAssemblySha256 ||
            root.GetProperty("public_candidate_sha256").GetString() != publicCandidateSha256 ||
            root.GetProperty("public_scan_manifest_sha256").GetString() != publicScanManifestSha256 ||
            root.GetProperty("cleanup_plan_sha256").GetString() != cleanupPlanSha256 ||
            credentials.EnumerateObject().Any(item => item.Value.ValueKind != JsonValueKind.True) ||
            root.GetProperty("projection_eligible").GetBoolean() != projectionEligible ||
            !root.GetProperty("finalized").GetBoolean())
        {
            throw new InvalidDataException("assembly_private_manifest_invalid");
        }
    }

    private static void ExactProperties(JsonElement value, string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !value.EnumerateObject().Select(item => item.Name).SequenceEqual(names, StringComparer.Ordinal))
        {
            throw new InvalidDataException("assembly_private_manifest_invalid");
        }
    }

    private static string ExactRoot(string value)
    {
        if (!System.IO.Path.IsPathFullyQualified(value))
        {
            throw new InvalidDataException("assembly_root_invalid");
        }
        var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(value));
        var directory = new DirectoryInfo(full);
        if (!directory.Exists || directory.LinkTarget is not null ||
            (directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("assembly_root_invalid");
        }
        return full;
    }

    private static string ResolveCandidate(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || System.IO.Path.IsPathFullyQualified(relative) ||
            Encoding.UTF8.GetByteCount(relative) > EvidenceLimits.MaximumRelativePathBytes)
        {
            throw new InvalidDataException("assembly_output_invalid");
        }
        var candidate = System.IO.Path.GetFullPath(System.IO.Path.Join(root, relative));
        if (!RestrictedEvidenceRoot.IsWithin(candidate, root) ||
            StringComparer.OrdinalIgnoreCase.Equals(candidate, root))
        {
            throw new InvalidDataException("assembly_output_invalid");
        }
        return candidate;
    }

    private static string ResolveNewOutput(string root, string relative)
    {
        var candidate = ResolveCandidate(root, relative);
        var parent = new DirectoryInfo(System.IO.Path.GetDirectoryName(candidate)!);
        if (File.Exists(candidate) || Directory.Exists(candidate) || !parent.Exists)
        {
            throw new InvalidDataException("assembly_output_invalid");
        }
        for (var current = parent; current is not null &&
            RestrictedEvidenceRoot.IsWithin(current.FullName, root); current = current.Parent)
        {
            if (current.LinkTarget is not null ||
                (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("assembly_output_invalid");
            }
            if (StringComparer.OrdinalIgnoreCase.Equals(current.FullName, root))
            {
                return candidate;
            }
        }
        throw new InvalidDataException("assembly_output_invalid");
    }

    internal static void WritePublicCreateNew(string path, ReadOnlySpan<byte> bytes)
    {
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(bytes);
        output.Flush(flushToDisk: true);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    internal static void DeleteFailedPublicOutput(string path, ReadOnlySpan<byte> expected)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.LinkTarget is not null ||
                (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return;
            }
            var current = File.ReadAllBytes(path);
            try
            {
                if (current.Length <= expected.Length &&
                    expected[..current.Length].SequenceEqual(current))
                {
                    File.Delete(path);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(current);
            }
        }
        catch
        {
        }
    }

    private static void AssertCredentialCopiesAbsent(string root)
    {
        if (new[] { "github-token", "current-state-key", "previous-state-key" }
            .Any(name => File.Exists(System.IO.Path.Join(root, name))))
        {
            throw new InvalidDataException("assembly_credential_copy_invalid");
        }
    }

    private static IReadOnlyDictionary<string, string> ParseArgs(string[] args)
    {
        var allowed = NodeArgumentNames.Append("--node-executable").Append("--public-output")
            .ToHashSet(StringComparer.Ordinal);
        if (args.Length != allowed.Count * 2)
        {
            throw new InvalidDataException("assembly_arguments_invalid");
        }
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (!allowed.Contains(args[index]) || !result.TryAdd(args[index], args[index + 1]))
            {
                throw new InvalidDataException("assembly_arguments_invalid");
            }
        }
        if (allowed.Any(name => !result.ContainsKey(name)))
        {
            throw new InvalidDataException("assembly_arguments_invalid");
        }
        return result;
    }

    private static readonly string[] NodeArgumentNames =
    [
        "--restricted-root",
        "--destination-identity",
        "--repository-root",
        "--worktree-root",
        "--public-log-root",
        "--source-bundle",
        "--capture-manifest",
        "--post-cleanup-capture-manifest",
        "--oracle-result",
        "--oracle-build-receipt",
        "--ui-attestation",
        "--cleanup-plan",
        "--public-leak-scan",
        "--restricted-package-readback",
        "--oracle-assembly",
        "--production-assembly",
        "--host-output",
        "--package-manifest-output",
        "--public-scan-output",
        "--public-candidate-output",
    ];

    [GeneratedRegex("^APR_R4_E3_ASSEMBLY_OK ([0-9a-f]{64}) ([0-9a-f]{64})\\n$", RegexOptions.CultureInvariant)]
    private static partial Regex SuccessOutput();

    [GeneratedRegex("^APR_R4_E3_ASSEMBLY_RECOVERY_ONLY ([0-9a-f]{64})\\n$", RegexOptions.CultureInvariant)]
    private static partial Regex RecoveryOutput();
}

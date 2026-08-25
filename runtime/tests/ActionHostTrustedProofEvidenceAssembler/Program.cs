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
        PinnedEvidenceLease? hostLease = null;
        PinnedEvidenceLease? manifestLease = null;
        try
        {
            var options = ParseArgs(args);
            var repositoryRoot = ExactRoot(options["--repository-root"]);
            var worktreeRoot = ExactRoot(options["--worktree-root"]);
            var root = RestrictedEvidenceRoot.Open(
                options["--restricted-root"],
                options["--destination-identity"],
                [repositoryRoot, worktreeRoot]);
            AssertCredentialCopiesAbsent(root.Path);

            foreach (var option in new[]
            {
                "--source-bundle",
                "--capture-manifest",
                "--oracle-result",
                "--oracle-build-receipt",
                "--ui-attestation",
                "--cleanup-plan",
                "--cleanup-readbacks",
                "--public-leak-scan",
                "--restricted-package-readback",
            })
            {
                leases.Add(root.AcquirePinnedFile(options[option], EvidenceLimits.MaximumDocumentBytes));
            }

            var captureLease = leases[1];
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
                leases.Add(AcquireExpected(
                    root,
                    artifact.ArchivePath,
                    EvidenceLimits.MaximumArchiveBytes,
                    artifact.ArchiveSize,
                    artifact.ArchiveSha256,
                    artifact.ArchiveFileIdentity));
                leases.Add(AcquireExpected(
                    root,
                    artifact.EncryptedObjectPath,
                    EvidenceLimits.MaximumEncryptedObjectBytes,
                    artifact.EncryptedObjectSize,
                    artifact.EncryptedObjectSha256,
                    artifact.EncryptedObjectFileIdentity));
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
            AssertCanonical(hostLease.Bytes);
            AssertCanonical(manifestLease.Bytes);
            var hostSha256 = CanonicalEvidence.Sha256(hostLease.Bytes);

            var success = SuccessOutput().Match(result);
            var recovery = RecoveryOutput().Match(result);
            ValidatePrivateManifest(
                manifestLease.Bytes,
                root.DestinationIdentitySha256,
                hostSha256,
                CanonicalEvidence.Sha256(leases[0].Bytes),
                CanonicalEvidence.Sha256(leases[1].Bytes),
                CanonicalEvidence.Sha256(leases[2].Bytes),
                CanonicalEvidence.Sha256(leases[5].Bytes),
                success.Success);
            if (success.Success)
            {
                if (!StringComparer.Ordinal.Equals(success.Groups[1].Value, hostSha256))
                {
                    throw new InvalidDataException("assembly_output_invalid");
                }
                var publicPath = ResolveNewReadback(worktreeRoot, options["--public-output"]);
                var publicFile = CanonicalEvidence.ReadPinnedAbsolute(
                    publicPath,
                    EvidenceLimits.MaximumDocumentBytes);
                var publicBytes = publicFile.Bytes;
                try
                {
                    AssertCanonical(publicBytes);
                    if (!StringComparer.Ordinal.Equals(
                            success.Groups[2].Value,
                            CanonicalEvidence.Sha256(publicBytes)))
                    {
                        throw new InvalidDataException("assembly_output_invalid");
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(publicBytes);
                }
            }
            else if (!recovery.Success ||
                !StringComparer.Ordinal.Equals(recovery.Groups[1].Value, hostSha256) ||
                File.Exists(ResolveCandidate(worktreeRoot, options["--public-output"])))
            {
                throw new InvalidDataException("assembly_output_invalid");
            }

            hostLease.Validate();
            manifestLease.Validate();
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

    private static void ValidatePrivateManifest(
        byte[] bytes,
        string destinationIdentitySha256,
        string hostSha256,
        string sourceBundleSha256,
        string captureManifestSha256,
        string oracleResultSha256,
        string cleanupPlanSha256,
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
            "oracle_result_sha256",
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
            root.GetProperty("oracle_result_sha256").GetString() != oracleResultSha256 ||
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

    private static string ResolveNewReadback(string root, string relative)
    {
        var candidate = ResolveCandidate(root, relative);
        var file = new FileInfo(candidate);
        if (!file.Exists || file.LinkTarget is not null ||
            (file.Attributes & FileAttributes.ReparsePoint) != 0 ||
            file.Length is < 1 or > EvidenceLimits.MaximumDocumentBytes)
        {
            throw new InvalidDataException("assembly_output_invalid");
        }
        for (var current = file.Directory; current is not null &&
            RestrictedEvidenceRoot.IsWithin(current.FullName, root); current = current.Parent)
        {
            if (current.LinkTarget is not null ||
                (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("assembly_output_invalid");
            }
            if (StringComparer.OrdinalIgnoreCase.Equals(current.FullName, root))
            {
                break;
            }
        }
        return candidate;
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
        var allowed = NodeArgumentNames.Append("--node-executable").ToHashSet(StringComparer.Ordinal);
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
        "--source-bundle",
        "--capture-manifest",
        "--oracle-result",
        "--oracle-build-receipt",
        "--ui-attestation",
        "--cleanup-plan",
        "--cleanup-readbacks",
        "--public-leak-scan",
        "--restricted-package-readback",
        "--host-output",
        "--package-manifest-output",
        "--public-output",
    ];

    [GeneratedRegex("^APR_R4_E3_ASSEMBLY_OK ([0-9a-f]{64}) ([0-9a-f]{64})\\n$", RegexOptions.CultureInvariant)]
    private static partial Regex SuccessOutput();

    [GeneratedRegex("^APR_R4_E3_ASSEMBLY_RECOVERY_ONLY ([0-9a-f]{64})\\n$", RegexOptions.CultureInvariant)]
    private static partial Regex RecoveryOutput();
}

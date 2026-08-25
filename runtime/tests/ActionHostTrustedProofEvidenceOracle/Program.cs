using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceContracts;
using AgenticPrReview.Runtime.Host.State.Evidence;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceOracle;

internal sealed record OracleRecord(
    string ArtifactId,
    string Role,
    string Scope,
    string ObjectClass,
    string ObjectIdentity,
    string ProducingRunIdentity,
    string ProducingRunAttempt,
    string PayloadSha256);

internal sealed record OracleDocument(
    string Kind,
    string CaptureManifestSha256,
    bool ExactSevenSuccess,
    bool RecoveryOnly,
    OracleRecord[] Records);

internal static class Program
{
    private static int Main(string[] args)
    {
        byte[] current = [];
        byte[]? previous = null;
        RestrictedEvidenceRoot? root = null;
        string? currentCredentialPath = null;
        string? previousCredentialPath = null;
        try
        {
            var options = Parse(args);
            root = RestrictedEvidenceRoot.Open(
                options["--restricted-root"],
                options["--destination-identity"],
                [options["--repository-root"], options["--worktree-root"]]);
            var manifestPath = root.ResolveExistingFile(
                options["--capture-manifest"],
                EvidenceLimits.MaximumDocumentBytes);
            var manifestBytes = File.ReadAllBytes(manifestPath);
            CaptureManifestDocument manifest;
            try
            {
                if (!StringComparer.Ordinal.Equals(
                        CanonicalEvidence.Sha256(manifestBytes),
                        options["--capture-manifest-sha256"]))
                {
                    throw new InvalidDataException("capture_manifest_digest_invalid");
                }

                manifest = JsonSerializer.Deserialize<CaptureManifestDocument>(
                    manifestBytes,
                    EvidenceJson.Options) ??
                    throw new InvalidDataException("capture_manifest_invalid");
                var canonical = CanonicalEvidence.Encode(manifest, EvidenceJson.Options);
                try
                {
                    if (!manifestBytes.AsSpan().SequenceEqual(canonical) ||
                        !manifest.Finalized ||
                        !StringComparer.Ordinal.Equals(
                            manifest.DestinationIdentitySha256,
                            root.DestinationIdentitySha256))
                    {
                        throw new InvalidDataException("capture_manifest_invalid");
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(canonical);
                }
                if (manifest.Kind != "apr-r4-e3-capture-manifest-v1" ||
                    manifest.OperationIds.Length != 2 ||
                    manifest.OperationIds.Distinct(StringComparer.Ordinal).Count() != 2 ||
                    manifest.Sources.Length == 0 ||
                    manifest.Artifacts.Length == 0 ||
                    manifest.Artifacts.Select(item => item.ArtifactId).Distinct(StringComparer.Ordinal).Count() != manifest.Artifacts.Length ||
                    manifest.Artifacts.Select(item => item.ArtifactName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Artifacts.Length)
                {
                    throw new InvalidDataException("capture_manifest_invalid");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(manifestBytes);
            }

            currentCredentialPath = options["--current-state-key-file"];
            current = root.ReadCredentialFile(
                currentCredentialPath,
                base64Key: true);
            if (options.TryGetValue("--previous-state-key-file", out var previousPath))
            {
                previousCredentialPath = previousPath;
                previous = root.ReadCredentialFile(previousCredentialPath, base64Key: true);
                if (CryptographicOperations.FixedTimeEquals(current, previous))
                {
                    throw new InvalidDataException("state_keys_duplicate");
                }
            }

            var encrypted = new List<TrustedProofEncryptedArtifact>(manifest.Artifacts.Length);
            foreach (var artifact in manifest.Artifacts)
            {
                var objectPath = ResolvePackageFile(root, manifestPath, artifact.EncryptedObjectPath);
                var bytes = File.ReadAllBytes(objectPath);
                if (!StringComparer.Ordinal.Equals(
                        CanonicalEvidence.Sha256(bytes),
                        artifact.EncryptedObjectSha256) ||
                    !StringComparer.Ordinal.Equals(bytes.Length.ToString(), artifact.EncryptedObjectSize))
                {
                    CryptographicOperations.ZeroMemory(bytes);
                    throw new InvalidDataException("capture_object_invalid");
                }

                encrypted.Add(new TrustedProofEncryptedArtifact(
                    artifact.ArtifactId,
                    artifact.ExpectedRole,
                    artifact.Scope,
                    artifact.OpaqueName,
                    bytes));
            }

            try
            {
                if (!TrustedProofEvidenceCodecOracle.TryDecode(
                        manifest.RepositoryId,
                        Convert.ToBase64String(current),
                        previous is null ? null : Convert.ToBase64String(previous),
                        encrypted,
                        out var decoded) ||
                    decoded is null)
                {
                    throw new InvalidDataException("codec_oracle_invalid");
                }

                var document = new OracleDocument(
                    "apr-r4-e3-production-codec-oracle-result-v1",
                    options["--capture-manifest-sha256"],
                    decoded.ExactSevenSuccess,
                    decoded.RecoveryOnly,
                    decoded.Records.Select(record => new OracleRecord(
                        record.ArtifactId,
                        record.Role,
                        record.Scope,
                        record.ObjectClass,
                        record.ObjectIdentity,
                        record.ProducingRunIdentity,
                        record.ProducingRunAttempt.ToString(),
                        record.PayloadSha256)).ToArray());
                var output = CanonicalEvidence.Encode(document, EvidenceJson.Options);
                try
                {
                    CanonicalEvidence.WriteCreateNew(
                        System.IO.Path.Combine(
                            System.IO.Path.GetDirectoryName(manifestPath)!,
                            options["--output"]),
                        output);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(output);
                }

                root.RemoveCredentialFile(currentCredentialPath);
                currentCredentialPath = null;
                if (previousCredentialPath is not null)
                {
                    root.RemoveCredentialFile(previousCredentialPath);
                    previousCredentialPath = null;
                }

                Console.Out.WriteLine("APR_R4_E3_CODEC_ORACLE_OK");
                return 0;
            }
            finally
            {
                foreach (var item in encrypted)
                {
                    CryptographicOperations.ZeroMemory(item.Envelope);
                }
            }
        }
        catch
        {
            Console.Error.WriteLine("APR_R4_E3_CODEC_ORACLE_INVALID");
            return 1;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(current);
            if (previous is not null)
            {
                CryptographicOperations.ZeroMemory(previous);
            }
            if (root is not null && currentCredentialPath is not null)
            {
                TryRemoveCredentialFile(root, currentCredentialPath);
            }
            if (root is not null && previousCredentialPath is not null)
            {
                TryRemoveCredentialFile(root, previousCredentialPath);
            }
        }
    }

    private static void TryRemoveCredentialFile(RestrictedEvidenceRoot root, string relativePath)
    {
        try
        {
            root.RemoveCredentialFile(relativePath);
        }
        catch
        {
            // Failure is already terminal; do not replace the stable non-leaking error marker.
        }
    }

    private static string ResolvePackageFile(
        RestrictedEvidenceRoot root,
        string manifestPath,
        string relativePath)
    {
        if (System.IO.Path.IsPathFullyQualified(relativePath))
        {
            throw new InvalidDataException("capture_object_path_invalid");
        }

        var candidate = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(System.IO.Path.GetDirectoryName(manifestPath)!, relativePath));
        var packagePath = System.IO.Path.GetDirectoryName(manifestPath)!;
        if (!RestrictedEvidenceRoot.IsWithin(candidate, packagePath))
        {
            throw new InvalidDataException("capture_object_path_invalid");
        }
        var rootRelative = System.IO.Path.GetRelativePath(root.Path, candidate);
        return root.ResolveExistingFile(rootRelative, EvidenceLimits.MaximumEncryptedObjectBytes);
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var required = new[]
        {
            "--restricted-root",
            "--destination-identity",
            "--repository-root",
            "--worktree-root",
            "--capture-manifest",
            "--capture-manifest-sha256",
            "--current-state-key-file",
            "--output",
        };
        var allowed = required.Append("--previous-state-key-file").ToHashSet(StringComparer.Ordinal);
        if (args.Length % 2 != 0)
        {
            throw new InvalidDataException("arguments_invalid");
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (!allowed.Contains(args[index]) || !result.TryAdd(args[index], args[index + 1]))
            {
                throw new InvalidDataException("arguments_invalid");
            }
        }

        if (required.Any(name => !result.ContainsKey(name)) ||
            result["--output"].IndexOfAny(['/', '\\', ':']) >= 0 ||
            !StringComparer.Ordinal.Equals(
                result["--current-state-key-file"],
                "current-state-key") ||
            (result.TryGetValue("--previous-state-key-file", out var previous) &&
                !StringComparer.Ordinal.Equals(previous, "previous-state-key")))
        {
            throw new InvalidDataException("arguments_invalid");
        }

        return result;
    }
}

using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceContracts;
using AgenticPrReview.Runtime.Host.State.Evidence;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceOracle;

internal sealed record OracleRecord(
    string ArtifactId,
    string Role,
    string Scope,
    string BaseScopeDigest,
    string ObjectClass,
    string ObjectIdentity,
    string ProducingRunIdentity,
    string ProducingRunAttempt,
    string OperationId,
    string OwnershipEvidenceSha256,
    string PayloadSha256);

internal sealed record OracleOwnershipEvidence(
    string ArtifactId,
    string ArtifactName,
    string Scope,
    string ObjectClass,
    string OperationId,
    string ProducingRunId,
    string ProducingRunAttempt,
    string ArchiveSha256,
    string EncryptedObjectSha256,
    string EncryptedObjectSize);

internal sealed record OracleDocument(
    string Kind,
    string CaptureManifestSha256,
    string OracleSourceSha,
    string OracleSourceTree,
    string OracleAssemblySha256,
    string ProductionAssemblySha256,
    bool ExactSevenSuccess,
    bool RecoveryOnly,
    OracleRecord[] Records);

internal static class Program
{
    private static int Main(string[] args)
    {
        if (CredentialLeaseAuthorityClient.IsGuardianCommand(args))
        {
            return CredentialLeaseAuthorityClient.RunGuardianAsync(args).GetAwaiter().GetResult();
        }
        byte[] current = [];
        byte[]? previous = null;
        RestrictedEvidenceRoot? root = null;
        string? currentCredentialPath = null;
        string? previousCredentialPath = null;
        var credentialLeaseLaunchAttempted = false;
        try
        {
            var options = Parse(args);
            var oracleAssembly = Assembly.GetExecutingAssembly();
            var productionAssembly = typeof(TrustedProofEvidenceCodecOracle).Assembly;
            var sourceSha = AssemblyMetadata(oracleAssembly, "TrustedProofOracleSourceSha");
            var sourceTree = AssemblyMetadata(oracleAssembly, "TrustedProofOracleSourceTree");
            if (!Sha(options["--oracle-source-sha"], 40) ||
                !Sha(options["--oracle-source-tree"], 40) ||
                !StringComparer.Ordinal.Equals(sourceSha, options["--oracle-source-sha"]) ||
                !StringComparer.Ordinal.Equals(sourceTree, options["--oracle-source-tree"]))
            {
                throw new InvalidDataException("oracle_source_identity_invalid");
            }
            root = RestrictedEvidenceRoot.Open(
                options["--restricted-root"],
                options["--destination-identity"],
                [options["--repository-root"], options["--worktree-root"]]);
            var manifestPath = root.ResolveExistingFile(
                options["--capture-manifest"],
                EvidenceLimits.MaximumDocumentBytes);
            var pinnedManifest = root.ReadPinnedFile(
                System.IO.Path.GetRelativePath(root.Path, manifestPath),
                EvidenceLimits.MaximumDocumentBytes);
            var manifestBytes = pinnedManifest.Bytes;
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
                     manifest.OperationRuns.Length != 4 ||
                     manifest.OperationRuns.Select(item => item.RunId).Distinct(StringComparer.Ordinal).Count() != 4 ||
                     manifest.OperationRuns.Any(item =>
                         !manifest.OperationIds.Contains(item.OperationId, StringComparer.Ordinal) ||
                         !new[] { "normal", "stale" }.Contains(item.Scope, StringComparer.Ordinal) ||
                         !PositiveDecimal(item.RunId) ||
                         item.RunAttempt != "1") ||
                     manifest.OperationRuns.GroupBy(item => item.OperationId, StringComparer.Ordinal)
                         .Any(group => group.Count() != 2 ||
                             group.Select(item => item.Scope).Distinct(StringComparer.Ordinal).Count() != 1) ||
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
            using var currentRepresentations = root.ReadCredentialFileRepresentations(
                currentCredentialPath,
                base64Key: true);
            current = currentRepresentations.DecodedKey!.ToArray();
            CredentialFileRepresentations? previousRepresentations = null;
            if (options.TryGetValue("--previous-state-key-file", out var previousPath))
            {
                previousCredentialPath = previousPath;
                previousRepresentations = root.ReadCredentialFileRepresentations(
                    previousCredentialPath,
                    base64Key: true);
                previous = previousRepresentations.DecodedKey!.ToArray();
                if (CryptographicOperations.FixedTimeEquals(current, previous))
                {
                    previousRepresentations.Dispose();
                    throw new InvalidDataException("state_keys_duplicate");
                }
            }
            try
            {
                var specs = new List<CredentialLeaseSpec>
                {
                    new(currentCredentialPath, Base64Key: true),
                };
                var representations = new List<CredentialFileRepresentations>
                {
                    currentRepresentations,
                };
                if (previousCredentialPath is not null && previousRepresentations is not null)
                {
                    specs.Add(new(previousCredentialPath, Base64Key: true));
                    representations.Add(previousRepresentations);
                }
                credentialLeaseLaunchAttempted = true;
                CredentialLeaseAuthorityClient.LaunchCurrentProcess(
                    root,
                    options["--destination-identity"],
                    [options["--repository-root"], options["--worktree-root"]],
                    CredentialLeaseAuthorityClient.StateKeyDescriptorName,
                    specs,
                    representations);
            }
            finally
            {
                previousRepresentations?.Dispose();
            }

            var encrypted = new List<TrustedProofEncryptedArtifact>(manifest.Artifacts.Length);
            try
            {
                foreach (var source in manifest.Sources)
                {
                    var sourcePath = ResolvePackageFile(
                        root,
                        manifestPath,
                        source.BodyPath,
                        EvidenceLimits.MaximumDocumentBytes);
                    var pinnedSource = root.ReadPinnedFile(
                        System.IO.Path.GetRelativePath(root.Path, sourcePath),
                        EvidenceLimits.MaximumDocumentBytes);
                    try
                    {
                        if (!StringComparer.Ordinal.Equals(CanonicalEvidence.Sha256(pinnedSource.Bytes), source.BodySha256) ||
                            !StringComparer.Ordinal.Equals(pinnedSource.Bytes.Length.ToString(), source.BodySize) ||
                            !StringComparer.Ordinal.Equals(pinnedSource.Identity, source.BodyFileIdentity))
                        {
                            throw new InvalidDataException("capture_source_invalid");
                        }
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(pinnedSource.Bytes);
                    }
                }
                foreach (var artifact in manifest.Artifacts)
                {
                    var archivePath = ResolvePackageFile(
                        root,
                        manifestPath,
                        artifact.ArchivePath,
                        EvidenceLimits.MaximumArchiveBytes);
                    var objectPath = ResolvePackageFile(
                        root,
                        manifestPath,
                        artifact.EncryptedObjectPath,
                        EvidenceLimits.MaximumEncryptedObjectBytes);
                    var archive = root.ReadPinnedFile(
                        System.IO.Path.GetRelativePath(root.Path, archivePath),
                        EvidenceLimits.MaximumArchiveBytes);
                    try
                    {
                        var encryptedObject = root.ReadPinnedFile(
                            System.IO.Path.GetRelativePath(root.Path, objectPath),
                            EvidenceLimits.MaximumEncryptedObjectBytes);
                        var retained = false;
                        try
                        {
                            if (!StringComparer.Ordinal.Equals(CanonicalEvidence.Sha256(archive.Bytes), artifact.ArchiveSha256) ||
                                !StringComparer.Ordinal.Equals(archive.Bytes.Length.ToString(), artifact.ArchiveSize) ||
                                !StringComparer.Ordinal.Equals(archive.Identity, artifact.ArchiveFileIdentity) ||
                                !StringComparer.Ordinal.Equals(CanonicalEvidence.Sha256(encryptedObject.Bytes), artifact.EncryptedObjectSha256) ||
                                !StringComparer.Ordinal.Equals(encryptedObject.Bytes.Length.ToString(), artifact.EncryptedObjectSize) ||
                                !StringComparer.Ordinal.Equals(encryptedObject.Identity, artifact.EncryptedObjectFileIdentity))
                            {
                                throw new InvalidDataException("capture_object_invalid");
                            }

                             encrypted.Add(new TrustedProofEncryptedArtifact(
                                 artifact.ArtifactId,
                                 artifact.ArtifactName,
                                 artifact.ProducingRunId,
                                 long.Parse(artifact.ProducingRunAttempt),
                                 encryptedObject.Bytes));
                            retained = true;
                        }
                        finally
                        {
                            if (!retained)
                            {
                                CryptographicOperations.ZeroMemory(encryptedObject.Bytes);
                            }
                        }
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(archive.Bytes);
                    }
                }
                if (!TrustedProofEvidenceCodecOracle.TryDecode(
                        manifest.RepositoryId,
                        Convert.ToBase64String(current),
                         previous is null ? null : Convert.ToBase64String(previous),
                         encrypted,
                         manifest.OperationRuns.Select(item => new TrustedProofOperationRun(
                             item.OperationId,
                             item.Scope,
                             item.RunId,
                             long.Parse(item.RunAttempt))).ToArray(),
                         out var decoded) ||
                    decoded is null)
                {
                    throw new InvalidDataException("codec_oracle_invalid");
                }

                var oracleAssemblySha256 = AssemblyDigest(oracleAssembly);
                var productionAssemblySha256 = AssemblyDigest(productionAssembly);
                var document = new OracleDocument(
                    "apr-r4-e3-production-codec-oracle-result-v1",
                    options["--capture-manifest-sha256"],
                    sourceSha,
                    sourceTree,
                    oracleAssemblySha256,
                    productionAssemblySha256,
                    decoded.ExactSevenSuccess,
                    decoded.RecoveryOnly,
                     decoded.Records.Select(record =>
                     {
                         var artifact = manifest.Artifacts.Single(item =>
                             StringComparer.Ordinal.Equals(item.ArtifactId, record.ArtifactId));
                         return new OracleRecord(
                             record.ArtifactId,
                             record.Role,
                             record.Scope,
                             record.BaseScopeDigest,
                             record.ObjectClass,
                             record.ObjectIdentity,
                             record.ProducingRunIdentity,
                             record.ProducingRunAttempt.ToString(),
                             record.OperationId,
                             OwnershipEvidenceSha256(record, artifact),
                             record.PayloadSha256);
                     }).ToArray());
                var output = CanonicalEvidence.Encode(document, EvidenceJson.Options);
                try
                {
                    var packagePath = System.IO.Path.GetDirectoryName(manifestPath)!;
                    CanonicalEvidence.WriteCreateNew(
                        RestrictedEvidenceRoot.ResolveChildPath(
                            packagePath,
                            options["--output"]),
                        output);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(output);
                }

                currentCredentialPath = null;
                if (previousCredentialPath is not null)
                {
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
        finally
        {
            CryptographicOperations.ZeroMemory(current);
            if (previous is not null)
            {
                CryptographicOperations.ZeroMemory(previous);
            }
            if (root is not null && currentCredentialPath is not null)
            {
                if (credentialLeaseLaunchAttempted)
                {
                    try
                    {
                        var specs = new List<CredentialLeaseSpec>
                        {
                            new(currentCredentialPath, Base64Key: true),
                        };
                        if (previousCredentialPath is not null)
                        {
                            specs.Add(new(previousCredentialPath, Base64Key: true));
                        }
                        CredentialLeaseAuthorityClient.DeleteAbandoned(
                            root,
                            CredentialLeaseAuthorityClient.StateKeyDescriptorName,
                            specs);
                    }
                    catch (Exception exception) when (
                        exception is InvalidDataException or IOException or UnauthorizedAccessException)
                    {
                        // Never fall back to pathname deletion after lease authority was attempted.
                    }
                }
                else
                {
                    TryRemoveCredentialFile(root, currentCredentialPath);
                }
            }
            if (root is not null && previousCredentialPath is not null &&
                !credentialLeaseLaunchAttempted)
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
        catch (InvalidDataException)
        {
            // Failure is already terminal; do not replace the stable non-leaking error marker.
        }
        catch (IOException)
        {
            // Failure is already terminal; do not replace the stable non-leaking error marker.
        }
        catch (UnauthorizedAccessException)
        {
            // Failure is already terminal; do not replace the stable non-leaking error marker.
        }
    }

    private static string ResolvePackageFile(
        RestrictedEvidenceRoot root,
        string manifestPath,
        string relativePath,
        int maximumBytes)
    {
        var packagePath = System.IO.Path.GetDirectoryName(manifestPath)!;
        var candidate = RestrictedEvidenceRoot.ResolveChildPath(packagePath, relativePath);
        var rootRelative = System.IO.Path.GetRelativePath(root.Path, candidate);
        return root.ResolveExistingFile(rootRelative, maximumBytes);
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
            "--oracle-source-sha",
            "--oracle-source-tree",
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
            !RestrictedEvidenceRoot.IsSinglePathSegment(result["--output"]) ||
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

    private static string AssemblyMetadata(Assembly assembly, string key)
    {
        var matches = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(attribute => StringComparer.Ordinal.Equals(attribute.Key, key))
            .ToArray();
        if (matches.Length != 1 || matches[0].Value is not { } value)
        {
            throw new InvalidDataException("oracle_source_identity_invalid");
        }
        return value;
    }

    private static string AssemblyDigest(Assembly assembly)
    {
        if (string.IsNullOrEmpty(assembly.Location))
        {
            throw new InvalidDataException("oracle_assembly_identity_invalid");
        }
        using var stream = new FileStream(
            assembly.Location,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static bool Sha(string value, int length) =>
        value.Length == length &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool PositiveDecimal(string value) =>
        value.Length is > 0 and <= 20 &&
        value.All(character => character is >= '0' and <= '9') &&
        (value.Length == 1 || value[0] != '0') &&
        ulong.TryParse(value, out var parsed) && parsed > 0;

    private static string OwnershipEvidenceSha256(
        TrustedProofDecodedArtifact record,
        CaptureManifestArtifact artifact)
    {
        var document = new OracleOwnershipEvidence(
            artifact.ArtifactId,
            artifact.ArtifactName,
            record.Scope,
            record.ObjectClass,
            record.OperationId,
            artifact.ProducingRunId,
            artifact.ProducingRunAttempt,
            artifact.ArchiveSha256,
            artifact.EncryptedObjectSha256,
            artifact.EncryptedObjectSize);
        var bytes = CanonicalEvidence.Encode(document, EvidenceJson.Options);
        try
        {
            return CanonicalEvidence.Sha256(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static int Invalid()
    {
        Console.Error.WriteLine("APR_R4_E3_CODEC_ORACLE_INVALID");
        return 1;
    }
}

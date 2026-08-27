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
        RestrictedEvidenceRoot? root = null;
        var credentialPaths = new List<string>();
        PinnedEvidenceLease? hostLease = null;
        PinnedEvidenceLease? manifestLease = null;
        PinnedEvidenceLease? publicScanLease = null;
        PinnedEvidenceLease? publicCandidateLease = null;
        PublicSurfaceCorpusLease? publicCorpus = null;
        CredentialLeaseAuthorityClient? githubCredentialAuthority = null;
        CredentialLeaseAuthorityClient? stateKeyCredentialAuthority = null;
        CredentialLeaseValue[] leasedCredentialValues = [];
        byte[] publicBytes = [];
        CreatedEvidenceFileReceipt? createdPublicOutput = null;
        try
        {
            var options = ParseArgs(args);
            var repositoryRoot = ExactRoot(options["--repository-root"]);
            var worktreeRoot = ExactRoot(options["--worktree-root"]);
            var publicLogRoot = ExactRoot(options["--public-log-root"]);
            root = RestrictedEvidenceRoot.Open(
                options["--restricted-root"],
                options["--destination-identity"],
                [repositoryRoot, worktreeRoot, publicLogRoot]);
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
                "--cleanup-execution",
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
            protectedScanValues = ReadProtectedScanInput(
                Console.OpenStandardInput(),
                root,
                capture);
            var authorizedCredentialIdentities = AuthorizedCredentialIdentities(root, capture);
            AppendLeasedActualCredentialValues(
                protectedScanValues,
                root,
                options,
                authorizedCredentialIdentities,
                credentialPaths,
                out githubCredentialAuthority,
                out stateKeyCredentialAuthority,
                out leasedCredentialValues);
            githubCredentialAuthority.DeleteCredentialFiles();
            stateKeyCredentialAuthority.DeleteCredentialFiles();
            AssertCredentialCopiesAbsent(root.Path, credentialPaths);
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

            var manifestDraftPath = $"{options["--package-manifest-output"]}.assembling";
            var nodeOptions = new Dictionary<string, string>(options, StringComparer.Ordinal)
            {
                ["--package-manifest-output"] = manifestDraftPath,
            };
            var result = RunNode(nodeOptions, repositoryRoot);
            foreach (var lease in leases)
            {
                lease.Validate();
            }

            hostLease = root.AcquirePinnedFile(options["--host-output"], EvidenceLimits.MaximumDocumentBytes);
            manifestLease = root.AcquirePinnedFile(
                manifestDraftPath,
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

            hostLease.Validate();
            manifestLease.Validate();
            publicScanLease.Validate();
            publicCandidateLease?.Validate();
            createdPublicOutput = EnforcePublicProjectionBoundary(
                publicCorpus,
                protectedScanValues,
                publicBytes,
                hostLease.Bytes,
                leases.Select(lease => lease.Bytes)
                    .Append(manifestLease.Bytes)
                    .Append(publicScanLease.Bytes)
                    .ToArray(),
                publicPath,
                () =>
                {
                    if (!githubCredentialAuthority.CredentialsDeleted ||
                        !stateKeyCredentialAuthority.CredentialsDeleted)
                    {
                        throw new InvalidDataException("credential_lease_deletion_invalid");
                    }
                    AssertCredentialCopiesAbsent(root.Path, credentialPaths);
                    credentialPaths.Clear();

                    manifestLease.Validate();
                    var manifestBytes = manifestLease.Bytes.ToArray();
                    try
                    {
                        root.WritePinnedFileCreateNew(
                            options["--package-manifest-output"],
                            manifestBytes);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(manifestBytes);
                    }
                    var finalizedManifest = root.AcquirePinnedFile(
                        options["--package-manifest-output"],
                        EvidenceLimits.MaximumDocumentBytes);
                    if (!finalizedManifest.Bytes.AsSpan().SequenceEqual(manifestLease.Bytes))
                    {
                        finalizedManifest.Dispose();
                        throw new InvalidDataException("assembly_manifest_finalization_invalid");
                    }
                    manifestLease.DeleteExactIdentity();
                    manifestLease = finalizedManifest;
                    manifestLease.Validate();
                });
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
            if (githubCredentialAuthority is not null &&
                stateKeyCredentialAuthority is not null &&
                credentialPaths.Count != 0)
            {
                try
                {
                    if (!githubCredentialAuthority.CredentialsDeleted)
                    {
                        githubCredentialAuthority.DeleteCredentialFiles();
                    }
                    if (!stateKeyCredentialAuthority.CredentialsDeleted)
                    {
                        stateKeyCredentialAuthority.DeleteCredentialFiles();
                    }
                    if (root is not null)
                    {
                        AssertCredentialCopiesAbsent(root.Path, credentialPaths);
                    }
                    credentialPaths.Clear();
                }
                catch (Exception exception) when (
                    exception is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    // Failure remains terminal. The guardians retain the only
                    // authority to remove the original physical identities.
                }
            }
            manifestLease?.Dispose();
            publicScanLease?.Dispose();
            publicCorpus?.Dispose();
            createdPublicOutput?.Dispose();
            publicCandidateLease?.Dispose();
            githubCredentialAuthority?.Dispose();
            stateKeyCredentialAuthority?.Dispose();
            foreach (var value in leasedCredentialValues)
            {
                value.Dispose();
            }
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

    internal static Dictionary<string, IReadOnlyList<byte[]>> ReadProtectedScanInput(
        Stream input,
        RestrictedEvidenceRoot? restrictedRoot = null,
        CaptureManifestDocument? capture = null,
        string? expectedDigestForTest = null,
        string? expectedRepositoryForTest = null,
        IReadOnlyList<string>? expectedOperationsForTest = null)
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
            ExactProperties(root, ["kind", "repository", "operation_ids", "categories"]);
            var expectedRepository = capture?.Repository ?? expectedRepositoryForTest;
            var expectedOperations = capture?.OperationIds ?? expectedOperationsForTest;
            var expectedDigest = expectedDigestForTest ??
                (capture is not null && restrictedRoot is not null
                    ? AuthorizedProtectedScanDigest(restrictedRoot, capture)
                    : null);
            if (root.GetProperty("kind").GetString() != "apr-r4-e3-public-scan-memory-input-v2" ||
                expectedRepository is null || expectedOperations is null || expectedDigest is null ||
                root.GetProperty("repository").GetString() != expectedRepository ||
                !root.GetProperty("operation_ids").EnumerateArray()
                    .Select(item => item.GetString()).SequenceEqual(expectedOperations) ||
                !StringComparer.Ordinal.Equals(
                    CanonicalEvidence.Sha256(bytes),
                    expectedDigest))
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
                "host_evidence",
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
                        if (candidate.Length is < 32 or > EvidenceLimits.MaximumCredentialBytes)
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
            if (result.Values.SelectMany(value => value)
                .Select(Convert.ToBase64String)
                .Distinct(StringComparer.Ordinal).Count() != result.Values.Sum(value => value.Count))
            {
                throw new InvalidDataException("public_scan_input_invalid");
            }
            ValidateOperationBoundProtectedValues(result, expectedOperations);
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

    private static string AuthorizedProtectedScanDigest(
        RestrictedEvidenceRoot root,
        CaptureManifestDocument capture)
    {
        return ReadExecutionAuthorization(root, capture, authorization =>
        {
            var binding = authorization.GetProperty("protected_scan_input");
            ExactProperties(binding, ["kind", "repository", "operation_ids", "categories", "sha256"]);
            var expectedCategories = new[]
            {
                "authorization",
                "state_keys",
                "session_plaintext",
                "provider_content",
                "tool_data",
                "host_evidence",
            };
            if (binding.GetProperty("kind").GetString() != "apr-r4-e3-operation-canary-binding-v1" ||
                binding.GetProperty("repository").GetString() != capture.Repository ||
                !binding.GetProperty("operation_ids").EnumerateArray()
                    .Select(item => item.GetString()).SequenceEqual(capture.OperationIds) ||
                !binding.GetProperty("categories").EnumerateArray()
                    .Select(item => item.GetString()).SequenceEqual(expectedCategories))
            {
                throw new InvalidDataException("public_scan_input_invalid");
            }
            var digest = binding.GetProperty("sha256").GetString() ?? "";
            if (!Regex.IsMatch(digest, "^[0-9a-f]{64}$"))
            {
                throw new InvalidDataException("public_scan_input_invalid");
            }
            return digest;
        });
    }

    private static T ReadExecutionAuthorization<T>(
        RestrictedEvidenceRoot root,
        CaptureManifestDocument capture,
        Func<JsonElement, T> read)
    {
        var source = capture.Sources.Single(item =>
            Regex.IsMatch(item.SourceId, "^authorization-execution-comment-[1-9][0-9]*:page:1$"));
        using var lease = root.AcquirePinnedFile(source.BodyPath, EvidenceLimits.MaximumDocumentBytes);
        if (!StringComparer.Ordinal.Equals(CanonicalEvidence.Sha256(lease.Bytes), source.BodySha256) ||
            !StringComparer.Ordinal.Equals(lease.Identity, source.BodyFileIdentity))
        {
            throw new InvalidDataException("public_scan_input_invalid");
        }
        using var response = JsonDocument.Parse(lease.Bytes);
        var body = response.RootElement.GetProperty("body").GetString() ?? "";
        const string prefix = "<!-- apr-r4-e3-authorization ";
        const string suffix = " -->";
        if (!body.StartsWith(prefix, StringComparison.Ordinal) ||
            !body.EndsWith(suffix, StringComparison.Ordinal))
        {
            throw new InvalidDataException("public_scan_input_invalid");
        }
        using var marker = JsonDocument.Parse(body[prefix.Length..^suffix.Length]);
        var authorization = marker.RootElement.GetProperty("authorization");
        return read(authorization);
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

    internal static void AppendActualCredentialValues(
        Dictionary<string, IReadOnlyList<byte[]>> values,
        RestrictedEvidenceRoot root,
        IReadOnlyDictionary<string, string> options,
        IReadOnlyDictionary<string, string> authorizedCredentialIdentities,
        ICollection<string> credentialPaths)
    {
        var tokenPath = options["--github-token-file"];
        var currentPath = options["--current-state-key-file"];
        var previousPath = options.TryGetValue("--previous-state-key-file", out var previous)
            ? previous
            : null;
        if (!StringComparer.Ordinal.Equals(tokenPath, "github-token") ||
            !StringComparer.Ordinal.Equals(currentPath, "current-state-key") ||
            (previousPath is not null &&
                !StringComparer.Ordinal.Equals(previousPath, "previous-state-key")))
        {
            throw new InvalidDataException("public_scan_input_invalid");
        }
        credentialPaths.Add(tokenPath);
        credentialPaths.Add(currentPath);
        if (previousPath is not null)
        {
            credentialPaths.Add(previousPath);
        }

        using var token = root.ReadCredentialFileRepresentations(tokenPath, base64Key: false);
        using var current = root.ReadCredentialFileRepresentations(currentPath, base64Key: true);
        using var previousKey = previousPath is null
            ? null
            : root.ReadCredentialFileRepresentations(previousPath, base64Key: true);
        var actualIdentities = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [tokenPath] = token.PhysicalIdentitySha256,
            [currentPath] = current.PhysicalIdentitySha256,
        };
        if (previousPath is not null && previousKey is not null)
        {
            actualIdentities.Add(previousPath, previousKey.PhysicalIdentitySha256);
        }
        if (authorizedCredentialIdentities.Count != actualIdentities.Count ||
            actualIdentities.Any(item =>
                !authorizedCredentialIdentities.TryGetValue(item.Key, out var expected) ||
                !StringComparer.Ordinal.Equals(item.Value, expected)))
        {
            throw new InvalidDataException("public_scan_input_invalid");
        }
        if (previousKey is not null && CryptographicOperations.FixedTimeEquals(
                current.DecodedKey!,
                previousKey.DecodedKey!))
        {
            throw new InvalidDataException("public_scan_input_invalid");
        }

        var bearer = new byte[7 + token.FileBytes.Length];
        "Bearer "u8.CopyTo(bearer);
        token.FileBytes.CopyTo(bearer, 7);
        var authorization = values["authorization"].Concat(
            new[] { token.FileBytes.ToArray(), bearer }).ToArray();
        var stateKeys = values["state_keys"].Concat(
            new[] { current.DecodedKey!.ToArray(), current.FileBytes.ToArray() }
                .Concat(previousKey is null
                    ? []
                    : [previousKey.DecodedKey!.ToArray(), previousKey.FileBytes.ToArray()]))
            .ToArray();
        if (ContainsDuplicateProtectedValues(authorization.Concat(stateKeys).ToArray()))
        {
            ZeroArrays(authorization.Skip(values["authorization"].Count));
            ZeroArrays(stateKeys.Skip(values["state_keys"].Count));
            throw new InvalidDataException("public_scan_input_invalid");
        }
        values["authorization"] = authorization;
        values["state_keys"] = stateKeys;
    }

    internal static void AppendLeasedActualCredentialValues(
        Dictionary<string, IReadOnlyList<byte[]>> values,
        RestrictedEvidenceRoot root,
        IReadOnlyDictionary<string, string> options,
        IReadOnlyDictionary<string, string> authorizedCredentialIdentities,
        ICollection<string> credentialPaths,
        out CredentialLeaseAuthorityClient githubAuthority,
        out CredentialLeaseAuthorityClient stateKeyAuthority,
        out CredentialLeaseValue[] leasedValues)
    {
        var tokenPath = options["--github-token-file"];
        var currentPath = options["--current-state-key-file"];
        var previousPath = options.TryGetValue("--previous-state-key-file", out var previous)
            ? previous
            : null;
        if (tokenPath != "github-token" || currentPath != "current-state-key" ||
            (previousPath is not null && previousPath != "previous-state-key"))
        {
            throw new InvalidDataException("public_scan_input_invalid");
        }
        var tokenSpecs = new[] { new CredentialLeaseSpec(tokenPath, Base64Key: false) };
        var keySpecs = previousPath is null
            ? [new CredentialLeaseSpec(currentPath, Base64Key: true)]
            : new[]
            {
                new CredentialLeaseSpec(currentPath, Base64Key: true),
                new CredentialLeaseSpec(previousPath, Base64Key: true),
            };
        githubAuthority = CredentialLeaseAuthorityClient.Open(
            root,
            CredentialLeaseAuthorityClient.GitHubDescriptorName,
            tokenSpecs);
        try
        {
            stateKeyAuthority = CredentialLeaseAuthorityClient.Open(
                root,
                CredentialLeaseAuthorityClient.StateKeyDescriptorName,
                keySpecs);
        }
        catch
        {
            githubAuthority.Dispose();
            throw;
        }
        var all = new List<CredentialLeaseValue>();
        try
        {
            all.AddRange(githubAuthority.ReadValues());
            all.AddRange(stateKeyAuthority.ReadValues());
            leasedValues = [.. all];
            var byName = leasedValues.ToDictionary(item => item.RelativePath, StringComparer.Ordinal);
            var expectedNames = previousPath is null
                ? new[] { tokenPath, currentPath }
                : new[] { tokenPath, currentPath, previousPath };
            if (byName.Count != expectedNames.Length ||
                expectedNames.Any(name => !byName.ContainsKey(name)) ||
                authorizedCredentialIdentities.Count != byName.Count ||
                byName.Any(item =>
                    !authorizedCredentialIdentities.TryGetValue(item.Key, out var identity) ||
                    identity != item.Value.PhysicalIdentitySha256))
            {
                throw new InvalidDataException("public_scan_input_invalid");
            }
            credentialPaths.Add(tokenPath);
            credentialPaths.Add(currentPath);
            if (previousPath is not null) credentialPaths.Add(previousPath);

            var token = byName[tokenPath];
            var current = byName[currentPath];
            var previousKey = previousPath is null ? null : byName[previousPath];
            if (current.DecodedKey is null ||
                (previousKey is not null && previousKey.DecodedKey is null) ||
                previousKey is not null && CryptographicOperations.FixedTimeEquals(
                    current.DecodedKey,
                    previousKey.DecodedKey!))
            {
                throw new InvalidDataException("public_scan_input_invalid");
            }
            var bearer = new byte[7 + token.FileBytes.Length];
            "Bearer "u8.CopyTo(bearer);
            token.FileBytes.CopyTo(bearer, 7);
            var authorization = values["authorization"].Concat(
                new[] { token.FileBytes, bearer }).ToArray();
            var stateKeys = values["state_keys"].Concat(
                new[] { current.DecodedKey, current.FileBytes }
                    .Concat(previousKey is null
                        ? []
                        : [previousKey.DecodedKey!, previousKey.FileBytes]))
                .ToArray();
            if (ContainsDuplicateProtectedValues(authorization.Concat(stateKeys).ToArray()))
            {
                ZeroArrays(authorization.Skip(values["authorization"].Count));
                ZeroArrays(stateKeys.Skip(values["state_keys"].Count));
                throw new InvalidDataException("public_scan_input_invalid");
            }
            values["authorization"] = authorization;
            values["state_keys"] = stateKeys;
        }
        catch
        {
            foreach (var value in all) value.Dispose();
            githubAuthority.Dispose();
            stateKeyAuthority.Dispose();
            leasedValues = [];
            throw;
        }
    }

    private static bool ContainsDuplicateProtectedValues(IReadOnlyList<byte[]> values)
    {
        for (var left = 0; left < values.Count; left++)
        {
            for (var right = left + 1; right < values.Count; right++)
            {
                if (values[left].Length == values[right].Length &&
                    CryptographicOperations.FixedTimeEquals(values[left], values[right]))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static IReadOnlyDictionary<string, string> AuthorizedCredentialIdentities(
        RestrictedEvidenceRoot root,
        CaptureManifestDocument capture)
    {
        return ReadExecutionAuthorization(root, capture, authorization =>
        {
            var credentials = authorization.GetProperty("credential_files").EnumerateArray().ToArray();
            var expectedNames = new[] { "github-token", "current-state-key", "previous-state-key" };
            if (credentials.Length != expectedNames.Length)
            {
                throw new InvalidDataException("public_scan_input_invalid");
            }
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < credentials.Length; index++)
            {
                ExactProperties(credentials[index], ["name", "file_identity_sha256"]);
                var name = credentials[index].GetProperty("name").GetString() ?? "";
                var identity = credentials[index].GetProperty("file_identity_sha256").GetString() ?? "";
                if (!StringComparer.Ordinal.Equals(name, expectedNames[index]) ||
                    !Regex.IsMatch(identity, "^[0-9a-f]{64}$") ||
                    !result.TryAdd(name, identity))
                {
                    throw new InvalidDataException("public_scan_input_invalid");
                }
            }
            return result;
        });
    }

    private static void ZeroArrays(IEnumerable<byte[]> values)
    {
        foreach (var value in values)
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }

    private static void ValidateOperationBoundProtectedValues(
        IReadOnlyDictionary<string, IReadOnlyList<byte[]>> actual,
        IReadOnlyList<string> operationIds)
    {
        if (operationIds.Count != 2 ||
            operationIds.Any(value => !Regex.IsMatch(value, "^[0-9a-f]{64}$")))
        {
            throw new InvalidDataException("public_scan_input_invalid");
        }
        var operation = operationIds[0];
        var state = Encoding.UTF8.GetBytes($"APR_R4_E4_STATE_KEY_{operation}");
        var expected = new Dictionary<string, IReadOnlyList<byte[]>>(StringComparer.Ordinal)
        {
            ["authorization"] =
            [
                Encoding.UTF8.GetBytes($"APR_R4_E4_AUTHORIZATION_{operation}"),
                Encoding.UTF8.GetBytes($"Bearer APR_R4_E4_AUTHORIZATION_{operation}"),
            ],
            ["state_keys"] = [state, Encoding.UTF8.GetBytes(Convert.ToBase64String(state))],
            ["session_plaintext"] = [Encoding.UTF8.GetBytes($"APR_R4_E4_SESSION_PLAINTEXT_{operation}")],
            ["provider_content"] = [Encoding.UTF8.GetBytes($"APR_R4_E4_PROVIDER_CONTENT_{operation}")],
            ["tool_data"] = [Encoding.UTF8.GetBytes($"APR_R4_E4_TOOL_DATA_{operation}")],
            ["host_evidence"] = [Encoding.UTF8.GetBytes($"APR_R4_E4_HOST_EVIDENCE_{operation}")],
        };
        try
        {
            if (expected.Any(category =>
                    !actual.TryGetValue(category.Key, out var observed) ||
                    observed.Count != category.Value.Count ||
                    observed.Where((value, index) =>
                        !value.AsSpan().SequenceEqual(category.Value[index])).Any()))
            {
                throw new InvalidDataException("public_scan_input_invalid");
            }
        }
        finally
        {
            ZeroProtectedValues(expected);
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

    internal static CreatedEvidenceFileReceipt? EnforcePublicProjectionBoundary(
        PublicSurfaceCorpusLease publicCorpus,
        IReadOnlyDictionary<string, IReadOnlyList<byte[]>> protectedScanValues,
        byte[] publicBytes,
        byte[] hostBytes,
        IReadOnlyList<byte[]> protectedDocuments,
        string? publicPath,
        Action? afterScanBeforePublication = null,
        Action<string>? afterPublicationForTest = null)
    {
        _ = HostOperationIds(hostBytes);
        using (var host = JsonDocument.Parse(hostBytes))
        {
            var observedCleanup = host.RootElement.GetProperty("inventories")
                .GetProperty("observed_cleanup").EnumerateArray().ToArray();
            var artifactIds = observedCleanup
                .Select(item => item.GetProperty("artifact_id").GetString() ?? "")
                .ToArray();
            if (artifactIds.Length < 15 ||
                (publicPath is not null && artifactIds.Length != 15) ||
                artifactIds.Distinct(StringComparer.Ordinal).Count() != artifactIds.Length ||
                artifactIds.Any(value => !Regex.IsMatch(value, "^[1-9][0-9]{0,19}$")))
            {
                throw new InvalidDataException("assembly_host_inventory_invalid");
            }
        }
        if ((publicPath is null) != (publicBytes.Length == 0))
        {
            throw new InvalidDataException("assembly_output_invalid");
        }

        publicCorpus.AssertAbsent(protectedScanValues, publicBytes);
        AssertProtectedValuesAbsent(protectedScanValues, hostBytes, protectedDocuments);
        publicCorpus.AssertExactDocumentAbsent(hostBytes, publicBytes);
        foreach (var document in protectedDocuments)
        {
            publicCorpus.AssertExactDocumentAbsent(document, publicBytes);
        }
        publicCorpus.ValidateComplete(null, []);
        afterScanBeforePublication?.Invoke();
        if (publicPath is null)
        {
            publicCorpus.ValidateComplete(null, []);
            return null;
        }

        CreatedEvidenceFileReceipt? receipt = null;
        try
        {
            receipt = WritePublicCreateNew(
                publicPath,
                publicBytes,
                afterPublishForTest: afterPublicationForTest);
            publicCorpus.ValidateComplete(publicPath, publicBytes);
            receipt.ValidatePublished();
            return receipt;
        }
        catch
        {
            receipt?.RetractIfOwned();
            receipt?.Dispose();
            throw;
        }
    }

    internal static void AssertProtectedValuesAbsent(
        IReadOnlyDictionary<string, IReadOnlyList<byte[]>> protectedValues,
        byte[] hostBytes,
        IReadOnlyList<byte[]> protectedDocuments)
    {
        foreach (var value in protectedValues.Values.SelectMany(category => category))
        {
            if (value.Length < 16 || hostBytes.AsSpan().IndexOf(value) >= 0 ||
                protectedDocuments.Any(document => document.AsSpan().IndexOf(value) >= 0))
            {
                throw new InvalidDataException("protected_output_scan_leak");
            }
        }
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

    internal static CreatedEvidenceFileReceipt WritePublicCreateNew(
        string path,
        ReadOnlySpan<byte> bytes,
        int? failAfterBytesForTest = null,
        Action<string>? beforePublishForTest = null,
        Action<string>? afterPublishForTest = null)
    {
        return CreatedEvidenceFileReceipt.WriteCreateNew(
            path,
            bytes,
            failAfterBytesForTest,
            beforePublishForTest,
            afterPublishForTest);
    }

    private static void AssertCredentialCopiesAbsent(
        string root,
        IEnumerable<string> credentialPaths)
    {
        if (credentialPaths.Any(name => EvidenceFileHandle.PathEntryExists(
                System.IO.Path.Join(root, name))))
        {
            throw new InvalidDataException("assembly_credential_copy_invalid");
        }
    }

    private static IReadOnlyDictionary<string, string> ParseArgs(string[] args)
    {
        var required = NodeArgumentNames.Append("--node-executable").Append("--public-output")
            .Append("--github-token-file").Append("--current-state-key-file")
            .Append("--previous-state-key-file")
            .ToHashSet(StringComparer.Ordinal);
        var allowed = required;
        if (args.Length % 2 != 0)
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
        if (required.Any(name => !result.ContainsKey(name)))
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
        "--cleanup-execution",
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

using System.Security.Cryptography;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceContracts;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofCapture;

internal static class Program
{
    private sealed record CapturedArtifactMetadata(
        string ArtifactId,
        string ArtifactName,
        string ProducingRunId,
        string SourceId,
        string BodySha256);

    private static async Task<int> Main(string[] args)
    {
        if (CredentialLeaseAuthorityClient.IsGuardianCommand(args))
        {
            return await CredentialLeaseAuthorityClient.RunGuardianAsync(args).ConfigureAwait(false);
        }
        RestrictedEvidenceRoot? root = null;
        string? credentialPath = null;
        var credentialLeaseLaunchAttempted = false;
        CredentialFileRepresentations? token = null;
        CredentialFileRepresentations? currentKey = null;
        CredentialFileRepresentations? previousKey = null;
        try
        {
            var options = Parse(args);
            root = RestrictedEvidenceRoot.Open(
                options["--restricted-root"],
                options["--destination-identity"],
                [options["--repository-root"], options["--worktree-root"]]);
            var plan = CapturePlan.Read(root, options["--capture-plan"]);
            credentialPath = options["--github-token-file"];
            var creationStarted = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            token = root.ReadCredentialFileRepresentations(
                options["--github-token-file"],
                base64Key: false,
                deleteExactIdentityOnFailure: true);
            currentKey = root.ReadCredentialFileRepresentations(
                options["--current-state-key-file"],
                base64Key: true,
                deleteExactIdentityOnFailure: true);
            previousKey = options.TryGetValue("--previous-state-key-file", out var previousPath)
                ? root.ReadCredentialFileRepresentations(
                    previousPath,
                    base64Key: true,
                    deleteExactIdentityOnFailure: true)
                : null;
            var createdCredentials = new Dictionary<string, CredentialFileRepresentations>(
                StringComparer.Ordinal)
            {
                ["github-token"] = token,
                ["current-state-key"] = currentKey,
            };
            if (previousKey is not null)
            {
                createdCredentials.Add("previous-state-key", previousKey);
            }
            var creationFinished = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var admission = CredentialAdmissionReceipt.MaterializeCreateNew(
                root,
                options["--credential-admission-receipt-output"],
                plan.OperationIds,
                createdCredentials,
                creationStarted,
                creationFinished);
            if (CredentialAdmissionReceipt.AuthorizedIdentities(admission)["github-token"] !=
                token.PhysicalIdentitySha256)
            {
                throw new InvalidDataException("credential_admission_invalid");
            }
            using var client = TrustedProofCaptureClient.CreateProduction(token.FileBytes);
            using var timeout = new CancellationTokenSource(
                EvidenceLimits.LogicalOperationTimeout);
            var writer = new CapturePackageWriter(root, plan.PackageName, plan.OperationIds);
            var artifactMetadata = new Dictionary<string, CapturedArtifactMetadata>(StringComparer.Ordinal);
            foreach (var source in plan.Sources)
            {
                var pages = await client.GetPaginatedAsync(
                    source.Route,
                    source.EndpointFamily,
                    timeout.Token);
                try
                {
                    for (var index = 0; index < pages.Captures.Length; index++)
                    {
                        writer.AddSource(
                            $"{source.SourceId}:page:{index + 1}",
                            pages.Captures[index],
                            pages.Bodies[index]);
                    }
                    if (plan.Artifacts.Any(item =>
                            StringComparer.Ordinal.Equals(item.MetadataSourceId, source.SourceId)))
                    {
                        IndexArtifactMetadata(source, pages, artifactMetadata);
                    }
                }
                finally
                {
                    foreach (var body in pages.Bodies)
                    {
                        CryptographicOperations.ZeroMemory(body);
                    }
                }
            }

            foreach (var artifact in plan.Artifacts)
            {
                if (!artifactMetadata.TryGetValue(artifact.ArtifactId, out var metadata) ||
                    !StringComparer.Ordinal.Equals(metadata.ArtifactName, artifact.ArtifactName) ||
                    !StringComparer.Ordinal.Equals(metadata.ProducingRunId, artifact.ProducingRunId) ||
                    !StringComparer.Ordinal.Equals(metadata.SourceId, artifact.MetadataSourceId))
                {
                    throw new InvalidDataException("artifact_metadata_invalid");
                }
                var downloaded = await client.DownloadArtifactAsync(
                    artifact.DownloadRoute,
                    timeout.Token);
                try
                {
                    writer.AddArtifact(
                        artifact.ArtifactId,
                        metadata.ArtifactName,
                        metadata.SourceId,
                        metadata.BodySha256,
                        downloaded.Archive,
                        CanonicalEvidence.Sha256(downloaded.Archive),
                        artifact.ProducingRunId,
                        artifact.ProducingRunAttempt,
                        downloaded.Capture);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(downloaded.Archive);
                }
            }

            var finalized = writer.Finalize(
                plan.RepositoryId,
                plan.Repository,
                plan.OperationIds,
                plan.ExpectedRoles.Select(item => new CaptureManifestExpectedRole(
                    item.Role,
                    item.OperationId,
                    item.Scope,
                    item.RunId,
                    item.RunAttempt,
                    item.ProducerSourceIds)).ToArray(),
                plan.ObservedRuns.Select(item => new CaptureManifestObservedRun(
                    item.OperationId,
                    item.Scope,
                    item.RunId,
                    item.RunAttempt)).ToArray(),
                plan.SourceMapSha256);
            credentialLeaseLaunchAttempted = true;
            CredentialLeaseAuthorityClient.LaunchCurrentProcess(
                root,
                options["--destination-identity"],
                [options["--repository-root"], options["--worktree-root"]],
                CredentialLeaseAuthorityClient.GitHubDescriptorName,
                [new CredentialLeaseSpec(options["--github-token-file"], Base64Key: false)],
                [token],
                timeouts: CredentialLeaseAuthorityTimeouts.CaptureSuccessor);
            credentialPath = null;
            Console.Out.WriteLine($"APR_R4_E3_CAPTURE_OK {finalized.Sha256}");
            return 0;
        }
        catch (InvalidDataException)
        {
            return Invalid();
        }
        catch (HttpRequestException)
        {
            return Invalid();
        }
        catch (OperationCanceledException)
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
            if (root is not null && credentialPath is not null)
            {
                if (credentialLeaseLaunchAttempted)
                {
                    try
                    {
                        CredentialLeaseAuthorityClient.DeleteAbandoned(
                            root,
                            CredentialLeaseAuthorityClient.GitHubDescriptorName,
                            [new CredentialLeaseSpec(credentialPath, Base64Key: false)]);
                    }
                    catch (Exception exception) when (
                        exception is InvalidDataException or IOException or UnauthorizedAccessException)
                    {
                        // Never fall back to a pathname delete after lease authority was attempted.
                    }
                }
                else if (token is not null)
                {
                    TryDeleteExactCredential(token);
                    if (currentKey is not null) TryDeleteExactCredential(currentKey);
                    if (previousKey is not null) TryDeleteExactCredential(previousKey);
                }
            }
            token?.Dispose();
            currentKey?.Dispose();
            previousKey?.Dispose();
        }
    }

    private static void TryDeleteExactCredential(CredentialFileRepresentations value)
    {
        try
        {
            value.DeleteExactIdentity();
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException or
            ObjectDisposedException)
        {
            // Failure remains terminal and a replacement pathname is never deletion authority.
        }
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var names = new[]
        {
            "--restricted-root",
            "--destination-identity",
            "--repository-root",
            "--worktree-root",
            "--github-token-file",
            "--current-state-key-file",
            "--capture-plan",
            "--credential-admission-receipt-output",
        };
        var allowed = names.Append("--previous-state-key-file").ToHashSet(StringComparer.Ordinal);
        if (args.Length % 2 != 0)
        {
            throw new InvalidDataException("arguments_invalid");
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (!allowed.Contains(args[index]) ||
                !result.TryAdd(args[index], args[index + 1]))
            {
                throw new InvalidDataException("arguments_invalid");
            }
        }

        if (names.Any(name => !result.ContainsKey(name)) ||
            !StringComparer.Ordinal.Equals(result["--github-token-file"], "github-token") ||
            !StringComparer.Ordinal.Equals(result["--current-state-key-file"], "current-state-key") ||
            (result.TryGetValue("--previous-state-key-file", out var previous) &&
                !StringComparer.Ordinal.Equals(previous, "previous-state-key")))
        {
            throw new InvalidDataException("arguments_invalid");
        }

        return result;
    }

    private static void IndexArtifactMetadata(
        CapturePlanSource source,
        CapturePageSet pages,
        Dictionary<string, CapturedArtifactMetadata> destination)
    {
        for (var page = 0; page < pages.Bodies.Length; page++)
        {
            try
            {
                using var document = JsonDocument.Parse(pages.Bodies[page], new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32,
                });
                if (!document.RootElement.TryGetProperty("artifacts", out var artifacts) ||
                    artifacts.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidDataException("artifact_metadata_invalid");
                }
                foreach (var artifact in artifacts.EnumerateArray())
                {
                    var id = DecimalString(artifact, "id");
                    var name = artifact.GetProperty("name").GetString();
                    var run = DecimalString(artifact.GetProperty("workflow_run"), "id");
                    if (string.IsNullOrWhiteSpace(name) ||
                        !destination.TryAdd(
                            id,
                            new CapturedArtifactMetadata(
                                id,
                                name,
                                run,
                                source.SourceId,
                                pages.Captures[page].BodySha256)))
                    {
                        throw new InvalidDataException("artifact_metadata_invalid");
                    }
                }
            }
            catch (JsonException)
            {
                throw new InvalidDataException("artifact_metadata_invalid");
            }
        }
    }

    private static string DecimalString(JsonElement value, string property)
    {
        var raw = value.GetProperty(property).GetRawText();
        if (raw.Length is < 1 or > 20 ||
            raw.Any(character => character is < '0' or > '9') ||
            (raw.Length > 1 && raw[0] == '0'))
        {
            throw new InvalidDataException("artifact_metadata_invalid");
        }
        return raw;
    }

    private static int Invalid()
    {
        Console.Error.WriteLine("APR_R4_E3_CAPTURE_INVALID");
        return 1;
    }
}

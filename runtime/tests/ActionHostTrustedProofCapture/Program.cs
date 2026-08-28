using System.Security.Cryptography;
using System.Reflection;
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
        if (CredentialMaterializer.IsCommand(args))
        {
            return CredentialMaterializer.Run(args);
        }
        if (CorrectionGateMaterializer.IsCommand(args))
        {
            return CorrectionGateMaterializer.Run(args);
        }
        if (CleanupAuthorizationMaterializer.IsCommand(args))
        {
            return CleanupAuthorizationMaterializer.Run(args);
        }
        if (ProducerJournalMaterializer.IsCommand(args))
        {
            return ProducerJournalMaterializer.Run(args);
        }
        if (PhaseFragmentMaterializer.IsCommand(args))
        {
            return PhaseFragmentMaterializer.Run(args);
        }
        RestrictedEvidenceRoot? root = null;
        CredentialAdmissionMaterialization? admission = null;
        var completed = false;
        CredentialFileRepresentations? token = null;
        try
        {
            var options = Parse(args);
            root = RestrictedEvidenceRoot.Open(
                options["--restricted-root"],
                options["--destination-identity"],
                [options["--repository-root"], options["--worktree-root"]]);
            var plan = CapturePlan.Read(root, options["--capture-plan"]);
            admission = CredentialAdmissionReceipt.Read(
                root,
                options["--credential-admission-receipt"],
                plan.OperationIds);
            token = root.ReadCredentialFileRepresentations(
                options["--github-token-file"],
                base64Key: false,
                deleteExactIdentityOnFailure: true);
            var captureIdentity = admission.Document.Consumers.Single(item => item.Component == "capture");
            if (captureIdentity.BuildSha256 != AssemblySha256() ||
                CredentialAdmissionReceipt.AuthorizedIdentities(admission.Document)["github-token"] !=
                    token.PhysicalIdentitySha256)
            {
                throw new InvalidDataException("credential_admission_invalid");
            }
            using var client = TrustedProofCaptureClient.CreateProduction(token.FileBytes);
            using var timeout = new CancellationTokenSource(
                EvidenceLimits.LogicalOperationTimeout);
            var producerJournal = ProducerOutcomeJournal.Open(
                root,
                plan.ProducerJournalDirectory,
                plan.ExecutionAuthorizationSha256);
            var writer = new CapturePackageWriter(
                root,
                plan.PackageName,
                plan.OperationIds,
                plan.ExecutionAuthorizationSha256,
                plan.PhaseMaterializerSourceSha256,
                plan.PhaseMaterializerBuildSha256,
                producerJournal,
                plan.ProducerJournalSealSha256,
                plan.Kind);
            var artifactMetadata = new Dictionary<string, CapturedArtifactMetadata>(StringComparer.Ordinal);
            foreach (var source in plan.Sources)
            {
                if (source.Phase == "producer-discovery" && !writer.HasAnySource(source.SourceId))
                {
                    var producerSeal = producerJournal.ReadSeal();
                    foreach (var discovery in producerSeal.Document.DiscoverySources)
                    {
                        using var lease = root.AcquirePinnedFile(
                            discovery.BodyPath,
                            EvidenceLimits.MaximumDocumentBytes);
                        writer.AddSource(
                            discovery.SourceId,
                            source.OperationId,
                            source.Phase,
                            new SafeResponseCapture(
                                discovery.Route,
                                discovery.Page,
                                discovery.Status,
                                discovery.BodySha256,
                                long.Parse(
                                    discovery.BodySize,
                                    System.Globalization.CultureInfo.InvariantCulture),
                                discovery.SafeHeadersSha256,
                                discovery.RequestStartedUnixMilliseconds,
                                discovery.ResponseReceivedUnixMilliseconds,
                                discovery.NextRoute),
                            lease.Bytes);
                    }
                    if (!writer.HasCompleteSource(source))
                    {
                        throw new InvalidDataException("producer_discovery_source_invalid");
                    }
                    continue;
                }
                if (writer.HasAnySource(source.SourceId))
                {
                    if (!writer.HasCompleteSource(source) ||
                        source.SourceId.StartsWith("artifacts-run-", StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("retained_phase_source_invalid");
                    }
                    continue;
                }
                if (PhaseFragmentMaterializer.RequiresRetained(source.Phase))
                {
                    throw new InvalidDataException("retained_phase_source_missing");
                }
                var pages = await client.GetPaginatedAsync(
                    source.Route,
                    source.EndpointFamily,
                    timeout.Token);
                try
                {
                    if (source.Phase.StartsWith("terminal-", StringComparison.Ordinal))
                    {
                        PhaseFragmentMaterializer.ValidateTerminalSemantics(
                            source,
                            pages,
                            plan.Repository,
                            plan.OperationIds,
                            plan.Disposition);
                    }
                    for (var index = 0; index < pages.Captures.Length; index++)
                    {
                        writer.AddSource(
                            $"{source.SourceId}:page:{index + 1}",
                            source.OperationId,
                            source.Phase,
                            pages.Captures[index],
                            pages.Bodies[index]);
                    }
                    if (source.SourceId.StartsWith("artifacts-run-", StringComparison.Ordinal))
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

            foreach (var metadata in artifactMetadata.Values.OrderBy(item =>
                ulong.Parse(item.ArtifactId, System.Globalization.CultureInfo.InvariantCulture)))
            {
                var observedRun = plan.ObservedRuns.SingleOrDefault(item =>
                    StringComparer.Ordinal.Equals(item.RunId, metadata.ProducingRunId)) ??
                    throw new InvalidDataException("artifact_metadata_invalid");
                var downloaded = await client.DownloadArtifactAsync(
                    $"/repos/{plan.Repository}/actions/artifacts/{metadata.ArtifactId}/zip",
                    timeout.Token);
                try
                {
                    writer.AddArtifact(
                        metadata.ArtifactId,
                        metadata.ArtifactName,
                        metadata.SourceId,
                        metadata.BodySha256,
                        downloaded.Archive,
                        CanonicalEvidence.Sha256(downloaded.Archive),
                        metadata.ProducingRunId,
                        observedRun.RunAttempt,
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
                plan.ExecutionAuthorizationSha256,
                plan.ProducerJournalDirectory,
                plan.ProducerJournalSealSha256,
                plan.ProducerJournalSealFileIdentity,
                plan.Disposition,
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
            completed = true;
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
            if (!completed && root is not null && admission is not null)
            {
                DeleteAbandonedCredentials(root, admission.Document);
            }
            token?.Dispose();
        }
    }

    private static void DeleteAbandonedCredentials(
        RestrictedEvidenceRoot root,
        CredentialAdmissionDocument admission)
    {
        var keySpecs = admission.CreatedSlots
            .Where(slot => slot.Name != "github-token")
            .Select(slot => new CredentialLeaseSpec(slot.Name, Base64Key: true))
            .ToArray();
        foreach (var item in new[]
        {
            (Descriptor: CredentialLeaseAuthorityClient.GitHubDescriptorName,
                Specs: new[] { new CredentialLeaseSpec("github-token", Base64Key: false) }),
            (Descriptor: CredentialLeaseAuthorityClient.StateKeyDescriptorName, Specs: keySpecs),
        })
        {
            try
            {
                CredentialLeaseAuthorityClient.DeleteAbandoned(root, item.Descriptor, item.Specs);
            }
            catch (Exception exception) when (
                exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                // Never fall back to pathname deletion after guardian admission.
            }
        }
    }

    private static string AssemblySha256()
    {
        var location = Assembly.GetExecutingAssembly().Location;
        if (string.IsNullOrWhiteSpace(location)) throw new InvalidDataException("capture_build_invalid");
        using var stream = new FileStream(location, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
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
            "--capture-plan",
            "--credential-admission-receipt",
        };
        var allowed = names.ToHashSet(StringComparer.Ordinal);
        if (args.Length != names.Length * 2)
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
            !StringComparer.Ordinal.Equals(result["--github-token-file"], "github-token"))
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

using System.Security.Cryptography;
using AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceContracts;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofCapture;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        RestrictedEvidenceRoot? root = null;
        string? credentialPath = null;
        try
        {
            var options = Parse(args);
            root = RestrictedEvidenceRoot.Open(
                options["--restricted-root"],
                options["--destination-identity"],
                [options["--repository-root"], options["--worktree-root"]]);
            var plan = CapturePlan.Read(root, options["--capture-plan"]);
            credentialPath = options["--github-token-file"];
            var token = root.ReadCredentialFile(options["--github-token-file"], base64Key: false);
            try
            {
                using var client = TrustedProofCaptureClient.CreateProduction(token);
                using var timeout = new CancellationTokenSource(
                    EvidenceLimits.LogicalOperationTimeout);
                var writer = new CapturePackageWriter(root, plan.PackageName);
                foreach (var source in plan.Sources)
                {
                    var pages = await client.GetPaginatedAsync(source.Route, timeout.Token);
                    try
                    {
                        for (var index = 0; index < pages.Captures.Length; index++)
                        {
                            writer.AddSource(
                                $"{source.SourceId}:page:{index + 1}",
                                pages.Captures[index],
                                pages.Bodies[index]);
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
                    var downloaded = await client.DownloadArtifactAsync(
                        artifact.DownloadRoute,
                        timeout.Token);
                    try
                    {
                        writer.AddArtifact(
                            artifact.ArtifactId,
                            artifact.ArtifactName,
                            artifact.ExpectedRole,
                            artifact.Scope,
                            artifact.OpaqueName,
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
                    plan.SourceMapSha256);
                Console.Out.WriteLine($"APR_R4_E3_CAPTURE_OK {finalized.Sha256}");
                return 0;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(token);
                root.RemoveCredentialFile(options["--github-token-file"]);
                credentialPath = null;
            }
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
                try
                {
                    root.RemoveCredentialFile(credentialPath);
                }
                catch (InvalidDataException)
                {
                    // Failure is already terminal; preserve the stable non-leaking marker.
                }
                catch (IOException)
                {
                    // Failure is already terminal; preserve the stable non-leaking marker.
                }
                catch (UnauthorizedAccessException)
                {
                    // Failure is already terminal; preserve the stable non-leaking marker.
                }
            }
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
            "--capture-plan",
        };
        if (args.Length != names.Length * 2)
        {
            throw new InvalidDataException("arguments_invalid");
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (!names.Contains(args[index], StringComparer.Ordinal) ||
                !result.TryAdd(args[index], args[index + 1]))
            {
                throw new InvalidDataException("arguments_invalid");
            }
        }

        if (!StringComparer.Ordinal.Equals(result["--github-token-file"], "github-token"))
        {
            throw new InvalidDataException("arguments_invalid");
        }

        return result;
    }

    private static int Invalid()
    {
        Console.Error.WriteLine("APR_R4_E3_CAPTURE_INVALID");
        return 1;
    }
}

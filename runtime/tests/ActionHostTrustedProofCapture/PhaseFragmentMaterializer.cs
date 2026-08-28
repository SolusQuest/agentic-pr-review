using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceContracts;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofCapture;

public static partial class PhaseFragmentMaterializer
{
    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] == "phase-capture";

    public static bool RequiresRetained(string phase) =>
        phase is not ("terminal-normal" or "terminal-stale" or
            "post-cleanup-normal" or "post-cleanup-stale");

    public static int Run(string[] args)
    {
        CredentialFileRepresentations? token = null;
        try
        {
            var options = Parse(args.Skip(1).ToArray());
            var root = RestrictedEvidenceRoot.Open(
                options["--restricted-root"],
                options["--destination-identity"],
                [options["--repository-root"], options["--worktree-root"]]);
            var authorization = ReadAuthorization(
                root,
                options["--execution-authorization"],
                options["--execution-authorization-sha256"],
                options["--destination-identity"]);
            if (authorization.MaterializerBuildSha256 != AssemblySha256())
            {
                throw new InvalidDataException("phase_materializer_build_invalid");
            }
            var producerJournal = ProducerOutcomeJournal.Open(
                root,
                options["--producer-journal-directory"],
                options["--execution-authorization-sha256"]);
            var producerSeal = producerJournal.ReadSeal();
            if (producerSeal.Sha256 != options["--producer-journal-seal-sha256"])
            {
                throw new InvalidDataException("phase_materializer_producer_invalid");
            }
            ValidateDescriptor(
                options,
                authorization.Repository,
                authorization.OperationIds,
                producerSeal.Document.ObservedRuns);
            var admission = CredentialAdmissionReceipt.Read(
                root,
                options["--credential-admission-receipt"],
                authorization.OperationIds);
            if (admission.Document.ExecutionAuthorizationSha256 !=
                    options["--execution-authorization-sha256"] ||
                admission.Document.Consumers.Single(item =>
                    item.Component == "phase-fragment-materializer").BuildSha256 !=
                    authorization.MaterializerBuildSha256)
            {
                throw new InvalidDataException("phase_materializer_credential_invalid");
            }
            token = root.ReadCredentialFileRepresentations(
                options["--github-token-file"],
                base64Key: false,
                deleteExactIdentityOnFailure: false);
            if (CredentialAdmissionReceipt.AuthorizedIdentities(admission.Document)["github-token"] !=
                token.PhysicalIdentitySha256)
            {
                throw new InvalidDataException("phase_materializer_credential_invalid");
            }

            var packageName = options["--package-name"];
            var packagePath = RestrictedEvidenceRoot.ResolveChildPath(root.Path, packageName);
            if (!Directory.Exists(packagePath)) root.CreateExclusiveDirectory(packageName);
            if (EvidenceFileHandle.PathEntryExists(
                    RestrictedEvidenceRoot.ResolveChildPath(packagePath, PhaseFragmentJournal.JournalName)))
            {
                throw new InvalidDataException("phase_materializer_sealed");
            }
            var partial = PhaseFragmentJournal.ReadPartial(
                root,
                packageName,
                authorization.OperationIds,
                options["--execution-authorization-sha256"],
                authorization.MaterializerSourceSha256,
                authorization.MaterializerBuildSha256,
                producerSeal.Sha256);
            if (partial.Sources.Any(source => source.SourceId.StartsWith(
                    $"{options["--source-id"]}:page:",
                    StringComparison.Ordinal)))
            {
                throw new InvalidDataException("phase_materializer_duplicate");
            }

            using var client = TrustedProofCaptureClient.CreateProduction(token.FileBytes);
            using var timeout = new CancellationTokenSource(EvidenceLimits.LogicalOperationTimeout);
            var pages = client.GetPaginatedAsync(
                options["--route"],
                options["--endpoint-family"],
                timeout.Token).GetAwaiter().GetResult();
            try
            {
                if (options["--pagination"] == "none" && pages.Captures.Length != 1)
                {
                    throw new InvalidDataException("phase_materializer_pagination_invalid");
                }
                var fragments = partial.Fragments.ToList();
                var sources = partial.Sources.ToList();
                for (var index = 0; index < pages.Captures.Length; index++)
                {
                    var bodyName = $"source-{sources.Count + 1:D4}.json";
                    var bodyIdentity = root.WritePinnedFileCreateNew(
                        $"{packageName}/{bodyName}",
                        pages.Bodies[index]);
                    var capture = pages.Captures[index];
                    var source = new CaptureManifestSource(
                        $"{options["--source-id"]}:page:{index + 1}",
                        options["--operation-id"],
                        options["--phase"],
                        capture.Route,
                        capture.Page,
                        capture.Status,
                        bodyName,
                        capture.BodySha256,
                        capture.BodySize.ToString(),
                        bodyIdentity,
                        capture.SafeHeadersSha256,
                        capture.RequestStartedUnixMilliseconds,
                        capture.ResponseReceivedUnixMilliseconds,
                        capture.NextRoute);
                    var predecessor = fragments.Count == 0 ? null : fragments[^1].Sha256;
                    fragments.Add(PhaseFragmentJournal.AppendCreateNew(
                        root,
                        packageName,
                        authorization.OperationIds,
                        options["--execution-authorization-sha256"],
                        authorization.MaterializerSourceSha256,
                        authorization.MaterializerBuildSha256,
                        producerSeal.Sha256,
                        fragments.Count + 1,
                        predecessor,
                        source));
                    sources.Add(source);
                }
            }
            finally
            {
                foreach (var body in pages.Bodies) CryptographicOperations.ZeroMemory(body);
            }
            Console.Out.WriteLine("APR_R4_E3_PHASE_CAPTURED");
            return 0;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or
            UnauthorizedAccessException or CryptographicException or JsonException or
            HttpRequestException or TaskCanceledException or ArgumentException or InvalidOperationException or
            KeyNotFoundException or FormatException)
        {
            Console.Error.WriteLine("APR_R4_E3_PHASE_CAPTURE_INVALID");
            return 1;
        }
        finally
        {
            token?.Dispose();
        }
    }

    private static PhaseAuthorization ReadAuthorization(
        RestrictedEvidenceRoot root,
        string relativePath,
        string expectedSha256,
        string destinationIdentitySha256)
    {
        using var lease = root.AcquirePinnedFile(relativePath, EvidenceLimits.MaximumDocumentBytes);
        if (CanonicalEvidence.Sha256(lease.Bytes) != expectedSha256)
        {
            throw new InvalidDataException("phase_materializer_authorization_invalid");
        }
        using var document = JsonDocument.Parse(lease.Bytes);
        var execution = document.RootElement;
        if (execution.GetProperty("kind").GetString() != "apr-r4-e3-execution-authorization-v1" ||
            execution.GetProperty("destinations").GetProperty("private")
                .GetProperty("identity_sha256").GetString() != destinationIdentitySha256)
        {
            throw new InvalidDataException("phase_materializer_authorization_invalid");
        }
        var operations = execution.GetProperty("operation_ids").EnumerateArray()
            .Select(item => item.GetString() ?? string.Empty).ToArray();
        var repository = execution.GetProperty("coordinates").GetProperty("repository")
            .GetString() ?? string.Empty;
        var identity = execution.GetProperty("correction_gate")
            .GetProperty("authority_identities").EnumerateArray()
            .Single(item => item.GetProperty("component").GetString() ==
                "phase-fragment-materializer");
        var source = identity.GetProperty("source_sha256").GetString() ?? string.Empty;
        var build = identity.GetProperty("build_sha256").GetString() ?? string.Empty;
        if (operations.Length != 2 || operations.Any(item => !Sha256(item)) ||
            repository.Split('/').Length != 2 || !Sha256(source) || !Sha256(build))
        {
            throw new InvalidDataException("phase_materializer_authorization_invalid");
        }
        return new PhaseAuthorization(repository, operations, source, build);
    }

    private static void ValidateDescriptor(
        IReadOnlyDictionary<string, string> options,
        string repository,
        string[] operationIds,
        IReadOnlyList<ProducerJournalObservedRun> observedRuns)
    {
        var phase = options["--phase"];
        var operationId = options["--operation-id"];
        var allowed = new[]
        {
            "baseline-normal", "baseline-stale", "normal-variable-readback",
            "bootstrap-readiness", "bootstrap-pending", "bootstrap-approval",
            "bootstrap-jobs", "bootstrap-concurrency", "continuation-readiness",
            "continuation-pending", "continuation-approval", "continuation-jobs",
            "stale-variable-readback", "stale-readiness", "stale-pending", "stale-approval",
            "stale-jobs", "stale-concurrency", "terminal-normal",
            "terminal-stale", "post-cleanup-normal", "post-cleanup-stale",
        };
        var stale = phase.Contains("stale", StringComparison.Ordinal);
        if (!allowed.Contains(phase, StringComparer.Ordinal) ||
            operationId != operationIds[stale ? 1 : 0] ||
            !ValidSourcePhase(options["--source-id"], phase) ||
            !options["--route"].StartsWith($"/repos/{repository}/", StringComparison.Ordinal) ||
            !options["--endpoint-family"].StartsWith($"/repos/{repository}/", StringComparison.Ordinal) ||
            options["--pagination"] is not ("none" or "complete-cursor"))
        {
            throw new InvalidDataException("phase_materializer_descriptor_invalid");
        }
        var run = RunRoute().Match(options["--route"]);
        if (run.Success && !observedRuns.Any(item => item.RunId == run.Groups[1].Value &&
                item.OperationId == operationId))
        {
            throw new InvalidDataException("phase_materializer_descriptor_invalid");
        }
    }

    private static bool ValidSourcePhase(string sourceId, string phase)
    {
        if (phase.StartsWith("baseline-", StringComparison.Ordinal))
        {
            var scope = phase["baseline-".Length..];
            return sourceId.StartsWith($"baseline-{scope}-", StringComparison.Ordinal) ||
                Regex.IsMatch(
                    sourceId,
                    $"^authorization-(setup|execution)-(comment|permission)-{Regex.Escape(scope)}-");
        }
        if (phase.EndsWith("-readiness", StringComparison.Ordinal))
        {
            var name = phase[..(phase.Length - "-readiness".Length)];
            return sourceId.StartsWith($"readiness-{name}-", StringComparison.Ordinal);
        }
        if (phase.EndsWith("-pending", StringComparison.Ordinal) ||
            phase.EndsWith("-jobs", StringComparison.Ordinal))
        {
            var split = phase.LastIndexOf('-');
            var name = phase[..split];
            var kind = phase[(split + 1)..];
            return sourceId.StartsWith($"transition-{name}-{kind}-", StringComparison.Ordinal);
        }
        if (phase.EndsWith("-approval", StringComparison.Ordinal))
        {
            var name = phase[..(phase.Length - "-approval".Length)];
            return sourceId.StartsWith($"transition-{name}-approvals-", StringComparison.Ordinal) ||
                sourceId.StartsWith($"proof-control-{name}-", StringComparison.Ordinal);
        }
        if (phase.EndsWith("-concurrency", StringComparison.Ordinal))
        {
            var scope = phase.StartsWith("stale", StringComparison.Ordinal) ? "stale" : "normal";
            return sourceId.StartsWith($"concurrency-{scope}-", StringComparison.Ordinal);
        }
        if (phase.StartsWith("terminal-", StringComparison.Ordinal))
        {
            return sourceId.StartsWith("run-terminal-", StringComparison.Ordinal) ||
                sourceId.StartsWith("artifacts-run-", StringComparison.Ordinal);
        }
        return phase.StartsWith("post-cleanup-", StringComparison.Ordinal) &&
            sourceId.StartsWith("post-cleanup-", StringComparison.Ordinal);
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var names = new[]
        {
            "--restricted-root", "--destination-identity", "--repository-root", "--worktree-root",
            "--execution-authorization", "--execution-authorization-sha256",
            "--producer-journal-directory", "--producer-journal-seal-sha256",
            "--credential-admission-receipt", "--github-token-file", "--package-name",
            "--source-id", "--operation-id", "--phase", "--route", "--endpoint-family",
            "--pagination",
        };
        if (args.Length != names.Length * 2) throw new InvalidDataException("arguments_invalid");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (!names.Contains(args[index], StringComparer.Ordinal) ||
                !result.TryAdd(args[index], args[index + 1]))
            {
                throw new InvalidDataException("arguments_invalid");
            }
        }
        if (names.Any(name => !result.ContainsKey(name)) ||
            !Sha256(result["--execution-authorization-sha256"]) ||
            !Sha256(result["--producer-journal-seal-sha256"]) ||
            !Sha256(result["--operation-id"]) ||
            !RestrictedEvidenceRoot.IsSinglePathSegment(result["--package-name"]))
        {
            throw new InvalidDataException("arguments_invalid");
        }
        return result;
    }

    private static string AssemblySha256()
    {
        var location = Assembly.GetExecutingAssembly().Location;
        if (string.IsNullOrWhiteSpace(location))
        {
            throw new InvalidDataException("phase_materializer_build_invalid");
        }
        using var stream = new FileStream(location, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static bool Sha256(string value) => value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    [GeneratedRegex(@"/actions/runs/([1-9][0-9]*)(?:/|$)", RegexOptions.CultureInvariant)]
    private static partial Regex RunRoute();

    private sealed record PhaseAuthorization(
        string Repository,
        string[] OperationIds,
        string MaterializerSourceSha256,
        string MaterializerBuildSha256);
}

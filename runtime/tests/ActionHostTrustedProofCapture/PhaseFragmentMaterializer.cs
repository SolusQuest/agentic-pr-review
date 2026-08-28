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
            ValidateDescriptor(
                options,
                authorization.Repository,
                authorization.OperationIds);
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
                producerJournal);
            if (partial.Sources.Any(source => source.SourceId.StartsWith(
                    $"{options["--source-id"]}:page:",
                    StringComparison.Ordinal)))
            {
                throw new InvalidDataException("phase_materializer_duplicate");
            }

            using var client = TrustedProofCaptureClient.CreateProduction(token.FileBytes);
            using var timeout = new CancellationTokenSource(EvidenceLimits.LogicalOperationTimeout);
            var expectedAbsent = options["--phase"].StartsWith("baseline-", StringComparison.Ordinal) &&
                options["--source-id"].EndsWith("-authorization-variable", StringComparison.Ordinal);
            var pages = expectedAbsent
                ? client.GetExpectedAsync(
                    options["--route"],
                    System.Net.HttpStatusCode.NotFound,
                    timeout.Token).GetAwaiter().GetResult()
                : client.GetPaginatedAsync(
                    options["--route"],
                    options["--endpoint-family"],
                    timeout.Token).GetAwaiter().GetResult();
            try
            {
                if (options["--pagination"] == "none" && pages.Captures.Length != 1)
                {
                    throw new InvalidDataException("phase_materializer_pagination_invalid");
                }
                ValidatePhaseSemantics(options, pages, authorization);
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
                        producerJournal,
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
        var environment = execution.GetProperty("environment_baseline");
        var activeSecrets = execution.GetProperty("active_secret_profile")
            .GetProperty("environment_secret_names").EnumerateArray()
            .Select(item => item.GetString() ?? string.Empty).ToArray();
        var manifests = execution.GetProperty("authorization_manifests").EnumerateArray()
            .Select(item => item.GetString() ?? string.Empty).ToArray();
        var reviewerIds = environment.GetProperty("required_reviewer_ids").EnumerateArray()
            .Select(item => item.GetString() ?? string.Empty).ToArray();
        var environmentId = environment.GetProperty("environment_id").GetString() ?? string.Empty;
        var environmentName = environment.GetProperty("name").GetString() ?? string.Empty;
        if (!PositiveDecimal(environmentId) || string.IsNullOrWhiteSpace(environmentName) ||
            reviewerIds.Length != 1 || reviewerIds.Any(item => !PositiveDecimal(item)) ||
            activeSecrets.Length == 0 ||
            activeSecrets.Distinct(StringComparer.Ordinal).Count() != activeSecrets.Length ||
            manifests.Length != 2 || manifests.Any(item => !Sha256(item)) ||
            manifests.Distinct(StringComparer.Ordinal).Count() != 2)
        {
            throw new InvalidDataException("phase_materializer_authorization_invalid");
        }
        return new PhaseAuthorization(
            repository,
            operations,
            source,
            build,
            environmentId,
            environmentName,
            reviewerIds,
            environment.GetProperty("prevent_self_review").GetBoolean(),
            environment.GetProperty("can_admins_bypass").GetBoolean(),
            activeSecrets,
            execution.GetProperty("authorization_variable_baseline").GetProperty("name")
                .GetString() ?? string.Empty,
            manifests);
    }

    private static void ValidateDescriptor(
        IReadOnlyDictionary<string, string> options,
        string repository,
        string[] operationIds)
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
        if (run.Success && !PositiveDecimal(run.Groups[1].Value))
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
                sourceId.StartsWith("artifacts-run-", StringComparison.Ordinal) ||
                sourceId.StartsWith($"proof-control-comments-{phase["terminal-".Length..]}-pr-",
                    StringComparison.Ordinal);
        }
        return phase.StartsWith("post-cleanup-", StringComparison.Ordinal) &&
            sourceId.StartsWith("post-cleanup-", StringComparison.Ordinal);
    }

    internal static void ValidatePhaseSemantics(
        IReadOnlyDictionary<string, string> options,
        CapturePageSet pages,
        PhaseAuthorization authorization)
    {
        if (pages.Captures.Length != pages.Bodies.Length || pages.Bodies.Length == 0)
        {
            throw new InvalidDataException("phase_materializer_semantics_invalid");
        }
        var baselineVariable = options["--phase"].StartsWith("baseline-", StringComparison.Ordinal) &&
            options["--source-id"].EndsWith("-authorization-variable", StringComparison.Ordinal);
        if (baselineVariable)
        {
            if (pages.Bodies.Length != 1 || pages.Captures[0].Status != 404)
            {
                throw new InvalidDataException("phase_materializer_semantics_invalid");
            }
            return;
        }
        if (pages.Captures.Any(capture => capture.Status != 200))
        {
            throw new InvalidDataException("phase_materializer_semantics_invalid");
        }
        var sourceId = options["--source-id"];
        if (sourceId.EndsWith("-environment-protection", StringComparison.Ordinal))
        {
            using var document = JsonDocument.Parse(pages.Bodies.Single());
            var environment = document.RootElement;
            var reviewerRule = environment.GetProperty("protection_rules").EnumerateArray()
                .SingleOrDefault(rule => rule.GetProperty("type").GetString() == "required_reviewers");
            var reviewers = reviewerRule.ValueKind == JsonValueKind.Undefined
                ? []
                : reviewerRule.GetProperty("reviewers").EnumerateArray().Select(item =>
                    item.GetProperty("reviewer").GetProperty("id").GetRawText()).ToArray();
            if (environment.GetProperty("id").GetRawText() != authorization.EnvironmentId ||
                environment.GetProperty("name").GetString() != authorization.EnvironmentName ||
                environment.GetProperty("can_admins_bypass").GetBoolean() !=
                    authorization.CanAdminsBypass ||
                reviewerRule.ValueKind == JsonValueKind.Undefined ||
                reviewerRule.GetProperty("prevent_self_review").GetBoolean() !=
                    authorization.PreventSelfReview ||
                !reviewers.SequenceEqual(authorization.RequiredReviewerIds, StringComparer.Ordinal) ||
                environment.GetProperty("deployment_branch_policy")
                    .GetProperty("protected_branches").GetBoolean() ||
                !environment.GetProperty("deployment_branch_policy")
                    .GetProperty("custom_branch_policies").GetBoolean())
            {
                throw new InvalidDataException("phase_materializer_semantics_invalid");
            }
            return;
        }
        if (sourceId.EndsWith("-environment-branch-policies", StringComparison.Ordinal))
        {
            var policies = new List<(string Id, string Name, string Type)>();
            int? total = null;
            foreach (var body in pages.Bodies)
            {
                using var document = JsonDocument.Parse(body);
                total ??= document.RootElement.GetProperty("total_count").GetInt32();
                policies.AddRange(document.RootElement.GetProperty("branch_policies").EnumerateArray()
                    .Select(item => (
                        item.GetProperty("id").GetRawText(),
                        item.GetProperty("name").GetString() ?? string.Empty,
                        item.GetProperty("type").GetString() ?? string.Empty)));
            }
            if (total != policies.Count || policies.Count != 1 ||
                policies[0] != ("58463845", "main", "branch"))
            {
                throw new InvalidDataException("phase_materializer_semantics_invalid");
            }
            return;
        }
        if (sourceId.EndsWith("-environment-secret-inventory", StringComparison.Ordinal))
        {
            var names = new List<string>();
            int? total = null;
            foreach (var body in pages.Bodies)
            {
                using var document = JsonDocument.Parse(body);
                total ??= document.RootElement.GetProperty("total_count").GetInt32();
                names.AddRange(document.RootElement.GetProperty("secrets").EnumerateArray()
                    .Select(item => item.GetProperty("name").GetString() ?? string.Empty));
            }
            var expected = options["--phase"].StartsWith("baseline-", StringComparison.Ordinal)
                ? []
                : authorization.ActiveSecretNames;
            if (total != names.Count || names.Distinct(StringComparer.Ordinal).Count() != names.Count ||
                !names.Order(StringComparer.Ordinal).SequenceEqual(
                    expected.Order(StringComparer.Ordinal),
                    StringComparer.Ordinal))
            {
                throw new InvalidDataException("phase_materializer_semantics_invalid");
            }
            return;
        }
        if (sourceId.EndsWith("-authorization-variable", StringComparison.Ordinal))
        {
            using var document = JsonDocument.Parse(pages.Bodies.Single());
            var variable = document.RootElement;
            var manifestIndex = options["--phase"].Contains("stale", StringComparison.Ordinal)
                ? 1
                : 0;
            if (variable.GetProperty("name").GetString() != authorization.AuthorizationVariableName ||
                variable.GetProperty("value").GetString() is not { } value ||
                authorization.AuthorizationManifests.Length != 2 ||
                value != authorization.AuthorizationManifests[manifestIndex])
            {
                throw new InvalidDataException("phase_materializer_semantics_invalid");
            }
            return;
        }
        if (sourceId.Contains("-jobs-run-", StringComparison.Ordinal))
        {
            ValidateJobTopology(options, pages);
            return;
        }
        if (sourceId.Contains("-pending-run-", StringComparison.Ordinal))
        {
            ValidatePendingDeployment(pages, authorization);
            return;
        }
        if (sourceId.Contains("-approvals-run-", StringComparison.Ordinal))
        {
            ValidateDeploymentApproval(pages, authorization);
            return;
        }
        if (sourceId.StartsWith("proof-control-", StringComparison.Ordinal) &&
            sourceId.Contains("-comment-", StringComparison.Ordinal))
        {
            ValidateProofControlComment(options, pages, authorization);
            return;
        }
        if (sourceId.StartsWith("proof-control-", StringComparison.Ordinal) &&
            sourceId.Contains("-permission-", StringComparison.Ordinal))
        {
            ValidateProofControlPermission(pages, authorization);
            return;
        }
        if (sourceId.StartsWith("concurrency-", StringComparison.Ordinal))
        {
            ValidateConcurrency(options, pages, authorization);
            return;
        }
        if (sourceId.StartsWith("run-terminal-", StringComparison.Ordinal))
        {
            ValidateTerminalRun(options, pages);
            return;
        }
        if (sourceId.StartsWith("artifacts-run-", StringComparison.Ordinal))
        {
            ValidateArtifactInventory(pages);
            return;
        }
        if (sourceId.StartsWith("proof-control-comments-", StringComparison.Ordinal))
        {
            ValidateProofControlInventory(options, pages, authorization, requireComplete: true);
            return;
        }
        foreach (var body in pages.Bodies)
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                throw new InvalidDataException("phase_materializer_semantics_invalid");
            }
        }
    }

    internal static void ValidateTerminalSemantics(
        CapturePlanSource source,
        CapturePageSet pages,
        string repository,
        string[] operationIds,
        string disposition)
    {
        if (!source.Phase.StartsWith("terminal-", StringComparison.Ordinal) ||
            !operationIds.Contains(source.OperationId, StringComparer.Ordinal))
        {
            throw new InvalidDataException("phase_materializer_semantics_invalid");
        }
        var authorization = new PhaseAuthorization(
            repository,
            operationIds,
            new string('0', 64),
            new string('0', 64),
            string.Empty,
            string.Empty,
            ["16307884"],
            false,
            false,
            [],
            string.Empty,
            []);
        var options = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["--operation-id"] = source.OperationId,
                ["--phase"] = source.Phase,
                ["--source-id"] = source.SourceId,
                ["--route"] = source.Route,
            };
        if (source.SourceId.StartsWith("run-terminal-", StringComparison.Ordinal))
        {
            ValidateTerminalRun(options, pages);
        }
        else if (source.SourceId.StartsWith("artifacts-run-", StringComparison.Ordinal))
        {
            ValidateArtifactInventory(pages);
        }
        else if (source.SourceId.StartsWith("proof-control-comments-", StringComparison.Ordinal))
        {
            ValidateProofControlInventory(
                options,
                pages,
                authorization,
                requireComplete: disposition == "success-candidate");
        }
        else
        {
            throw new InvalidDataException("phase_materializer_semantics_invalid");
        }
    }

    private static void ValidatePendingDeployment(
        CapturePageSet pages,
        PhaseAuthorization authorization)
    {
        using var document = JsonDocument.Parse(pages.Bodies.Single());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != 1)
        {
            throw new InvalidDataException("phase_materializer_semantics_invalid");
        }
        var pending = root[0];
        var environment = pending.GetProperty("environment");
        var reviewers = pending.GetProperty("reviewers").EnumerateArray().Select(item =>
            item.GetProperty("reviewer").GetProperty("id").GetRawText()).ToArray();
        if (environment.GetProperty("id").GetRawText() != authorization.EnvironmentId ||
            environment.GetProperty("name").GetString() != authorization.EnvironmentName ||
            !reviewers.SequenceEqual(authorization.RequiredReviewerIds, StringComparer.Ordinal))
        {
            throw new InvalidDataException("phase_materializer_semantics_invalid");
        }
    }

    private static void ValidateDeploymentApproval(
        CapturePageSet pages,
        PhaseAuthorization authorization)
    {
        using var document = JsonDocument.Parse(pages.Bodies.Single());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != 1)
        {
            throw new InvalidDataException("phase_materializer_semantics_invalid");
        }
        var approval = root[0];
        var environments = approval.GetProperty("environments");
        var userId = approval.GetProperty("user").GetProperty("id").GetRawText();
        if (approval.GetProperty("state").GetString() != "approved" ||
            !authorization.RequiredReviewerIds.Contains(userId, StringComparer.Ordinal) ||
            environments.ValueKind != JsonValueKind.Array || environments.GetArrayLength() != 1 ||
            environments[0].GetProperty("id").GetRawText() != authorization.EnvironmentId ||
            environments[0].GetProperty("name").GetString() != authorization.EnvironmentName)
        {
            throw new InvalidDataException("phase_materializer_semantics_invalid");
        }
    }

    private static void ValidateProofControlComment(
        IReadOnlyDictionary<string, string> options,
        CapturePageSet pages,
        PhaseAuthorization authorization)
    {
        using var document = JsonDocument.Parse(pages.Bodies.Single());
        var response = document.RootElement;
        var match = Regex.Match(options["--source-id"],
            "^proof-control-(bootstrap|stale)-comment-([1-9][0-9]*)$");
        if (!match.Success || response.GetProperty("id").GetRawText() != match.Groups[2].Value)
        {
            throw new InvalidDataException("phase_materializer_semantics_invalid");
        }
        ValidateProofControlResponse(
            response,
            options["--operation-id"],
            match.Groups[1].Value,
            authorization);
    }

    private static void ValidateProofControlInventory(
        IReadOnlyDictionary<string, string> options,
        CapturePageSet pages,
        PhaseAuthorization authorization,
        bool requireComplete)
    {
        var responses = new List<JsonElement>();
        var documents = new List<JsonDocument>();
        try
        {
            foreach (var body in pages.Bodies)
            {
                var document = JsonDocument.Parse(body);
                documents.Add(document);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidDataException("phase_materializer_semantics_invalid");
                }
                responses.AddRange(document.RootElement.EnumerateArray());
            }
            var scope = options["--phase"]["terminal-".Length..];
            var phase = scope == "normal" ? "bootstrap" : "stale";
            var expectedKinds = scope == "normal"
                ? new[] { "ready", "release" }
                : new[] { "ready", "release", "stale-ready", "stale-release" };
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var kinds = new List<string>();
            foreach (var response in responses)
            {
                if (!response.TryGetProperty("body", out var bodyElement) ||
                    bodyElement.GetString() is not { } body ||
                    !body.StartsWith("<!-- apr-r4-e2p-control ", StringComparison.Ordinal))
                {
                    continue;
                }
                const string prefix = "<!-- apr-r4-e2p-control ";
                const string suffix = " -->";
                if (!body.EndsWith(suffix, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("phase_materializer_semantics_invalid");
                }
                using (var marker = JsonDocument.Parse(body[prefix.Length..^suffix.Length]))
                {
                    if (marker.RootElement.GetProperty("operation_id").GetString() !=
                        options["--operation-id"])
                    {
                        continue;
                    }
                }
                kinds.Add(ValidateProofControlResponse(
                    response,
                    options["--operation-id"],
                    phase,
                    authorization));
                if (!ids.Add(response.GetProperty("id").GetRawText()))
                {
                    throw new InvalidDataException("phase_materializer_semantics_invalid");
                }
            }
            if (kinds.Distinct(StringComparer.Ordinal).Count() != kinds.Count ||
                (requireComplete && !kinds.Order(StringComparer.Ordinal).SequenceEqual(
                    expectedKinds.Order(StringComparer.Ordinal), StringComparer.Ordinal)))
            {
                throw new InvalidDataException("phase_materializer_semantics_invalid");
            }
        }
        finally
        {
            foreach (var document in documents) document.Dispose();
        }
    }

    private static string ValidateProofControlResponse(
        JsonElement response,
        string operationId,
        string phase,
        PhaseAuthorization authorization)
    {
        var body = response.GetProperty("body").GetString() ?? string.Empty;
        const string prefix = "<!-- apr-r4-e2p-control ";
        const string suffix = " -->";
        if (!body.StartsWith(prefix, StringComparison.Ordinal) ||
            !body.EndsWith(suffix, StringComparison.Ordinal))
        {
            throw new InvalidDataException("phase_materializer_semantics_invalid");
        }
        using var markerDocument = JsonDocument.Parse(body[prefix.Length..^suffix.Length]);
        var marker = markerDocument.RootElement;
        var kind = marker.GetProperty("kind").GetString() ?? string.Empty;
        var allowed = phase == "bootstrap"
            ? new[] { "ready", "release" }
            : new[] { "ready", "release", "stale-ready", "stale-release" };
        var user = response.GetProperty("user");
        var userId = user.GetProperty("id").GetRawText();
        var readyActor = kind is "ready" or "stale-ready";
        if (marker.GetProperty("contract").GetString() != "apr-r4-e2p-proof-control-v1" ||
            !allowed.Contains(kind, StringComparer.Ordinal) ||
            marker.GetProperty("operation_id").GetString() != operationId ||
            marker.GetProperty("repository").GetString() != authorization.Repository ||
            marker.GetProperty("run_attempt").GetInt32() != 1 ||
            !PositiveDecimal(marker.GetProperty("run_id").GetRawText()) ||
            !PositiveDecimal(marker.GetProperty("pr_number").GetRawText()) ||
            !Sha256(marker.GetProperty("payload_sha256").GetString() ?? string.Empty) ||
            !Hex40(marker.GetProperty("fixture_head_sha").GetString() ?? string.Empty) ||
            !Hex40(marker.GetProperty("workflow_sha").GetString() ?? string.Empty) ||
            !Hex40(marker.GetProperty("action_source_sha").GetString() ?? string.Empty) ||
            marker.GetProperty("body_sha256").GetString() != ProofControlPreimageSha256(marker) ||
            (readyActor
                ? userId != "41898282" || user.GetProperty("login").GetString() != "github-actions[bot]"
                : !authorization.RequiredReviewerIds.Contains(userId, StringComparer.Ordinal)))
        {
            throw new InvalidDataException("phase_materializer_semantics_invalid");
        }
        return kind;
    }

    private static string ProofControlPreimageSha256(JsonElement marker)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in marker.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);
                if (property.NameEquals("body_sha256")) writer.WriteStringValue(string.Empty);
                else property.Value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private static void ValidateProofControlPermission(
        CapturePageSet pages,
        PhaseAuthorization authorization)
    {
        using var document = JsonDocument.Parse(pages.Bodies.Single());
        var permission = document.RootElement;
        var userId = permission.GetProperty("user").GetProperty("id").GetRawText();
        if (permission.GetProperty("permission").GetString() is not ("write" or "admin") ||
            !authorization.RequiredReviewerIds.Contains(userId, StringComparer.Ordinal))
        {
            throw new InvalidDataException("phase_materializer_semantics_invalid");
        }
    }

    private static void ValidateConcurrency(
        IReadOnlyDictionary<string, string> options,
        CapturePageSet pages,
        PhaseAuthorization authorization)
    {
        using var document = JsonDocument.Parse(pages.Bodies.Single());
        var root = document.RootElement;
        var members = root.GetProperty("group_members").EnumerateArray().ToArray();
        var source = Regex.Match(options["--source-id"],
            "^concurrency-(normal|stale)-run-([1-9][0-9]*)$");
        var group = root.GetProperty("group_name").GetString() ?? string.Empty;
        if (!source.Success || !Regex.IsMatch(group,
                "^agentic-pr-review-r4-[1-9][0-9]*-pr-[1-9][0-9]*$") ||
            root.GetProperty("total_count").GetInt32() != 2 || members.Length != 2 ||
            members[0].GetProperty("run_id").GetRawText() != source.Groups[2].Value ||
            members[0].GetProperty("position").GetInt32() != 0 ||
            members[0].GetProperty("status").GetString() != "in_progress" ||
            members[1].GetProperty("position").GetInt32() != 1 ||
            members[1].GetProperty("status").GetString() != "pending" ||
            members[0].GetProperty("run_id").GetRawText() ==
                members[1].GetProperty("run_id").GetRawText() ||
            options["--route"] !=
                $"/repos/{authorization.Repository}/actions/concurrency_groups/" +
                $"{Uri.EscapeDataString(group)}?ahead_of_run=" +
                members[1].GetProperty("run_id").GetRawText())
        {
            throw new InvalidDataException("phase_materializer_semantics_invalid");
        }
    }

    private static void ValidateTerminalRun(
        IReadOnlyDictionary<string, string> options,
        CapturePageSet pages)
    {
        using var document = JsonDocument.Parse(pages.Bodies.Single());
        var run = document.RootElement;
        var source = Regex.Match(options["--source-id"], "^run-terminal-([1-9][0-9]*)$");
        if (!source.Success || run.GetProperty("id").GetRawText() != source.Groups[1].Value ||
            run.GetProperty("status").GetString() != "completed" ||
            string.IsNullOrWhiteSpace(run.GetProperty("conclusion").GetString()) ||
            !DateTimeOffset.TryParse(run.GetProperty("run_started_at").GetString(), out _) ||
            !DateTimeOffset.TryParse(run.GetProperty("updated_at").GetString(), out _))
        {
            throw new InvalidDataException("phase_materializer_semantics_invalid");
        }
        if ((run.TryGetProperty("event", out var eventName) &&
                eventName.GetString() is not ("workflow_run" or "workflow_dispatch")) ||
            (run.TryGetProperty("head_sha", out var headSha) &&
                !Hex40(headSha.GetString() ?? string.Empty)))
        {
            throw new InvalidDataException("phase_materializer_semantics_invalid");
        }
    }

    private static void ValidateArtifactInventory(CapturePageSet pages)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        int? total = null;
        foreach (var body in pages.Bodies)
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var currentTotal = root.GetProperty("total_count").GetInt32();
            total ??= currentTotal;
            if (currentTotal != total)
            {
                throw new InvalidDataException("phase_materializer_semantics_invalid");
            }
            foreach (var artifact in root.GetProperty("artifacts").EnumerateArray())
            {
                count++;
                if (!ids.Add(artifact.GetProperty("id").GetRawText()) ||
                    !PositiveDecimal(artifact.GetProperty("workflow_run")
                        .GetProperty("id").GetRawText()))
                {
                    throw new InvalidDataException("phase_materializer_semantics_invalid");
                }
            }
        }
        if (total != count)
        {
            throw new InvalidDataException("phase_materializer_semantics_invalid");
        }
    }

    private static void ValidateJobTopology(
        IReadOnlyDictionary<string, string> options,
        CapturePageSet pages)
    {
        var names = new Dictionary<string, (string Status, string Conclusion)>(StringComparer.Ordinal);
        int? total = null;
        string? runId = null;
        foreach (var body in pages.Bodies)
        {
            using var document = JsonDocument.Parse(body);
            total ??= document.RootElement.GetProperty("total_count").GetInt32();
            foreach (var job in document.RootElement.GetProperty("jobs").EnumerateArray())
            {
                var currentRun = job.GetProperty("run_id").GetRawText();
                runId ??= currentRun;
                if (currentRun != runId || job.GetProperty("run_attempt").GetInt32() != 1 ||
                    !names.TryAdd(
                        job.GetProperty("name").GetString() ?? string.Empty,
                        (job.GetProperty("status").GetString() ?? string.Empty,
                            job.GetProperty("conclusion").GetString() ?? string.Empty)))
                {
                    throw new InvalidDataException("phase_materializer_semantics_invalid");
                }
            }
        }
        var selected = options["--phase"].StartsWith("continuation-", StringComparison.Ordinal)
            ? "workflow-dispatch-review"
            : "workflow-run-review";
        var other = selected == "workflow-run-review"
            ? "workflow-dispatch-review"
            : "workflow-run-review";
        if (total != 3 || names.Count != 3 ||
            !names.TryGetValue("authorization-preflight", out var preflight) ||
            preflight != ("completed", "success") ||
            !names.TryGetValue(selected, out var admitted) || admitted != ("completed", "success") ||
            !names.TryGetValue(other, out var skipped) || skipped != ("completed", "skipped"))
        {
            throw new InvalidDataException("phase_materializer_semantics_invalid");
        }
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var names = new[]
        {
            "--restricted-root", "--destination-identity", "--repository-root", "--worktree-root",
            "--execution-authorization", "--execution-authorization-sha256",
            "--producer-journal-directory",
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

    private static bool PositiveDecimal(string value) => value.Length > 0 && value != "0" &&
        value.All(character => character is >= '0' and <= '9') &&
        (value.Length == 1 || value[0] != '0');

    private static bool Hex40(string value) => value.Length == 40 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    [GeneratedRegex(@"/actions/runs/([1-9][0-9]*)(?:/|$)", RegexOptions.CultureInvariant)]
    private static partial Regex RunRoute();

    internal sealed record PhaseAuthorization(
        string Repository,
        string[] OperationIds,
        string MaterializerSourceSha256,
        string MaterializerBuildSha256,
        string EnvironmentId,
        string EnvironmentName,
        string[] RequiredReviewerIds,
        bool PreventSelfReview,
        bool CanAdminsBypass,
        string[] ActiveSecretNames,
        string AuthorizationVariableName,
        string[] AuthorizationManifests);
}

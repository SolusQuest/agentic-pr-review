using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceContracts;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofCapture;

internal static class ProducerJournalMaterializer
{
    private static readonly string[] Commands =
    [
        "producer-journal-create",
        "producer-journal-begin",
        "producer-journal-execute",
        "producer-journal-mark-unknown",
        "producer-journal-record",
        "producer-journal-reconcile",
        "producer-journal-seal",
    ];

    public static bool IsCommand(string[] args) =>
        args.Length > 0 && Commands.Contains(args[0], StringComparer.Ordinal);

    public static int Run(string[] args)
    {
        CredentialFileRepresentations? token = null;
        try
        {
            var command = args[0];
            var options = Parse(args.Skip(1).ToArray(), command);
            var root = RestrictedEvidenceRoot.Open(
                options["--restricted-root"],
                options["--destination-identity"],
                [options["--repository-root"], options["--worktree-root"]]);
            var authorization = ReadAuthorization(
                root,
                options["--execution-authorization"],
                options["--execution-authorization-sha256"],
                options["--destination-identity"]);
            var materializerBuildSha256 = AssemblySha256();
            if (authorization.MaterializerBuildSha256 != materializerBuildSha256)
            {
                throw new InvalidDataException("producer_materializer_build_invalid");
            }

            if (command == "producer-journal-create")
            {
                _ = ProducerOutcomeJournal.CreateNew(
                    root,
                    options["--journal-directory"],
                    options["--execution-authorization-sha256"],
                    authorization.MaterializerSourceSha256,
                    authorization.MaterializerBuildSha256,
                    authorization.Repository,
                    authorization.OperationIds,
                    authorization.Targets,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                Console.Out.WriteLine("APR_R4_E3_PRODUCER_JOURNAL_CREATED");
                return 0;
            }

            var journal = ProducerOutcomeJournal.Open(
                root,
                options["--journal-directory"],
                options["--execution-authorization-sha256"]);
            if (command == "producer-journal-seal")
            {
                token = ReadAuthorizedToken(root, options, authorization, materializerBuildSha256);
                using var client = TrustedProofCaptureClient.CreateProduction(token.FileBytes);
                var sources = CaptureDiscovery(
                    root,
                    journal.Authority,
                    options["--journal-directory"],
                    "final",
                    client);
                var seal = journal.SealCreateNew(sources);
                Console.Out.WriteLine(
                    $"APR_R4_E3_PRODUCER_JOURNAL_SEALED {seal.Sha256} " +
                    $"{seal.PhysicalIdentitySha256} {seal.Document.Disposition}");
                return 0;
            }

            var target = authorization.Targets.SingleOrDefault(item =>
                item.AuthorityId == options["--authority-id"]) ??
                throw new InvalidDataException("producer_materializer_authority_invalid");
            if (command == "producer-journal-begin")
            {
                EnsurePreconditions(root, options, authorization, journal, target);
                return Begin(journal, target);
            }
            if (command == "producer-journal-mark-unknown") return MarkUnknown(journal, target);
            if (command == "producer-journal-record")
            {
                return Record(root, options, authorization, journal, target);
            }
            var producer = authorization.Commands.SingleOrDefault(item =>
                item.Target.AuthorityId == target.AuthorityId) ??
                throw new InvalidDataException("producer_materializer_command_invalid");
            token = ReadAuthorizedToken(root, options, authorization, materializerBuildSha256);
            using var producerClient = TrustedProofCaptureClient.CreateProduction(token.FileBytes);
            return command == "producer-journal-reconcile"
                ? Reconcile(root, options, journal, producer, producerClient)
                : Execute(root, options, authorization, journal, producer, producerClient);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or
            UnauthorizedAccessException or CryptographicException or JsonException or
            ArgumentException or InvalidOperationException or OverflowException or
            HttpRequestException or OperationCanceledException or KeyNotFoundException or
            FormatException)
        {
            Console.Error.WriteLine("APR_R4_E3_PRODUCER_JOURNAL_INVALID");
            return 1;
        }
        finally
        {
            token?.Dispose();
        }
    }

    private static int Begin(ProducerOutcomeJournal journal, ProducerTargetAuthority target)
    {
        var prior = journal.Entries.LastOrDefault(item => item.AuthorityId == target.AuthorityId);
        var attempt = prior is null ? 1 : checked(prior.Attempt + 1);
        var observed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        journal.AppendCreateNew(
            target.AuthorityId,
            attempt,
            "before-dispatch",
            observed,
            observed,
            []);
        Console.Out.WriteLine("APR_R4_E3_PRODUCER_BEGUN");
        return 0;
    }

    private static int MarkUnknown(ProducerOutcomeJournal journal, ProducerTargetAuthority target)
    {
        var prior = journal.Entries.LastOrDefault(item => item.AuthorityId == target.AuthorityId);
        if (prior is null || prior.Outcome != "before-dispatch")
        {
            throw new InvalidDataException("producer_materializer_unknown_invalid");
        }
        var observed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        journal.AppendCreateNew(
            target.AuthorityId,
            prior.Attempt,
            "outcome-unknown",
            prior.Observation.RequestStartedUnixMilliseconds,
            observed,
            []);
        Console.Out.WriteLine("APR_R4_E3_PRODUCER_OUTCOME_UNKNOWN");
        return 2;
    }

    private static int Record(
        RestrictedEvidenceRoot root,
        IReadOnlyDictionary<string, string> options,
        ProducerAuthorization authorization,
        ProducerOutcomeJournal journal,
        ProducerTargetAuthority target)
    {
        if (target.TargetKind == "trigger")
        {
            throw new InvalidDataException("producer_materializer_record_invalid");
        }
        var prior = journal.Entries.LastOrDefault(item => item.AuthorityId == target.AuthorityId);
        if (prior is null || prior.Outcome is not ("before-dispatch" or "outcome-unknown"))
        {
            throw new InvalidDataException("producer_materializer_record_invalid");
        }
        var partial = PhaseFragmentJournal.ReadPartial(
            root,
            options["--package-name"],
            authorization.OperationIds,
            options["--execution-authorization-sha256"],
            authorization.PhaseMaterializerSourceSha256,
            authorization.PhaseMaterializerBuildSha256,
            journal);
        var source = partial.Sources.SingleOrDefault(item =>
            item.OperationId == target.OperationId &&
            item.Phase == target.RequiredReadbackPhase &&
            item.SourceId == options["--source-id"] &&
            item.SourceId.StartsWith(target.RequiredSourcePrefix, StringComparison.Ordinal)) ??
            throw new InvalidDataException("producer_materializer_record_invalid");
        ValidateProducerReadback(root, options["--package-name"], target, source);
        journal.AppendCreateNew(
            target.AuthorityId,
            prior.Attempt,
            prior.Outcome == "outcome-unknown" ? "reconciled-committed" : "committed",
            source.RequestStartedUnixMilliseconds,
            source.ResponseReceivedUnixMilliseconds,
            [source.BodySha256]);
        Console.Out.WriteLine("APR_R4_E3_PRODUCER_RECORDED");
        return 0;
    }

    private static void ValidateProducerReadback(
        RestrictedEvidenceRoot root,
        string packageName,
        ProducerTargetAuthority target,
        CaptureManifestSource source)
    {
        using var lease = root.AcquirePinnedFile(
            $"{packageName}/{source.BodyPath}",
            EvidenceLimits.MaximumDocumentBytes);
        using var document = JsonDocument.Parse(lease.Bytes);
        var value = document.RootElement;
        if (target.TargetKind == "environment-secret")
        {
            var names = value.GetProperty("secrets").EnumerateArray()
                .Select(item => item.GetProperty("name").GetString() ?? string.Empty).ToArray();
            if (value.GetProperty("total_count").GetInt32() != names.Length ||
                !names.Contains(target.Role, StringComparer.Ordinal))
            {
                throw new InvalidDataException("producer_materializer_record_invalid");
            }
            return;
        }
        if (target.TargetKind == "authorization-variable")
        {
            if (value.GetProperty("name").GetString() != "R4_TRUSTED_PROOF_AUTHORIZATION" ||
                string.IsNullOrWhiteSpace(value.GetProperty("value").GetString()))
            {
                throw new InvalidDataException("producer_materializer_record_invalid");
            }
            return;
        }
        if (target.TargetKind == "deployment-approval")
        {
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 1 ||
                value[0].GetProperty("state").GetString() != "approved")
            {
                throw new InvalidDataException("producer_materializer_record_invalid");
            }
            return;
        }
        if (target.TargetKind == "proof-control-comment")
        {
            var body = value.GetProperty("body").GetString() ?? string.Empty;
            const string prefix = "<!-- apr-r4-e2p-control ";
            const string suffix = " -->";
            if (!body.StartsWith(prefix, StringComparison.Ordinal) ||
                !body.EndsWith(suffix, StringComparison.Ordinal))
            {
                throw new InvalidDataException("producer_materializer_record_invalid");
            }
            using var marker = JsonDocument.Parse(body[prefix.Length..^suffix.Length]);
            if (marker.RootElement.GetProperty("kind").GetString() != target.Role ||
                marker.RootElement.GetProperty("operation_id").GetString() != target.OperationId)
            {
                throw new InvalidDataException("producer_materializer_record_invalid");
            }
            return;
        }
        throw new InvalidDataException("producer_materializer_record_invalid");
    }

    private static int Execute(
        RestrictedEvidenceRoot root,
        IReadOnlyDictionary<string, string> options,
        ProducerAuthorization authorization,
        ProducerOutcomeJournal journal,
        ProducerCommand producer,
        TrustedProofCaptureClient client)
    {
        EnsurePreconditions(root, options, authorization, journal, producer.Target);
        var prior = journal.Entries.LastOrDefault(item => item.AuthorityId == producer.Target.AuthorityId);
        var attempt = prior is null ? 1 : checked(prior.Attempt + 1);
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        journal.AppendCreateNew(
            producer.Target.AuthorityId,
            attempt,
            "before-dispatch",
            before,
            before,
            []);
        try
        {
            using var timeout = new CancellationTokenSource(EvidenceLimits.LogicalOperationTimeout);
            var response = client.SendProducerAsync(
                producer.Method,
                producer.Route,
                producer.Body,
                producer.ExpectedStatus,
                timeout.Token).GetAwaiter().GetResult();
            try
            {
                var committedRunId = producer.Target.ExpectedEvent == "workflow_dispatch"
                    ? ReadDispatchRunId(response.Body, journal.Authority.Repository)
                    : null;
                var captureBytes = CanonicalEvidence.Encode(response.Capture, EvidenceJson.Options);
                try
                {
                    var sourceIds = committedRunId is null
                        ? new[] { CanonicalEvidence.Sha256(captureBytes) }
                        : new[]
                        {
                            CanonicalEvidence.Sha256(captureBytes),
                            response.Capture.BodySha256,
                        };
                    journal.AppendCreateNew(
                        producer.Target.AuthorityId,
                        attempt,
                        "committed",
                        response.Capture.RequestStartedUnixMilliseconds,
                        response.Capture.ResponseReceivedUnixMilliseconds,
                        sourceIds,
                        committedRunId);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(captureBytes);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(response.Body);
            }
            Console.Out.WriteLine("APR_R4_E3_PRODUCER_COMMITTED");
            return 0;
        }
        catch (Exception exception) when (exception is InvalidDataException or HttpRequestException or
            OperationCanceledException or IOException)
        {
            var observed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            journal.AppendCreateNew(
                producer.Target.AuthorityId,
                attempt,
                "outcome-unknown",
                before,
                observed,
                []);
            Console.Error.WriteLine("APR_R4_E3_PRODUCER_OUTCOME_UNKNOWN");
            return 2;
        }
        finally
        {
            if (producer.Body is not null) CryptographicOperations.ZeroMemory(producer.Body);
        }
    }

    private static void EnsurePreconditions(
        RestrictedEvidenceRoot root,
        IReadOnlyDictionary<string, string> options,
        ProducerAuthorization authorization,
        ProducerOutcomeJournal journal,
        ProducerTargetAuthority target)
    {
        var phases = target.RequiredPreconditionPhases ?? [];
        var prefixes = target.RequiredPreconditionSourcePrefixes ?? [];
        if (phases.Length == 0 || phases.Length != prefixes.Length)
        {
            throw new InvalidDataException("producer_materializer_precondition_invalid");
        }
        var partial = PhaseFragmentJournal.ReadPartial(
            root,
            options["--package-name"],
            authorization.OperationIds,
            options["--execution-authorization-sha256"],
            authorization.PhaseMaterializerSourceSha256,
            authorization.PhaseMaterializerBuildSha256,
            journal);
        for (var index = 0; index < phases.Length; index++)
        {
            if (!partial.Sources.Any(source =>
                    source.Phase == phases[index] &&
                    source.SourceId.StartsWith(prefixes[index], StringComparison.Ordinal)))
            {
                throw new InvalidDataException("producer_materializer_precondition_invalid");
            }
        }
    }

    private static int Reconcile(
        RestrictedEvidenceRoot root,
        IReadOnlyDictionary<string, string> options,
        ProducerOutcomeJournal journal,
        ProducerCommand producer,
        TrustedProofCaptureClient client)
    {
        var unknown = journal.Entries.LastOrDefault(item =>
            item.AuthorityId == producer.Target.AuthorityId && item.Outcome == "outcome-unknown");
        if (unknown is null || journal.Entries.Last(item =>
                item.AuthorityId == producer.Target.AuthorityId) != unknown)
        {
            throw new InvalidDataException("producer_reconciliation_invalid");
        }
        if (producer.Target.ExpectedEvent == "workflow_dispatch")
        {
            Console.Error.WriteLine("APR_R4_E3_PRODUCER_OUTCOME_STILL_UNKNOWN");
            return 2;
        }
        var phase = $"reconcile-{journal.Entries.Count + 1:D4}";
        var sources = CaptureDiscovery(
            root,
            journal.Authority,
            options["--journal-directory"],
            phase,
            client);
        if (!DiscoveryContainsCandidate(
                root,
                sources,
                journal.Authority.Repository,
                producer.Target,
                unknown.Observation.RequestStartedUnixMilliseconds))
        {
            Console.Error.WriteLine("APR_R4_E3_PRODUCER_OUTCOME_STILL_UNKNOWN");
            return 2;
        }
        journal.AppendCreateNew(
            producer.Target.AuthorityId,
            unknown.Attempt,
            "reconciled-committed",
            sources[0].RequestStartedUnixMilliseconds,
            sources[^1].ResponseReceivedUnixMilliseconds,
            sources.Select(item => item.BodySha256).ToArray());
        Console.Out.WriteLine("APR_R4_E3_PRODUCER_RECONCILED");
        return 0;
    }

    private static ProducerDiscoverySource[] CaptureDiscovery(
        RestrictedEvidenceRoot root,
        ProducerAuthorityDocument authority,
        string journalDirectory,
        string phase,
        TrustedProofCaptureClient client)
    {
        var endpoint = $"/repos/{authority.Repository}/actions/workflows/r4-trusted-proof.yml/runs";
        using var timeout = new CancellationTokenSource(EvidenceLimits.LogicalOperationTimeout);
        var pages = client.GetPaginatedAsync(
            $"{endpoint}?per_page=100",
            endpoint,
            timeout.Token).GetAwaiter().GetResult();
        try
        {
            var sources = new ProducerDiscoverySource[pages.Captures.Length];
            for (var index = 0; index < pages.Captures.Length; index++)
            {
                var name = $"discovery-{phase}-page-{index + 1:D4}.json";
                var relativePath = $"{journalDirectory}/{name}";
                var identity = root.WritePinnedFileCreateNew(relativePath, pages.Bodies[index]);
                var capture = pages.Captures[index];
                sources[index] = new ProducerDiscoverySource(
                    $"producer-discovery-{phase}:page:{index + 1}",
                    capture.Route,
                    capture.Page,
                    capture.Status,
                    relativePath,
                    capture.BodySha256,
                    capture.BodySize.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    identity,
                    capture.SafeHeadersSha256,
                    capture.RequestStartedUnixMilliseconds,
                    capture.ResponseReceivedUnixMilliseconds,
                    capture.NextRoute);
            }
            return sources;
        }
        finally
        {
            foreach (var body in pages.Bodies) CryptographicOperations.ZeroMemory(body);
        }
    }

    private static bool DiscoveryContainsCandidate(
        RestrictedEvidenceRoot root,
        IReadOnlyList<ProducerDiscoverySource> sources,
        string repository,
        ProducerTargetAuthority target,
        long notBefore)
    {
        var matches = 0;
        foreach (var source in sources)
        {
            using var lease = root.AcquirePinnedFile(source.BodyPath, EvidenceLimits.MaximumDocumentBytes);
            using var document = JsonDocument.Parse(lease.Bytes);
            foreach (var run in document.RootElement.GetProperty("workflow_runs").EnumerateArray())
            {
                if (!DateTimeOffset.TryParse(
                    run.GetProperty("created_at").GetString() ?? string.Empty,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal |
                        System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var created))
                {
                    throw new InvalidDataException("producer_reconciliation_invalid");
                }
                var pullRequests = run.TryGetProperty("pull_requests", out var pulls) &&
                    pulls.ValueKind == JsonValueKind.Array
                    ? pulls.EnumerateArray().Select(item => item.GetProperty("number").GetRawText()).ToArray()
                    : [];
                var runRepository = run.GetProperty("head_repository").GetProperty("full_name")
                    .GetString() ?? string.Empty;
                var workflowPath = run.GetProperty("path").GetString() ?? string.Empty;
                if (created.ToUnixTimeMilliseconds() + 999 >= notBefore &&
                    runRepository == repository &&
                    workflowPath == ".github/workflows/r4-trusted-proof.yml" &&
                    run.GetProperty("event").GetString() == target.ExpectedEvent &&
                    (target.ExpectedHeadSha.Length == 0 ||
                        run.GetProperty("head_sha").GetString() == target.ExpectedHeadSha) &&
                    (target.ExpectedHeadBranch.Length == 0 ||
                        run.GetProperty("head_branch").GetString() == target.ExpectedHeadBranch) &&
                    (target.ExpectedEvent == "workflow_dispatch"
                        ? pullRequests.Length == 0
                        : target.ExpectedPullRequestNumber.Length == 0
                            ? pullRequests.Length == 0
                            : pullRequests.Contains(
                                target.ExpectedPullRequestNumber,
                                StringComparer.Ordinal)))
                {
                    matches++;
                }
            }
        }
        return matches == 1;
    }

    private static CredentialFileRepresentations ReadAuthorizedToken(
        RestrictedEvidenceRoot root,
        IReadOnlyDictionary<string, string> options,
        ProducerAuthorization authorization,
        string materializerBuildSha256)
    {
        var admission = CredentialAdmissionReceipt.Read(
            root,
            options["--credential-admission-receipt"],
            authorization.OperationIds);
        if (admission.Document.ExecutionAuthorizationSha256 !=
                options["--execution-authorization-sha256"] ||
            admission.Document.Consumers.Single(item =>
                item.Component == "producer-journal-materializer").BuildSha256 !=
                materializerBuildSha256)
        {
            throw new InvalidDataException("producer_materializer_credential_invalid");
        }
        var token = root.ReadCredentialFileRepresentations(
            options["--github-token-file"],
            base64Key: false,
            deleteExactIdentityOnFailure: false);
        if (CredentialAdmissionReceipt.AuthorizedIdentities(admission.Document)["github-token"] !=
            token.PhysicalIdentitySha256)
        {
            token.Dispose();
            throw new InvalidDataException("producer_materializer_credential_invalid");
        }
        return token;
    }

    private static ProducerAuthorization ReadAuthorization(
        RestrictedEvidenceRoot root,
        string relativePath,
        string expectedSha256,
        string destinationIdentitySha256)
    {
        using var lease = root.AcquirePinnedFile(relativePath, EvidenceLimits.MaximumDocumentBytes);
        if (CanonicalEvidence.Sha256(lease.Bytes) != expectedSha256)
        {
            throw new InvalidDataException("producer_materializer_authorization_invalid");
        }
        using var document = JsonDocument.Parse(lease.Bytes, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            MaxDepth = 64,
        });
        var execution = document.RootElement;
        if (execution.GetProperty("kind").GetString() != "apr-r4-e3-execution-authorization-v1" ||
            execution.GetProperty("destinations").GetProperty("private")
                .GetProperty("identity_sha256").GetString() != destinationIdentitySha256)
        {
            throw new InvalidDataException("producer_materializer_authorization_invalid");
        }
        var operationIds = execution.GetProperty("operation_ids").EnumerateArray()
            .Select(item => item.GetString() ?? string.Empty).ToArray();
        var repository = execution.GetProperty("coordinates").GetProperty("repository")
            .GetString() ?? string.Empty;
        if (operationIds.Length != 2 || operationIds.Any(item => !Sha256(item)) ||
            repository.Split('/').Length != 2)
        {
            throw new InvalidDataException("producer_materializer_authorization_invalid");
        }
        var identity = execution.GetProperty("correction_gate")
            .GetProperty("authority_identities").EnumerateArray()
            .Single(item => item.GetProperty("component").GetString() ==
                "producer-journal-materializer");
        var sourceSha256 = identity.GetProperty("source_sha256").GetString() ?? string.Empty;
        var buildSha256 = identity.GetProperty("build_sha256").GetString() ?? string.Empty;
        if (!Sha256(sourceSha256) || !Sha256(buildSha256))
        {
            throw new InvalidDataException("producer_materializer_authorization_invalid");
        }
        var phaseIdentity = execution.GetProperty("correction_gate")
            .GetProperty("authority_identities").EnumerateArray()
            .Single(item => item.GetProperty("component").GetString() ==
                "phase-fragment-materializer");
        var phaseSourceSha256 = phaseIdentity.GetProperty("source_sha256").GetString() ?? string.Empty;
        var phaseBuildSha256 = phaseIdentity.GetProperty("build_sha256").GetString() ?? string.Empty;
        if (!Sha256(phaseSourceSha256) || !Sha256(phaseBuildSha256))
        {
            throw new InvalidDataException("producer_materializer_authorization_invalid");
        }
        var roles = new[]
        {
            "normal-bootstrap",
            "normal-continuation",
            "stale-protected",
            "stale-follow-on",
        };
        var triggers = execution.GetProperty("trigger_plan").EnumerateArray().ToArray();
        var workflowSha = RequiredHex40(execution.GetProperty("coordinates"), "workflow_sha");
        if (triggers.Length != roles.Length)
        {
            throw new InvalidDataException("producer_materializer_authorization_invalid");
        }
        var commands = triggers.Select((trigger, index) =>
        {
            var bytes = CanonicalEvidence.Encode(trigger, EvidenceJson.Options);
            try
            {
                var descriptorSha256 = CanonicalEvidence.Sha256(bytes);
                var role = trigger.GetProperty("role").GetString() ?? string.Empty;
                var operationId = trigger.GetProperty("operation_id").GetString() ?? string.Empty;
                var scope = trigger.GetProperty("scope").GetString() ?? string.Empty;
                var producer = trigger.GetProperty("producer").GetString() ?? string.Empty;
                var expectedEvent = trigger.GetProperty("expected_event").GetString() ?? string.Empty;
                var prNumber = trigger.GetProperty("pr_number").GetString() ?? string.Empty;
                var reference = trigger.GetProperty("ref").GetString() ?? string.Empty;
                var authorizedHeadSha = trigger.GetProperty("authorized_head_sha").GetString() ?? string.Empty;
                var source = trigger.GetProperty("source_coordinate");
                if (role != roles[index] || !operationIds.Contains(operationId, StringComparer.Ordinal) ||
                    scope is not ("normal" or "stale") ||
                    expectedEvent is not ("workflow_run" or "workflow_dispatch") ||
                    !PositiveDecimal(prNumber) || !reference.StartsWith("refs/heads/", StringComparison.Ordinal) ||
                    authorizedHeadSha.Length != 40)
                {
                    throw new InvalidDataException("producer_materializer_authorization_invalid");
                }
                var (preconditionPhase, preconditionPrefix) = role switch
                {
                    "normal-bootstrap" =>
                        ("bootstrap-readiness", "readiness-bootstrap-authorization-variable:"),
                    "normal-continuation" =>
                        ("continuation-readiness", "readiness-continuation-authorization-variable:"),
                    "stale-protected" =>
                        ("stale-readiness", "readiness-stale-authorization-variable:"),
                    _ => ("stale-jobs", "transition-stale-jobs-"),
                };
                var target = new ProducerTargetAuthority(
                    descriptorSha256,
                    operationId,
                    role,
                    scope,
                    producer,
                    expectedEvent,
                    descriptorSha256,
                    workflowSha,
                    producer == "dispatch-proof-workflow" ? RequiredText(source, "value") : "main",
                    string.Empty,
                    RequiredPreconditionPhases: [preconditionPhase],
                    RequiredPreconditionSourcePrefixes: [preconditionPrefix]);
                return producer switch
                {
                    "rerun-upstream-ci" => new ProducerCommand(
                        target,
                        HttpMethod.Post,
                        $"/repos/{repository}/actions/runs/{RequiredDecimal(source, "id")}/rerun",
                        null,
                        HttpStatusCode.Created),
                    "dispatch-proof-workflow" => new ProducerCommand(
                        target,
                        HttpMethod.Post,
                        $"/repos/{repository}/actions/workflows/r4-trusted-proof.yml/dispatches",
                        BuildDispatchRequestBody(RequiredText(source, "value"), prNumber),
                        HttpStatusCode.OK),
                    "advance-stale-ref" => new ProducerCommand(
                        target,
                        HttpMethod.Patch,
                        $"/repos/{repository}/git/refs/{reference["refs/".Length..]}",
                        EncodeBody(new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            ["sha"] = RequiredHex40(source, "value"),
                            ["force"] = false,
                        }),
                        HttpStatusCode.OK),
                    _ => throw new InvalidDataException("producer_materializer_authorization_invalid"),
                };
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }).ToArray();
        var mutationTargets = BuildMutationTargets(execution, operationIds);
        return new ProducerAuthorization(
            repository,
            operationIds,
            sourceSha256,
            buildSha256,
            phaseSourceSha256,
            phaseBuildSha256,
            commands.Select(item => item.Target).Concat(mutationTargets).ToArray(),
            commands);
    }

    private static ProducerTargetAuthority[] BuildMutationTargets(
        JsonElement execution,
        string[] operationIds)
    {
        var targets = new List<ProducerTargetAuthority>();
        foreach (var secret in execution.GetProperty("active_secret_profile")
                     .GetProperty("environment_secret_names").EnumerateArray()
                     .Select(item => item.GetString() ?? string.Empty))
        {
            targets.Add(MutationTarget(
                "environment-secret",
                secret,
                operationIds[0],
                "normal",
                "bootstrap-readiness",
                "readiness-bootstrap-environment-secret-inventory:"));
        }
        targets.Add(MutationTarget(
            "authorization-variable",
            "normal",
            operationIds[0],
            "normal",
            "bootstrap-readiness",
            "readiness-bootstrap-authorization-variable:"));
        targets.Add(MutationTarget(
            "authorization-variable",
            "stale",
            operationIds[1],
            "stale",
            "stale-readiness",
            "readiness-stale-authorization-variable:"));
        foreach (var phase in new[] { "bootstrap", "continuation", "stale" })
        {
            var stale = phase == "stale";
            targets.Add(MutationTarget(
                "deployment-approval",
                phase,
                operationIds[stale ? 1 : 0],
                stale ? "stale" : "normal",
                $"{phase}-approval",
                $"transition-{phase}-approvals-"));
        }
        foreach (var kind in new[] { "ready", "release" })
        {
            targets.Add(MutationTarget(
                "proof-control-comment",
                kind,
                operationIds[0],
                "normal",
                "bootstrap-approval",
                "proof-control-bootstrap-comment-"));
        }
        foreach (var kind in new[] { "ready", "release", "stale-ready", "stale-release" })
        {
            targets.Add(MutationTarget(
                "proof-control-comment",
                kind,
                operationIds[1],
                "stale",
                "stale-approval",
                "proof-control-stale-comment-"));
        }
        return [.. targets];
    }

    private static ProducerTargetAuthority MutationTarget(
        string targetKind,
        string role,
        string operationId,
        string scope,
        string phase,
        string sourcePrefix)
    {
        var (preconditionPhases, preconditionPrefixes) = targetKind switch
        {
            "environment-secret" => (
                new[] { "baseline-normal", "baseline-stale" },
                new[]
                {
                    "baseline-normal-environment-secret-inventory:",
                    "baseline-stale-environment-secret-inventory:",
                }),
            "authorization-variable" => (
                [scope == "normal" ? "baseline-normal" : "baseline-stale"],
                [scope == "normal"
                    ? "baseline-normal-authorization-variable:"
                    : "baseline-stale-authorization-variable:"]),
            "deployment-approval" => (
                new[]
                {
                    $"{role}-readiness",
                    $"{role}-pending",
                },
                new[]
                {
                    $"readiness-{role}-environment-protection:",
                    $"transition-{role}-pending-",
                }),
            _ => (
                [phase],
                [phase.StartsWith("bootstrap", StringComparison.Ordinal)
                    ? "transition-bootstrap-approvals-"
                    : "transition-stale-approvals-"]),
        };
        var descriptor = new MutationTargetDescriptor(
            targetKind,
            operationId,
            role,
            scope,
            phase,
            sourcePrefix,
            preconditionPhases,
            preconditionPrefixes);
        var bytes = CanonicalEvidence.Encode(descriptor, EvidenceJson.Options);
        try
        {
            var sha256 = CanonicalEvidence.Sha256(bytes);
            return new ProducerTargetAuthority(
                sha256,
                operationId,
                role,
                scope,
                targetKind,
                string.Empty,
                sha256,
                TargetKind: targetKind,
                RequiredReadbackPhase: phase,
                RequiredSourcePrefix: sourcePrefix,
                RequiredPreconditionPhases: preconditionPhases,
                RequiredPreconditionSourcePrefixes: preconditionPrefixes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static byte[] EncodeBody(object value) => CanonicalEvidence.Encode(value, EvidenceJson.Options);

    private static string RequiredDecimal(JsonElement value, string property)
    {
        var result = value.GetProperty(property).GetString() ?? string.Empty;
        return PositiveDecimal(result)
            ? result
            : throw new InvalidDataException("producer_materializer_authorization_invalid");
    }

    private static string RequiredText(JsonElement value, string property)
    {
        var result = value.GetProperty(property).GetString() ?? string.Empty;
        return result.Length is > 0 and <= EvidenceLimits.MaximumNameBytes
            ? result
            : throw new InvalidDataException("producer_materializer_authorization_invalid");
    }

    private static string RequiredHex40(JsonElement value, string property)
    {
        var result = RequiredText(value, property);
        return result.Length == 40 && result.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f')
            ? result
            : throw new InvalidDataException("producer_materializer_authorization_invalid");
    }

    private static Dictionary<string, string> Parse(string[] args, string command)
    {
        var common = new[]
        {
            "--restricted-root", "--destination-identity", "--repository-root", "--worktree-root",
            "--execution-authorization", "--execution-authorization-sha256", "--journal-directory",
        };
        var credential = new[] { "--credential-admission-receipt", "--github-token-file" };
        var names = command switch
        {
            "producer-journal-create" => common,
            "producer-journal-seal" => common.Concat(credential).ToArray(),
            "producer-journal-begin" or "producer-journal-mark-unknown" =>
                command == "producer-journal-begin"
                    ? common.Concat(["--authority-id", "--package-name"]).ToArray()
                    : common.Append("--authority-id").ToArray(),
            "producer-journal-record" => common.Concat(
                ["--authority-id", "--package-name", "--source-id"]).ToArray(),
            _ => common.Concat(credential).Concat(["--authority-id", "--package-name"]).ToArray(),
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
            (names.Contains("--authority-id", StringComparer.Ordinal) &&
                !Sha256(result["--authority-id"])) ||
            (names.Contains("--package-name", StringComparer.Ordinal) &&
                !RestrictedEvidenceRoot.IsSinglePathSegment(result["--package-name"])) ||
            (names.Contains("--source-id", StringComparer.Ordinal) &&
                string.IsNullOrWhiteSpace(result["--source-id"])))
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
            throw new InvalidDataException("producer_materializer_build_invalid");
        }
        using var stream = new FileStream(location, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static bool Sha256(string value) => value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool PositiveDecimal(string value) => value.Length > 0 && value != "0" &&
        value.All(character => character is >= '0' and <= '9') &&
        (value.Length == 1 || value[0] != '0');

    internal static byte[] BuildDispatchRequestBody(string reference, string pullRequestNumber)
    {
        if (string.IsNullOrWhiteSpace(reference) || !PositiveDecimal(pullRequestNumber))
        {
            throw new InvalidDataException("producer_dispatch_request_invalid");
        }
        return EncodeBody(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["ref"] = reference,
            ["inputs"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["pr-number"] = pullRequestNumber,
            },
        });
    }

    internal static string ReadDispatchRunId(byte[] responseBody, string repository)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            var names = root.ValueKind == JsonValueKind.Object
                ? root.EnumerateObject().Select(item => item.Name).ToArray()
                : [];
            var runId = root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("workflow_run_id", out var runIdElement)
                    ? runIdElement.GetRawText()
                    : string.Empty;
            if (names.Length != 3 || names.Distinct(StringComparer.Ordinal).Count() != 3 ||
                !names.Order(StringComparer.Ordinal).SequenceEqual(
                    new[] { "html_url", "run_url", "workflow_run_id" },
                    StringComparer.Ordinal) ||
                !PositiveDecimal(runId) ||
                root.GetProperty("run_url").GetString() !=
                    $"https://api.github.com/repos/{repository}/actions/runs/{runId}" ||
                root.GetProperty("html_url").GetString() !=
                    $"https://github.com/{repository}/actions/runs/{runId}")
            {
                throw new InvalidDataException("producer_dispatch_response_invalid");
            }
            return runId;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or
            KeyNotFoundException)
        {
            throw new InvalidDataException("producer_dispatch_response_invalid", exception);
        }
    }

    private sealed record ProducerAuthorization(
        string Repository,
        string[] OperationIds,
        string MaterializerSourceSha256,
        string MaterializerBuildSha256,
        string PhaseMaterializerSourceSha256,
        string PhaseMaterializerBuildSha256,
        ProducerTargetAuthority[] Targets,
        ProducerCommand[] Commands);

    private sealed record MutationTargetDescriptor(
        string TargetKind,
        string OperationId,
        string Role,
        string Scope,
        string RequiredReadbackPhase,
        string RequiredSourcePrefix,
        string[] RequiredPreconditionPhases,
        string[] RequiredPreconditionSourcePrefixes);

    private sealed record ProducerCommand(
        ProducerTargetAuthority Target,
        HttpMethod Method,
        string Route,
        byte[]? Body,
        HttpStatusCode ExpectedStatus);
}

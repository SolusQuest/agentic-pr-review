using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceContracts;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofCapture;

/// <summary>
/// Derives a narrow, canonical role receipt from phase fragments already captured by the
/// host. It is deliberately a reader: it neither owns credentials nor contacts GitHub.
/// </summary>
internal static class EnrollmentObservationMaterializer
{
    private const string ReceiptKind = "apr-r4-e2p-enrollment-role-observation-v1";
    private static readonly string[] Roles =
    [
        "normal-bootstrap", "normal-continuation", "stale-protected", "stale-follow-on",
    ];

    internal sealed record ObservationSourceReceipt(
        string SourceId,
        string Phase,
        string FragmentSha256,
        string FragmentPhysicalIdentitySha256,
        string BodySha256,
        string BodySize,
        string BodyPhysicalIdentitySha256);

    internal sealed record EnrollmentRoleObservationReceipt(
        string Kind,
        string ExecutionAuthorizationSha256,
        string DestinationIdentitySha256,
        string MaterializerSourceSha256,
        string MaterializerBuildSha256,
        string OperationId,
        string Role,
        string RunId,
        string RunAttempt,
        string ExpectedEvent,
        string ExpectedRunHeadSha,
        ObservationSourceReceipt[] Sources,
        bool Finalized);

    // This checkpoint is intentionally separate from the receipt emitted to the Node executor.
    // The executor consumes the receipt as data, while the producer uses this pinned, C#-verified
    // checkpoint as the authority for the one stale-ref mutation.  Keeping the wire receipt stable
    // also prevents an accidental expansion of the executor's trusted surface.
    internal sealed record EnrollmentRoleObservationCheckpoint(
        string Kind,
        string ExecutionAuthorizationSha256,
        string DestinationIdentitySha256,
        string MaterializerSourceSha256,
        string MaterializerBuildSha256,
        string ProducerTargetDescriptorSha256,
        string OperationId,
        string Role,
        string RunId,
        string RunAttempt,
        string ExpectedEvent,
        string ExpectedRunHeadSha,
        string ReceiptSha256,
        ProducerJournalCheckpointDocument ProducerJournalCheckpoint,
        string ProducerJournalCheckpointSha256,
        bool Finalized);

    private const string CheckpointKind = "apr-r4-e3-enrollment-role-checkpoint-v1";

    public static bool IsCommand(string[] args) => args.Length > 0 &&
        args[0] == "enrollment-observation";

    public static int Run(string[] args)
    {
        try
        {
            var options = Parse(args.Skip(1).ToArray());
            var root = RestrictedEvidenceRoot.Open(
                options["--restricted-root"],
                options["--destination-identity"],
                [options["--repository-root"], options["--worktree-root"]]);
            var authorization = PhaseFragmentMaterializer.ReadAuthorization(
                root,
                options["--execution-authorization"],
                options["--execution-authorization-sha256"],
                options["--destination-identity"]);
            if (authorization.MaterializerBuildSha256 != AssemblySha256())
            {
                throw new InvalidDataException("enrollment_observation_build_invalid");
            }
            var journal = ProducerOutcomeJournal.Open(
                root,
                options["--producer-journal-directory"],
                options["--execution-authorization-sha256"]);
            var receipt = Materialize(root, authorization, journal, options);
            MaterializeCheckpointCreateOrVerify(root, authorization, journal, options, receipt);
            WriteCanonicalReceipt(Console.Out, receipt);
            return 0;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or
            UnauthorizedAccessException or CryptographicException or JsonException or ArgumentException or
            InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            Console.Error.WriteLine("APR_R4_E2P_ENROLLMENT_OBSERVATION_INVALID");
            return 1;
        }
    }

    internal static void WriteCanonicalReceipt(TextWriter output, EnrollmentRoleObservationReceipt receipt)
    {
        var bytes = CanonicalEvidence.Encode(receipt, EvidenceJson.Options);
        try
        {
            // CanonicalEvidence already terminates JSON with exactly one LF. A second
            // WriteLine would make the cross-language exact-byte receipt unverifiable.
            output.Write(System.Text.Encoding.UTF8.GetString(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal static EnrollmentRoleObservationReceipt Materialize(
        RestrictedEvidenceRoot root,
        PhaseFragmentMaterializer.PhaseAuthorization authorization,
        ProducerOutcomeJournal journal,
        IReadOnlyDictionary<string, string> options)
    {
        var role = options["--role"];
        var operationId = options["--operation-id"];
        var runId = options["--run-id"];
        var expectedEvent = options["--expected-event"];
        var expectedHead = options["--expected-run-head"];
        if (!Roles.Contains(role, StringComparer.Ordinal) ||
            !authorization.OperationIds.Contains(operationId, StringComparer.Ordinal) ||
            !PositiveDecimal(runId) || expectedEvent is not ("workflow_run" or "workflow_dispatch") ||
            !Hex40(expectedHead) || journal.Authority.Repository != authorization.Repository ||
            !journal.Authority.OperationIds.SequenceEqual(authorization.OperationIds, StringComparer.Ordinal) ||
            !RoleMatchesOperation(role, operationId, authorization.OperationIds) ||
            !RoleMatchesEvent(role, expectedEvent))
        {
            throw new InvalidDataException("enrollment_observation_descriptor_invalid");
        }

        ValidateProducerTarget(journal.Authority.Targets, role, operationId, expectedEvent, expectedHead);

        var partial = PhaseFragmentJournal.ReadPartial(
            root,
            options["--package-name"],
            authorization.OperationIds,
            options["--execution-authorization-sha256"],
            authorization.MaterializerSourceSha256,
            authorization.MaterializerBuildSha256,
            journal);
        if (partial.Fragments.Length != partial.Sources.Length)
        {
            throw new InvalidDataException("enrollment_observation_fragment_invalid");
        }
        var pairs = partial.Fragments.Zip(partial.Sources)
            .Select(pair => (Fragment: pair.First, Source: pair.Second)).ToArray();
        var prefix = $"enrollment-{role}";
        var target = ValidateProducerTarget(journal.Authority.Targets, role, operationId, expectedEvent, expectedHead);
        var run = RequireOne(pairs, $"{prefix}-run-{runId}:page:1", $"{prefix}-terminal", operationId);
        ValidateTerminal(root, options["--package-name"], run.Source, run.Fragment, authorization.Repository,
            runId, expectedEvent, target);
        var jobs = RequireMany(pairs, $"{prefix}-jobs-run-{runId}:page:", $"{prefix}-jobs", operationId);
        ValidateJobs(root, options["--package-name"], jobs, authorization.Repository, runId, role);

        // The phase materializer, rather than the executor, captures the complete discovery
        // and exact PR readback.  Bind the selected run to those authenticated captures before
        // accepting it as a role observation.
        var discovery = RequireMany(pairs, $"{prefix}-discovery-run-{runId}:page:",
            $"{prefix}-discovery", operationId);
        var pull = RequireOne(pairs, $"{prefix}-pull-run-{runId}:page:1", $"{prefix}-pull", operationId);
        var discoveryRuns = ValidateDiscovery(root, options["--package-name"], discovery,
            authorization.Repository, target, runId);
        ValidatePull(root, options["--package-name"], pull.Source, pull.Fragment,
            authorization.Repository, target);
        var discoveryFragment = ReadFragment(root, options["--package-name"], discovery[0].Fragment);
        var terminalFragment = ReadFragment(root, options["--package-name"], run.Fragment);
        if (discovery.Any(item => item.Fragment.Sequence <= run.Fragment.Sequence ||
                item.Source.RequestStartedUnixMilliseconds < run.Source.ResponseReceivedUnixMilliseconds ||
                item.Source.ResponseReceivedUnixMilliseconds < run.Source.ResponseReceivedUnixMilliseconds) ||
            discoveryFragment.Sequence <= terminalFragment.Sequence)
        {
            throw new InvalidDataException("enrollment_observation_discovery_order_invalid");
        }
        journal.ValidateEnrollmentRoleRunBinding(role, operationId, runId, expectedEvent,
            discovery[0].Source.RequestStartedUnixMilliseconds,
            discoveryFragment.ProducerJournalCheckpoint,
            discoveryFragment.ProducerJournalCheckpointSha256,
            discoveryRuns);

        var selected = new List<(PhaseFragmentReference Fragment, CaptureManifestSource Source)> { run };
        selected.AddRange(jobs);
        selected.AddRange(discovery);
        selected.Add(pull);
        if (Protected(role))
        {
            var pending = RequireOne(pairs, $"{prefix}-pending-run-{runId}:page:1", $"{prefix}-pending", operationId);
            var approval = RequireOne(pairs, $"{prefix}-approvals-run-{runId}:page:1", $"{prefix}-approval", operationId);
            ValidatePending(root, options["--package-name"], pending.Source, pending.Fragment, authorization, runId);
            ValidateApproval(root, options["--package-name"], approval.Source, approval.Fragment, authorization, runId);
            selected.Add(pending);
            selected.Add(approval);
        }
        else if (pairs.Any(pair => pair.Source.SourceId.StartsWith(prefix + "-pending-", StringComparison.Ordinal) ||
            pair.Source.SourceId.StartsWith(prefix + "-approvals-", StringComparison.Ordinal)))
        {
            throw new InvalidDataException("enrollment_observation_unexpected_protection");
        }

        foreach (var item in selected)
        {
            var fragment = ReadFragment(root, options["--package-name"], item.Fragment);
            journal.ValidateEnrollmentFragmentCheckpoint(role, operationId,
                fragment.ProducerJournalCheckpoint, fragment.ProducerJournalCheckpointSha256);
        }

        var sources = selected.OrderBy(item => item.Fragment.Sequence).Select(item => new ObservationSourceReceipt(
            item.Source.SourceId, item.Source.Phase, item.Fragment.Sha256,
            item.Fragment.PhysicalIdentitySha256, item.Source.BodySha256, item.Source.BodySize,
            item.Source.BodyFileIdentity)).ToArray();
        return new EnrollmentRoleObservationReceipt(
            ReceiptKind, options["--execution-authorization-sha256"], root.DestinationIdentitySha256,
            authorization.MaterializerSourceSha256, authorization.MaterializerBuildSha256, operationId, role,
            runId, "1", expectedEvent, expectedHead, sources, Finalized: true);
    }

    internal static void MaterializeCheckpointCreateOrVerify(
        RestrictedEvidenceRoot root,
        PhaseFragmentMaterializer.PhaseAuthorization authorization,
        ProducerOutcomeJournal journal,
        IReadOnlyDictionary<string, string> options,
        EnrollmentRoleObservationReceipt receipt)
    {
        var target = ValidateProducerTarget(
            journal.Authority.Targets,
            receipt.Role,
            receipt.OperationId,
            receipt.ExpectedEvent,
            receipt.ExpectedRunHeadSha);
        var terminal = journal.Entries.LastOrDefault(entry => entry.AuthorityId == target.AuthorityId);
        if (terminal is null || terminal.Outcome is not ("committed" or "reconciled-committed"))
        {
            throw new InvalidDataException("enrollment_observation_checkpoint_invalid");
        }
        // Bind the receipt to this role's terminal producer prefix rather than the mutable journal
        // head.  Later readback records (and a crash-resume after the ref write) cannot rewrite a
        // previously authenticated role receipt.
        var checkpoint = journal.CheckpointAt(terminal.Sequence);
        var document = new EnrollmentRoleObservationCheckpoint(
            CheckpointKind,
            receipt.ExecutionAuthorizationSha256,
            receipt.DestinationIdentitySha256,
            receipt.MaterializerSourceSha256,
            receipt.MaterializerBuildSha256,
            target.TargetDescriptorSha256,
            receipt.OperationId,
            receipt.Role,
            receipt.RunId,
            receipt.RunAttempt,
            receipt.ExpectedEvent,
            receipt.ExpectedRunHeadSha,
            CanonicalSha256(receipt),
            checkpoint.Document,
            checkpoint.Sha256,
            Finalized: true);
        var bytes = CanonicalEvidence.Encode(document, EvidenceJson.Options);
        try
        {
            var relativePath = CheckpointPath(options["--package-name"], receipt.Role);
            var packagePath = RestrictedEvidenceRoot.ResolveChildPath(root.Path, options["--package-name"]);
            var absolutePath = RestrictedEvidenceRoot.ResolveChildPath(packagePath, CheckpointFileName(receipt.Role));
            if (EvidenceFileHandle.PathEntryExists(absolutePath))
            {
                using var lease = root.AcquirePinnedFile(relativePath, EvidenceLimits.MaximumDocumentBytes);
                if (!lease.Bytes.AsSpan().SequenceEqual(bytes))
                {
                    throw new InvalidDataException("enrollment_observation_checkpoint_invalid");
                }
                return;
            }
            _ = root.WritePinnedFileCreateNew(relativePath, bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal static void ValidatePinnedCheckpointForStaleAdvance(
        RestrictedEvidenceRoot root,
        PhaseFragmentMaterializer.PhaseAuthorization authorization,
        ProducerOutcomeJournal journal,
        IReadOnlyDictionary<string, string> options)
    {
        const string role = "stale-protected";
        var relativePath = CheckpointPath(options["--package-name"], role);
        using var lease = root.AcquirePinnedFile(relativePath, EvidenceLimits.MaximumDocumentBytes);
        var document = ParseCanonicalCheckpoint(lease.Bytes);
        if (document.Kind != CheckpointKind || !document.Finalized ||
            document.ExecutionAuthorizationSha256 != options["--execution-authorization-sha256"] ||
            document.DestinationIdentitySha256 != root.DestinationIdentitySha256 ||
            document.MaterializerSourceSha256 != authorization.MaterializerSourceSha256 ||
            document.MaterializerBuildSha256 != authorization.MaterializerBuildSha256 ||
            document.Role != role || document.RunAttempt != "1" ||
            !PositiveDecimal(document.RunId) || !Hex40(document.ExpectedRunHeadSha) ||
            document.OperationId != authorization.OperationIds[1] ||
            !Sha256(document.ReceiptSha256) || !Sha256(document.ProducerTargetDescriptorSha256) ||
            !Sha256(document.ProducerJournalCheckpointSha256))
        {
            throw new InvalidDataException("enrollment_observation_checkpoint_invalid");
        }
        var target = ValidateProducerTarget(
            journal.Authority.Targets,
            document.Role,
            document.OperationId,
            document.ExpectedEvent,
            document.ExpectedRunHeadSha);
        if (target.TargetDescriptorSha256 != document.ProducerTargetDescriptorSha256)
        {
            throw new InvalidDataException("enrollment_observation_checkpoint_invalid");
        }
        journal.ValidateCheckpoint(document.ProducerJournalCheckpoint,
            document.ProducerJournalCheckpointSha256);
        var staleProtected = journal.Entries.LastOrDefault(entry =>
            entry.AuthorityId == target.AuthorityId);
        var staleFollowOn = journal.Authority.Targets.Single(item =>
            item.TargetKind == "trigger" && item.Role == "stale-follow-on");
        if (staleProtected is null || staleProtected.Outcome is not ("committed" or "reconciled-committed") ||
            document.ProducerJournalCheckpoint.EntryCount < staleProtected.Sequence ||
            journal.Entries.Any(entry =>
                entry.AuthorityId == staleFollowOn.AuthorityId))
        {
            throw new InvalidDataException("enrollment_observation_checkpoint_invalid");
        }

        // Re-materialize from the pinned phase fragments.  This makes the checkpoint a concise,
        // durable index of authenticated evidence, not a caller-provided claim about a run.
        var revalidated = Materialize(root, authorization, journal,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["--package-name"] = options["--package-name"],
                ["--execution-authorization-sha256"] = options["--execution-authorization-sha256"],
                ["--role"] = document.Role,
                ["--operation-id"] = document.OperationId,
                ["--run-id"] = document.RunId,
                ["--expected-event"] = document.ExpectedEvent,
                ["--expected-run-head"] = document.ExpectedRunHeadSha,
            });
        if (CanonicalSha256(revalidated) != document.ReceiptSha256)
        {
            throw new InvalidDataException("enrollment_observation_checkpoint_invalid");
        }
    }

    private static string CheckpointPath(string packageName, string role) =>
        $"{packageName}/{CheckpointFileName(role)}";

    private static string CheckpointFileName(string role) =>
        $"enrollment-observation-{role}.checkpoint.json";

    private static EnrollmentRoleObservationCheckpoint ParseCanonicalCheckpoint(byte[] bytes)
    {
        try
        {
            var document = JsonSerializer.Deserialize<EnrollmentRoleObservationCheckpoint>(bytes, EvidenceJson.Options) ??
                throw new InvalidDataException("enrollment_observation_checkpoint_invalid");
            var canonical = CanonicalEvidence.Encode(document, EvidenceJson.Options);
            try
            {
                if (!bytes.AsSpan().SequenceEqual(canonical))
                {
                    throw new InvalidDataException("enrollment_observation_checkpoint_invalid");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(canonical);
            }
            return document;
        }
        catch (JsonException)
        {
            throw new InvalidDataException("enrollment_observation_checkpoint_invalid");
        }
    }

    private static string CanonicalSha256<T>(T value)
    {
        var bytes = CanonicalEvidence.Encode(value, EvidenceJson.Options);
        try
        {
            return CanonicalEvidence.Sha256(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static (PhaseFragmentReference Fragment, CaptureManifestSource Source) RequireOne(
        IEnumerable<(PhaseFragmentReference Fragment, CaptureManifestSource Source)> pairs,
        string sourceId,
        string phase,
        string operationId)
    {
        var values = pairs.Where(pair => pair.Source.SourceId == sourceId && pair.Source.Phase == phase &&
            pair.Source.OperationId == operationId).ToArray();
        if (values.Length != 1) throw new InvalidDataException("enrollment_observation_sources_invalid");
        return values[0];
    }

    private static (PhaseFragmentReference Fragment, CaptureManifestSource Source)[] RequireMany(
        IEnumerable<(PhaseFragmentReference Fragment, CaptureManifestSource Source)> pairs,
        string prefix,
        string phase,
        string operationId)
    {
        var values = pairs.Where(pair => pair.Source.SourceId.StartsWith(prefix, StringComparison.Ordinal) &&
                pair.Source.Phase == phase && pair.Source.OperationId == operationId)
            .OrderBy(pair => pair.Source.Page).ToArray();
        if (values.Length == 0 || values.Select(item => item.Source.Page).Distinct().Count() != values.Length ||
            !values.Select((item, index) => item.Source.Page == index + 1).All(value => value))
        {
            throw new InvalidDataException("enrollment_observation_sources_invalid");
        }
        return values;
    }

    private static void ValidateTerminal(RestrictedEvidenceRoot root, string package, CaptureManifestSource source,
        PhaseFragmentReference fragment, string repository, string runId, string expectedEvent,
        ProducerTargetAuthority target)
    {
        ValidateSource(root, package, source, fragment,
            $"/repos/{repository}/actions/runs/{runId}", 1, 200, null);
        using var lease = root.AcquirePinnedFile($"{package}/{source.BodyPath}", EvidenceLimits.MaximumDocumentBytes);
        using var document = JsonDocument.Parse(lease.Bytes);
        var value = document.RootElement;
        if (value.GetProperty("id").GetRawText() != runId || value.GetProperty("run_attempt").GetRawText() != "1" ||
            value.GetProperty("event").GetString() != expectedEvent ||
            value.GetProperty("path").GetString() != ".github/workflows/r4-trusted-proof.yml" ||
            value.GetProperty("status").GetString() != "completed" || value.GetProperty("conclusion").GetString() != "success" ||
            value.GetProperty("head_sha").GetString() != target.ExpectedHeadSha ||
            value.GetProperty("head_branch").GetString() != target.ExpectedHeadBranch ||
            value.GetProperty("display_title").GetString() != ExpectedDisplayTitle(target) ||
            value.GetProperty("head_repository").GetProperty("full_name").GetString() != target.ExpectedFixtureRepository ||
            !HasExactTerminalPullRequestBinding(value, expectedEvent, target.ExpectedPullRequestNumber))
        {
            throw new InvalidDataException("enrollment_observation_run_invalid");
        }
    }

    /// <summary>
    /// The terminal endpoint is a sealed evidence schema: a missing <c>pull_requests</c> field
    /// is not equivalent to an empty dispatch array. workflow_run has one exact fixture PR;
    /// workflow_dispatch has GitHub's explicit empty array because its PR identity is instead
    /// bound by the separately captured fixture pull.
    /// </summary>
    internal static bool HasExactTerminalPullRequestBinding(
        JsonElement value,
        string expectedEvent,
        string expectedPullRequestNumber)
    {
        if (!value.TryGetProperty("pull_requests", out var pulls) ||
            pulls.ValueKind != JsonValueKind.Array)
        {
            return false;
        }
        if (expectedEvent == "workflow_dispatch") return pulls.GetArrayLength() == 0;
        if (expectedEvent != "workflow_run" || pulls.GetArrayLength() != 1 ||
            pulls[0].ValueKind != JsonValueKind.Object ||
            !pulls[0].TryGetProperty("number", out var number))
        {
            return false;
        }
        return number.ValueKind == JsonValueKind.Number &&
            number.GetRawText() == expectedPullRequestNumber;
    }

    private static void ValidateJobs(RestrictedEvidenceRoot root, string package,
        IReadOnlyList<(PhaseFragmentReference Fragment, CaptureManifestSource Source)> sources,
        string repository, string runId, string role)
    {
        var jobs = new Dictionary<string, (string Status, string Conclusion)>(StringComparer.Ordinal);
        for (var index = 0; index < sources.Count; index++)
        {
            var item = sources[index];
            var expectedNext = index + 1 < sources.Count ? sources[index + 1].Source.Route : null;
            var endpoint = $"/repos/{repository}/actions/runs/{runId}/attempts/1/jobs";
            if (!item.Source.Route.StartsWith(endpoint, StringComparison.Ordinal) ||
                (item.Source.Route.Length > endpoint.Length && item.Source.Route[endpoint.Length] != '?'))
            {
                throw new InvalidDataException("enrollment_observation_jobs_invalid");
            }
            ValidateSource(root, package, item.Source, item.Fragment,
                item.Source.Route, index + 1, 200, expectedNext);
            using var lease = root.AcquirePinnedFile($"{package}/{item.Source.BodyPath}", EvidenceLimits.MaximumDocumentBytes);
            using var document = JsonDocument.Parse(lease.Bytes);
            var value = document.RootElement;
            foreach (var job in value.GetProperty("jobs").EnumerateArray())
            {
                var name = job.GetProperty("name").GetString() ?? string.Empty;
                if (job.GetProperty("run_id").GetRawText() != runId || job.GetProperty("run_attempt").GetRawText() != "1" ||
                    !jobs.TryAdd(name, (job.GetProperty("status").GetString() ?? string.Empty,
                        job.GetProperty("conclusion").GetString() ?? string.Empty)))
                {
                    throw new InvalidDataException("enrollment_observation_jobs_invalid");
                }
            }
        }
        var protectedJobs = Protected(role);
        var selected = role == "normal-continuation" ? "workflow-dispatch-review" : "workflow-run-review";
        if (jobs.Count != 3 || !jobs.TryGetValue("authorization-preflight", out var preflight) ||
            preflight != ("completed", "success") ||
            !jobs.TryGetValue("workflow-run-review", out var workflowRun) ||
            !jobs.TryGetValue("workflow-dispatch-review", out var workflowDispatch) ||
            (protectedJobs
                ? (selected == "workflow-run-review"
                    ? workflowRun != ("completed", "success") || workflowDispatch != ("completed", "skipped")
                    : workflowRun != ("completed", "skipped") || workflowDispatch != ("completed", "success"))
                : workflowRun != ("completed", "skipped") || workflowDispatch != ("completed", "skipped")))
        {
            throw new InvalidDataException("enrollment_observation_jobs_invalid");
        }
    }

    private static void ValidatePending(RestrictedEvidenceRoot root, string package, CaptureManifestSource source,
        PhaseFragmentReference fragment, PhaseFragmentMaterializer.PhaseAuthorization authorization, string runId)
    {
        ValidateSource(root, package, source, fragment,
            $"/repos/{authorization.Repository}/actions/runs/{runId}/pending_deployments", 1, 200, null);
        using var lease = root.AcquirePinnedFile($"{package}/{source.BodyPath}", EvidenceLimits.MaximumDocumentBytes);
        using var document = JsonDocument.Parse(lease.Bytes);
        var entries = document.RootElement.EnumerateArray().ToArray();
        if (entries.Length != 1 || entries[0].GetProperty("environment").GetProperty("id").GetRawText() != authorization.EnvironmentId ||
            entries[0].GetProperty("environment").GetProperty("name").GetString() != authorization.EnvironmentName ||
            !entries[0].GetProperty("reviewers").EnumerateArray().Select(item => item.GetProperty("reviewer").GetProperty("id").GetRawText())
                .SequenceEqual(authorization.RequiredReviewerIds, StringComparer.Ordinal))
        {
            throw new InvalidDataException("enrollment_observation_pending_invalid");
        }
    }

    private static void ValidateApproval(RestrictedEvidenceRoot root, string package, CaptureManifestSource source,
        PhaseFragmentReference fragment, PhaseFragmentMaterializer.PhaseAuthorization authorization, string runId)
    {
        ValidateSource(root, package, source, fragment,
            $"/repos/{authorization.Repository}/actions/runs/{runId}/approvals", 1, 200, null);
        using var lease = root.AcquirePinnedFile($"{package}/{source.BodyPath}", EvidenceLimits.MaximumDocumentBytes);
        using var document = JsonDocument.Parse(lease.Bytes);
        var entries = document.RootElement.EnumerateArray().ToArray();
        if (entries.Length != 1 || entries[0].GetProperty("state").GetString() != "approved" ||
            !authorization.RequiredReviewerIds.Contains(entries[0].GetProperty("user").GetProperty("id").GetRawText(), StringComparer.Ordinal) ||
            !entries[0].GetProperty("environments").EnumerateArray().Any(item =>
                item.GetProperty("id").GetRawText() == authorization.EnvironmentId &&
                item.GetProperty("name").GetString() == authorization.EnvironmentName))
        {
            throw new InvalidDataException("enrollment_observation_approval_invalid");
        }
    }

    private static ProducerEnrollmentDiscoveryRun[] ValidateDiscovery(
        RestrictedEvidenceRoot root,
        string package,
        IReadOnlyList<(PhaseFragmentReference Fragment, CaptureManifestSource Source)> sources,
        string repository,
        ProducerTargetAuthority target,
        string selectedRunId)
    {
        var endpoint = $"/repos/{repository}/actions/workflows/r4-trusted-proof.yml/runs";
        var result = new List<ProducerEnrollmentDiscoveryRun>();
        int? totalCount = null;
        for (var index = 0; index < sources.Count; index++)
        {
            var item = sources[index];
            var next = index + 1 < sources.Count ? sources[index + 1].Source.Route : null;
            if ((index == 0
                    ? item.Source.Route != endpoint + "?per_page=100"
                    : item.Source.Route != sources[index - 1].Source.NextRoute) ||
                item.Source.NextRoute != next)
            {
                throw new InvalidDataException("enrollment_observation_discovery_invalid");
            }
            ValidateSource(root, package, item.Source, item.Fragment, item.Source.Route, index + 1, 200, next);
            using var lease = root.AcquirePinnedFile($"{package}/{item.Source.BodyPath}", EvidenceLimits.MaximumDocumentBytes);
            using var document = JsonDocument.Parse(lease.Bytes);
            var currentTotal = document.RootElement.GetProperty("total_count").GetInt32();
            var runs = document.RootElement.GetProperty("workflow_runs");
            totalCount ??= currentTotal;
            if (currentTotal != totalCount || currentTotal < 0 || currentTotal > EvidenceLimits.MaximumRecords ||
                runs.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("enrollment_observation_discovery_invalid");
            }
            foreach (var run in runs.EnumerateArray())
            {
                var pullRequests = run.TryGetProperty("pull_requests", out var pulls) &&
                    pulls.ValueKind == JsonValueKind.Array
                    ? pulls.EnumerateArray().Select(item => item.GetProperty("number").GetRawText()).ToArray()
                    : [];
                var conclusion = run.GetProperty("conclusion").ValueKind == JsonValueKind.Null
                    ? null : run.GetProperty("conclusion").GetString();
                var runId = run.GetProperty("id").GetRawText();
                var attempt = run.GetProperty("run_attempt").GetRawText();
                var @event = run.GetProperty("event").GetString() ?? string.Empty;
                var status = run.GetProperty("status").GetString() ?? string.Empty;
                var workflowHead = run.GetProperty("head_sha").GetString() ?? string.Empty;
                var workflowBranch = run.GetProperty("head_branch").GetString() ?? string.Empty;
                var displayTitle = run.GetProperty("display_title").GetString() ?? string.Empty;
                var runRepository = run.GetProperty("head_repository").GetProperty("full_name").GetString() ?? string.Empty;
                var workflowPath = run.GetProperty("path").GetString() ?? string.Empty;
                if (!PositiveDecimal(runId) || !PositiveDecimal(attempt) || @event is not ("workflow_run" or "workflow_dispatch") ||
                    status is not ("completed" or "in_progress" or "pending" or "queued" or "requested" or "waiting") ||
                    (status == "completed" ? conclusion is not ("action_required" or "cancelled" or "failure" or "neutral" or "skipped" or "stale" or "startup_failure" or "success" or "timed_out") : conclusion is not null) ||
                    !Hex40(workflowHead) || string.IsNullOrWhiteSpace(workflowBranch) ||
                    string.IsNullOrWhiteSpace(displayTitle) || runRepository != repository ||
                    workflowPath != ".github/workflows/r4-trusted-proof.yml" ||
                    pullRequests.Any(number => !PositiveDecimal(number)) || pullRequests.Distinct(StringComparer.Ordinal).Count() != pullRequests.Length ||
                    !DateTimeOffset.TryParse(run.GetProperty("created_at").GetString(),
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal |
                            System.Globalization.DateTimeStyles.AdjustToUniversal, out var created))
                {
                    throw new InvalidDataException("enrollment_observation_discovery_invalid");
                }
                result.Add(new ProducerEnrollmentDiscoveryRun(
                    runId, attempt, @event, status, conclusion, workflowHead, workflowBranch, pullRequests,
                    runRepository, workflowPath, created.ToUnixTimeMilliseconds(), displayTitle));
            }
        }
        if (totalCount is null || result.Count != totalCount || result.Count == 0 || result.Count > EvidenceLimits.MaximumRecords ||
            result.Select(item => item.RunId).Distinct(StringComparer.Ordinal).Count() != result.Count ||
            !result.Any(item => item.RunId == selectedRunId && item.Event == target.ExpectedEvent))
        {
            throw new InvalidDataException("enrollment_observation_discovery_invalid");
        }
        return [.. result];
    }

    private static void ValidatePull(RestrictedEvidenceRoot root, string package, CaptureManifestSource source,
        PhaseFragmentReference fragment, string repository, ProducerTargetAuthority target)
    {
        ValidateSource(root, package, source, fragment,
            $"/repos/{repository}/pulls/{target.ExpectedPullRequestNumber}", 1, 200, null);
        using var lease = root.AcquirePinnedFile($"{package}/{source.BodyPath}", EvidenceLimits.MaximumDocumentBytes);
        using var document = JsonDocument.Parse(lease.Bytes);
        var value = document.RootElement;
        if (value.GetProperty("number").GetRawText() != target.ExpectedPullRequestNumber ||
            value.GetProperty("state").GetString() != "open" || value.GetProperty("draft").GetBoolean() ||
            value.GetProperty("head").GetProperty("sha").GetString() != target.ExpectedFixtureHeadSha ||
            value.GetProperty("head").GetProperty("ref").GetString() != target.ExpectedFixtureHeadRef ||
            value.GetProperty("head").GetProperty("repo").GetProperty("full_name").GetString() != target.ExpectedFixtureRepository ||
            value.GetProperty("base").GetProperty("sha").GetString() != target.ExpectedFixtureBaseSha ||
            value.GetProperty("base").GetProperty("ref").GetString() != "main" ||
            value.GetProperty("base").GetProperty("repo").GetProperty("full_name").GetString() != target.ExpectedFixtureRepository)
        {
            throw new InvalidDataException("enrollment_observation_pull_invalid");
        }
    }

    private static PhaseFragmentDocument ReadFragment(RestrictedEvidenceRoot root, string package,
        PhaseFragmentReference reference)
    {
        using var lease = root.AcquirePinnedFile($"{package}/{reference.Path}", EvidenceLimits.MaximumDocumentBytes);
        var value = JsonSerializer.Deserialize<PhaseFragmentDocument>(lease.Bytes, EvidenceJson.Options) ??
            throw new InvalidDataException("enrollment_observation_fragment_invalid");
        var canonical = CanonicalEvidence.Encode(value, EvidenceJson.Options);
        try
        {
            if (!lease.Bytes.AsSpan().SequenceEqual(canonical) || CanonicalEvidence.Sha256(lease.Bytes) != reference.Sha256 ||
                lease.Identity != reference.PhysicalIdentitySha256)
            {
                throw new InvalidDataException("enrollment_observation_fragment_invalid");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
        }
        return value;
    }

    private static void ValidateSource(RestrictedEvidenceRoot root, string package, CaptureManifestSource source,
        PhaseFragmentReference fragment, string route, int page, int status, string? nextRoute)
    {
        if (source.Route != route || source.Page != page || source.Status != status || source.NextRoute != nextRoute ||
            !Sha256(fragment.Sha256) || !Sha256(fragment.PhysicalIdentitySha256) || !Sha256(source.BodySha256) ||
            !Sha256(source.BodyFileIdentity) || !long.TryParse(source.BodySize, out var size) || size < 1)
        {
            throw new InvalidDataException("enrollment_observation_source_invalid");
        }
        using var fragmentLease = root.AcquirePinnedFile($"{package}/{fragment.Path}", EvidenceLimits.MaximumDocumentBytes);
        using var bodyLease = root.AcquirePinnedFile($"{package}/{source.BodyPath}", EvidenceLimits.MaximumDocumentBytes);
        if (CanonicalEvidence.Sha256(fragmentLease.Bytes) != fragment.Sha256 ||
            fragmentLease.Identity != fragment.PhysicalIdentitySha256 || CanonicalEvidence.Sha256(bodyLease.Bytes) != source.BodySha256 ||
            bodyLease.Bytes.Length != size || bodyLease.Identity != source.BodyFileIdentity)
        {
            throw new InvalidDataException("enrollment_observation_source_invalid");
        }
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var names = new[]
        {
            "--restricted-root", "--destination-identity", "--repository-root", "--worktree-root",
            "--execution-authorization", "--execution-authorization-sha256", "--producer-journal-directory",
            "--package-name", "--role", "--run-id", "--expected-event", "--expected-run-head", "--operation-id",
        };
        if (args.Length != names.Length * 2) throw new InvalidDataException("arguments_invalid");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (!names.Contains(args[index], StringComparer.Ordinal) || !result.TryAdd(args[index], args[index + 1]))
            {
                throw new InvalidDataException("arguments_invalid");
            }
        }
        if (names.Any(name => !result.ContainsKey(name)) || !Sha256(result["--execution-authorization-sha256"]) ||
            !Sha256(result["--operation-id"]) || !RestrictedEvidenceRoot.IsSinglePathSegment(result["--package-name"]) ||
            !RestrictedEvidenceRoot.IsSinglePathSegment(result["--producer-journal-directory"]))
        {
            throw new InvalidDataException("arguments_invalid");
        }
        return result;
    }

    private static bool Protected(string role) => role is "normal-bootstrap" or "normal-continuation" or "stale-protected";
    internal static ProducerTargetAuthority ValidateProducerTarget(
        IEnumerable<ProducerTargetAuthority> targets,
        string role,
        string operationId,
        string expectedEvent,
        string expectedHead)
    {
        var target = targets.SingleOrDefault(item => item.Role == role &&
            item.OperationId == operationId && item.ExpectedEvent == expectedEvent);
        if (target is null || target.ExpectedHeadSha != expectedHead || target.ExpectedHeadBranch != "main" ||
            !PositiveDecimal(target.ExpectedPullRequestNumber) || !Hex40(target.ExpectedFixtureHeadSha) ||
            !Hex40(target.ExpectedFixtureBaseSha) || target.ExpectedFixtureBaseSha != target.ExpectedHeadSha ||
            target.ExpectedFixtureRepository.Length == 0 || !target.ExpectedFixtureHeadRef.StartsWith("r4-trusted-proof/", StringComparison.Ordinal))
        {
            throw new InvalidDataException("enrollment_observation_producer_invalid");
        }
        return target;
    }
    private static string ExpectedDisplayTitle(ProducerTargetAuthority target) =>
        target.ExpectedEvent == "workflow_dispatch"
            ? $"apr-r4-e2p-{target.OperationId}"
            : $"apr-r4-e2p-{target.ExpectedFixtureHeadRef}";
    private static bool RoleMatchesOperation(string role, string operationId, IReadOnlyList<string> operations) =>
        operationId == operations[(role.StartsWith("normal", StringComparison.Ordinal) ? 0 : 1)];
    private static bool RoleMatchesEvent(string role, string @event) =>
        @event == (role == "normal-continuation" ? "workflow_dispatch" : "workflow_run");
    private static string AssemblySha256()
    {
        var location = Assembly.GetExecutingAssembly().Location;
        if (string.IsNullOrWhiteSpace(location)) throw new InvalidDataException("enrollment_observation_build_invalid");
        using var stream = new FileStream(location, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
    private static bool Sha256(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Hex40(string value) => value.Length == 40 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool PositiveDecimal(string value) => value.Length > 0 && value != "0" && value.All(c => c is >= '0' and <= '9') && (value.Length == 1 || value[0] != '0');
}

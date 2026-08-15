using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgenticPrReview.Runtime.ActionHostVerifierFixture;

internal static class FrameworkSupervisor
{
    internal const string RuntimeToken =
        "eyJhbGciOiJub25lIiwidHlwIjoiSldUIn0." +
        "eyJzY3AiOiJBY3Rpb25zLlJlc3VsdHM6YXByMTc4LXByb29mLXJ1bi1iYWNrZW5kLWlkOmFw" +
        "cjE3OC1wcm9vZi1qb2ItYmFja2VuZC1pZCJ9.";

    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(3);

    internal static async Task<int> RunAsync(string[] args)
    {
        if (!OperatingSystem.IsLinux()) return 1;
        var values = ParseArguments(args);
        var root = Required(values, "root");
        var repository = Required(values, "repo");
        var payload = Required(values, "payload");
        var bundle = Required(values, "bundle");
        var record = Required(values, "record");
        var inventory = Required(values, "inventory");
        var golden = Required(values, "golden");
        var canaries = Required(values, "canaries");
        var node = Required(values, "node");

        var prerequisites = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["root"] = Directory.Exists(root),
            ["repository"] = Directory.Exists(repository),
            ["payload"] = File.Exists(payload),
            ["bundle"] = File.Exists(bundle),
            ["record"] = File.Exists(record),
            ["inventory"] = File.Exists(inventory),
            ["golden"] = File.Exists(golden),
            ["canaries"] = File.Exists(canaries),
            ["node"] = File.Exists(node),
            ["single-file"] = File.Exists(payload) &&
                ValidateSingleFile(payload),
            ["replacement-record"] = File.Exists(record) &&
                ValidateReplacementRecord(record),
            ["base-inventory"] = Directory.Exists(repository) &&
                File.Exists(inventory) && ValidateInventory(inventory),
            ["canary-table"] = File.Exists(canaries) &&
                ValidateCanaryTable(canaries),
        };
        if (prerequisites.Any(pair => !pair.Value))
        {
            Console.Error.WriteLine("APR_ACTION_HOST_FRAMEWORK_INVALID " +
                string.Join(',', prerequisites.Where(pair => !pair.Value)
                    .Select(pair => pair.Key)));
            return 1;
        }

        Directory.CreateDirectory(root);
        await using var platform = SyntheticOfficialPlatform.Start(root);
        var cases = new List<CaseResult>();

        cases.Add(await RunCaseAsync(new CaseSpec("dispatch-bootstrap",
            "continuation-seed", "reviewed", ExpectedProviderRequests: 6),
            root, repository, payload,
            bundle, node, platform).ConfigureAwait(false));
        cases.Add(await RunCaseAsync(new CaseSpec("dispatch-continuation",
            "continuation", "state_conflict", ExpectContinuation: true,
            ExpectedProviderRequests: 3),
            root, repository, payload, bundle, node, platform)
            .ConfigureAwait(false));
        cases.Add(await RunCaseAsync(new CaseSpec("artifact-pagination-changed",
            "artifact-pagination-changed", "state_conflict"), root,
            repository, payload, bundle, node, platform).ConfigureAwait(false));
        cases.Add(await RunCaseAsync(new CaseSpec("artifact-pagination-late",
            "artifact-pagination-late", "state_conflict"), root,
            repository, payload, bundle, node, platform).ConfigureAwait(false));
        cases.Add(await RunCaseAsync(new CaseSpec("artifact-list-duplicate",
            "artifact-list-duplicate", "state_conflict"), root,
            repository, payload, bundle, node, platform).ConfigureAwait(false));
        cases.Add(await RunCaseAsync(new CaseSpec("artifact-digest-mismatch",
            "artifact-digest-mismatch", "outcome_ambiguous"), root,
            repository, payload, bundle, node, platform).ConfigureAwait(false));
        cases.Add(await RunCaseAsync(new CaseSpec("artifact-expired",
            "artifact-expired", "outcome_ambiguous"), root, repository,
            payload, bundle, node, platform).ConfigureAwait(false));
        platform.ResetArtifacts();
        cases.Add(await RunCaseAsync(new CaseSpec(
            "artifact-upload-outcome-unknown",
            "artifact-upload-outcome-unknown", "outcome_ambiguous",
            RequiredGlobalEvidence: "upload-outcome-unknown-committed",
            RequiredStateOperation: "upload\tOutcomeUnknown\t"),
            root, repository, payload, bundle, node, platform)
            .ConfigureAwait(false));
        platform.ResetArtifacts();
        cases.Add(await RunCaseAsync(new CaseSpec(
            "artifact-delete-outcome-unknown",
            "artifact-delete-outcome-unknown", "reviewed",
            RequiredGlobalEvidence: "delete-outcome-unknown-committed",
            RequiredStateOperation: "delete\tOutcomeUnknown\t"),
            root, repository, payload, bundle, node, platform)
            .ConfigureAwait(false));
        platform.ResetArtifacts();
        cases.Add(await RunCaseAsync(new CaseSpec("workflow-run",
            "workflow-run", "reviewed", WorkflowRun: true),
            root, repository, payload, bundle, node, platform)
            .ConfigureAwait(false));
        platform.ResetArtifacts();
        cases.Add(await RunCaseAsync(new CaseSpec("delete-exact",
            "delete-exact", "reviewed",
            RequiredGlobalEvidence: "exact-delete-proof"), root, repository,
            payload, bundle, node, platform).ConfigureAwait(false));
        platform.ResetArtifacts();
        cases.Add(await RunCaseAsync(new CaseSpec("inline", "inline",
            "reviewed"), root, repository, payload, bundle, node, platform)
            .ConfigureAwait(false));
        platform.ResetArtifacts();
        cases.Add(await RunCaseAsync(new CaseSpec("inline-warning",
            "inline-warning", "reviewed_with_inline_warnings"),
            root, repository, payload, bundle, node, platform)
            .ConfigureAwait(false));
        cases.Add(await RunCaseAsync(new CaseSpec("unsupported",
            "unsupported", "skipped_untrusted_event", Unsupported: true),
            root, repository, payload, bundle, node, platform)
            .ConfigureAwait(false));
        cases.Add(await RunCaseAsync(new CaseSpec("fork", "fork",
            "skipped_fork"), root, repository, payload, bundle, node,
            platform).ConfigureAwait(false));
        cases.Add(await RunCaseAsync(new CaseSpec("permission", "permission",
            "authorization_failed"), root, repository, payload, bundle,
            node, platform).ConfigureAwait(false));
        cases.Add(await RunCaseAsync(new CaseSpec("wrong-action",
            "wrong-action", "authorization_failed"), root, repository,
            payload, bundle, node, platform).ConfigureAwait(false));
        cases.Add(await RunCaseAsync(new CaseSpec("concurrency",
            "concurrency", "authorization_failed"), root, repository,
            payload, bundle, node, platform).ConfigureAwait(false));
        cases.Add(await RunCaseAsync(new CaseSpec("stale-head", "stale",
            "stale_head"), root, repository, payload, bundle, node, platform)
            .ConfigureAwait(false));
        platform.ResetArtifacts();
        cases.Add(await RunCaseAsync(new CaseSpec("provider-malformed",
            "provider-malformed", "agent_result_invalid"), root, repository,
            payload, bundle, node, platform).ConfigureAwait(false));
        platform.ResetArtifacts();
        cases.Add(await RunCaseAsync(new CaseSpec("public-result",
            "public-result", "agent_result_invalid"), root, repository,
            payload, bundle, node, platform).ConfigureAwait(false));
        platform.ResetArtifacts();
        cases.Add(await RunCaseAsync(new CaseSpec("credentials-missing",
            "sticky", "credentials_missing", MissingProvider: true),
            root, repository, payload, bundle, node, platform)
            .ConfigureAwait(false));
        platform.ResetArtifacts();
        cases.Add(await RunCaseAsync(new CaseSpec("crash-mutation",
            "mutation-crash", "wrapper_failure",
            CrashAfterGate: "mutation-committed"), root, repository, payload,
            bundle, node, platform).ConfigureAwait(false));
        cases.Add(await RunCaseAsync(new CaseSpec("crash-recovery",
            "mutation-recovery", "reviewed", ExpectNoProvider: true,
            ExpectedStickyMutations: 0), root,
            repository, payload, bundle, node, platform).ConfigureAwait(false));
        platform.ResetArtifacts();
        cases.Add(await RunCaseAsync(new CaseSpec("cancel-before-side-effect",
            "cancel-before-side-effect", "cancelled",
            SignalAfterGate: "cancel-before-side-effect-ready",
            ExpectedProviderRequests: 0, ExpectedStickyMutations: 0),
            root, repository, payload, bundle, node, platform)
            .ConfigureAwait(false));
        platform.ResetArtifacts();
        cases.Add(await RunCaseAsync(new CaseSpec("cancel-before-dispatch",
            "cancel-before-dispatch", "state_conflict",
            SignalAfterGate: "cancel-before-dispatch-ready",
            ExpectedProviderRequests: 6, ExpectedStickyMutations: 0), root,
            repository, payload, bundle,
            node, platform).ConfigureAwait(false));
        platform.ResetArtifacts();
        cases.Add(await RunCaseAsync(new CaseSpec("cancel-known-commit",
            "cancel-known-commit", "state_conflict",
            SignalAfterGate: "cancel-known-commit-ready",
            ExpectedProviderRequests: 6, ExpectedStickyMutations: 1), root,
            repository, payload, bundle,
            node, platform).ConfigureAwait(false));
        platform.ResetArtifacts();
        cases.Add(await RunCaseAsync(new CaseSpec("cancel-outcome-unknown",
            "cancel-outcome-unknown", "reviewed",
            SignalAfterGate: "cancel-outcome-unknown-committed",
            ExpectedProviderRequests: 6, ExpectedStickyMutations: 1,
            RequiredScenarioEvidence: "cancel-outcome-unknown-committed"),
            root, repository, payload, bundle, node, platform)
            .ConfigureAwait(false));
        platform.ResetArtifacts();
        cases.Add(await RunCaseAsync(new CaseSpec("cancel-escalation",
            "sticky", "wrapper_failure", SignalAfterHostStart: true,
            ForceEscalation: true, ExpectedProviderRequests: 0,
            ExpectedStickyMutations: 0),
            root, repository, payload, bundle, node, platform)
            .ConfigureAwait(false));
        cases.Add(await RunCaseAsync(new CaseSpec("host-crash", "sticky",
            "wrapper_failure", CrashHost: true), root, repository, payload,
            bundle, node, platform).ConfigureAwait(false));

        var canaryRoutesPassed = EvaluateCanaryRoutes(root, canaries) &&
            !File.Exists(Path.Join(root, "canary-route-violation"));
        if (cases.Any(result => !result.Passed) ||
            cases.Select(result => result.HostPid)
                .Where(value => value > 0).Distinct().Count() < 2 ||
            !canaryRoutesPassed ||
            platform.ArtifactNames.Count < 1 ||
            !File.Exists(Path.Join(root, "official-delete-count")) ||
            !File.Exists(Path.Join(root, "official-signed-download-count")) ||
            !File.Exists(Path.Join(root, "official-finalize-count")))
        {
            await WriteEvidenceAsync(root, payload, platform, cases, false)
                .ConfigureAwait(false);
            return 1;
        }

        var normalized = new
        {
            schema = "apr.action-host.framework-evidence.v2",
            source_inventory_digest = SourceInventoryDigest(repository),
            replacement_record_digest = Sha256(record),
            base_inventory_digest = JsonProperty(inventory,
                "aggregate_sha256"),
            scenarios = cases.Select(result => new
            {
                name = result.Name,
                status = result.ActualStatus ?? "wrapper_failure",
                result.ExitCode,
                result.ProviderRequests,
                result.ToolSequence,
                result.GitHubRequests,
                result.StateOperations,
                result.StickyMutations,
                result.InlineMutations,
                result.CanarySafe,
                result.ContinuationObserved,
            }),
            process = new
            {
                distinct_host_processes = cases.Select(result => result.HostPid)
                    .Where(value => value > 0).Distinct().Count() ==
                    cases.Count(result => result.HostPid > 0),
                all_process_groups_quiet = cases.All(result =>
                    result.ProcessGroupQuiet),
            },
            official_bridge = new
            {
                twirp = ReadInt(root, "official-twirp-count"),
                blob = ReadInt(root, "official-blob-count"),
                rest = ReadInt(root, "official-rest-count"),
                finalize = ReadInt(root, "official-finalize-count"),
                signed_download = ReadInt(root,
                    "official-signed-download-count"),
                delete = ReadInt(root, "official-delete-count"),
            },
            exact_child_environment = cases.All(result =>
                result.ExactEnvironment),
            output_file_unchanged = cases.All(result =>
                result.OutputUnchanged),
            canary_oracle_passed = canaryRoutesPassed,
            canary_observation_digest = CanaryObservationDigest(root),
        };
        var normalizedBytes = JsonSerializer.SerializeToUtf8Bytes(normalized,
            new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllBytesAsync(
            Path.Join(root, "normalized-evidence.json"), normalizedBytes)
            .ConfigureAwait(false);
        var expectedBytes = await File.ReadAllBytesAsync(golden)
            .ConfigureAwait(false);
        if (!JsonEquivalent(normalizedBytes, expectedBytes))
        {
            await WriteEvidenceAsync(root, payload, platform, cases, false)
                .ConfigureAwait(false);
            return 1;
        }

        await WriteEvidenceAsync(root, payload, platform, cases, true)
            .ConfigureAwait(false);
        Console.WriteLine("APR_ACTION_HOST_FRAMEWORK_VERIFY_OK");
        return 0;
    }

    private static async Task<CaseResult> RunCaseAsync(
        CaseSpec spec,
        string root,
        string repository,
        string payload,
        string bundle,
        string node,
        SyntheticOfficialPlatform platform)
    {
        var scenario = Path.Join(root, spec.Name);
        platform.BeginScenario(spec.Mode);
        Directory.CreateDirectory(scenario);
        await File.WriteAllTextAsync(Path.Join(scenario, "mode"), spec.Mode)
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Join(scenario, "run-id"),
            RunId(spec).ToString(CultureInfo.InvariantCulture))
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Join(scenario, "run-attempt"), "1")
            .ConfigureAwait(false);
        if (spec.ExpectContinuation)
        {
            await File.WriteAllTextAsync(
                Path.Join(scenario, "expect-continuation"), "1")
                .ConfigureAwait(false);
        }

        if (spec.CrashHost)
        {
            await File.WriteAllTextAsync(Path.Join(scenario, "wait-for-crash"),
                "1").ConfigureAwait(false);
        }

        if (spec.CrashAfterProviderCheckpoint)
        {
            await File.WriteAllTextAsync(
                Path.Join(scenario, "crash-after-provider-checkpoint"), "1")
                .ConfigureAwait(false);
        }

        if (spec.ForceEscalation)
        {
            await File.WriteAllTextAsync(
                Path.Join(scenario, "stall-after-signal"), "1")
                .ConfigureAwait(false);
        }

        var eventPath = Path.Join(scenario, "event.json");
        var summaryPath = Path.Join(scenario, "summary.md");
        var outputPath = Path.Join(scenario, "output.txt");
        await File.WriteAllTextAsync(eventPath, Event(spec),
            new UTF8Encoding(false)).ConfigureAwait(false);
        await File.WriteAllTextAsync(summaryPath, "", new UTF8Encoding(false))
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(outputPath,
            FrameworkCanaries.OutputSentinel, new UTF8Encoding(false))
            .ConfigureAwait(false);

        using var process = StartWrapper(spec, repository, payload, bundle,
            node, scenario, eventPath, summaryPath, outputPath, platform);
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        var hostPid = await WaitForHostPidAsync(scenario,
            spec.CrashHost || spec.SignalAfterHostStart ||
                spec.CrashAfterGate is not null ||
                spec.SignalAfterGate is not null
                ? TimeSpan.FromSeconds(20)
                : TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        var crashGateReached = false;

        var signalGateReached = spec.SignalAfterGate is null;
        if (spec.CrashAfterGate is not null && hostPid > 0)
        {
            crashGateReached = await WaitForFileAsync(
                Path.Join(scenario, spec.CrashAfterGate),
                TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (crashGateReached) _ = Kill(-process.Id, 9);
        }
        else if (spec.SignalAfterGate is not null && hostPid > 0)
        {
            signalGateReached = await WaitForFileAsync(
                Path.Join(scenario, spec.SignalAfterGate),
                TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (signalGateReached) _ = Kill(process.Id, 15);
        }
        else if (spec.CrashAfterProviderCheckpoint && hostPid > 0)
        {
            var checkpoint = await WaitForFileAsync(
                Path.Join(scenario, "provider-checkpoint-ready"),
                TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (checkpoint) _ = KillProcess(hostPid);
        }
        else if (spec.SignalAfterHostStart && hostPid > 0)
        {
            _ = Kill(process.Id, 15);
        }
        else if (spec.CrashHost && hostPid > 0)
        {
            var environmentRecorded = await WaitForFileAsync(
                Path.Join(scenario, "host-environment.keys"),
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            if (environmentRecorded) _ = KillProcess(hostPid);
        }

        var exited = await WaitForExitAsync(process, ProcessTimeout)
            .ConfigureAwait(false);
        if (!exited)
        {
            _ = Kill(-process.Id, 9);
            await process.WaitForExitAsync().ConfigureAwait(false);
        }

        var stdout = await standardOutput.ConfigureAwait(false);
        var stderr = await standardError.ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Join(scenario, "wrapper-stdout.redacted.txt"),
            RedactCanaries(SanitizePrivateMaskCommands(stdout)))
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Join(scenario, "wrapper-stderr.redacted.txt"),
            RedactCanaries(stderr)).ConfigureAwait(false);
        var summary = File.Exists(summaryPath)
            ? await File.ReadAllTextAsync(summaryPath).ConfigureAwait(false)
            : "";
        var output = File.Exists(outputPath)
            ? await File.ReadAllTextAsync(outputPath).ConfigureAwait(false)
            : "";
        var status = ParseStatus(summary);
        var sanitized = SanitizePrivateMaskCommands(stdout);
        var publicBodies = ReadOptionalText(scenario, "sticky-comment.json") +
            ReadOptionalText(scenario, "inline-comments.json");
        var noLeak = PublicCanaryOracle(
            sanitized + stderr + summary + output + publicBodies);
        var closedEnvironment = hostPid < 1 ||
            File.Exists(Path.Join(scenario, "host-environment.keys"));
        var outputUnchanged = output == FrameworkCanaries.OutputSentinel;
        var groupQuiet = !(spec.CrashHost ||
                spec.CrashAfterProviderCheckpoint ||
                spec.CrashAfterGate is not null || spec.ForceEscalation ||
                spec.SignalAfterHostStart ||
                spec.SignalAfterGate is not null) ||
            await WaitForGroupQuietAsync(
                process.Id, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var platformQuiet = await WaitForPlatformQuietAsync(platform,
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var expected = spec.CrashAfterGate is not null
            ? crashGateReached && process.ExitCode != 0
            : spec.ExpectedStatus == "wrapper_failure"
            ? process.ExitCode == 1 && summary.Contains(
                "failed safely", StringComparison.Ordinal)
            : status == spec.ExpectedStatus &&
                process.ExitCode == (spec.ExpectedStatus is "reviewed" or
                    "reviewed_with_inline_warnings" or
                    "skipped_untrusted_event" or "skipped_fork" ? 0 : 1);
        var continuation = !spec.ExpectContinuation || File.Exists(
            Path.Join(scenario, "provider-continuation-observed"));
        var sixTools = spec.ExpectContinuation || spec.ExpectNoProvider ||
            spec.ExpectedStatus != "reviewed" &&
                spec.ExpectedStatus != "reviewed_with_inline_warnings" ||
            ReadInt(scenario, "provider-sequence") >= 6;
        var passed = exited && expected && noLeak && closedEnvironment &&
            outputUnchanged && groupQuiet && platformQuiet && continuation &&
            sixTools && signalGateReached &&
            (!spec.ExpectNoProvider || ReadInt(
                scenario, "provider-request-count") == 0) &&
            (spec.ExpectedProviderRequests is null || ReadInt(scenario,
                "provider-request-count") == spec.ExpectedProviderRequests) &&
            (spec.ExpectedStickyMutations is null ||
                ReadInt(scenario, "sticky-create-count") +
                    ReadInt(scenario, "sticky-update-count") ==
                    spec.ExpectedStickyMutations) &&
            (spec.RequiredScenarioEvidence is null || File.Exists(
                Path.Join(scenario, spec.RequiredScenarioEvidence))) &&
            (spec.RequiredStateOperation is null || File.Exists(
                    Path.Join(scenario, "state-operations.tsv")) &&
                File.ReadAllText(Path.Join(scenario, "state-operations.tsv"))
                    .Contains(spec.RequiredStateOperation,
                        StringComparison.Ordinal)) &&
            (spec.RequiredGlobalEvidence is null || File.Exists(
                Path.Join(root, spec.RequiredGlobalEvidence))) &&
            !File.Exists(Path.Join(scenario, "unexpected-github-request"));
        await File.WriteAllTextAsync(Path.Join(scenario, "case-result.txt"),
            passed ? "pass\n" : "fail\n").ConfigureAwait(false);
        return new CaseResult(
            spec.Name,
            spec.ExpectedStatus,
            status,
            process.ExitCode,
            hostPid,
            ReadInt(scenario, "provider-request-count"),
            ReadInt(scenario, "provider-sequence"),
            ReadInt(scenario, "github-request-count"),
            File.Exists(Path.Join(scenario, "state-operations.tsv"))
                ? File.ReadLines(Path.Join(scenario, "state-operations.tsv"))
                    .Count()
                : 0,
            ReadInt(scenario, "sticky-create-count") +
                ReadInt(scenario, "sticky-update-count"),
            ReadInt(scenario, "inline-batch-count"),
            closedEnvironment,
            outputUnchanged,
            groupQuiet,
            noLeak,
            continuation,
            passed);
    }

    private static Process StartWrapper(
        CaseSpec spec,
        string repository,
        string payload,
        string bundle,
        string node,
        string scenario,
        string eventPath,
        string summaryPath,
        string outputPath,
        SyntheticOfficialPlatform platform)
    {
        var info = new ProcessStartInfo("/usr/bin/setsid")
        {
            WorkingDirectory = repository,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add(node);
        info.ArgumentList.Add(bundle);
        info.Environment.Clear();
        var payloadRoot = Path.GetDirectoryName(payload)!;
        info.Environment["PATH"] = "/usr/local/bin:/usr/bin:/bin";
        info.Environment["HOME"] = scenario;
        var shortTemporaryRoot = Path.Join(
            Directory.GetParent(Path.GetDirectoryName(scenario)!)!.FullName,
            "tmp");
        Directory.CreateDirectory(shortTemporaryRoot);
        info.Environment["TMPDIR"] = shortTemporaryRoot;
        info.Environment["NO_COLOR"] = "1";
        info.Environment["CI"] = "true";
        info.Environment["AGENTIC_PR_REVIEW_PREPARED_ROOT"] = payloadRoot;
        info.Environment["AGENTIC_PR_REVIEW_PREPARED_EXECUTABLE"] =
            Path.GetFileName(payload);
        info.Environment["AGENTIC_PR_REVIEW_PREPARED_PAYLOAD_SHA256"] =
            Sha256(payload);
        info.Environment["AGENTIC_PR_REVIEW_ACTION_SOURCE_SHA"] =
            FrameworkGitHubHandler.ActionSha;
        info.Environment["AGENTIC_PR_REVIEW_PAYLOAD_BUILD_DISCRIMINATOR"] =
            FrameworkCanaries.BuildDiscriminator;
        info.Environment["GITHUB_EVENT_PATH"] = eventPath;
        info.Environment["GITHUB_REPOSITORY"] = FrameworkCanaries.Repository;
        info.Environment["GITHUB_REPOSITORY_ID"] =
            FrameworkGitHubHandler.RepositoryId.ToString(
                CultureInfo.InvariantCulture);
        info.Environment["GITHUB_RUN_ID"] =
            RunId(spec).ToString(CultureInfo.InvariantCulture);
        info.Environment["GITHUB_RUN_ATTEMPT"] = "1";
        info.Environment["GITHUB_WORKFLOW_REF"] =
            FrameworkCanaries.Repository +
            "/.github/workflows/r4-trusted-proof.yml@refs/heads/main";
        info.Environment["GITHUB_WORKFLOW_SHA"] =
            FrameworkGitHubHandler.WorkflowSha;
        info.Environment["GITHUB_STEP_SUMMARY"] = summaryPath;
        info.Environment["GITHUB_OUTPUT"] = outputPath;
        info.Environment["GITHUB_API_URL"] = platform.BaseUrl;
        info.Environment["GITHUB_SERVER_URL"] = "https://github.com";
        info.Environment["GITHUB_WORKSPACE"] = repository;
        info.Environment["ACTIONS_RESULTS_URL"] = platform.BaseUrl;
        info.Environment["ACTIONS_RUNTIME_TOKEN"] = RuntimeToken;
        info.Environment["ACTIONS_ARTIFACT_UPLOAD_CONCURRENCY"] = "1";
        info.Environment["INPUT_GITHUB-TOKEN"] = FrameworkCanaries.GitHubToken;
        info.Environment["INPUT_PROVIDER-API-KEY"] = spec.MissingProvider
            ? ""
            : FrameworkCanaries.ProviderKey;
        info.Environment["INPUT_STATE-KEY"] = spec.Mode == "continuation-seed"
            ? FrameworkCanaries.PreviousStateKey
            : FrameworkCanaries.StateKey;
        info.Environment["INPUT_PREVIOUS-STATE-KEY"] =
            spec.Mode == "continuation-seed"
                ? ""
                : FrameworkCanaries.PreviousStateKey;
        info.Environment["INPUT_CONFIG-PATH"] =
            ".github/agentic-pr-review.json";
        info.Environment["INPUT_PR-NUMBER"] = spec.WorkflowRun ? "" :
            FrameworkGitHubHandler.PullRequestNumber.ToString(
                CultureInfo.InvariantCulture);
        info.Environment["INPUT_STATE-MODE"] = "auto";
        var process = new Process { StartInfo = info };
        if (!process.Start()) throw new InvalidOperationException();
        return process;
    }

    private static long RunId(CaseSpec spec) => spec.Name switch
    {
        "dispatch-bootstrap" => 900,
        "dispatch-continuation" => 901,
        "artifact-pagination-changed" => 902,
        "artifact-pagination-late" => 903,
        "artifact-list-duplicate" => 904,
        "artifact-digest-mismatch" => 905,
        "artifact-expired" => 906,
        "artifact-upload-outcome-unknown" => 907,
        "artifact-delete-outcome-unknown" => 908,
        "workflow-run" => 909,
        "delete-exact" => 910,
        "inline" => 911,
        "inline-warning" => 912,
        "unsupported" => 913,
        "fork" => 914,
        "permission" => 915,
        "wrong-action" => 916,
        "concurrency" => 917,
        "stale-head" => 918,
        "provider-malformed" => 919,
        "public-result" => 920,
        "credentials-missing" => 921,
        "crash-mutation" => 922,
        "crash-recovery" => 923,
        "cancel-before-side-effect" => 924,
        "cancel-before-dispatch" => 925,
        "cancel-known-commit" => 926,
        "cancel-outcome-unknown" => 927,
        "cancel-escalation" => 928,
        "host-crash" => 929,
        _ => throw new InvalidOperationException(),
    };

    private static string Event(CaseSpec spec)
    {
        if (spec.Unsupported)
        {
            return $$$"""
                {"repository":{"id":42,"full_name":"{{{FrameworkCanaries.Repository}}}"},"sender":{"id":7,"login":"maintainer"}}
                """;
        }

        if (!spec.WorkflowRun)
        {
            return $$$"""
                {"inputs":{"pr-number":"147"},"repository":{"id":42,"full_name":"{{{FrameworkCanaries.Repository}}}"},"sender":{"id":7,"login":"maintainer"}}
                """;
        }

        var identity = new
        {
            id = 42,
            full_name = FrameworkCanaries.Repository,
        };
        var actor = new { id = 7, login = "maintainer" };
        var repositoryReference = new
        {
            id = 42,
            url = "https://api.github.com/repos/" +
                FrameworkCanaries.Repository,
            name = "apr178-repository-canary",
        };
        return JsonSerializer.Serialize(new
        {
            action = "completed",
            workflow_run = new
            {
                id = 800,
                run_attempt = 1,
                workflow_id = 71,
                name = "CI",
                path = ".github/workflows/ci.yml",
                head_branch = "feature",
                head_sha = FrameworkGitHubHandler.TriggerSha,
                @event = "pull_request",
                conclusion = "success",
                repository = identity,
                head_repository = identity,
                actor,
                triggering_actor = actor,
                pull_requests = new[]
                {
                    new
                    {
                        id = 1000,
                        number = 147,
                        @base = new
                        {
                            sha = FrameworkGitHubHandler.BaseSha,
                            repo = repositoryReference,
                        },
                        head = new
                        {
                            sha = FrameworkGitHubHandler.HeadSha,
                            repo = repositoryReference,
                        },
                    },
                },
            },
            repository = identity,
            sender = actor,
        });
    }

    private static string SanitizePrivateMaskCommands(string stdout)
    {
        var lines = stdout.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');
        return string.Join('\n', lines.Where(line =>
            !line.StartsWith("::add-mask::", StringComparison.Ordinal)));
    }

    private static string RedactCanaries(string value)
    {
        string[] canaries =
        [
            FrameworkCanaries.ProviderKey,
            FrameworkCanaries.GitHubToken,
            FrameworkCanaries.StateKey,
            FrameworkCanaries.PreviousStateKey,
            RuntimeToken,
            FrameworkCanaries.SignedUrl,
            FrameworkCanaries.Prompt,
            FrameworkCanaries.ToolData,
            FrameworkCanaries.ContinuationMarker,
            FrameworkCanaries.PublicResult,
        ];
        foreach (var canary in canaries)
        {
            value = value.Replace(canary, "[redacted]",
                StringComparison.Ordinal);
        }
        return value;
    }

    private static bool PublicCanaryOracle(string value)
    {
        string[] forbidden =
        [
            FrameworkCanaries.ProviderKey,
            FrameworkCanaries.GitHubToken,
            FrameworkCanaries.StateKey,
            FrameworkCanaries.PreviousStateKey,
            FrameworkCanaries.ToolData,
            FrameworkCanaries.Plaintext,
            FrameworkCanaries.ContinuationMarker,
            FrameworkCanaries.SignedUrl,
            FrameworkCanaries.PublicResult,
        ];
        return forbidden.All(canary =>
            !value.Contains(canary, StringComparison.Ordinal) &&
            !value.Contains(Convert.ToBase64String(
                Encoding.UTF8.GetBytes(canary)), StringComparison.Ordinal) &&
            !value.Contains(Uri.EscapeDataString(canary),
                StringComparison.Ordinal));
    }

    private static string? ParseStatus(string summary)
    {
        var line = summary.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .FirstOrDefault(value =>
                value.StartsWith("| Status | ", StringComparison.Ordinal) &&
                value.EndsWith(" |", StringComparison.Ordinal));
        return line is null ? null : line[11..^2];
    }

    private static async Task<int> WaitForHostPidAsync(
        string scenario,
        TimeSpan timeout)
    {
        var path = Path.Join(scenario, "host.pid");
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            if (File.Exists(path) && int.TryParse(
                    await File.ReadAllTextAsync(path).ConfigureAwait(false),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var pid))
            {
                return pid;
            }

            await Task.Delay(20).ConfigureAwait(false);
        }

        return -1;
    }

    private static async Task<bool> WaitForFileAsync(
        string path,
        TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            if (File.Exists(path)) return true;
            await Task.Delay(20).ConfigureAwait(false);
        }

        return false;
    }

    private static async Task<bool> WaitForExitAsync(
        Process process,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token)
                .ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<bool> WaitForGroupQuietAsync(
        int processGroup,
        TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            if (Kill(-processGroup, 0) != 0) return true;
            await Task.Delay(20).ConfigureAwait(false);
        }

        return false;
    }

    private static bool KillProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill();
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private static async Task<bool> WaitForPlatformQuietAsync(
        SyntheticOfficialPlatform platform,
        TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            if (platform.InFlight == 0) return true;
            await Task.Delay(20).ConfigureAwait(false);
        }

        return false;
    }

    private static bool ValidateSingleFile(string payload)
    {
        var directory = Path.GetDirectoryName(payload)!;
        var stem = Path.GetFileName(payload);
        return !File.Exists(Path.Join(directory, stem + ".dll")) &&
            !File.Exists(Path.Join(directory, stem + ".deps.json")) &&
            !File.Exists(Path.Join(directory, stem + ".runtimeconfig.json"));
    }

    private static bool ValidateReplacementRecord(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        if (root.GetProperty("schema").GetString() !=
                "apr.action-host.replacement-record.v2" ||
            root.GetProperty("issue_number").GetInt32() != 178)
        {
            return false;
        }

        string[] expectedLeaves =
        [
            "W3", "W4", "W5", "W6", "W7", "W8", "W9", "W10",
            "W11", "W12", "W14", "W15",
        ];
        int[] expectedIssues =
        [165, 166, 167, 168, 169, 170, 171, 172, 173, 174, 176, 177];
        var entries = root.GetProperty("entries").EnumerateArray().ToArray();
        var owned = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            if (index >= expectedLeaves.Length ||
                entry.GetProperty("leaf_id").GetString() !=
                    expectedLeaves[index] ||
                entry.GetProperty("issue_number").GetInt32() !=
                    expectedIssues[index] ||
                !RequiredText(entry, "classification") ||
                !RequiredText(entry, "deletion_gate") ||
                !RequiredTextArray(entry, "owned_paths", pathValue =>
                    IsClosedPath(pathValue) && owned.Add(pathValue)) ||
                !RequiredTextArray(entry, "retained_paths", IsClosedPath) ||
                !RequiredTextArray(entry, "protective_behaviors") ||
                !RequiredTextArray(entry, "csharp_owners") ||
                !RequiredTextArray(entry, "framework_scenario_ids") ||
                !RequiredTextArray(entry, "deletion_prerequisites"))
            {
                return false;
            }
        }

        return entries.Length == expectedLeaves.Length &&
            !File.ReadAllText(path).Contains("W13", StringComparison.Ordinal);
    }

    private static bool ValidateInventory(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        if (root.GetProperty("schema").GetString() !=
                "apr.action-host.e1-base-inventory.v1" ||
            root.GetProperty("base_sha").GetString() !=
                "e698fb1df6daf49f393e87fac4f00e3a2ec2c716")
        {
            return false;
        }

        var framing = new StringBuilder();
        var previous = string.Empty;
        var count = 0;
        foreach (var entry in root.GetProperty("files").EnumerateArray())
        {
            var relative = entry.GetProperty("path").GetString();
            var digest = entry.GetProperty("sha256").GetString();
            if (relative is null || digest is null ||
                !IsClosedPath(relative) || !IsLowerHex(digest, 64) ||
                string.CompareOrdinal(previous, relative) >= 0)
            {
                return false;
            }

            framing.Append(relative).Append('\0').Append(digest).Append('\n');
            previous = relative;
            count++;
        }

        var actual = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(framing.ToString()))).ToLowerInvariant();
        return count == 339 && root.GetProperty("aggregate_sha256")
            .GetString() == actual;
    }

    private static bool RequiredText(JsonElement value, string property) =>
        value.TryGetProperty(property, out var item) &&
        item.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(item.GetString());

    private static bool RequiredTextArray(
        JsonElement value,
        string property,
        Func<string, bool>? predicate = null)
    {
        if (!value.TryGetProperty(property, out var items) ||
            items.ValueKind != JsonValueKind.Array ||
            items.GetArrayLength() == 0)
        {
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in items.EnumerateArray().Select(item =>
                     item.ValueKind == JsonValueKind.String
                         ? item.GetString()
                         : null))
        {
            if (string.IsNullOrWhiteSpace(text) || !seen.Add(text) ||
                predicate is not null && !predicate(text))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsClosedPath(string value) =>
        value.Length <= 512 && !value.StartsWith("/", StringComparison.Ordinal) &&
        !value.Contains('\\') &&
        !value.Contains("**", StringComparison.Ordinal) &&
        !value.Split('/').Any(part => part is "." or "..");

    private static bool IsLowerHex(string value, int length) =>
        value.Length == length && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool ValidateCanaryTable(string path)
    {
        string[] classes =
        [
            "provider-key", "github-token", "state-key-current",
            "state-key-previous", "actions-runtime-jwt", "signed-url-sig",
            "repository", "reviewed-path", "workflow-source", "prompt",
            "tool-data", "session-plaintext", "artifact-ciphertext",
            "public-result",
        ];
        var lines = File.ReadAllLines(path);
        if (lines.Length != classes.Length + 1 || lines[0] !=
            "class\tsource\tapproved_private_sinks\tterminal_sinks\t" +
            "forbidden_sinks\tcardinality")
        {
            return false;
        }

        var parsed = lines.Skip(1).Select(line => line.Split('\t')).ToArray();
        return parsed.All(fields => fields.Length == 6 &&
                fields.All(field => field.Length > 0)) &&
            parsed.Select(fields => fields[0])
                .SequenceEqual(classes, StringComparer.Ordinal) &&
            parsed.Select(fields => fields[0])
                .Distinct(StringComparer.Ordinal).Count() == classes.Length;
    }

    private static bool EvaluateCanaryRoutes(string root, string tablePath)
    {
        var routes = File.ReadAllLines(tablePath).Skip(1)
            .Select(line => line.Split('\t'))
            .ToDictionary(
                fields => fields[0],
                fields => new CanaryRoute(
                    fields[2].Split(';', StringSplitOptions.RemoveEmptyEntries)
                        .Concat(fields[3].Split(';',
                            StringSplitOptions.RemoveEmptyEntries))
                        .Where(sink => sink != "-")
                        .ToHashSet(StringComparer.Ordinal),
                    fields[5]),
                StringComparer.Ordinal);
        var observations = Directory.EnumerateFiles(root,
                "canary-observations.tsv", SearchOption.AllDirectories)
            .SelectMany(File.ReadAllLines)
            .Where(line => line.Length > 0)
            .Select(line => line.Split('\t'))
            .ToArray();
        if (observations.Any(fields => fields.Length != 2 ||
                !routes.TryGetValue(fields[0], out var route) ||
                !route.AllowedSinks.Contains(fields[1])))
        {
            return false;
        }

        foreach (var (canaryClass, route) in routes)
        {
            var count = observations.Count(fields => fields[0] == canaryClass);
            var expected = route.Cardinality switch
            {
                "exactly-one" => 1,
                "equals-provider-requests" => SumScenarioCounter(root,
                    "provider-request-count"),
                "equals-github-and-artifact-rest-requests" =>
                    SumScenarioCounter(root, "github-request-count") +
                    ReadInt(root, "official-rest-count"),
                "equals-twirp-requests" =>
                    ReadInt(root, "official-twirp-count"),
                "equals-blob-requests" =>
                    ReadInt(root, "official-blob-count") +
                    ReadInt(root, "official-signed-download-count"),
                "at-least-one" => count,
                _ => -1,
            };
            if (route.Cardinality == "at-least-one"
                    ? count < 1
                    : expected < 1 || count != expected)
            {
                return false;
            }
        }

        return true;
    }

    private static int SumScenarioCounter(string root, string name) =>
        Directory.EnumerateFiles(root, name, SearchOption.AllDirectories)
            .Where(path => Path.GetDirectoryName(path) != root)
            .Sum(path => int.TryParse(File.ReadAllText(path),
                NumberStyles.None, CultureInfo.InvariantCulture,
                out var value) ? value : 0);

    private static string ReadOptionalText(string root, string name)
    {
        var path = Path.Join(root, name);
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    private static bool JsonEquivalent(byte[] left, byte[] right)
    {
        using var first = JsonDocument.Parse(left);
        using var second = JsonDocument.Parse(right);
        return JsonElement.DeepEquals(first.RootElement, second.RootElement);
    }

    private static async Task WriteEvidenceAsync(
        string root,
        string payload,
        SyntheticOfficialPlatform platform,
        IReadOnlyList<CaseResult> cases,
        bool passed)
    {
        var evidence = new
        {
            passed,
            payload_sha256 = Sha256(payload),
            sdk = RuntimeInformation.FrameworkDescription,
            official_artifacts = new
            {
                locator = platform.ArtifactNames.Any(name =>
                    name == "agentic-pr-review-state-root-v1"),
                scoped = platform.ArtifactNames.Any(name =>
                    name.StartsWith("apr-state-", StringComparison.Ordinal)),
            },
            cases,
        };
        await File.WriteAllTextAsync(Path.Join(root, "evidence.json"),
            JsonSerializer.Serialize(evidence,
                new JsonSerializerOptions { WriteIndented = true }))
            .ConfigureAwait(false);
    }

    private static int ReadInt(string root, string name)
    {
        var path = Path.Join(root, name);
        return File.Exists(path) && int.TryParse(File.ReadAllText(path),
            NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }

    private static string SourceInventoryDigest(string repository)
    {
        var sourceRoot = Path.Join(repository, "runtime", "tests",
            "ActionHostVerifierFixture");
        var framing = new StringBuilder();
        foreach (var path in Directory.EnumerateFiles(sourceRoot, "*",
                     SearchOption.AllDirectories)
                     .Where(path => !path.Contains(
                         Path.DirectorySeparatorChar + "bin" +
                         Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                         !path.Contains(
                         Path.DirectorySeparatorChar + "obj" +
                         Path.DirectorySeparatorChar, StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(repository, path)
                .Replace(Path.DirectorySeparatorChar, '/');
            framing.Append(relative).Append('\0').Append(Sha256(path))
                .Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(framing.ToString()))).ToLowerInvariant();
    }

    private static string CanaryObservationDigest(string root)
    {
        var framing = string.Join('\n', Directory.EnumerateFiles(root,
                    "canary-observations.tsv", SearchOption.AllDirectories)
                .SelectMany(File.ReadAllLines)
                .Where(line => line.Length > 0)
                .Order(StringComparer.Ordinal)) + "\n";
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(framing))).ToLowerInvariant();
    }

    private static string JsonProperty(string path, string property)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        return document.RootElement.GetProperty(property).GetString() ?? "";
    }

    private static string Sha256(string path) => Convert.ToHexString(
        SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static Dictionary<string, string> ParseArguments(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (args.Length % 2 != 0) return values;
        for (var index = 0; index + 1 < args.Length; index += 2)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal))
            {
                return [];
            }

            values[args[index][2..]] = args[index + 1];
        }

        return values;
    }

    private static string Required(
        IReadOnlyDictionary<string, string> values,
        string name) => values.TryGetValue(name, out var value)
            ? Path.GetFullPath(value)
            : throw new InvalidOperationException("missing verifier argument");

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int pid, int signal);

    private sealed record CaseSpec(
        string Name,
        string Mode,
        string ExpectedStatus,
        bool WorkflowRun = false,
        bool Unsupported = false,
        bool MissingProvider = false,
        bool ExpectContinuation = false,
        bool SignalAfterHostStart = false,
        string? SignalAfterGate = null,
        bool ForceEscalation = false,
        bool CrashHost = false,
        bool CrashAfterProviderCheckpoint = false,
        string? CrashAfterGate = null,
        bool ExpectNoProvider = false,
        int? ExpectedProviderRequests = null,
        int? ExpectedStickyMutations = null,
        string? RequiredScenarioEvidence = null,
        string? RequiredStateOperation = null,
        string? RequiredGlobalEvidence = null);

    private sealed record CaseResult(
        string Name,
        string ExpectedStatus,
        string? ActualStatus,
        int ExitCode,
        int HostPid,
        int ProviderRequests,
        int ToolSequence,
        int GitHubRequests,
        int StateOperations,
        int StickyMutations,
        int InlineMutations,
        bool ExactEnvironment,
        bool OutputUnchanged,
        bool ProcessGroupQuiet,
        bool CanarySafe,
        bool ContinuationObserved,
        bool Passed);

    private sealed record CanaryRoute(
        IReadOnlySet<string> AllowedSinks,
        string Cardinality);
}

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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
                ValidateReplacementRecord(record, repository, inventory),
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
        var artifactMetadataRouteProbeCount =
            await VerifyArtifactMetadataRouteCaptureAsync(root, canaries)
                .ConfigureAwait(false);
        if (artifactMetadataRouteProbeCount != 3)
        {
            Console.Error.WriteLine(
                "APR_ACTION_HOST_FRAMEWORK_INVALID artifact-metadata-route");
            return 1;
        }

        await using var platform = SyntheticOfficialPlatform.Start(root);
        var cases = new List<CaseResult>();

        cases.Add(await RunCaseAsync(new CaseSpec("dispatch-bootstrap",
            "continuation-seed", "reviewed", ExpectedProviderRequests: 6),
            root, repository, payload,
            bundle, node, platform).ConfigureAwait(false));
        cases.Add(await RunCaseAsync(new CaseSpec("dispatch-continuation",
            "continuation", "reviewed", ExpectContinuation: true,
            ExpectedProviderRequests: 3, ExpectedStickyMutations: 1,
            RequireSuccessfulContinuation: true),
            root, repository, payload, bundle, node, platform)
            .ConfigureAwait(false));
        cases.Add(await RunCaseAsync(new CaseSpec(
            "dispatch-cross-head-conflict", "cross-head-conflict",
            "state_conflict", ExpectedProviderRequests: 0,
            ExpectedStickyMutations: 0), root, repository, payload, bundle,
            node, platform).ConfigureAwait(false));
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
            RequiredGlobalEvidence: "exact-delete-proof",
            RequiredStateOperation: "delete\tNone\t1"), root, repository,
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
            schema = "apr.action-host.framework-evidence.v3",
            source_inventory_digest = SourceInventoryDigest(repository),
            replacement_record_digest = Sha256(record),
            base_inventory_digest = JsonProperty(inventory,
                "aggregate_sha256"),
            canary_table_digest = Sha256(canaries),
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
            continuation = new
            {
                second_process_status = cases.Single(result =>
                    result.Name == "dispatch-continuation").ActualStatus,
                successor_accepted = cases.Single(result =>
                    result.Name == "dispatch-continuation").Passed,
                reviewed_head_advanced = ContinuationHeadAdvanced(root),
                state_identity_digest = CanonicalizedIdentityDigest(Path.Join(root,
                    "dispatch-continuation",
                    "state-operation-identities.tsv")),
                sticky_lineage_digest = ContinuationStickyDigest(root),
                prior_marker_first_request_exact = File.Exists(Path.Join(
                    root, "dispatch-continuation",
                    "provider-continuation-first-request-exact")),
                prior_marker_carried_without_reinjection =
                    File.Exists(Path.Join(root, "dispatch-continuation",
                        "provider-continuation-carried-history-2")) &&
                    File.Exists(Path.Join(root, "dispatch-continuation",
                        "provider-continuation-carried-history-3")),
                exact_tool_exchange_relations =
                    File.Exists(Path.Join(root, "dispatch-continuation",
                        "provider-continuation-relation-2")) &&
                    File.Exists(Path.Join(root, "dispatch-continuation",
                        "provider-continuation-relation-3")),
            },
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
            canary_route_coverage_digest = CanaryRouteCoverageDigest(root),
            canary_negative_injection_count = ReadInt(root,
                "canary-negative-injection-count"),
            artifact_metadata_route_probe_count =
                artifactMetadataRouteProbeCount,
            normalized_exact_delete_identity_digest =
                CanonicalizedIdentityDigest(
                Path.Join(root, "exact-delete-proof")),
        };
        var normalizedBytes = Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(normalized,
                new JsonSerializerOptions { WriteIndented = true }) + "\n");
        if (!FrameworkCanaryCapture.AssertPublicSafe(
                root, "evidence.normalized", normalizedBytes))
        {
            await WriteEvidenceAsync(root, payload, platform, cases, false)
                .ConfigureAwait(false);
            return 1;
        }
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

    private static async Task<int> VerifyArtifactMetadataRouteCaptureAsync(
        string root,
        string canaryTable)
    {
        var probeRoot = Path.Join(Path.GetTempPath(),
            "apr-artifact-metadata-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(probeRoot);
        try
        {
            await using (var platform = SyntheticOfficialPlatform.Start(probeRoot))
            using (var client = new HttpClient
                   {
                       Timeout = TimeSpan.FromSeconds(10),
                   })
            {
                using var create = new HttpRequestMessage(HttpMethod.Post,
                    platform.BaseUrl +
                    "/twirp/github.actions.results.api.v1.ArtifactService/" +
                    "CreateArtifact");
                create.Headers.TryAddWithoutValidation("Authorization",
                    "Bearer " + RuntimeToken);
                create.Content = new StringContent(JsonSerializer.Serialize(new
                {
                    workflowRunBackendId = FrameworkCanaries.RunBackendId,
                    workflowJobRunBackendId = FrameworkCanaries.JobBackendId,
                    name = FrameworkCanaries.ToolData,
                }), Encoding.UTF8, "application/json");
                using var createResponse = await client.SendAsync(create)
                    .ConfigureAwait(false);

                using var finalize = new HttpRequestMessage(HttpMethod.Post,
                    platform.BaseUrl +
                    "/twirp/github.actions.results.api.v1.ArtifactService/" +
                    "FinalizeArtifact");
                finalize.Headers.TryAddWithoutValidation("Authorization",
                    "Bearer " + RuntimeToken);
                finalize.Content = new StringContent(JsonSerializer.Serialize(new
                {
                    workflowRunBackendId = FrameworkCanaries.RunBackendId,
                    workflowJobRunBackendId = FrameworkCanaries.JobBackendId,
                    name = FrameworkCanaries.Prompt,
                }), Encoding.UTF8, "application/json");
                using var finalizeResponse = await client.SendAsync(finalize)
                    .ConfigureAwait(false);

                using var rest = new HttpRequestMessage(HttpMethod.Get,
                    platform.BaseUrl + "/repos/" + FrameworkCanaries.Repository +
                    "/actions/artifacts?name=" +
                    Uri.EscapeDataString(FrameworkCanaries.StateKey));
                rest.Headers.TryAddWithoutValidation("Authorization",
                    "Bearer " + FrameworkCanaries.GitHubToken);
                using var restResponse = await client.SendAsync(rest)
                    .ConfigureAwait(false);

                if (createResponse.StatusCode != HttpStatusCode.OK ||
                    finalizeResponse.StatusCode != HttpStatusCode.BadRequest ||
                    restResponse.StatusCode != HttpStatusCode.OK)
                {
                    return -1;
                }

                for (var attempt = 0;
                     attempt < 100 && platform.InFlight != 0;
                     attempt++)
                {
                    await Task.Delay(10).ConfigureAwait(false);
                }
                if (platform.InFlight != 0) return -1;
            }

            var observationPath = Path.Join(probeRoot,
                "canary-observations.tsv");
            if (!File.Exists(observationPath)) return -1;
            var observations = File.ReadAllLines(observationPath)
                .Where(line => line.EndsWith("\tartifact.metadata",
                    StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToArray();
            string[] expected =
            [
                "prompt\tartifact.metadata",
                "state-key-current\tartifact.metadata",
                "tool-data\tartifact.metadata",
            ];
            if (!observations.SequenceEqual(expected, StringComparer.Ordinal))
            {
                return -1;
            }

            var forbiddenByClass = File.ReadAllLines(canaryTable).Skip(1)
                .Select(line => line.Split('\t'))
                .Where(fields => fields.Length == 6)
                .ToDictionary(fields => fields[0], fields => fields[4]
                    .Split(';', StringSplitOptions.RemoveEmptyEntries),
                    StringComparer.Ordinal);
            if (expected.Select(line => line.Split('\t')[0]).Any(canaryClass =>
                    !forbiddenByClass.TryGetValue(canaryClass,
                        out var forbidden) ||
                    !forbidden.Any(pattern =>
                        SinkMatches(pattern, "artifact.metadata"))))
            {
                return -1;
            }

            File.WriteAllLines(Path.Join(root,
                "artifact-metadata-route-probes.tsv"), observations);
            return observations.Length;
        }
        catch (Exception exception) when (exception is HttpRequestException or
            TaskCanceledException or IOException)
        {
            return -1;
        }
        finally
        {
            if (Directory.Exists(probeRoot))
            {
                Directory.Delete(probeRoot, recursive: true);
            }
        }
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
                sanitized + stderr + summary + output + publicBodies) &&
            FrameworkCanaryCapture.AssertPublicSafe(
                scenario, "public.output",
                sanitized, stderr, summary, output, publicBodies) &&
            FrameworkCanaryCapture.AssertPublicSafe(
                scenario, "command.state-evidence",
                ReadOptionalText(scenario, "state-operations.tsv"),
                ReadOptionalText(scenario, "host-environment.keys"));
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
        var successfulContinuation = !spec.RequireSuccessfulContinuation ||
            File.Exists(Path.Join(scenario,
                "provider-continuation-first-request-exact")) &&
            File.Exists(Path.Join(scenario,
                "provider-continuation-relation-2")) &&
            File.Exists(Path.Join(scenario,
                "provider-continuation-relation-3")) &&
            File.Exists(Path.Join(scenario,
                "provider-continuation-carried-history-2")) &&
            File.Exists(Path.Join(scenario,
                "provider-continuation-carried-history-3")) &&
            !File.Exists(Path.Join(scenario,
                "provider-continuation-order-violation")) &&
            ReadInt(scenario, "sticky-create-count") == 0 &&
            ReadInt(scenario, "sticky-update-count") == 1 &&
            ReadOptionalText(scenario, "sticky-update-comment-id") == "701" &&
            ReadOptionalText(scenario, "sticky-readback-comment-id") == "701" &&
            summary.Contains("| State disposition | accepted |",
                StringComparison.Ordinal) &&
            ValidateContinuationStateTrace(scenario) &&
            ContinuationHeadAdvanced(root);
        var sixTools = spec.ExpectContinuation || spec.ExpectNoProvider ||
            spec.ExpectedStatus != "reviewed" &&
                spec.ExpectedStatus != "reviewed_with_inline_warnings" ||
            ReadInt(scenario, "provider-sequence") >= 6;
        var passed = exited && expected && noLeak && closedEnvironment &&
            outputUnchanged && groupQuiet && platformQuiet && continuation &&
            successfulContinuation &&
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
        "dispatch-cross-head-conflict" => 930,
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

    private static bool ValidateReplacementRecord(
        string path,
        string repository,
        string inventoryPath)
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
        var retained = new HashSet<string>(StringComparer.Ordinal);
        var inventory = InventoryPaths(inventoryPath);
        var scenarios = FrameworkScenarioIds();
        var csharp = Directory.EnumerateFiles(
                Path.Join(repository, "runtime"), "*.cs",
                SearchOption.AllDirectories)
            .Where(source => !source.Contains(
                    Path.DirectorySeparatorChar + "bin" +
                    Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                !source.Contains(Path.DirectorySeparatorChar + "obj" +
                    Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Select(File.ReadAllText)
            .ToArray();
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
                    IsClosedPath(pathValue) && owned.Add(pathValue) &&
                    InventoryCovers(inventory, pathValue)) ||
                !RequiredTextArray(entry, "retained_paths", pathValue =>
                    IsClosedPath(pathValue) &&
                    LandingPathExists(repository, pathValue) &&
                    Remember(retained, pathValue)) ||
                !RequiredTextArray(entry, "protective_behaviors") ||
                !RequiredTextArray(entry, "csharp_owners", owner =>
                    csharp.Any(source => source.Contains(owner,
                        StringComparison.Ordinal))) ||
                !RequiredTextArray(entry, "framework_scenario_ids",
                    scenarios.Contains) ||
                !RequiredTextArray(entry, "deletion_prerequisites"))
            {
                return false;
            }

            if (entry.GetProperty("leaf_id").GetString() == "W3" &&
                !ValidateW3Ownership(entry, repository, owned))
            {
                return false;
            }

            if (entry.GetProperty("leaf_id").GetString() == "W4" &&
                !ValidateW4Ownership(entry, repository))
            {
                return false;
            }

            if (entry.GetProperty("leaf_id").GetString() == "W11" &&
                !ValidateW11Ownership(entry, repository, inventoryPath))
            {
                return false;
            }

            if (entry.GetProperty("leaf_id").GetString() == "W12" &&
                !ValidateW12Ownership(entry, repository))
            {
                return false;
            }
        }

        return entries.Length == expectedLeaves.Length &&
            !owned.Any(ownedPath => retained.Any(retainedPath =>
                PathsOverlap(ownedPath, retainedPath))) &&
            !File.ReadAllText(path).Contains("W13", StringComparison.Ordinal);
    }

    private static bool ValidateW3Ownership(
        JsonElement entry,
        string repository,
        IReadOnlySet<string> owned)
    {
        var inventory = InventoryPaths(Path.Join(repository,
            "runtime", "tests", "fixtures", "action-host", "framework",
            "e1-base-inventory.json"));
        if (entry.GetProperty("disposition").GetString() != "removed" ||
            !RequiredTextArray(entry, "removed_paths", value =>
                IsClosedPath(value) && !LandingPathExists(repository, value)) ||
            !RequiredTextArray(entry, "removed_consumer_paths", value =>
                IsClosedPath(value) && !LandingPathExists(repository, value)) ||
            !RequiredTextArray(entry, "retained_carrier_paths", value =>
                IsClosedPath(value) && LandingPathExists(repository, value) &&
                InventoryCovers(inventory, value)) ||
            !RequiredTextArray(entry, "owned_csharp_members", member =>
                !MemberExists(repository, member) &&
                InventoryCovers(inventory, member.Split('#', 2)[0])) ||
            !RequiredTextArray(entry, "referenced_tests_and_docs", value =>
                IsClosedPath(value) && LandingPathExists(repository, value) &&
                InventoryCovers(inventory, value)))
        {
            return false;
        }

        var memberPaths = TextArray(entry, "owned_csharp_members")
            .Select(value => value.Split('#', 2)[0])
            .ToHashSet(StringComparer.Ordinal);
        var removedPaths = TextArray(entry, "removed_paths")
            .ToHashSet(StringComparer.Ordinal);
        var removedConsumers = TextArray(entry, "removed_consumer_paths")
            .ToHashSet(StringComparer.Ordinal);
        var retainedCarriers = TextArray(entry, "retained_carrier_paths")
            .ToHashSet(StringComparer.Ordinal);
        return removedPaths.SetEquals(
        [
            "src/runtime-invocation/",
            "src/live-runtime-invocation/",
            "protocol/schemas/live-runtime-invocation-context.v1.json",
            "src/runtime-integration/runtime-integration.test.ts",
            "runtime/tests/IntegrationFixtures/",
        ]) &&
            removedConsumers.SetEquals(
        [
            "src/runtime-integration/runtime-integration.test.ts",
            "runtime/tests/IntegrationFixtures/",
        ]) &&
            retainedCarriers.SetEquals(
        [
            "runtime/src/AgenticPrReview.Runtime/Protocol/SchemaContracts.cs",
            "runtime/src/AgenticPrReview.Runtime/AgenticPrReview.Runtime.csproj",
        ]) &&
            memberPaths.SetEquals(
        [
            "runtime/src/AgenticPrReview.Runtime/Protocol/SchemaContracts.cs",
            "runtime/src/AgenticPrReview.Runtime/AgenticPrReview.Runtime.csproj",
        ]);
    }

    private static bool ValidateW4Ownership(
        JsonElement entry,
        string repository)
    {
        var inventory = InventoryPaths(Path.Join(repository,
            "runtime", "tests", "fixtures", "action-host", "framework",
            "e1-base-inventory.json"));
        if (entry.GetProperty("disposition").GetString() != "removed" ||
            !RequiredTextArray(entry, "removed_paths", value =>
                IsClosedPath(value) && !LandingPathExists(repository, value)) ||
            !RequiredTextArray(entry, "retained_evidence_paths", value =>
                IsClosedPath(value) && LandingPathExists(repository, value)) ||
            !RequiredTextArray(entry, "inventory_evidence_paths", value =>
                IsClosedPath(value) && LandingPathExists(repository, value) &&
                InventoryCovers(inventory, value)) ||
            !RequiredTextArray(entry, "retained_assertion_groups") ||
            !RequiredTextArray(entry, "superseded_assertion_groups") ||
            !RequiredTextArray(entry, "later_leaf_assertion_owners"))
        {
            return false;
        }

        var removedPaths = TextArray(entry, "removed_paths")
            .ToHashSet(StringComparer.Ordinal);
        var retainedEvidence = TextArray(entry, "retained_evidence_paths")
            .ToHashSet(StringComparer.Ordinal);
        var inventoryEvidence = TextArray(entry, "inventory_evidence_paths")
            .ToHashSet(StringComparer.Ordinal);
        var retainedAssertions = TextArray(entry, "retained_assertion_groups")
            .ToHashSet(StringComparer.Ordinal);
        var supersededAssertions = TextArray(entry, "superseded_assertion_groups")
            .ToHashSet(StringComparer.Ordinal);
        var laterOwners = TextArray(entry, "later_leaf_assertion_owners")
            .ToHashSet(StringComparer.Ordinal);
        return removedPaths.SetEquals(["src/live-provider/"]) &&
            retainedEvidence.SetEquals(
        [
            "runtime/tests/AgenticPrReview.Runtime.Tests/Execution/DeepSeek/DeepSeekRequestWriterTests.cs",
            "runtime/tests/AgenticPrReview.Runtime.Tests/Execution/DeepSeek/DeepSeekTransportTests.cs",
            "runtime/tests/AgenticPrReview.Runtime.Tests/Execution/DeepSeek/DeepSeekTransportArchitectureTests.cs",
            "runtime/tests/AgenticPrReview.Runtime.Tests/Execution/DeepSeek/DeepSeekResponseParserTests.cs",
            "runtime/tests/AgenticPrReview.Runtime.Tests/Execution/DeepSeek/DeepSeekResponseParserArchitectureTests.cs",
            "runtime/tests/AgenticPrReview.Runtime.Tests/Execution/DeepSeek/DeepSeekChatBackendTests.cs",
            "runtime/tests/AgenticPrReview.Runtime.Tests/Agent/Core/LiveAgentVerifierRetirementArchitectureTests.cs",
            "runtime/tests/AgenticPrReview.Runtime.Tests/Host/Action/Policy/ActionHostTrustedPolicyArchitectureTests.cs",
            "runtime/tests/AgenticPrReview.Runtime.Tests/Host/Action/ActionHostFrameworkVerifierArchitectureTests.cs",
            "docs/20_architecture/r4-actionhost-wrapper-plan.md",
        ]) &&
            inventoryEvidence.SetEquals(
        [
            "docs/20_architecture/r4-actionhost-wrapper-plan.md",
        ]) &&
            retainedAssertions.SetEquals(
        [
            "current request projection, message and tool ordering, bounds, and untrusted-data treatment",
            "endpoint, authorization-only credential placement, transport policy, cancellation, and sanitized failures",
            "response parsing, bounded provider-private failures, usage extraction, and terminal Agent validation",
            "provider-to-Agent state transaction, fake-provider canaries, and public result presentation",
        ]) &&
            supersededAssertions.SetEquals(
        [
            "disabled-thinking no-tool fixed-JSON request shape with temperature and response_format",
            "M4 request-contract digest and TypeScript cache-envelope identities",
        ]) &&
            laterOwners.SetEquals(
        [
            "W11: src/prefix-contract/",
            "W12: src/provider-metadata/",
            "W14: src/canonical-json/",
        ]);
    }

    private static bool ValidateW11Ownership(
        JsonElement entry,
        string repository,
        string inventoryPath)
    {
        if (entry.GetProperty("disposition").GetString() != "removed" ||
            !RequiredTextArray(entry, "removed_paths", value =>
                IsClosedPath(value) && !LandingPathExists(repository, value)) ||
            !RequiredTextArray(entry, "retained_evidence_paths", value =>
                IsClosedPath(value) && LandingPathExists(repository, value)) ||
            !RequiredTextArray(entry, "historical_provenance_paths", value =>
                IsClosedPath(value) && LandingPathExists(repository, value)) ||
            !RequiredTextArray(entry, "retained_assertion_groups") ||
            !RequiredTextArray(entry, "superseded_assertion_groups") ||
            !RequiredTextArray(entry, "manifest_test_dispositions") ||
            !RequiredTextArray(entry, "digest_test_dispositions") ||
            !RequiredTextArray(entry, "later_leaf_assertion_owners"))
        {
            return false;
        }

        var removed = TextArray(entry, "removed_paths")
            .ToHashSet(StringComparer.Ordinal);
        var retainedEvidence = TextArray(entry, "retained_evidence_paths")
            .ToHashSet(StringComparer.Ordinal);
        var provenance = TextArray(entry, "historical_provenance_paths")
            .ToHashSet(StringComparer.Ordinal);
        var retainedAssertions = TextArray(entry, "retained_assertion_groups")
            .ToHashSet(StringComparer.Ordinal);
        var supersededAssertions = TextArray(entry, "superseded_assertion_groups")
            .ToHashSet(StringComparer.Ordinal);
        var digestDispositions = TextArray(entry, "digest_test_dispositions")
            .ToHashSet(StringComparer.Ordinal);
        var laterOwners = TextArray(entry, "later_leaf_assertion_owners")
            .ToHashSet(StringComparer.Ordinal);
        var manifestNames = TextArray(entry, "manifest_test_dispositions")
            .Select(value => value.Split(':', 2)[0])
            .ToHashSet(StringComparer.Ordinal);

        if (!removed.SetEquals(
            [
                "src/prefix-contract/",
                "scripts/regenerate-prefix-contract-fixtures.mjs",
            ]) ||
            !retainedEvidence.SetEquals(
            [
                "runtime/tests/AgenticPrReview.Runtime.Tests/Prefix/PrefixFixtureLoader.cs",
                "runtime/tests/AgenticPrReview.Runtime.Tests/Prefix/PrefixGoldenVectorTests.cs",
                "runtime/tests/AgenticPrReview.Runtime.Tests/Prefix/PrefixFixtureManifestRejectionTests.cs",
                "runtime/tests/AgenticPrReview.Runtime.Tests/Ledger/LedgerBuilderTests.cs",
                "runtime/tests/AgenticPrReview.Runtime.Tests/Host/Action/ActionHostFrameworkVerifierArchitectureTests.cs",
                "docs/20_architecture/r4-actionhost-wrapper-plan.md",
            ]) ||
            !provenance.SetEquals(
            [
                "protocol/fixtures/prefix-contract/v1/manifest.json",
                "runtime/tests/fixtures/action-host/framework/e1-base-inventory.json",
            ]) ||
            !retainedAssertions.SetEquals(
            [
                "framing identities digests materialization append invalidation bounds diagnostics and continuation",
                "closed corpus manifest vector shapes references and mutation semantics",
                "immutable historical TypeScript diagnostic provenance",
                "seven-field Ledger cache-contract digest known answer",
            ]) ||
            !supersededAssertions.SetEquals(
            [
                "TypeScript barrel exports import restrictions and generator execution",
                "review-subject digest with no current C# producer or consumer",
                "JavaScript Proxy descriptor getter sparse-array alias cycle and mutable-object TOCTOU mechanics",
            ]) ||
            !digestDispositions.SetEquals(
            [
                "uses the review-subject domain tag and one NUL separator: obsolete M4 TypeScript behavior with no current producer or consumer",
                "uses the exact untagged seven-field cache-contract object: retained C# Ledger known-answer test",
            ]) ||
            !laterOwners.SetEquals(
            [
                "W14: RFC 8785 canonical JSON production and retirement evidence",
            ]) ||
            !manifestNames.SetEquals(
            [
                "real corpus satisfies every manifest rule",
                "rejects duplicate ids",
                "rejects duplicate file references",
                "rejects missing listed files",
                "rejects unlisted files on disk",
                "rejects unsafe paths",
                "rejects id/kind mismatch with vector content",
                "rejects bad references",
                "rejects wrong-kind references",
                "rejects unknown vector fields",
                "rejects missing per-kind required fields",
                "returns stable violations for malformed top-level manifest containers",
                "returns a stable violation for malformed manifest entries",
                "rejects unknown invalidation mode",
                "rejects canonical vectors missing typescriptCode",
                "rejects invalid expected union on invalid-vectors",
                "rejects wrong recursive value types in framing and append unions",
                "rejects malformed nested materialization fields and invalid diagnostics",
                "requires envelope mutations and their matching digest updates together",
                "keeps dotted property names distinct from nested mutation paths",
                "locks the declared invalidation matrix instead of trusting fixture booleans",
                "closes hash-framing mutation stream and boolean semantics",
                "rejects non-object vector files with a stable rule id",
            ]))
        {
            return false;
        }

        const string corpusPrefix = "protocol/fixtures/prefix-contract/";
        var expected = InventoryDigests(inventoryPath)
            .Where(pair => pair.Key.StartsWith(corpusPrefix,
                StringComparison.Ordinal))
            .ToDictionary(pair => pair.Key, pair => pair.Value,
                StringComparer.Ordinal);
        var corpusRoot = Path.Join(repository,
            corpusPrefix.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar));
        var actual = Directory.EnumerateFiles(corpusRoot, "*",
                SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(repository, path)
                .Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);
        return expected.Count > 0 && actual.SetEquals(expected.Keys) &&
            expected.All(pair => Sha256(Path.Join(repository,
                    pair.Key.Replace('/', Path.DirectorySeparatorChar))) ==
                pair.Value);
    }

    private static bool ValidateW12Ownership(JsonElement entry, string repository) =>
        entry.GetProperty("disposition").GetString() == "removed" &&
        RequiredTextArray(entry, "removed_paths", value =>
            IsClosedPath(value) && !LandingPathExists(repository, value)) &&
        RequiredTextArray(entry, "removed_csharp_members", member =>
            !MemberExists(repository, member)) &&
        RequiredTextArray(entry, "retained_evidence_paths", value =>
            IsClosedPath(value) && LandingPathExists(repository, value)) &&
        RequiredTextArray(entry, "retained_owner_groups") &&
        RequiredTextArray(entry, "obsolete_groups");

    private static bool MemberExists(string repository, string member)
    {
        var parts = member.Split('#', 2);
        if (parts.Length != 2 || !IsClosedPath(parts[0]) ||
            string.IsNullOrWhiteSpace(parts[1]))
        {
            return false;
        }

        var path = Path.Join(repository,
            parts[0].Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) && File.ReadAllText(path)
            .Contains(parts[1], StringComparison.Ordinal);
    }

    private static string[] TextArray(JsonElement value, string property) =>
        value.GetProperty(property).EnumerateArray()
            .Select(item => item.GetString() ?? "").ToArray();

    private static HashSet<string> InventoryPaths(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        return document.RootElement.GetProperty("files").EnumerateArray()
            .Select(entry => entry.GetProperty("path").GetString() ?? "")
            .ToHashSet(StringComparer.Ordinal);
    }

    private static Dictionary<string, string> InventoryDigests(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        return document.RootElement.GetProperty("files").EnumerateArray()
            .ToDictionary(
                entry => entry.GetProperty("path").GetString() ?? "",
                entry => entry.GetProperty("sha256").GetString() ?? "",
                StringComparer.Ordinal);
    }

    private static bool InventoryCovers(
        IReadOnlySet<string> inventory,
        string ownedPath) => ownedPath.EndsWith('/')
        ? inventory.Any(path => path.StartsWith(ownedPath,
            StringComparison.Ordinal))
        : inventory.Contains(ownedPath);

    private static bool LandingPathExists(string repository, string value)
    {
        var path = Path.Join(repository,
            value.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar));
        return value.EndsWith('/') ? Directory.Exists(path) : File.Exists(path);
    }

    private static bool Remember(ISet<string> values, string value)
    {
        values.Add(value);
        return true;
    }

    private static bool PathsOverlap(string first, string second) =>
        first == second ||
        first.EndsWith('/') && second.StartsWith(first, StringComparison.Ordinal) ||
        second.EndsWith('/') && first.StartsWith(second, StringComparison.Ordinal);

    private static HashSet<string> FrameworkScenarioIds() => new(
    [
        "dispatch-bootstrap", "dispatch-continuation",
        "dispatch-cross-head-conflict",
        "artifact-pagination-changed", "artifact-pagination-late",
        "artifact-list-duplicate", "artifact-digest-mismatch",
        "artifact-expired", "artifact-upload-outcome-unknown",
        "artifact-delete-outcome-unknown", "workflow-run", "delete-exact",
        "inline", "inline-warning", "unsupported", "fork", "permission",
        "wrong-action", "concurrency", "stale-head", "provider-malformed",
        "public-result", "credentials-missing", "crash-mutation",
        "crash-recovery", "cancel-before-side-effect",
        "cancel-before-dispatch", "cancel-known-commit",
        "cancel-outcome-unknown", "cancel-escalation", "host-crash",
    ], StringComparer.Ordinal);

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
        return count == 349 && root.GetProperty("aggregate_sha256")
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
        return items.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String
                ? item.GetString()
                : null)
            .All(text => !string.IsNullOrWhiteSpace(text) &&
                seen.Add(text) &&
                (predicate is null || predicate(text)));
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
                        .Where(sink => sink != "-")
                        .ToHashSet(StringComparer.Ordinal),
                    fields[3].Split(';', StringSplitOptions.RemoveEmptyEntries)
                        .Where(sink => sink != "-")
                        .ToHashSet(StringComparer.Ordinal),
                    fields[4].Split(';', StringSplitOptions.RemoveEmptyEntries)
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
                !RouteAllows(route, fields[1])))
        {
            return false;
        }

        foreach (var (canaryClass, route) in routes)
        {
            if (route.ForbiddenSinks.Count == 0 ||
                route.ForbiddenSinks.Any(pattern =>
                    !SinkMatches(pattern, NegativeSink(pattern)) ||
                    RouteAllows(route, NegativeSink(pattern))))
            {
                return false;
            }

            var count = observations.Count(fields => fields[0] == canaryClass);
            var expected = route.Cardinality switch
            {
                "exactly-one" => 1,
                "equals-provider-requests-and-host-credentials" =>
                    SumScenarioCounter(root, "provider-request-count") +
                    SumScenarioCounter(root,
                        "host-provider-credential-count"),
                "equals-github-rest-and-host-credentials" =>
                    SumScenarioCounter(root, "github-request-count") +
                    ReadInt(root, "official-rest-count") +
                    SumScenarioCounter(root, "host-github-credential-count"),
                "equals-twirp-requests" =>
                    ReadInt(root, "official-twirp-count"),
                "equals-blob-requests" =>
                    ReadInt(root, "official-blob-count") +
                    ReadInt(root, "official-signed-download-count"),
                "equals-session-plaintext-provider-requests" =>
                    SumScenarioCounter(root,
                        "provider-session-plaintext-request-count"),
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

        var negativeInjectionCount = RunNegativeInjectionMatrix(root, routes);
        if (negativeInjectionCount < 1) return false;
        File.WriteAllText(Path.Join(root, "canary-negative-injection-count"),
            negativeInjectionCount.ToString(CultureInfo.InvariantCulture));
        return true;
    }

    private static bool RouteAllows(CanaryRoute route, string sink) =>
        (route.AllowedSinks.Contains(sink) ||
            route.TerminalSinks.Contains(sink)) &&
        !route.ForbiddenSinks.Any(pattern => SinkMatches(pattern, sink));

    private static bool SinkMatches(string pattern, string sink) =>
        pattern.EndsWith('*')
            ? sink.StartsWith(pattern[..^1], StringComparison.Ordinal)
            : sink == pattern;

    private static string NegativeSink(string pattern) =>
        pattern.EndsWith('*') ? pattern[..^1] + "negative-injection" : pattern;

    private static int RunNegativeInjectionMatrix(
        string root,
        IReadOnlyDictionary<string, CanaryRoute> routes)
    {
        var workRoot = Path.Join(root, "canary-negative-work");
        var receipts = new List<string>();
        Directory.CreateDirectory(workRoot);
        try
        {
            foreach (var (canaryClass, route) in routes
                         .OrderBy(value => value.Key, StringComparer.Ordinal))
            {
                var forbiddenPattern = route.ForbiddenSinks
                    .Order(StringComparer.Ordinal).First();
                var sink = NegativeSink(forbiddenPattern);
                if (!SinkMatches(forbiddenPattern, sink) ||
                    RouteAllows(route, sink))
                {
                    return -1;
                }

                var canary = canaryClass == "artifact-ciphertext"
                    ? ArtifactCiphertextCanary(root)
                    : FrameworkCanaryCapture.RequiredCanaryValue(canaryClass);
                (string Name, byte[] Capture, bool Archive)[] captures =
                [
                    ("raw", Encoding.UTF8.GetBytes(canary), false),
                    ("url", JsonStringCapture(PercentEncodeAll(canary)), false),
                    ("base64", JsonStringCapture(DoubleBase64(canary)), false),
                    ("nested-json", NestedJsonCapture(canary), false),
                ];
                foreach (var capture in captures)
                {
                    if (!RunNegativeInjectionCase(workRoot, receipts,
                            canaryClass, canary, sink, capture.Name,
                            capture.Capture, capture.Archive))
                    {
                        return -1;
                    }
                }

                if ((canaryClass is "state-key-current" or
                    "state-key-previous" or "artifact-ciphertext") &&
                    !RunNegativeInjectionCase(workRoot, receipts,
                        canaryClass, canary, sink, "artifact-envelope",
                        ArtifactEnvelopeCapture(canary), true))
                {
                    return -1;
                }
            }

            File.WriteAllLines(
                Path.Join(root, "canary-negative-injections.tsv"), receipts);
            return receipts.Count;
        }
        finally
        {
            Directory.Delete(workRoot, recursive: true);
        }
    }

    private static bool RunNegativeInjectionCase(
        string workRoot,
        ICollection<string> receipts,
        string canaryClass,
        string canary,
        string sink,
        string representation,
        byte[] capture,
        bool archive)
    {
        var caseRoot = Path.Join(workRoot,
            receipts.Count.ToString("D3", CultureInfo.InvariantCulture) + "-" +
            canaryClass + "-" + representation);
        Directory.CreateDirectory(caseRoot);
        var absent = archive
            ? FrameworkCanaryCapture.ArchiveHasNoCanary(caseRoot, canaryClass,
                canary, capture, sink)
            : FrameworkCanaryCapture.AssertCanaryAbsent(caseRoot, canaryClass,
                canary, sink, capture);
        var expectedViolation = canaryClass + "\t" + sink +
            "\tforbidden_present";
        var violationPath = Path.Join(caseRoot, "canary-route-violation");
        var observationsPath = Path.Join(caseRoot, "canary-observations.tsv");
        var violations = File.Exists(violationPath)
            ? File.ReadAllLines(violationPath)
            : [];
        if (absent || File.Exists(observationsPath) ||
            violations.Length == 0 ||
            violations.Any(line => line != expectedViolation))
        {
            return false;
        }

        receipts.Add(canaryClass + "\t" + representation + "\t" + sink);
        return true;
    }

    private static string ArtifactCiphertextCanary(string root) =>
        Directory.EnumerateFiles(root, "artifact-ciphertext-proof.tsv",
                SearchOption.AllDirectories)
            .SelectMany(File.ReadAllLines)
            .Select(line => line.Split('\t'))
            .Where(fields => fields.Length == 3 && IsLowerHex(fields[0], 64))
            .Select(fields => fields[0])
            .Order(StringComparer.Ordinal)
            .FirstOrDefault() ??
        throw new InvalidOperationException("ciphertext canary evidence missing");

    private static string PercentEncodeAll(string value) => string.Concat(
        Encoding.UTF8.GetBytes(value).Select(value => "%" +
            value.ToString("X2", CultureInfo.InvariantCulture)));

    private static string DoubleBase64(string value) => Convert.ToBase64String(
        Encoding.UTF8.GetBytes(Convert.ToBase64String(
            Encoding.UTF8.GetBytes(value))));

    private static byte[] JsonStringCapture(string value) =>
        JsonSerializer.SerializeToUtf8Bytes(new { value });

    private static byte[] NestedJsonCapture(string value)
    {
        var escaped = string.Concat(value.Select(character => "\\u" +
            ((int)character).ToString("x4", CultureInfo.InvariantCulture)));
        return Encoding.UTF8.GetBytes(
            "{\"outer\":{\"value\":\"" + escaped + "\"}}");
    }

    private static byte[] ArtifactEnvelopeCapture(string value)
    {
        using var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create,
                   leaveOpen: true))
        {
            var entry = zip.CreateEntry("artifact-envelope.json",
                CompressionLevel.SmallestSize);
            using var writer = new StreamWriter(entry.Open(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write("{\"encrypted_object_base64\":\"");
            writer.Write(DoubleBase64(value));
            writer.Write("\"}");
        }

        return archive.ToArray();
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

    private static string CanaryRouteCoverageDigest(string root)
    {
        var framing = string.Join('\n', Directory.EnumerateFiles(root,
                    "canary-observations.tsv", SearchOption.AllDirectories)
                .SelectMany(File.ReadAllLines)
                .Where(line => line.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)) + "\n";
        return Sha256Text(framing);
    }

    private static string CanonicalizedIdentityDigest(string path)
    {
        var identities = new Dictionary<string, string>(StringComparer.Ordinal);
        var normalized = Regex.Replace(
            File.ReadAllText(path),
            "(?<![0-9a-f])[0-9a-f]{64}(?![0-9a-f])",
            match =>
            {
                if (!identities.TryGetValue(match.Value, out var identity))
                {
                    identity = "<identity-" + identities.Count.ToString(
                        "D3", CultureInfo.InvariantCulture) + ">";
                    identities.Add(match.Value, identity);
                }

                return identity;
            },
            RegexOptions.CultureInvariant);
        return Sha256Text(normalized);
    }

    private static string JsonProperty(string path, string property)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        return document.RootElement.GetProperty(property).GetString() ?? "";
    }

    private static bool ValidateContinuationStateTrace(string scenario)
    {
        var path = Path.Join(scenario, "state-operation-identities.tsv");
        if (!File.Exists(path)) return false;
        var lines = File.ReadAllLines(path)
            .Select(line => line.Split('\t')).Where(parts => parts.Length == 7)
            .ToArray();
        var predecessor = lines.Any(parts => parts[0] == "download" &&
            parts[4] == "None" && parts[6].Split('|') is { Length: 7 } values &&
            values[4] == "900" && values[5] == "1");
        var successor = lines.Any(parts => parts[0] == "upload" &&
            parts[4] == "None" && parts[5] == "Committed" &&
            parts[6].Split('|') is { Length: 7 } values &&
            values[2] == parts[3] && values[4] == "901" &&
            values[5] == "1");
        var acceptedReadBack = lines.Any(parts => parts[0] == "readback" &&
            parts[4] == "None" &&
            parts[6].Split('|') is { Length: 7 } values &&
            values[0] == parts[2] && values[2] == parts[3] &&
            values[4] == "901" && values[5] == "1");
        return predecessor && successor && acceptedReadBack;
    }

    private static bool ContinuationHeadAdvanced(string root)
    {
        var scenario = Path.Join(root, "dispatch-continuation");
        var predecessor = ReadOptionalText(scenario,
            "sticky-predecessor-comment.json");
        var successor = ReadOptionalText(scenario,
            "sticky-successor-comment.json");
        return predecessor.Contains(FrameworkGitHubHandler.HeadSha,
                StringComparison.Ordinal) &&
            !predecessor.Contains(FrameworkGitHubHandler.ContinuedHeadSha,
                StringComparison.Ordinal) &&
            successor.Contains(FrameworkGitHubHandler.ContinuedHeadSha,
                StringComparison.Ordinal);
    }

    private static string ContinuationStickyDigest(string root)
    {
        var scenario = Path.Join(root, "dispatch-continuation");
        var framing = ReadOptionalText(scenario,
            "sticky-predecessor-comment.json") + "\0" +
            ReadOptionalText(scenario, "sticky-successor-comment.json");
        var normalized = Regex.Replace(
            framing,
            "scope_sha256=[0-9a-f]{64}",
            "scope_sha256=<prepared-payload-scope>",
            RegexOptions.CultureInvariant);
        return Sha256Text(normalized);
    }

    private static string Sha256Text(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

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
        bool RequireSuccessfulContinuation = false,
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
        IReadOnlySet<string> TerminalSinks,
        IReadOnlySet<string> ForbiddenSinks,
        string Cardinality);
}

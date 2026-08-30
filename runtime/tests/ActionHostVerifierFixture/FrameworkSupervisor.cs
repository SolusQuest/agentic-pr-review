using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        var aotProof = AotProof.TryCreate(values, payload);

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
            ["execution-profile"] = aotProof.IsValid,
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
        if (values.TryGetValue("trusted-proof-only", out var trustedOnly) &&
            trustedOnly == "true")
        {
            return await RunTrustedProofPayloadAsync(
                root,
                repository,
                payload,
                bundle,
                node,
                platform,
                ReadCompiledPayloadSourceExpectation(values))
                .ConfigureAwait(false);
        }

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

        var canaryRouteFailures = new List<string>();
        var canaryRoutesPassed = EvaluateCanaryRoutes(
            root, canaries, canaryRouteFailures);
        canaryRouteFailures.AddRange(CanaryRouteViolationFailures(root));
        canaryRoutesPassed &= canaryRouteFailures.Count == 0;
        var failedGlobalGates = FailedGlobalGateNames(
            cases.Select(result => result.HostPid)
                .Where(value => value > 0).Distinct().Count(),
            canaryRoutesPassed,
            platform.ArtifactNames.Count,
            File.Exists(Path.Join(root, "official-delete-count")),
            File.Exists(Path.Join(root, "official-signed-download-count")),
            File.Exists(Path.Join(root, "official-finalize-count")));
        if (cases.Any(result => !result.Passed) || failedGlobalGates.Length != 0)
        {
            if (failedGlobalGates.Length != 0)
            {
                await File.WriteAllTextAsync(
                    Path.Join(root, "supervisor-global-diagnostic.json"),
                    FrameworkJson.Serialize(FrameworkJson.Object(
                        ("kind", "apr-r4-e2-supervisor-global-diagnostic-v2"),
                        ("failed_gates", FrameworkJson.Array(failedGlobalGates)),
                        ("canary_route_failures", FrameworkJson.Array(
                            canaryRouteFailures)))) + "\n")
                    .ConfigureAwait(false);
            }
            await WriteEvidenceAsync(root, payload, platform, cases, false)
                .ConfigureAwait(false);
            return 1;
        }

        var normalized = NormalizedEvidence(root, repository, record,
            inventory, canaries, cases, canaryRoutesPassed,
            artifactMetadataRouteProbeCount);
        var normalizedBytes = Encoding.UTF8.GetBytes(
            FrameworkJson.SerializeIndented(normalized) + "\n");
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
        if (aotProof.Context is { } context)
        {
            await WriteAotIdentityAsync(context, root, repository, record,
                inventory, canaries).ConfigureAwait(false);
            Console.WriteLine("APR_ACTION_HOST_AOT_VERIFY_OK");
        }
        Console.WriteLine("APR_ACTION_HOST_FRAMEWORK_VERIFY_OK");
        return 0;
    }

    internal static string[] FailedGlobalGateNames(
        int distinctPositiveHostPids,
        bool canaryRoutesPassed,
        int artifactNameCount,
        bool deleteCountObserved,
        bool signedDownloadCountObserved,
        bool finalizeCountObserved)
    {
        var failed = new List<string>();
        if (distinctPositiveHostPids < 2) failed.Add("host-pid-diversity");
        if (!canaryRoutesPassed) failed.Add("canary-routes");
        if (artifactNameCount < 1) failed.Add("official-artifact-name");
        if (!deleteCountObserved) failed.Add("official-delete-count");
        if (!signedDownloadCountObserved) failed.Add("official-signed-download-count");
        if (!finalizeCountObserved) failed.Add("official-finalize-count");
        return [.. failed];
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
                create.Content = new StringContent(FrameworkJson.Serialize(
                    FrameworkJson.Object(
                        ("workflowRunBackendId",
                            FrameworkCanaries.RunBackendId),
                        ("workflowJobRunBackendId",
                            FrameworkCanaries.JobBackendId),
                        ("name", FrameworkCanaries.ToolData))), Encoding.UTF8,
                    "application/json");
                using var createResponse = await client.SendAsync(create)
                    .ConfigureAwait(false);

                using var finalize = new HttpRequestMessage(HttpMethod.Post,
                    platform.BaseUrl +
                    "/twirp/github.actions.results.api.v1.ArtifactService/" +
                    "FinalizeArtifact");
                finalize.Headers.TryAddWithoutValidation("Authorization",
                    "Bearer " + RuntimeToken);
                finalize.Content = new StringContent(FrameworkJson.Serialize(
                    FrameworkJson.Object(
                        ("workflowRunBackendId",
                            FrameworkCanaries.RunBackendId),
                        ("workflowJobRunBackendId",
                            FrameworkCanaries.JobBackendId),
                        ("name", FrameworkCanaries.Prompt))), Encoding.UTF8,
                    "application/json");
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

    private static JsonObject NormalizedEvidence(
        string root,
        string repository,
        string record,
        string inventory,
        string canaries,
        IReadOnlyList<CaseResult> cases,
        bool canaryRoutesPassed,
        int artifactMetadataRouteProbeCount)
    {
        var continuation = cases.Single(result =>
            result.Name == "dispatch-continuation");
        return FrameworkJson.Object(
            ("schema", "apr.action-host.framework-evidence.v3"),
            ("source_inventory_digest", SourceInventoryDigest(repository)),
            ("replacement_record_digest", Sha256(record)),
            ("base_inventory_digest", JsonProperty(inventory,
                "aggregate_sha256")),
            ("canary_table_digest", Sha256(canaries)),
            ("scenarios", FrameworkJson.Array(cases.Select(result =>
                FrameworkJson.Object(
                    ("name", result.Name),
                    ("status", result.ActualStatus ?? "wrapper_failure"),
                    ("ExitCode", result.ExitCode),
                    ("ProviderRequests", result.ProviderRequests),
                    ("ToolSequence", result.ToolSequence),
                    ("GitHubRequests", result.GitHubRequests),
                    ("StateOperations", result.StateOperations),
                    ("StickyMutations", result.StickyMutations),
                    ("InlineMutations", result.InlineMutations),
                    ("CanarySafe", result.CanarySafe),
                    ("ContinuationObserved", result.ContinuationObserved))))),
            ("continuation", FrameworkJson.Object(
                ("second_process_status", continuation.ActualStatus),
                ("successor_accepted", continuation.Passed),
                ("reviewed_head_advanced", ContinuationHeadAdvanced(root)),
                ("state_identity_digest", CanonicalizedIdentityDigest(
                    Path.Join(root, "dispatch-continuation",
                        "state-operation-identities.tsv"))),
                ("sticky_lineage_digest", ContinuationStickyDigest(root)),
                ("prior_marker_first_request_exact", File.Exists(Path.Join(
                    root, "dispatch-continuation",
                    "provider-continuation-first-request-exact"))),
                ("prior_marker_carried_without_reinjection", File.Exists(
                    Path.Join(root, "dispatch-continuation",
                        "provider-continuation-carried-history-2")) &&
                    File.Exists(Path.Join(root, "dispatch-continuation",
                        "provider-continuation-carried-history-3"))),
                ("exact_tool_exchange_relations", File.Exists(Path.Join(
                    root, "dispatch-continuation",
                    "provider-continuation-relation-2")) &&
                    File.Exists(Path.Join(root, "dispatch-continuation",
                        "provider-continuation-relation-3"))))),
            ("process", FrameworkJson.Object(
                ("distinct_host_processes", cases.Select(result =>
                    result.HostPid).Where(value => value > 0).Distinct()
                    .Count() == cases.Count(result => result.HostPid > 0)),
                ("all_process_groups_quiet", cases.All(result =>
                    result.ProcessGroupQuiet)))),
            ("official_bridge", FrameworkJson.Object(
                ("twirp", ReadInt(root, "official-twirp-count")),
                ("blob", ReadInt(root, "official-blob-count")),
                ("rest", ReadInt(root, "official-rest-count")),
                ("rest_not_modified", ReadInt(root,
                    "official-rest-not-modified-count")),
                ("rest_primary", ReadInt(root,
                    "official-rest-primary-count")),
                ("rest_secondary_points", ReadInt(root,
                    "official-rest-secondary-points")),
                ("finalize", ReadInt(root, "official-finalize-count")),
                ("signed_download", ReadInt(root,
                    "official-signed-download-count")),
                ("delete", ReadInt(root, "official-delete-count")))),
            ("exact_child_environment", cases.All(result =>
                result.ExactEnvironment)),
            ("output_file_unchanged", cases.All(result =>
                result.OutputUnchanged)),
            ("canary_oracle_passed", canaryRoutesPassed),
            ("canary_route_coverage_digest",
                CanaryRouteCoverageDigest(root)),
            ("canary_negative_injection_count", ReadInt(root,
                "canary-negative-injection-count")),
            ("artifact_metadata_route_probe_count",
                artifactMetadataRouteProbeCount),
            ("normalized_exact_delete_identity_digest",
                CanonicalizedIdentityDigest(
                    Path.Join(root, "exact-delete-proof"))));
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
        Directory.CreateDirectory(scenario);
        var officialRestBefore = ReadInt(root, "official-rest-count");
        var officialRestNotModifiedBefore = ReadInt(root,
            "official-rest-not-modified-count");
        var officialRestPrimaryBefore = ReadInt(root,
            "official-rest-primary-count");
        var officialRestSecondaryPointsBefore = ReadInt(root,
            "official-rest-secondary-points");
        var signedUploadBefore = ReadInt(root, "official-blob-count");
        var signedDownloadBefore = ReadInt(root,
            "official-signed-download-count");
        platform.BeginScenario(spec.Mode, scenario, Sha256(payload));
        await File.WriteAllTextAsync(Path.Join(scenario, "mode"), spec.Mode)
            .ConfigureAwait(false);
        if (spec.TrustedProofPayload)
        {
            await File.WriteAllTextAsync(
                Path.Join(scenario, "trusted-proof-payload"),
                "1").ConfigureAwait(false);
        }
        await File.WriteAllTextAsync(Path.Join(scenario, "run-id"),
            RunId(spec).ToString(CultureInfo.InvariantCulture))
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Join(scenario, "run-attempt"), "1")
            .ConfigureAwait(false);
        // The receipt is bound to the exact executable bytes that this
        // scenario launches, not merely to a source identity.
        await File.WriteAllTextAsync(Path.Join(scenario, "payload-sha256"),
            Sha256(payload) + "\n").ConfigureAwait(false);
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

        if (spec.BarrierBefore is not null &&
            !await RunBarrierAsync(
                payload,
                spec,
                platform,
                scenario,
                spec.BarrierBefore).ConfigureAwait(false))
        {
            return FailedCase(spec);
        }

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
        var hostInitializationObserved =
            !spec.SignalAfterHostStart && !spec.CrashHost;
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
            hostInitializationObserved = await WaitForFileAsync(
                Path.Join(scenario, "host-initialization-complete"),
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            if (hostInitializationObserved) _ = Kill(process.Id, 15);
        }
        else if (spec.CrashHost && hostPid > 0)
        {
            hostInitializationObserved = await WaitForFileAsync(
                Path.Join(scenario, "host-initialization-complete"),
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            if (hostInitializationObserved) _ = KillProcess(hostPid);
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
        var barrierAfterPassed = spec.BarrierAfter is null ||
            await RunBarrierAsync(
                payload,
                spec,
                platform,
                scenario,
                spec.BarrierAfter).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Join(scenario, "wrapper-stdout.redacted.txt"),
            RedactCanaries(SanitizePrivateMaskCommands(stdout)))
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Join(scenario, "wrapper-stderr.redacted.txt"),
            RedactCanaries(stderr)).ConfigureAwait(false);
        if (spec.TrustedProofPayload)
        {
            CaptureTrustedProofRequestBudgetReceipts(scenario, stderr);
        }
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
        var reorderedHistoryRejected = !spec.TrustedProofPayload ||
            File.Exists(Path.Join(
                scenario,
                "provider-reordered-history-rejected"));
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
        var continuation = !spec.ExpectContinuation || spec.TrustedProofPayload ||
            File.Exists(
            Path.Join(scenario, "provider-continuation-observed"));
        var trustedContinuation = spec.TrustedProofPayload &&
            ReadInt(scenario, "sticky-create-count") == 0 &&
            ReadInt(scenario, "sticky-update-count") == 1 &&
            summary.Contains("| State disposition | accepted |",
                StringComparison.Ordinal) &&
            ReadOptionalJsonString(
                scenario,
                "sticky-successor-comment.json",
                "body")
                .Contains("Trusted continuation complete&#x2E;",
                    StringComparison.Ordinal) &&
            ValidateContinuationStateTrace(scenario);
        var successfulContinuation = !spec.RequireSuccessfulContinuation ||
            trustedContinuation ||
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
        var sixTools = spec.TrustedProofPayload || spec.ExpectContinuation ||
            spec.ExpectNoProvider ||
            spec.ExpectedStatus != "reviewed" &&
                spec.ExpectedStatus != "reviewed_with_inline_warnings" ||
            ReadInt(scenario, "provider-sequence") >= 6;
        var providerRequests = ReadInt(scenario, "provider-request-count");
        var stickyMutations = ReadInt(scenario, "sticky-create-count") +
            ReadInt(scenario, "sticky-update-count");
        var noProviderSatisfied = !spec.ExpectNoProvider ||
            providerRequests == 0;
        var providerCountSatisfied = spec.ExpectedProviderRequests is null ||
            providerRequests == spec.ExpectedProviderRequests;
        var stickyCountSatisfied = spec.ExpectedStickyMutations is null ||
            stickyMutations == spec.ExpectedStickyMutations;
        var scenarioEvidenceSatisfied = spec.RequiredScenarioEvidence is null ||
            File.Exists(Path.Join(scenario, spec.RequiredScenarioEvidence));
        var archiveTransportSatisfied = spec.RequiredScenarioEvidence ==
            "head-archive-served"
            ? HeadArchiveTransportEvidenceIsExact(scenario)
            : true;
        var stateOperationSatisfied = spec.RequiredStateOperation is null ||
            File.Exists(Path.Join(scenario, "state-operations.tsv")) &&
            File.ReadAllText(Path.Join(scenario, "state-operations.tsv"))
                .Contains(spec.RequiredStateOperation, StringComparison.Ordinal);
        var globalEvidenceSatisfied = spec.RequiredGlobalEvidence is null ||
            File.Exists(Path.Join(root, spec.RequiredGlobalEvidence));
        var noUnexpectedGitHubRequest = !File.Exists(
            Path.Join(scenario, "unexpected-github-request"));
        var artifactRestRequests = ReadInt(root, "official-rest-count") -
            officialRestBefore;
        var artifactRestNotModified = ReadInt(root,
            "official-rest-not-modified-count") -
            officialRestNotModifiedBefore;
        var artifactRestPrimary = ReadInt(root,
            "official-rest-primary-count") -
            officialRestPrimaryBefore;
        var artifactRestSecondaryPoints = ReadInt(root,
            "official-rest-secondary-points") -
            officialRestSecondaryPointsBefore;
        var anonymousSignedDownloads = ReadInt(root,
            "official-signed-download-count") - signedDownloadBefore;
        var anonymousSignedUploads = ReadInt(root, "official-blob-count") -
            signedUploadBefore;
        var trustedProofRequestBudgetSatisfied = !spec.TrustedProofPayload ||
            TrustedProofRequestBudgetReceiptsAreExact(
                scenario,
                artifactRestRequests,
                artifactRestNotModified,
                artifactRestPrimary,
                artifactRestSecondaryPoints,
                anonymousSignedDownloads);
        var artifactRestBudgetSatisfied = trustedProofRequestBudgetSatisfied;
        var passed = exited && expected && barrierAfterPassed && noLeak &&
            closedEnvironment && reorderedHistoryRejected && outputUnchanged &&
            groupQuiet && platformQuiet && continuation &&
            successfulContinuation && sixTools && signalGateReached &&
            hostInitializationObserved &&
            noProviderSatisfied && providerCountSatisfied &&
            stickyCountSatisfied && scenarioEvidenceSatisfied &&
            archiveTransportSatisfied && trustedProofRequestBudgetSatisfied &&
            artifactRestBudgetSatisfied &&
            stateOperationSatisfied && globalEvidenceSatisfied &&
            noUnexpectedGitHubRequest;
        if (!passed)
        {
            await File.WriteAllTextAsync(
                Path.Join(scenario, "case-diagnostic.json"),
                FrameworkJson.Serialize(FrameworkJson.Object(
                    ("case", spec.Name),
                    ("expected_status", spec.ExpectedStatus),
                    ("actual_status", status),
                    ("exited", exited),
                    ("exit_code", process.ExitCode),
                    ("expected_outcome", expected),
                    ("barrier_after", barrierAfterPassed),
                    ("canary_safe", noLeak),
                    ("environment_recorded", closedEnvironment),
                    ("reordered_history_rejected", reorderedHistoryRejected),
                    ("output_unchanged", outputUnchanged),
                    ("process_group_quiet", groupQuiet),
                    ("platform_quiet", platformQuiet),
                    ("continuation_observed", continuation),
                    ("successful_continuation", successfulContinuation),
                    ("tool_sequence_satisfied", sixTools),
                    ("signal_gate_reached", signalGateReached),
                    ("host_initialization_observed",
                        hostInitializationObserved),
                    ("no_provider_satisfied", noProviderSatisfied),
                    ("provider_count_satisfied", providerCountSatisfied),
                    ("sticky_count_satisfied", stickyCountSatisfied),
                    ("scenario_evidence_satisfied", scenarioEvidenceSatisfied),
                    ("archive_transport_satisfied", archiveTransportSatisfied),
                    ("trusted_proof_request_budget_satisfied",
                        trustedProofRequestBudgetSatisfied),
                    ("artifact_rest_budget_satisfied",
                        artifactRestBudgetSatisfied),
                    ("state_operation_satisfied", stateOperationSatisfied),
                    ("global_evidence_satisfied", globalEvidenceSatisfied),
                    ("no_unexpected_github_request",
                        noUnexpectedGitHubRequest))) + "\n")
                .ConfigureAwait(false);
        }
        await File.WriteAllTextAsync(Path.Join(scenario, "case-result.txt"),
            passed ? "pass\n" : "fail\n").ConfigureAwait(false);
        return new CaseResult(
            spec.Name,
            spec.ExpectedStatus,
            status,
            process.ExitCode,
            hostPid,
            providerRequests,
            ReadInt(scenario, "provider-sequence"),
            ReadInt(scenario, "github-request-count"),
            artifactRestRequests,
            artifactRestNotModified,
            artifactRestPrimary,
            artifactRestSecondaryPoints,
            anonymousSignedUploads,
            anonymousSignedDownloads,
            File.Exists(Path.Join(scenario, "state-operations.tsv"))
                ? File.ReadLines(Path.Join(scenario, "state-operations.tsv"))
                    .Count()
                : 0,
            stickyMutations,
            ReadInt(scenario, "inline-batch-count"),
            closedEnvironment,
            outputUnchanged,
            groupQuiet,
            platformQuiet,
            noLeak,
            continuation,
            passed);
    }

    private static async Task<bool> RunBarrierAsync(
        string payload,
        CaseSpec spec,
        SyntheticOfficialPlatform platform,
        string scenario,
        string mode)
    {
        var info = new ProcessStartInfo(payload)
        {
            WorkingDirectory = scenario,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add("barrier");
        info.ArgumentList.Add(mode);
        info.Environment.Clear();
        info.Environment["PATH"] = "/usr/local/bin:/usr/bin:/bin";
        info.Environment["HOME"] = scenario;
        info.Environment["TMPDIR"] = scenario;
        info.Environment["GITHUB_API_URL"] = platform.BaseUrl;
        info.Environment["GITHUB_TOKEN"] = FrameworkCanaries.GitHubToken;
        info.Environment["REPOSITORY"] =
            FrameworkCanaries.ProofControlRepository;
        info.Environment["REPOSITORY_ID"] =
            FrameworkGitHubHandler.RepositoryId.ToString(
                CultureInfo.InvariantCulture);
        info.Environment["PR_NUMBER"] =
            FrameworkGitHubHandler.PullRequestNumber.ToString(
                CultureInfo.InvariantCulture);
        info.Environment["FIXTURE_HEAD_SHA"] = FrameworkGitHubHandler.HeadSha;
        info.Environment["OPERATION_ID"] = spec.Mode == "stale"
            ? FrameworkCanaries.StaleProofOperationId
            : FrameworkCanaries.ProofOperationId;
        info.Environment["WORKFLOW_SHA"] = FrameworkGitHubHandler.WorkflowSha;
        info.Environment["ACTION_SOURCE_SHA"] = FrameworkGitHubHandler.ActionSha;
        info.Environment["PAYLOAD_SHA256"] = Sha256(payload);
        info.Environment["AGENTIC_PR_REVIEW_R4_REQUEST_BUDGET_PROFILE"] =
            "measurement";
        info.Environment["RUN_ID"] = RunId(spec).ToString(
            CultureInfo.InvariantCulture);
        info.Environment["RUN_ATTEMPT"] = "1";
        using var process = new Process { StartInfo = info };
        if (!process.Start())
        {
            return false;
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!await WaitForExitAsync(process, TimeSpan.FromSeconds(30))
                .ConfigureAwait(false))
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
            return false;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Join(scenario, "barrier-" + mode + ".stdout"),
            stdout).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Join(scenario, "barrier-" + mode + ".stderr"),
            stderr).ConfigureAwait(false);
        CaptureExternalControlRequestBudgetReceipt(scenario, mode, stderr);
        return process.ExitCode == 0 &&
            !stdout.Contains(FrameworkCanaries.GitHubToken,
                StringComparison.Ordinal) &&
            !stderr.Contains(FrameworkCanaries.GitHubToken,
                StringComparison.Ordinal) &&
            (mode != "cleanup" || stdout.Contains(
                "apr-r4-e2p-proof-control-cleanup-v1",
                StringComparison.Ordinal));
    }

    private static CaseResult FailedCase(CaseSpec spec) => new(
        spec.Name,
        spec.ExpectedStatus,
        null,
        1,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        false,
        false,
        true,
        true,
        true,
        false,
        false);

    private static async Task<int> RunTrustedProofPayloadAsync(
        string root,
        string repository,
        string payload,
        string bundle,
        string node,
        SyntheticOfficialPlatform platform,
        CompiledPayloadSourceExpectation? sourceExpectation)
    {
        var cases = new List<CaseResult>
        {
            await RunCaseAsync(new CaseSpec(
                "dispatch-bootstrap",
                "continuation-seed",
                "reviewed",
                WorkflowRun: true,
                ExpectedStickyMutations: 1,
                TrustedProofPayload: true,
                RequiredScenarioEvidence: "head-archive-served",
                BarrierBefore: "hold"),
                root, repository, payload, bundle, node, platform)
                .ConfigureAwait(false),
            await RunCaseAsync(new CaseSpec(
                "dispatch-continuation",
                "continuation",
                "reviewed",
                ExpectContinuation: true,
                ExpectedStickyMutations: 1,
                RequireSuccessfulContinuation: true,
                TrustedProofPayload: true,
                RequiredScenarioEvidence: "head-archive-served",
                BarrierBefore: "verify-completed",
                BarrierAfter: "cleanup"),
                root, repository, payload, bundle, node, platform)
                .ConfigureAwait(false),
        };
        platform.ResetArtifacts();
        cases.Add(await RunCaseAsync(new CaseSpec(
            "stale-head",
            "stale",
            "stale_head",
            ExpectedStickyMutations: 0,
            TrustedProofPayload: true,
            RequiredScenarioEvidence: "head-archive-served",
            BarrierBefore: "hold"),
            root, repository, payload, bundle, node, platform)
            .ConfigureAwait(false));
        cases.Add(await RecordStaleUnauthorizedFollowOnAsync(root, repository,
            node)
            .ConfigureAwait(false));
        var payloadCases = cases.Where(result =>
            result.Name != "stale-unauthorized-follow-on").ToArray();
        var compiledIdentities = payloadCases
            .Select(result => ReadCompiledPayloadIdentity(root, result.Name))
            .ToArray();
        var compiledIdentity = compiledIdentities.FirstOrDefault();
        var compiledIdentityValid = sourceExpectation is not null &&
            compiledIdentity is not null &&
            compiledIdentities.All(value => value == compiledIdentity) &&
            StringComparer.Ordinal.Equals(
                compiledIdentity.ProofKind,
                "apr-r4-e2p-trusted-proof-payload-v2") &&
            StringComparer.Ordinal.Equals(
                compiledIdentity.SourceCommit,
                sourceExpectation.SourceCommit) &&
            StringComparer.Ordinal.Equals(
                compiledIdentity.SourceTree,
                sourceExpectation.SourceTree);
        var requestBudgetValid = await VerifyTrustedProofRequestBudgetAsync(
            root, repository, cases).ConfigureAwait(false);
        var passed = cases.All(result => result.Passed) &&
            compiledIdentityValid && requestBudgetValid;
        var evidence = FrameworkJson.Object(
            ("passed", passed),
            ("verifier_executable_sha256", Sha256(payload)),
            ("compiled_payload_proof_kind", compiledIdentity?.ProofKind),
            ("compiled_payload_source_commit", compiledIdentity?.SourceCommit),
            ("compiled_payload_source_tree", compiledIdentity?.SourceTree),
            ("trusted_proof_request_budget_satisfied", requestBudgetValid),
            ("cases", FrameworkJson.Array(cases.Select(CaseEvidence))));
        await File.WriteAllTextAsync(
            Path.Join(root, "trusted-proof-payload-evidence.json"),
            FrameworkJson.SerializeIndented(evidence))
            .ConfigureAwait(false);
        return passed ? 0 : 1;
    }

    // This records the denied preflight boundary without launching a payload.
    // It is intentionally not RunCaseAsync: an unauthorized follow-on must
    // prove that no protected job, token, provider, state, snapshot, or
    // publisher route can begin before the workflow preflight rejects it.
    private static async Task<CaseResult>
        RecordStaleUnauthorizedFollowOnAsync(
            string root,
            string repository,
            string node)
    {
        const string name = "stale-unauthorized-follow-on";
        var scenario = Path.Join(root, name);
        Directory.CreateDirectory(scenario);
        var preflight = await RunUnauthorizedNoLaunchProbeAsync(repository, node)
            .ConfigureAwait(false);
        var receiptNames = new[]
        {
            "trusted-proof-github-request-budget.json",
            "trusted-proof-artifact-rest-request-budget.json",
            "trusted-proof-embedded-control-request-budget.json",
        };
        var counters = new[]
        {
            "github-request-count",
            "provider-request-count",
            "publisher-api-count",
            "head-commit-api-count",
            "head-tree-api-count",
            "head-archive-api-count",
            "head-blob-api-count",
            "policy-api-count",
            "authorization-api-count",
        };
        var csharpPayloadReceiptPresent = File.Exists(Path.Join(scenario,
            receiptNames[0]));
        var nodeArtifactReceiptPresent = File.Exists(Path.Join(scenario,
            receiptNames[1]));
        var embeddedControlReceiptPresent = File.Exists(Path.Join(scenario,
            receiptNames[2]));
        var externalControlReceiptPresent = Directory.EnumerateFiles(scenario,
                "trusted-proof-external-control-*-request-budget.json")
            .Any();
        var githubProtectedRequests = ReadInt(scenario, "github-request-count");
        var providerRequests = ReadInt(scenario, "provider-request-count");
        var publisherRequests = ReadInt(scenario, "publisher-api-count");
        var stateOperations = File.Exists(Path.Join(scenario,
            "state-operations.tsv"))
            ? File.ReadLines(Path.Join(scenario, "state-operations.tsv")).Count()
            : 0;
        var passed = preflight.Passed && !csharpPayloadReceiptPresent &&
            !nodeArtifactReceiptPresent && !embeddedControlReceiptPresent &&
            !externalControlReceiptPresent &&
            counters.All(counter => ReadInt(scenario, counter) == 0) &&
            stateOperations == 0 &&
            !File.Exists(Path.Join(scenario, "host-pid"));
        var evidence = FrameworkJson.Object(
            ("preflight_admitted", false),
            ("public_preflight_requests", preflight.PublicPreflightRequests),
            ("preflight_authorization_header_present",
                preflight.AuthorizationHeaderPresent),
            ("workflow_run_review_eligible", preflight.WorkflowRunEligible),
            ("workflow_dispatch_review_eligible",
                preflight.WorkflowDispatchEligible),
            ("payload_started", false),
            ("payload_start_attempts", preflight.PayloadStarts),
            ("wrapper_start_attempts", preflight.WrapperStarts),
            ("provider_start_attempts", preflight.ProviderStarts),
            ("state_start_attempts", preflight.StateStarts),
            ("publisher_start_attempts", preflight.PublisherStarts),
            ("csharp_payload_receipt_attempts", preflight.CSharpReceiptStarts),
            ("node_artifact_receipt_attempts", preflight.NodeReceiptStarts),
            ("embedded_control_receipt_attempts",
                preflight.EmbeddedControlReceiptStarts),
            ("external_control_receipt_attempts",
                preflight.ExternalControlReceiptStarts),
            ("csharp_payload_receipt_present", csharpPayloadReceiptPresent),
            ("node_artifact_bridge_receipt_present", nodeArtifactReceiptPresent),
            ("embedded_control_receipt_present", embeddedControlReceiptPresent),
            ("external_control_receipt_present", externalControlReceiptPresent),
            ("github_protected_requests", githubProtectedRequests),
            ("provider_requests", providerRequests),
            ("state_operations", stateOperations),
            ("publisher_requests", publisherRequests));
        await File.WriteAllTextAsync(Path.Join(scenario,
            "preflight-denial-evidence.json"),
            FrameworkJson.SerializeIndented(evidence) + "\n")
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Join(scenario, "case-result.txt"),
            passed ? "pass\n" : "fail\n").ConfigureAwait(false);
        return new CaseResult(
            name,
            "preflight_rejected",
            "preflight_rejected",
            passed ? 0 : 1,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            true,
            true,
            true,
            true,
            true,
            true,
            passed);
    }

    private static async Task<UnauthorizedNoLaunchProbe>
        RunUnauthorizedNoLaunchProbeAsync(string repository, string node)
    {
        var script = Path.Join(repository, "scripts",
            "probe-r4-e2p-unauthorized-no-launch.mjs");
        var workflow = Path.Join(repository, ".github", "workflows",
            "r4-trusted-proof.yml");
        if (!File.Exists(script) || !File.Exists(workflow))
        {
            return UnauthorizedNoLaunchProbe.Failed;
        }

        try
        {
            var info = new ProcessStartInfo(node)
            {
                WorkingDirectory = repository,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            info.ArgumentList.Add(script);
            info.ArgumentList.Add(workflow);
            using var process = new Process { StartInfo = info };
            if (!process.Start()) return UnauthorizedNoLaunchProbe.Failed;
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!await WaitForExitAsync(process, TimeSpan.FromSeconds(30))
                    .ConfigureAwait(false))
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
                return UnauthorizedNoLaunchProbe.Failed;
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            _ = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0) return UnauthorizedNoLaunchProbe.Failed;
            using var document = JsonDocument.Parse(stdout);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !StringComparer.Ordinal.Equals(root.GetProperty("schema")
                    .GetString(), "apr.r4.e2p.unauthorized-no-launch.v1") ||
                root.GetProperty("preflight_admitted").GetBoolean() ||
                root.GetProperty("public_preflight_requests").GetInt32() != 1 ||
                root.GetProperty("preflight_authorization_header_present")
                    .GetBoolean() ||
                root.GetProperty("workflow_run_review_eligible").GetBoolean() ||
                root.GetProperty("workflow_dispatch_review_eligible").GetBoolean())
            {
                return UnauthorizedNoLaunchProbe.Failed;
            }

            var starts = root.GetProperty("starts");
            var expectedStarts = new[]
            {
                "payload",
                "wrapper",
                "provider",
                "state",
                "publisher",
                "csharp_payload_receipt",
                "node_artifact_receipt",
                "embedded_control_receipt",
                "external_control_receipt",
            };
            if (starts.ValueKind != JsonValueKind.Object ||
                starts.EnumerateObject().Select(property => property.Name)
                    .Order(StringComparer.Ordinal)
                    .SequenceEqual(expectedStarts.Order(StringComparer.Ordinal)) is false ||
                expectedStarts.Any(property => starts.GetProperty(property)
                    .GetInt32() != 0))
            {
                return UnauthorizedNoLaunchProbe.Failed;
            }

            return new UnauthorizedNoLaunchProbe(true, 1, false, false, false,
                0, 0, 0, 0, 0, 0, 0, 0, 0);
        }
        catch (Exception exception) when (exception is IOException or
            InvalidOperationException or JsonException or
            System.ComponentModel.Win32Exception)
        {
            return UnauthorizedNoLaunchProbe.Failed;
        }
    }

    private static async Task<bool> VerifyTrustedProofRequestBudgetAsync(
        string root,
        string repository,
        IReadOnlyList<CaseResult> cases)
    {
        var golden = Path.Join(repository, "runtime", "tests", "fixtures",
            "action-host", "framework",
            "trusted-proof-request-budget.json.golden");
        if (!File.Exists(golden))
        {
            return false;
        }

        var names = new[]
        {
            "dispatch-bootstrap",
            "dispatch-continuation",
            "stale-head",
        };
        var selected = names.Select(name => cases.SingleOrDefault(result =>
            result.Name == name)).ToArray();
        if (selected.Any(result => result is null))
        {
            return false;
        }

        var payloadReceipts = selected.Select(result =>
            TryReadTrustedProofRequestBudgetReceipt(Path.Join(root,
                result!.Name))).ToArray();
        var artifactReceipts = selected.Select(result =>
            TryReadArtifactRestRequestBudgetReceipt(Path.Join(root,
                result!.Name))).ToArray();
        var embeddedControlReceipts = selected.Select(result =>
            TryReadEmbeddedControlRequestBudgetReceipt(Path.Join(root,
                result!.Name))).ToArray();
        var allExternalControlReceipts = selected.Select(result =>
            ReadExternalControlRequestBudgetReceipts(Path.Join(root,
                result!.Name))).ToArray();
        var protectedExternalControlReceipts = allExternalControlReceipts
            .Select(receipts => receipts.Where(receipt =>
                receipt.Phase != "cleanup").ToArray()).ToArray();
        var postOperationCleanupReceipts = allExternalControlReceipts
            .Select(receipts => receipts.Where(receipt =>
                receipt.Phase == "cleanup").ToArray()).ToArray();
        if (payloadReceipts.Any(receipt => receipt is null) ||
            artifactReceipts.Any(receipt => receipt is null) ||
            embeddedControlReceipts.Any(receipt => receipt is null))
        {
            return false;
        }

        const int payloadMaximum = 216;
        const int controlMaximum = 64;
        const int repositoryPrimaryMaximum = 1_000;
        const int minimumPrimaryReserve = 64;
        const int secondaryPointMaximumPerMinute = 600;
        const int minimumMutativeSpacingMilliseconds = 1_000;
        var artifactMaximumTotals = artifactReceipts.Select(receipt =>
                receipt!.MaximumTotalAuthenticatedApiRequests)
            .Distinct().ToArray();
        var artifactMaximumPrimaries = artifactReceipts.Select(receipt =>
                receipt!.MaximumPrimaryRateLimitRequests)
            .Distinct().ToArray();
        if (artifactMaximumTotals.Length != 1 ||
            artifactMaximumPrimaries.Length != 1)
        {
            return false;
        }
        var roleTotals = ObservedRoleTotals(payloadReceipts,
            artifactReceipts, embeddedControlReceipts,
            protectedExternalControlReceipts, postOperationCleanupReceipts);
        var operationPrimaryRequests = roleTotals.Values.Sum();
        var operationPrimaryReserve = repositoryPrimaryMaximum -
            operationPrimaryRequests;
        var finalAllocationValid = SatisfiesFrozenRoleAllocation(
            roleTotals, FrozenOperationPrimaryRoleCaps,
            minimumPrimaryReserve);
        var operationEvents = ReadOperationRequestEvents(root, names);
        var domainTails = operationEvents.DomainTails;
        var finalRemainingTailValid = SatisfiesFrozenRemainingTailGuard(
            domainTails);
        var perScenarioControlPrimary = Enumerable.Range(0, selected.Length)
            .Select(index => checked(embeddedControlReceipts[index]!.Primary +
                protectedExternalControlReceipts[index].Sum(receipt => receipt.Primary) +
                postOperationCleanupReceipts[index].Sum(receipt => receipt.Primary)))
            .ToArray();
        var perScenarioControlWithinCap = perScenarioControlPrimary.All(value =>
            value <= controlMaximum);
        var roleTotalsWithinBudget = roleTotals.Values.All(value => value >= 0 &&
            value + minimumPrimaryReserve <= repositoryPrimaryMaximum) &&
            operationPrimaryRequests + minimumPrimaryReserve <=
                repositoryPrimaryMaximum;
        // A tail is the largest future charged suffix after one observation in
        // its own scenario.  It is deliberately not a sum: each runtime
        // observation needs only the remaining suffix for that one execution.
        var domainTailsWithinBudget = domainTails.Values.All(value => value >= 0 &&
            value + minimumPrimaryReserve < repositoryPrimaryMaximum);
        var protectedEventJoins = selected.Select((result, index) =>
            ProtectedEventJoinIsExact(Path.Join(root, result!.Name), result,
                payloadReceipts[index]!, artifactReceipts[index]!,
                embeddedControlReceipts[index]!,
                protectedExternalControlReceipts[index],
                postOperationCleanupReceipts[index]))
            .ToArray();
        var measurementOnly = payloadReceipts.Any(receipt =>
                receipt!.MeasurementOnly) ||
            embeddedControlReceipts.Any(receipt => receipt!.MeasurementOnly) ||
            artifactReceipts.Any(receipt => receipt!.MeasurementOnly) ||
            protectedExternalControlReceipts.SelectMany(receipts => receipts)
                .Any(receipt => receipt.MeasurementOnly) ||
            postOperationCleanupReceipts.SelectMany(receipts => receipts)
                .Any(receipt => receipt.MeasurementOnly);
        var actualCases = FrameworkJson.Array(selected.Select((result, index) =>
        {
            var externalControls = FrameworkJson.Array(
                protectedExternalControlReceipts[index].Select(receipt =>
                    FrameworkJson.Object(
                        ("phase", receipt.Phase),
                        ("requests", receipt.Consumed))));
            var postOperationCleanup = FrameworkJson.Array(
                postOperationCleanupReceipts[index].Select(receipt =>
                    FrameworkJson.Object(
                        ("requests", receipt.Consumed))));
            return FrameworkJson.Object(
                ("name", result!.Name),
                ("csharp_payload", FrameworkJson.Object(
                    ("authenticated_rest_requests",
                        payloadReceipts[index]!.AuthenticatedRestRequests),
                    ("anonymous_codeload_requests",
                        payloadReceipts[index]!.AnonymousCodeloadRequests),
                    ("rejected_requests",
                        payloadReceipts[index]!.RejectedRequests))),
                ("node_artifact_bridge", FrameworkJson.Object(
                    ("total_authenticated_rest_requests",
                        artifactReceipts[index]!.TotalAuthenticatedApiRequests),
                    ("conditional_not_modified_requests",
                        artifactReceipts[index]!
                            .ConditionalNotModifiedRequests),
                    ("primary_rate_limit_requests",
                        artifactReceipts[index]!.PrimaryRateLimitRequests),
                    ("secondary_limit_points",
                        artifactReceipts[index]!.SecondaryLimitPoints),
                    ("permission_denied",
                        artifactReceipts[index]!.PermissionDenied),
                    ("anonymous_signed_downloads",
                        result.AnonymousSignedDownloads))),
                ("embedded_control", FrameworkJson.Object(
                    ("requests", embeddedControlReceipts[index]!.Consumed),
                    ("primary", embeddedControlReceipts[index]!.Primary))),
                ("external_control", externalControls),
                ("post_operation_cleanup", postOperationCleanup),
                ("roles", RoleEvidence(
                    payloadReceipts[index]!, artifactReceipts[index]!,
                    embeddedControlReceipts[index]!,
                    protectedExternalControlReceipts[index],
                    postOperationCleanupReceipts[index])),
                ("control_primary_total", perScenarioControlPrimary[index]),
                ("control_primary_within_64", perScenarioControlPrimary[index] <=
                    controlMaximum),
                ("receipt_event_join", protectedEventJoins[index]));
        }));
        var receiptEventJoins = FrameworkJson.Array(selected.Select((result, index) =>
            ReceiptEventJoinEvidence(Path.Join(root, result!.Name), result,
                payloadReceipts[index]!, artifactReceipts[index]!,
                embeddedControlReceipts[index]!,
                protectedExternalControlReceipts[index],
                postOperationCleanupReceipts[index], protectedEventJoins[index])));
        var roleTotalEvidence = FrameworkJson.Object(
            ("node_artifact_rest", roleTotals["node_artifact_rest"]),
            ("host_head_source_rest", roleTotals["host_head_source_rest"]),
            ("host_other_github_rest", roleTotals["host_other_github_rest"]),
            ("embedded_control", roleTotals["embedded_control"]),
            ("external_control", roleTotals["external_control"]),
            ("cleanup_control", roleTotals["cleanup_control"]));
        var domainTailEvidence = FrameworkJson.Object(
            ("node_artifact_rest", domainTails["node_artifact_rest"]),
            ("host_head_source_rest", domainTails["host_head_source_rest"]),
            ("host_other_github_rest", domainTails["host_other_github_rest"]),
            ("trusted_control_rest", domainTails["trusted_control_rest"]));
        var actual = FrameworkJson.Object(
            ("schema", "apr.r4.trusted-proof.github-request-budget.v3"),
            ("head_source_shape", "production_shaped_synthetic"),
            ("head_source_authenticated_rest_requests", 180),
            ("head_source_formula", "commit:1 + trees:178 + tarball:1"),
            ("head_source_scope",
                "synthetic tree-request and archive transport shape only"),
            ("includes_resolve_initial_pr_get", true),
            ("payload_maximum_authenticated_rest_requests", payloadMaximum),
            ("control_max_requests_per_phase", controlMaximum),
            ("artifact_bridge_maximum_total_authenticated_rest_requests",
                artifactMaximumTotals[0]),
            ("artifact_bridge_maximum_primary_rate_limit_requests",
                artifactMaximumPrimaries[0]),
            ("artifact_bridge_secondary_point_maximum_per_minute",
                secondaryPointMaximumPerMinute),
            ("artifact_bridge_minimum_mutative_spacing_milliseconds",
                minimumMutativeSpacingMilliseconds),
            ("external_control_max_requests_per_phase", controlMaximum),
            ("repository_primary_rate_limit_maximum",
                repositoryPrimaryMaximum),
            ("operation_primary_rate_limit_formula",
                "payload + artifact + embedded control + external control + cleanup"),
            ("operation_primary_rate_limit_requests",
                operationPrimaryRequests),
            ("operation_primary_rate_limit_reserve", operationPrimaryReserve),
            ("minimum_required_operation_primary_rate_limit_reserve",
                minimumPrimaryReserve),
            ("final_role_allocation_frozen", finalAllocationValid),
            ("remaining_tail_guard_frozen", finalRemainingTailValid),
            ("fixed_scenario_order", FrameworkJson.Array(names)),
            ("suite_role_totals", roleTotalEvidence),
            ("domain_tails", domainTailEvidence),
            ("primary_budget", FrameworkJson.Object(
                ("total", operationPrimaryRequests),
                ("reserve", minimumPrimaryReserve),
                ("slack", repositoryPrimaryMaximum - operationPrimaryRequests -
                    minimumPrimaryReserve),
                ("role_totals_within_budget", roleTotalsWithinBudget),
                ("domain_tails_within_budget", domainTailsWithinBudget))),
            ("event_sequence", FrameworkJson.Object(
                ("digest", operationEvents.SequenceDigest),
                ("ordinal_policy",
                    "one-based append order in fixed scenario aggregate"),
                ("scenario_ordinals", FrameworkJson.Array(names.Select((name, index) =>
                    (object?)FrameworkJson.Object(("name", name),
                        ("ordinal", index + 1))))),
                ("event_count", operationEvents.Events.Count),
                ("first_ordinal", operationEvents.Events.Count == 0 ? 0 : 1),
                ("last_ordinal", operationEvents.Events.Count),
                ("primary_charged_events", operationEvents.Events.Count(
                    IsPrimaryCharged)))),
            ("operation_wide_measurement", FrameworkJson.Object(
                ("measurement_only", measurementOnly),
                ("event_shape_valid", operationEvents.ShapeValid),
                ("node_secondary_points_within_600", operationEvents.NodeWindowValid),
                ("all_authenticated_secondary_points_below_900",
                    operationEvents.AllWindowValid),
                ("authenticated_mutations_spaced", operationEvents.MutationSpacingValid),
                ("node_artifact_raw_events", operationEvents.NodeArtifactRaw),
                ("host_head_source_raw_events", operationEvents.HostHeadRaw),
                ("host_other_raw_events", operationEvents.HostOtherRaw),
                ("trusted_control_raw_events", operationEvents.ControlRaw),
                ("actions_results_events", operationEvents.ResultsRaw),
                ("anonymous_transfer_events", operationEvents.AnonymousRaw))),
            ("protected_event_receipt_joins", FrameworkJson.Array(
                protectedEventJoins.Select(value => (object?)value))),
            ("receipt_event_joins", receiptEventJoins),
            ("cases", actualCases));
        var actualBytes = Encoding.UTF8.GetBytes(
            FrameworkJson.SerializeIndented(actual) + "\n");
        await File.WriteAllBytesAsync(Path.Join(root,
            "trusted-proof-request-budget-evidence.json"), actualBytes)
            .ConfigureAwait(false);
        return payloadReceipts.All(receipt =>
                PayloadRequestBudgetReceiptIsExact(receipt!, payloadMaximum)) &&
            selected.Select((result, index) =>
                ArtifactRestRequestBudgetReceiptIsExact(
                    artifactReceipts[index]!,
                    Path.Join(root, selected[index]!.Name),
                    result!.ArtifactRestRequests,
                    result.ArtifactRestNotModified,
                    result.ArtifactRestPrimary,
                    result.ArtifactRestSecondaryPoints,
                    result.AnonymousSignedDownloads)).All(value => value) &&
            embeddedControlReceipts.All(receipt => receipt is not null &&
                ControlRequestBudgetReceiptIsExact(receipt)) &&
            protectedExternalControlReceipts.Select((receipts, index) =>
                receipts.Length == 1 && receipts[0].Phase ==
                    RequiredProtectedExternalControlPhase(selected[index]!.Name))
                .All(value => value) &&
            protectedExternalControlReceipts.All(receipts =>
                receipts.All(ControlExternalReceiptIsExact)) &&
            postOperationCleanupReceipts.Select((receipts, index) =>
                selected[index]!.Name == "dispatch-continuation"
                    ? receipts.Length == 1
                    : receipts.Length == 0).All(value => value) &&
            postOperationCleanupReceipts.All(receipts =>
                receipts.All(ControlExternalReceiptIsExact)) &&
            operationEvents.ShapeValid &&
            operationEvents.NodeWindowValid &&
            operationEvents.AllWindowValid &&
            operationEvents.MutationSpacingValid &&
            protectedEventJoins.All(value => value) &&
            perScenarioControlWithinCap &&
            roleTotalsWithinBudget &&
            domainTailsWithinBudget &&
            !measurementOnly &&
            finalAllocationValid &&
            finalRemainingTailValid &&
            operationPrimaryReserve >= minimumPrimaryReserve &&
            JsonEquivalent(actualBytes, await File.ReadAllBytesAsync(golden)
                .ConfigureAwait(false));
    }

    private static string RequiredProtectedExternalControlPhase(string name) =>
        name switch
        {
            "dispatch-bootstrap" => "hold",
            "dispatch-continuation" => "verify-completed",
            "stale-head" => "hold",
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };

    private static bool ControlExternalReceiptIsExact(
        FrameworkExternalControlRequestBudgetReceipt receipt) =>
        receipt.Consumed >= 0 && receipt.Consumed <= receipt.Limit &&
        receipt.Limit == 64 && receipt.Primary >= 0 &&
        receipt.NotModified >= 0 && receipt.Primary + receipt.NotModified ==
            receipt.Consumed && receipt.SecondaryPoints >= receipt.Consumed &&
        receipt.MutationCount >= 0 && receipt.MutationCount <= receipt.Consumed &&
        receipt.PermissionDenied == 0 &&
        receipt.PrimaryRateLimited == 0 &&
        receipt.SecondaryRateLimited == 0 &&
        receipt.CombinedRateLimited == 0 &&
        !receipt.InvalidRemainingHeader && RemainingTailProfileIsExact(receipt) &&
        !receipt.RateLimited;

    private static bool RemainingTailProfileIsExact(
        FrameworkExternalControlRequestBudgetReceipt receipt) => receipt.MeasurementOnly
        ? receipt.RemainingTailReserve ==
            TrustedProofOperationRequestAccounting.OperationPrimaryReserve &&
            receipt.RemainingTailRequired == 0
        : FrozenTailReceiptIsExact(TrustedProofRequestDomain.TrustedControlRest,
            receipt.RemainingTailRequired, receipt.RemainingTailReserve);

    // Empty until the first complete AOT measurement is reviewed.  Keeping the
    // final map unpopulated is fail-closed: no measurement profile can become
    // a live verdict merely because its wide local guard happened to pass.
    private static readonly IReadOnlyDictionary<string, int>
        FrozenOperationPrimaryRoleCaps = new Dictionary<string, int>(
            StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, int> ObservedRoleTotals(
        FrameworkRequestBudgetReceipt?[] host,
        FrameworkArtifactRestRequestBudgetReceipt?[] artifact,
        FrameworkControlRequestBudgetReceipt?[] embedded,
        IReadOnlyList<FrameworkExternalControlRequestBudgetReceipt>[] external,
        IReadOnlyList<FrameworkExternalControlRequestBudgetReceipt>[] cleanup) =>
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["node_artifact_rest"] = artifact.Sum(value => value!.PrimaryRateLimitRequests),
            ["host_head_source_rest"] = host.Sum(value => value!.HostHeadSourcePrimary),
            ["host_other_github_rest"] = host.Sum(value => value!.HostOtherGitHubPrimary),
            ["embedded_control"] = embedded.Sum(value => value!.Primary),
            ["external_control"] = external.Sum(values => values.Sum(value => value.Primary)),
            ["cleanup_control"] = cleanup.Sum(values => values.Sum(value => value.Primary)),
        };

    private static bool SatisfiesFrozenRoleAllocation(
        IReadOnlyDictionary<string, int> observed,
        IReadOnlyDictionary<string, int> frozen,
        int reserve)
    {
        var roles = new[]
        {
            "node_artifact_rest", "host_head_source_rest", "host_other_github_rest",
            "embedded_control", "external_control", "cleanup_control",
        };
        return observed.Count == roles.Length && frozen.Count == roles.Length &&
            roles.All(observed.ContainsKey) && roles.All(frozen.ContainsKey) &&
            roles.All(role => observed[role] >= 0 && frozen[role] == observed[role]) &&
            frozen.Values.Sum() + reserve <=
                TrustedProofOperationRequestAccounting.OperationPrimaryBudget;
    }

    private static bool SatisfiesFrozenRemainingTailGuard(
        IReadOnlyDictionary<string, int> observed)
    {
        var domains = new[]
        {
            "node_artifact_rest", "host_head_source_rest", "host_other_github_rest",
            "trusted_control_rest",
        };
        return TrustedProofRequestBudgetProfile.TryGetFrozenTailProfile(
                out var frozen, out var reserve) &&
            observed.Count == domains.Length && domains.All(observed.ContainsKey) &&
            domains.All(domain => observed[domain] >= 0 &&
                frozen[DomainForTailName(domain)] == observed[domain]) &&
            domains.All(domain => frozen[DomainForTailName(domain)] + reserve <
                TrustedProofOperationRequestAccounting.OperationPrimaryBudget);
    }

    private static TrustedProofRequestDomain DomainForTailName(string domain) =>
        domain switch
        {
            "node_artifact_rest" => TrustedProofRequestDomain.NodeArtifactRest,
            "host_head_source_rest" => TrustedProofRequestDomain.HostHeadSourceRest,
            "host_other_github_rest" => TrustedProofRequestDomain.HostOtherGitHubRest,
            "trusted_control_rest" => TrustedProofRequestDomain.TrustedControlRest,
            _ => throw new ArgumentOutOfRangeException(nameof(domain)),
        };

    private static CompiledPayloadSourceExpectation?
        ReadCompiledPayloadSourceExpectation(
            IReadOnlyDictionary<string, string> values) =>
        values.TryGetValue("payload-source-commit", out var commit) &&
        values.TryGetValue("payload-source-tree", out var tree) &&
        IsLowerHex(commit, 40) &&
        IsLowerHex(tree, 40)
            ? new(commit, tree)
            : null;

    private static CompiledPayloadIdentity? ReadCompiledPayloadIdentity(
        string root,
        string scenario)
    {
        var path = Path.Join(
            root,
            scenario,
            "compiled-payload-identity.tsv");
        if (!File.Exists(path))
        {
            return null;
        }

        var lines = File.ReadAllLines(path);
        if (lines.Length != 1 || lines[0].Split('\t') is not
            [var proofKind, var sourceCommit, var sourceTree] ||
            !StringComparer.Ordinal.Equals(
                proofKind,
                "apr-r4-e2p-trusted-proof-payload-v2") ||
            !IsLowerHex(sourceCommit, 40) ||
            !IsLowerHex(sourceTree, 40))
        {
            return null;
        }

        return new CompiledPayloadIdentity(
            proofKind,
            sourceCommit,
            sourceTree);
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
        info.Environment["AGENTIC_PR_REVIEW_R4_REQUEST_BUDGET_PROFILE"] =
            "measurement";
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
            ".github/agentic-pr-review/trusted-proof.json";
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

        JsonObject Identity() => FrameworkJson.Object(
            ("id", 42),
            ("full_name", FrameworkCanaries.Repository));
        JsonObject Actor() => FrameworkJson.Object(
            ("id", 7),
            ("login", "maintainer"));
        JsonObject RepositoryReference() => FrameworkJson.Object(
            ("id", 42),
            ("url", "https://api.github.com/repos/" +
                FrameworkCanaries.Repository),
            ("name", "apr178-repository-canary"));
        return FrameworkJson.Serialize(FrameworkJson.Object(
            ("action", "completed"),
            ("workflow_run", FrameworkJson.Object(
                ("id", 800),
                ("run_attempt", 1),
                ("workflow_id", 71),
                ("name", "CI"),
                ("path", ".github/workflows/ci.yml"),
                ("head_branch", "feature"),
                ("head_sha", FrameworkGitHubHandler.TriggerSha),
                ("event", "pull_request"),
                ("conclusion", "success"),
                ("repository", Identity()),
                ("head_repository", Identity()),
                ("actor", Actor()),
                ("triggering_actor", Actor()),
                ("pull_requests", FrameworkJson.Array(
                [
                    FrameworkJson.Object(
                        ("id", 1000),
                        ("number", 147),
                        ("base", FrameworkJson.Object(
                            ("sha", FrameworkGitHubHandler.PullRequestBaseSha(
                                spec.TrustedProofPayload)),
                            ("repo", RepositoryReference()))),
                        ("head", FrameworkJson.Object(
                            ("sha", FrameworkGitHubHandler.HeadSha),
                            ("repo", RepositoryReference())))),
                ])))),
            ("repository", Identity()),
            ("sender", Actor())));
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
                    (InventoryCovers(inventory, pathValue) ||
                        entry.GetProperty("leaf_id").GetString() == "W5" &&
                        pathValue is "scripts/regenerate-state-v2-fixtures.mjs" or
                            "scripts/regenerate-state-v2-compat-fixtures.mjs")) ||
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

            if (entry.GetProperty("leaf_id").GetString() == "W5" &&
                !ValidateW5Ownership(entry, repository, inventory))
            {
                return false;
            }

            if (entry.GetProperty("leaf_id").GetString() == "W6" &&
                !ValidateW6Ownership(entry, repository))
            {
                return false;
            }

            if (entry.GetProperty("leaf_id").GetString() == "W8" &&
                !ValidateW8Ownership(entry, repository))
            {
                return false;
            }

            if (entry.GetProperty("leaf_id").GetString() == "W10" &&
                !RequiredObjectArray(entry,
                    "inherited_w8_replacement_handoffs", 5,
                    "prior_typescript_assertion", "later_owner",
                    "replacement", "deliberate_difference"))
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

            if (entry.GetProperty("leaf_id").GetString() == "W14" &&
                !ValidateW14Ownership(entry, repository))
            {
                return false;
            }

            if (entry.GetProperty("leaf_id").GetString() == "W15" &&
                !RequiredObjectArray(entry,
                    "retired_w8_consumer_handoffs", 2,
                    "prior_typescript_consumer", "later_owner",
                    "disposition", "deliberate_difference"))
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

    private static bool ValidateW8Ownership(
        JsonElement entry,
        string repository)
    {
        if (entry.GetProperty("disposition").GetString() != "removed" ||
            !RequiredTextArray(entry, "removed_paths", value =>
                IsClosedPath(value) && !LandingPathExists(repository, value)) ||
            !RequiredExactTextArray(entry, "retained_paths",
                new HashSet<string>(
            [
                "runtime/src/AgenticPrReview.Runtime/Host/Publishing/Rendering/",
                "runtime/src/AgenticPrReview.Runtime/Host/Publishing/GitHub/Sticky/",
                "runtime/src/AgenticPrReview.Runtime/Host/Publishing/Recovery/",
                "runtime/src/AgenticPrReview.Runtime/Host/Action/",
            ], StringComparer.Ordinal)) ||
            !RequiredExactTextArray(entry, "csharp_owners",
                new HashSet<string>(
            [
                "R4StickyRenderer",
                "R4StickyMarker",
                "R4PublicationIdentityV1",
                "StickyCommentPublisher",
                "PublicationRecoveryService",
                "ActionHostCoordinator",
                "ActionHostComposition",
            ], StringComparer.Ordinal)) ||
            !RequiredExactTextArray(entry, "owner_members",
                new HashSet<string>(
            [
                "runtime/src/AgenticPrReview.Runtime/Host/Publishing/Rendering/R4StickyRenderer.cs#R4StickyRenderer",
                "runtime/src/AgenticPrReview.Runtime/Host/Publishing/Rendering/R4StickyMarker.cs#R4StickyMarker",
                "runtime/src/AgenticPrReview.Runtime/Host/Publishing/Rendering/R4PublicationIdentityV1.cs#R4PublicationIdentityV1",
                "runtime/src/AgenticPrReview.Runtime/Host/Publishing/GitHub/Sticky/StickyCommentPublisher.cs#StickyCommentPublisher",
                "runtime/src/AgenticPrReview.Runtime/Host/Publishing/Recovery/PublicationRecoveryService.cs#PublicationRecoveryService",
                "runtime/src/AgenticPrReview.Runtime/Host/Action/ActionHostCoordinator.cs#ActionHostCoordinator",
                "runtime/src/AgenticPrReview.Runtime/Host/Action/ActionHostComposition.cs#ActionHostComposition",
            ], StringComparer.Ordinal), member => MemberExists(repository, member)) ||
            !RequiredExactTextArray(entry, "deletion_prerequisites",
                new HashSet<string>(
            [
                "P2 / #158 merged",
                "P6 / #162 merged",
                "W7 / #169 merged",
                "E1 / #178 framework evidence green",
            ], StringComparer.Ordinal)) ||
            !RequiredTextArray(entry, "retained_evidence_paths", value =>
                IsClosedPath(value) && LandingPathExists(repository, value)) ||
            !RequiredTextArray(entry, "historical_provenance_paths", value =>
                IsClosedPath(value) && LandingPathExists(repository, value)) ||
            !RequiredTextArray(entry, "named_replacement_vectors", value =>
            {
                var parts = value.Split(':', 3);
                return parts.Length == 3 &&
                    new[] { "P1", "P2", "P5", "P6" }.Contains(
                        parts[1], StringComparer.Ordinal) &&
                    parts[2].Contains('.');
            }) ||
            !RequiredTextArray(entry, "superseded_assertion_groups") ||
            !RequiredTextArray(entry, "later_leaf_assertion_owners"))
        {
            return false;
        }

        var removed = TextArray(entry, "removed_paths")
            .ToHashSet(StringComparer.Ordinal);
        var retainedEvidence = TextArray(entry, "retained_evidence_paths")
            .ToHashSet(StringComparer.Ordinal);
        var historical = TextArray(entry, "historical_provenance_paths")
            .ToHashSet(StringComparer.Ordinal);
        var vectors = TextArray(entry, "named_replacement_vectors");
        var categories = vectors.Select(value => value.Split(':', 3)[0])
            .ToHashSet(StringComparer.Ordinal);
        var scenarios = TextArray(entry, "framework_scenario_ids")
            .ToHashSet(StringComparer.Ordinal);
        var retainedAssertions = TextArray(entry, "retained_assertion_groups")
            .ToHashSet(StringComparer.Ordinal);
        var laterOwners = TextArray(entry, "later_leaf_assertion_owners")
            .ToHashSet(StringComparer.Ordinal);
        return removed.SetEquals(["src/comments.ts", "src/comments.test.ts"]) &&
            retainedEvidence.SetEquals(
        [
            "runtime/tests/AgenticPrReview.Runtime.Tests/Host/Publishing/Rendering/R4StickyRendererTests.cs",
            "runtime/tests/AgenticPrReview.Runtime.Tests/Host/Publishing/Rendering/R4StickyMarkerTests.cs",
            "runtime/tests/AgenticPrReview.Runtime.Tests/Host/Publishing/Rendering/R4PublicationIdentityTests.cs",
            "runtime/tests/AgenticPrReview.Runtime.Tests/Host/Publishing/GitHub/Sticky/StickyCommentSerializerTests.cs",
            "runtime/tests/AgenticPrReview.Runtime.Tests/Host/Publishing/GitHub/Sticky/StickyPublicationContractsTests.cs",
            "runtime/tests/AgenticPrReview.Runtime.Tests/Host/Publishing/GitHub/Sticky/StickyCommentPublisherTests.cs",
            "runtime/tests/AgenticPrReview.Runtime.Tests/Host/Publishing/Recovery/PublicationRecoveryClassifierTests.cs",
            "runtime/tests/AgenticPrReview.Runtime.Tests/Host/Publishing/Recovery/PublicationRecoveryServiceTests.cs",
            "runtime/tests/AgenticPrReview.Runtime.Tests/Host/Action/ActionHostCompositionTests.cs",
            "runtime/tests/AgenticPrReview.Runtime.Tests/Host/Action/ActionHostFrameworkVerifierArchitectureTests.cs",
        ]) &&
            historical.SetEquals(
        [
            "runtime/tests/fixtures/action-host/framework/e1-base-inventory.json",
        ]) &&
            vectors.Length == vectors.Distinct(StringComparer.Ordinal).Count() &&
            categories.SetEquals(
        [
            "rendering",
            "marker",
            "fingerprint",
            "pathless_rejection",
            "target_discovery",
            "duplicate_handling",
            "create_update",
            "empty_result",
            "escaping",
            "bounds",
            "response_validation_readback",
            "outcome_unknown",
        ]) &&
            scenarios.SetEquals(
        [
            "workflow-run",
            "dispatch-continuation",
            "crash-mutation",
            "crash-recovery",
        ]) &&
            retainedAssertions.SetEquals(
        [
            "complete ordered projection with whole-block public truncation and an explicit omission notice",
            "grounded findings reject empty evidence before identity or rendering",
            "bounded-complete discovery fails closed on page item and completeness overflow",
        ]) &&
            laterOwners.SetEquals(
        [
            "W9: inline candidate and target publication behavior",
            "W10: structured review validation grounding and host-owned metadata",
            "W15: root shared DTO and utility consumer retirement",
        ]);
    }

    private static bool ValidateW5Ownership(
        JsonElement entry,
        string repository,
        IReadOnlySet<string> inventory)
    {
        var removed = new HashSet<string>([
            "src/state-v2/", "protocol/schemas/state-manifest.v2.json",
            "protocol/fixtures/state-manifest-v2/",
            "protocol/fixtures/state-manifest-v2-compat/",
            "scripts/regenerate-state-v2-fixtures.mjs",
            "scripts/regenerate-state-v2-compat-fixtures.mjs",
        ], StringComparer.Ordinal);
        if (entry.GetProperty("disposition").GetString() != "removed" ||
            !RequiredExactTextArray(entry, "removed_paths", removed,
                value => !LandingPathExists(repository, value) &&
                    (InventoryCovers(inventory, value) || value is
                        "scripts/regenerate-state-v2-fixtures.mjs" or
                        "scripts/regenerate-state-v2-compat-fixtures.mjs")) ||
            !RequiredExactTextArray(entry, "owner_members", new HashSet<string>([
                "runtime/src/AgenticPrReview.Runtime/Host/State/RestrictedStateService.cs#RestrictedStateService",
                "runtime/src/AgenticPrReview.Runtime/Host/State/Restore/AuthorizedAcceptedStateComposer.cs#AuthorizedAcceptedStateComposer",
                "runtime/src/AgenticPrReview.Runtime/Host/State/Restore/AcceptedStateSelector.cs#AcceptedStateSelector",
                "runtime/src/AgenticPrReview.Runtime/Host/State/Restore/TrustedHeadAncestryClassifier.cs#TrustedHeadAncestryClassifier",
                "runtime/src/AgenticPrReview.Runtime/Host/State/Restore/AcceptedStateRecordCodecs.cs#AcceptedStateGenerationRecordCodec",
                "runtime/src/AgenticPrReview.Runtime/Host/State/Transactions/RetainedStateTransactionAuthority.cs#RetainedStateTransactionAuthority",
                "runtime/src/AgenticPrReview.Runtime/Host/State/Transactions/RetainedStateTransactionService.cs#RetainedStateTransactionService",
                "runtime/src/AgenticPrReview.Runtime/Host/State/Transactions/RetainedStatePersistence.cs#RetainedStatePersistence",
                "runtime/src/AgenticPrReview.Runtime/Host/Publishing/Recovery/PublicationRecoveryService.cs#PublicationRecoveryService",
                "runtime/src/AgenticPrReview.Runtime/Host/Action/ActionHostCoordinator.cs#ActionHostCoordinator",
            ], StringComparer.Ordinal), member => MemberExists(repository, member)) ||
            !RequiredExactTextArray(entry, "retained_evidence_paths", new HashSet<string>([
                "runtime/tests/AgenticPrReview.Runtime.Tests/Host/State/Restore/AcceptedStateProductionEndToEndTests.cs",
                "runtime/tests/AgenticPrReview.Runtime.Tests/Host/State/Restore/AcceptedStateSelectorTests.cs",
                "runtime/tests/AgenticPrReview.Runtime.Tests/Host/State/Restore/TrustedHeadAncestryClassifierTests.cs",
                "runtime/tests/AgenticPrReview.Runtime.Tests/Host/State/Restore/AcceptedStateRecordCodecTests.cs",
                "runtime/tests/AgenticPrReview.Runtime.Tests/Host/State/Transactions/RetainedStateTransactionEndToEndTests.cs",
                "runtime/tests/AgenticPrReview.Runtime.Tests/Host/State/Transactions/RetainedStateTransactionContractTests.cs",
                "runtime/tests/AgenticPrReview.Runtime.Tests/Host/Publishing/Recovery/PublicationRecoveryServiceTests.cs",
                "runtime/tests/AgenticPrReview.Runtime.Tests/Host/Action/ActionHostCoordinatorTests.cs",
                "runtime/tests/AgenticPrReview.Runtime.Tests/Host/Action/ActionHostFrameworkVerifierArchitectureTests.cs",
            ], StringComparer.Ordinal), value => LandingPathExists(repository, value)) ||
            !RequiredExactTextArray(entry, "framework_scenario_ids", new HashSet<string>([
                "dispatch-bootstrap", "dispatch-continuation", "dispatch-cross-head-conflict",
                "stale-head", "artifact-digest-mismatch", "artifact-expired",
                "artifact-upload-outcome-unknown", "crash-mutation", "crash-recovery",
                "cancel-outcome-unknown",
            ], StringComparer.Ordinal), FrameworkScenarioIds().Contains) ||
            !RequiredExactTextArray(entry, "deletion_prerequisites", new HashSet<string>([
                "S5 / #155 merged", "S6 / #156 merged", "P5 / #161 merged",
                "P6 / #162 merged", "E1 / #178 framework evidence green",
                "W3 / #165 merged", "W6 / #168 merged", "W7 / #169 merged",
                "W12 / #174 merged",
            ], StringComparer.Ordinal)) ||
            !RequiredExactTextArray(entry, "updated_reference_paths", new HashSet<string>([
                ".prettierignore", ".github/workflows/ci.yml",
                "src/residual-reference-allowlist.ts",
                "src/canonical-json/import-boundary.test.ts",
                "docs/00_project/project-context.md",
                "docs/20_architecture/agent-runtime-rebaseline.md",
                "docs/20_architecture/agent-session-format.md",
                "docs/20_architecture/r1-legacy-removal-handoff.md",
                "docs/20_architecture/r4-actionhost-wrapper-plan.md",
                "docs/20_architecture/runtime-protocol.md",
                "docs/20_architecture/security-boundary.md",
                "docs/20_architecture/session-ledger-and-prefix-contract.md",
                "docs/20_architecture/state-manifest-v2.md",
                "docs/90_roadmap/roadmap-seed.md",
                "runtime/tests/AgenticPrReview.Runtime.Tests/Agent/Session/AgentSessionArchitectureTests.cs",
                "runtime/tests/AgenticPrReview.Runtime.Tests/Host/State/RestrictedStateArchitectureTests.cs",
                "runtime/tests/AgenticPrReview.Runtime.Tests/Ledger/LedgerSchemaConformanceTests.cs",
                "runtime/tests/ActionHostVerifierFixture/FrameworkSupervisor.cs",
                "runtime/tests/AgenticPrReview.Runtime.Tests/Host/Action/ActionHostFrameworkVerifierArchitectureTests.cs",
                "runtime/tests/fixtures/action-host/framework/replacement-record.json",
            ], StringComparer.Ordinal), value =>
                value == "src/canonical-json/import-boundary.test.ts"
                    ? !LandingPathExists(repository, value)
                    : LandingPathExists(repository, value)) ||
            !ValidateW5ReferenceDispositions(entry) ||
            !ValidateW5LegacyGroups(entry, repository, inventory) ||
            !ValidateW5Fixtures(entry, repository) ||
            !ValidateW5ResidualScan(entry, repository)) return false;

        return !Directory.Exists(Path.Join(repository, "src", "state-v2")) &&
            !File.Exists(Path.Join(repository, "protocol", "schemas",
                "state-manifest.v2.json")) &&
            !Directory.Exists(Path.Join(repository, "protocol", "fixtures",
                "state-manifest-v2")) &&
            !Directory.Exists(Path.Join(repository, "protocol", "fixtures",
                "state-manifest-v2-compat")) &&
            !File.Exists(Path.Join(repository, "scripts",
                "regenerate-state-v2-fixtures.mjs")) &&
            !File.Exists(Path.Join(repository, "scripts",
                "regenerate-state-v2-compat-fixtures.mjs")) &&
            !File.ReadAllText(Path.Join(repository, ".prettierignore"))
                .Contains("state-manifest-v2", StringComparison.Ordinal) &&
            !File.ReadAllText(Path.Join(repository, ".github", "workflows", "ci.yml"))
                .Contains("src/state-v2", StringComparison.Ordinal) &&
            !File.ReadAllText(Path.Join(repository, "src",
                "residual-reference-allowlist.ts"))
                .Contains("RR-012", StringComparison.Ordinal);
    }

    private static bool ValidateW5ReferenceDispositions(JsonElement entry)
    {
        var dispositions = entry.GetProperty("updated_reference_dispositions")
            .EnumerateArray().ToArray();
        var expected = new HashSet<string>([
            "runtime/tests/AgenticPrReview.Runtime.Tests/Agent/Session/AgentSessionArchitectureTests.cs",
            "runtime/tests/AgenticPrReview.Runtime.Tests/Host/State/RestrictedStateArchitectureTests.cs",
        ], StringComparer.Ordinal);
        return dispositions.Length == expected.Count && dispositions.All(disposition =>
            expected.Contains(disposition.GetProperty("path").GetString() ?? "") &&
            disposition.GetProperty("disposition").GetString() ==
                "superseded_by_w5_residual_scan" &&
            !string.IsNullOrWhiteSpace(disposition.GetProperty("reason").GetString()));
    }

    private static bool ValidateW5LegacyGroups(
        JsonElement entry,
        string repository,
        IReadOnlySet<string> inventory)
    {
        var groups = entry.GetProperty("legacy_test_groups").EnumerateArray().ToArray();
        var expected = new HashSet<string>([
            "aggregation.test.ts::bounded diagnostic aggregation",
            "builder-input-domain.test.ts::candidate input rejection",
            "builder-string-safety.test.ts::bounded safe strings and paths",
            "classifier-precedence.test.ts::selected-current failure precedence",
            "classifier-wire-format.test.ts::tampered bundle rejection",
            "compat-fixtures.test.ts::compatibility outcome corpus",
            "compatibility.test.ts::ancestry and state-key compatibility",
            "constants-mirror.test.ts::StateV2 schema parity",
            "core.test.ts::StateV2 parser serializer and classifier representation",
            "cross-field.test.ts::provenance generation and transition binding",
            "deep-path-oracle.test.ts::M4 sidecar traversal oracle",
            "diagnostic-bounds.test.ts::bounded failure diagnostics",
            "diagnostic-privacy.test.ts::private diagnostic suppression",
            "empty-name-unknown-field.test.ts::closed names and unknown fields",
            "fixtures.test.ts::byte-identical StateV2 fixture bundles",
            "import-boundary.test.ts::StateV2 dependency and directory contract",
            "import-boundary.test.ts::canonical-json recursive AST filesystem boundary",
            "public-surface.test.ts::StateV2 barrel exports",
            "resolver-runtime-consequence.test.ts::M4 resolver runtime consequences",
            "rfc3339.test.ts::accepted-state timestamp grammar",
            "schema-conformance.test.ts::closed schema and reference validation",
            "shared-vectors.test.ts::shared M4 vector projection",
            "shared-vocabulary.test.ts::StateV2 vocabulary parity",
            "short-circuit-and-exhaustive.test.ts::failure precedence and exhaustive branches",
            "strict-json.test.ts::strict JSON byte and duplicate-property rejection",
        ], StringComparer.Ordinal);
        if (groups.Length != expected.Count ||
            !groups.Select(group => group.GetProperty("id").GetString() ?? "")
                .ToHashSet(StringComparer.Ordinal).SetEquals(expected) ||
            entry.GetProperty("mapping_digest").GetString() !=
                "1c143510afc05a57af6b7bca8ccaa982329bd3409e77e39b7b72be99346a69c2" ||
            !StringComparer.Ordinal.Equals(
                Sha256Text(W5MappingText(entry)),
                "1c143510afc05a57af6b7bca8ccaa982329bd3409e77e39b7b72be99346a69c2")) return false;
        foreach (var group in groups)
        {
            var source = group.GetProperty("source_path").GetString() ?? "";
            var disposition = group.GetProperty("disposition").GetString();
            if (!source.StartsWith("src/state-v2/", StringComparison.Ordinal) ||
                !InventoryCovers(inventory, source)) return false;
            if (disposition == "retained" &&
                group.TryGetProperty("owner", out var owner) &&
                owner.GetString() is "S5" or "S6" or "P5" or "P6" or "E1" &&
                group.TryGetProperty("owner_member", out var member) &&
                MemberExists(repository, member.GetString() ?? "") &&
                group.TryGetProperty("evidence_path", out var evidence) &&
                LandingPathExists(repository, evidence.GetString() ?? "")) continue;
            if (disposition == "retired_by_w14" &&
                group.GetProperty("owner").GetString() == "W14" &&
                group.GetProperty("target_path").GetString() ==
                    "src/canonical-json/import-boundary.test.ts" &&
                group.GetProperty("evidence_path").GetString() ==
                    "runtime/tests/fixtures/action-host/framework/replacement-record.json" &&
                !LandingPathExists(repository, "src/canonical-json/import-boundary.test.ts") &&
                group.TryGetProperty("reason", out var retirementReason) &&
                !string.IsNullOrWhiteSpace(retirementReason.GetString())) continue;
            if (disposition == "reviewed_obsolete" &&
                group.TryGetProperty("reason", out var reason) &&
                !string.IsNullOrWhiteSpace(reason.GetString())) continue;
            return false;
        }
        return true;
    }

    private static bool ValidateW5Fixtures(JsonElement entry, string repository)
    {
        var fixtures = entry.GetProperty("fixture_dispositions").EnumerateArray().ToArray();
        var expected = new HashSet<string>([
            "positive-bootstrap", "positive-continuation", "positive-recovery-root",
            "positive-reset", "compat-base-change", "compat-cache-contract-change",
            "compat-continuation", "compat-contract-version-mismatch",
            "compat-nondescendant-head", "compat-state-key-mismatch",
            "compat-unknown-ancestry", "compat-unsafe-provenance",
        ], StringComparer.Ordinal);
        return fixtures.Length == expected.Count && fixtures.Select(fixture =>
                fixture.GetProperty("id").GetString() ?? "").ToHashSet(StringComparer.Ordinal)
                .SetEquals(expected) && fixtures.All(fixture =>
                ((fixture.TryGetProperty("semantic_owner", out var owner) &&
                    owner.GetString() is "S5" or "S6" or "P5" or "P6" or "E1" &&
                    fixture.TryGetProperty("semantic_evidence_path", out var evidence) &&
                    LandingPathExists(repository, evidence.GetString() ?? "")) ||
                 (fixture.TryGetProperty("semantic_disposition", out var semanticDisposition) &&
                    semanticDisposition.GetString() == "reviewed_obsolete" &&
                    fixture.TryGetProperty("semantic_reason", out var semanticReason) &&
                    !string.IsNullOrWhiteSpace(semanticReason.GetString()))) &&
                fixture.GetProperty("representation_disposition").GetString() == "reviewed_obsolete" &&
                !string.IsNullOrWhiteSpace(
                    fixture.GetProperty("representation_reason").GetString()));
    }

    private static string W5MappingText(JsonElement entry)
    {
        var rows = new List<string>();
        foreach (var group in entry.GetProperty("legacy_test_groups").EnumerateArray()
                     .OrderBy(value => value.GetProperty("id").GetString(), StringComparer.Ordinal))
        {
            rows.Add(string.Join("\u001f", [
                "group", OptionalText(group, "id"), OptionalText(group, "disposition"),
                OptionalText(group, "owner"), OptionalText(group, "owner_member"),
                OptionalText(group, "evidence_path"), OptionalText(group, "target_path"),
                OptionalText(group, "reason"),
            ]));
        }
        foreach (var fixture in entry.GetProperty("fixture_dispositions").EnumerateArray()
                     .OrderBy(value => value.GetProperty("id").GetString(), StringComparer.Ordinal))
        {
            rows.Add(string.Join("\u001f", [
                "fixture", OptionalText(fixture, "id"), OptionalText(fixture, "semantic_owner"),
                OptionalText(fixture, "semantic_evidence_path"),
                OptionalText(fixture, "semantic_disposition"), OptionalText(fixture, "semantic_reason"),
                OptionalText(fixture, "representation_disposition"),
                OptionalText(fixture, "representation_reason"),
            ]));
        }
        return string.Join("\n", rows);
    }

    private static string OptionalText(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) ? value.GetString() ?? "" : "";

    private static bool ValidateW5ResidualScan(JsonElement entry, string repository)
    {
        var scan = entry.GetProperty("w5_residual_scan");
        var forbidden = new HashSet<string>([
            "src/state-v2", "state-manifest.v2.json", "state-manifest-v2/",
            "state-manifest-v2-compat/", "regenerate-state-v2-fixtures.mjs",
            "regenerate-state-v2-compat-fixtures.mjs", "StateManifestV2", "StateKeyV2",
            "buildStateBundleV2", "classifyStateBundleV2", "validateStateManifestV2",
            "serializeStateManifestV2", "checkStateManifestV2Compatibility",
            "ExpectedStateManifestV2Context", "state_unsupported_legacy_v1",
            "unsupported_legacy_v1",
        ], StringComparer.Ordinal);
        if (!TextArray(scan, "forbidden_tokens").ToHashSet(StringComparer.Ordinal)
                .SetEquals(forbidden)) return false;
        var evidence = TextArray(scan, "deletion_evidence_paths")
            .ToHashSet(StringComparer.Ordinal);
        var immutable = TextArray(scan, "immutable_provenance_paths")
            .ToHashSet(StringComparer.Ordinal);
        var historical = TextArray(scan, "historical_document_paths")
            .ToHashSet(StringComparer.Ordinal);
        var currentPosition = TextArray(scan, "current_position_document_paths")
            .ToHashSet(StringComparer.Ordinal);
        var derived = TextArray(scan, "derived_bundle_paths")
            .ToHashSet(StringComparer.Ordinal);
        if (!evidence.SetEquals([
                "src/r4-migration-cutover-audit.test.ts",
                "runtime/tests/ActionHostVerifierFixture/FrameworkSupervisor.cs",
                "runtime/tests/AgenticPrReview.Runtime.Tests/Host/Action/ActionHostFrameworkVerifierArchitectureTests.cs",
                "runtime/tests/fixtures/action-host/framework/replacement-record.json",
            ]) ||
            !immutable.SetEquals([
                "runtime/tests/fixtures/action-host/framework/e1-base-inventory.json",
            ]) ||
            !historical.SetEquals([
                "docs/20_architecture/session-ledger-and-prefix-contract.md",
                "docs/20_architecture/state-manifest-v2.md",
            ]) ||
            !currentPosition.SetEquals([
                "docs/00_project/project-context.md",
                "docs/20_architecture/agent-runtime-rebaseline.md",
                "docs/20_architecture/agent-session-format.md",
                "docs/20_architecture/r1-legacy-removal-handoff.md",
                "docs/20_architecture/r4-actionhost-wrapper-plan.md",
                "docs/20_architecture/runtime-protocol.md",
                "docs/20_architecture/security-boundary.md",
                "docs/90_roadmap/roadmap-seed.md",
            ]) ||
            !derived.SetEquals([".github/actions/agentic-pr-review/dist/index.js"])) return false;
        const string retirementMarker =
            "R4-W5 retired StateV2; no current reader or compatibility surface.";
        foreach (var path in TrackedPaths(repository).Where(path =>
                     !derived.Contains(path)))
        {
            var fullPath = Path.Join(repository,
                path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath)) continue;
            var text = File.ReadAllText(fullPath);
            if (evidence.Contains(path) || immutable.Contains(path)) continue;
            if (historical.Contains(path))
            {
                if (!text.Contains(retirementMarker, StringComparison.Ordinal) ||
                    text.Contains("remains the acceptance contract", StringComparison.Ordinal) ||
                    text.Contains("under its current owner", StringComparison.Ordinal)) return false;
                continue;
            }
            if (currentPosition.Contains(path))
            {
                if (!text.Contains(retirementMarker, StringComparison.Ordinal) ||
                    text.Contains("remains the acceptance contract", StringComparison.Ordinal) ||
                    text.Contains("under its current owner", StringComparison.Ordinal) ||
                    text.Split('\n').Any(line => forbidden.Any(token =>
                        line.Contains(token, StringComparison.Ordinal)) &&
                        !line.Contains(retirementMarker, StringComparison.Ordinal))) return false;
                continue;
            }
            if (forbidden.Any(token => text.Contains(token, StringComparison.Ordinal))) return false;
        }
        return true;
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

    private static bool ValidateW14Ownership(JsonElement entry, string repository)
    {
        var removedPaths = new HashSet<string>([
            "src/canonical-json/index.ts",
            "src/canonical-json/index.test.ts",
            "src/canonical-json/edge-cases.test.ts",
            "src/canonical-json/import-boundary.test.ts",
        ], StringComparer.Ordinal);
        var ownerMembers = new HashSet<string>([
            "runtime/src/AgenticPrReview.Runtime/Canonical/Rfc8785Writer.cs#Rfc8785Writer",
            "runtime/src/AgenticPrReview.Runtime/Canonical/JsonElementCanonicalizer.cs#JsonElementCanonicalizer",
            "runtime/src/AgenticPrReview.Runtime/Canonical/EcmaScriptNumberFormatter.cs#EcmaScriptNumberFormatter",
            "runtime/src/AgenticPrReview.Runtime/Canonical/LenientJsonObjectEnumerator.cs#LenientJsonObjectEnumerator",
            "runtime/src/AgenticPrReview.Runtime/Canonical/Rfc8785CanonicalizationException.cs#Rfc8785CanonicalizationException",
            "runtime/src/AgenticPrReview.Runtime/Canonical/CanonicalJsonWriter.cs#CanonicalJsonWriter",
        ], StringComparer.Ordinal);
        var evidencePaths = new HashSet<string>([
            "runtime/tests/AgenticPrReview.Runtime.Tests/Canonical/CanonicalWriterTests.cs",
            "runtime/tests/AgenticPrReview.Runtime.Tests/Canonical/EcmaScriptNumberFormatterCorpusTests.cs",
            "runtime/tests/AgenticPrReview.Runtime.Tests/Canonical/LenientJsonObjectEnumeratorTests.cs",
            "runtime/tests/AgenticPrReview.Runtime.Tests/Prefix/PrefixCanonicalBoundaryTests.cs",
            "runtime/tests/AgenticPrReview.Runtime.Tests/Prefix/PrefixEnvelopeValidatorTests.cs",
            "runtime/tests/AgenticPrReview.Runtime.Tests/Prefix/PrefixGoldenVectorTests.cs",
            "runtime/tests/AgenticPrReview.Runtime.Tests/Prefix/PrefixFixtureManifestRejectionTests.cs",
            "runtime/tests/AgenticPrReview.Runtime.Tests/Prefix/PrefixMaterializerTests.cs",
            "runtime/tests/AgenticPrReview.Runtime.Tests/Ledger/LedgerBuilderTests.cs",
            "runtime/tests/AgenticPrReview.Runtime.Tests/Host/Action/ActionHostFrameworkVerifierArchitectureTests.cs",
        ], StringComparer.Ordinal);
        var historicalPaths = new HashSet<string>([
            "protocol/fixtures/prefix-contract/v1/manifest.json",
            "runtime/tests/fixtures/action-host/framework/e1-base-inventory.json",
        ], StringComparer.Ordinal);
        if (entry.GetProperty("disposition").GetString() != "removed" ||
            !RequiredExactTextArray(entry, "removed_paths", removedPaths,
                value => !LandingPathExists(repository, value)) ||
            Directory.Exists(Path.Join(repository, "src", "canonical-json")) ||
            !RequiredExactTextArray(entry, "owner_members", ownerMembers,
                member => MemberExists(repository, member)) ||
            !RequiredExactTextArray(entry, "retained_evidence_paths", evidencePaths,
                value => LandingPathExists(repository, value)) ||
            !RequiredExactTextArray(entry, "historical_provenance_paths", historicalPaths,
                value => LandingPathExists(repository, value)) ||
            !RequiredExactTextArray(entry, "framework_scenario_ids",
                new HashSet<string>(["dispatch-continuation"], StringComparer.Ordinal),
                FrameworkScenarioIds().Contains) ||
            !RequiredExactTextArray(entry, "deletion_prerequisites",
                new HashSet<string>([
                    "W3 W4 W5 W6 W11 and W12 merged",
                    "repository-wide no-import proof",
                    "E1 green",
                ], StringComparer.Ordinal)) ||
            !ValidateW14TestDispositions(entry, repository) ||
            !ValidateW14ResidualScan(entry, repository)) return false;

        var cap = entry.GetProperty("cap_precedence_difference");
        return RequiredText(cap, "retired_behavior") &&
            RequiredText(cap, "retained_behavior") &&
            RequiredExactTextArray(cap, "evidence_methods",
                new HashSet<string>([
                    "CanonicalWriterTests#DiscardModeRemainsLatchedAfterTheFirstExceededAppend",
                    "PrefixCanonicalBoundaryTests#EarlyOversizeStringDoesNotMaskLaterLoneSurrogate",
                    "PrefixStagePrecedenceTests#CanonicalDefectBeatsEnvelopeCap",
                ], StringComparer.Ordinal), method =>
                    EvidenceMethodExists(repository, method)) &&
            RequiredExactTextArray(entry, "retained_integration_evidence_methods",
                new HashSet<string>([
                    "PrefixGoldenVectorTests#DigestVectorsMatch",
                    "PrefixGoldenVectorTests#InteractionVectorsMatch",
                    "PrefixFixtureManifestRejectionTests#SyntheticManifestViolationsReachTheirIntendedBranch",
                    "LedgerBuilderTests#CacheContractDigestMatchesIndependentSevenFieldKnownAnswer",
                    "ActionHostFrameworkVerifierArchitectureTests#ReplacementAndInventoryArtifactsAreClosedAndPinned",
                ], StringComparer.Ordinal), method =>
                    EvidenceMethodExists(repository, method));
    }

    private static bool ValidateW14TestDispositions(
        JsonElement entry,
        string repository)
    {
        var expected = new HashSet<string>([
            "index.test.ts::sorts object keys by UTF-16 code units",
            "index.test.ts::emits ECMAScript ToString for numbers",
            "index.test.ts::serializes negative zero as 0 per RFC 8785",
            "index.test.ts::rejects NaN Infinity and -Infinity",
            "index.test.ts::rejects non-JSON JavaScript values and built-ins",
            "index.test.ts::rejects cyclic structures",
            "index.test.ts::rejects sparse arrays",
            "index.test.ts::rejects symbol-keyed own properties",
            "index.test.ts::rejects accessor properties",
            "index.test.ts::rejects non-enumerable own properties",
            "index.test.ts::rejects custom object prototypes",
            "index.test.ts::rejects arrays with extra string properties",
            "index.test.ts::rejects arrays with accessor indices",
            "index.test.ts::rejects arrays with custom prototypes",
            "index.test.ts::rejects lone surrogates in values and names",
            "index.test.ts::produces byte-stable output on repeated calls",
            "index.test.ts::does not import node fs",
            "edge-cases.test.ts::exports canonical JSON version 1",
            "edge-cases.test.ts::positive and negative zero are byte-equal",
            "edge-cases.test.ts::accepts a null-prototype object",
            "edge-cases.test.ts::accepts a repeated non-cyclic reference",
            "edge-cases.test.ts::sorts non-ASCII names by UTF-16 code units",
            "edge-cases.test.ts::encodes ordinary Unicode without unnecessary escaping",
            "edge-cases.test.ts::encodes controls with unicode escapes",
            "edge-cases.test.ts::encodes standard short escapes",
            "edge-cases.test.ts::preserves supplementary-plane characters",
            "edge-cases.test.ts::nested rejection carries a useful path",
            "edge-cases.test.ts::canonicalize parse canonicalize is byte-stable",
            "edge-cases.test.ts::output parses to an equal JSON value",
            "edge-cases.test.ts::near-cap performance smoke",
            "import-boundary.test.ts::canonical-json recursive AST filesystem boundary",
        ], StringComparer.Ordinal);
        var allowedMethods = new HashSet<string>([
            "CanonicalWriterTests#ObjectKeysSortByUtf16CodeUnits",
            "CanonicalWriterTests#NumberFormattingMatchesEcmaScript",
            "EcmaScriptNumberFormatterCorpusTests#MatchesNodeCorpus",
            "CanonicalWriterTests#NonFiniteNumbersAreRejected",
            "CanonicalWriterTests#LoneSurrogateIsRejected",
            "PrefixCanonicalBoundaryTests#InvalidPropertyNameAtOpenJsonRoot",
            "PrefixMaterializerTests#SameInputProducesByteIdenticalOutput",
            "PrefixGoldenVectorTests#FramingVectorsMatch",
            "CanonicalWriterTests#CanonicalizationRoundTripsSemanticallyAndIsIdempotent",
            "CanonicalWriterTests#StringEscapingMatchesRfc8785",
            "PrefixCanonicalBoundaryTests#InvalidPropertyNameUnderUnknownAncestor",
            "LenientJsonObjectEnumeratorTests#LongCommonPrefixSortingHasLinearDecodedWork",
            "PrefixCanonicalBoundaryTests#OversizeStringValidationAllocationIsBoundedBelowTokenSize",
        ], StringComparer.Ordinal);
        var dispositions = entry.GetProperty("typescript_test_dispositions")
            .EnumerateArray().ToArray();
        if (dispositions.Length != expected.Count ||
            !dispositions.Select(value => value.GetProperty("id").GetString() ?? "")
                .ToHashSet(StringComparer.Ordinal).SetEquals(expected) ||
            dispositions.Count(value => value.GetProperty("disposition").GetString() ==
                "retained") != 16 ||
            dispositions.Count(value => value.GetProperty("disposition").GetString() ==
                "reviewed_obsolete") != 15) return false;

        foreach (var disposition in dispositions)
        {
            var id = disposition.GetProperty("id").GetString() ?? "";
            var source = disposition.GetProperty("source_path").GetString() ?? "";
            if (!id.StartsWith(Path.GetFileName(source) + "::", StringComparison.Ordinal) ||
                source is not (
                    "src/canonical-json/index.test.ts" or
                    "src/canonical-json/edge-cases.test.ts" or
                    "src/canonical-json/import-boundary.test.ts")) return false;
            if (disposition.GetProperty("disposition").GetString() == "retained")
            {
                if (!RequiredTextArray(disposition, "evidence_methods", method =>
                        allowedMethods.Contains(method) &&
                        EvidenceMethodExists(repository, method))) return false;
                continue;
            }
            if (disposition.GetProperty("disposition").GetString() ==
                    "reviewed_obsolete" &&
                RequiredText(disposition, "reason")) continue;
            return false;
        }

        var exactRoundTripMethod = new HashSet<string>([
            "CanonicalWriterTests#CanonicalizationRoundTripsSemanticallyAndIsIdempotent",
        ], StringComparer.Ordinal);
        foreach (var id in new[]
                 {
                     "edge-cases.test.ts::canonicalize parse canonicalize is byte-stable",
                     "edge-cases.test.ts::output parses to an equal JSON value",
                 })
        {
            var disposition = dispositions.Single(value =>
                value.GetProperty("id").GetString() == id);
            if (!RequiredExactTextArray(disposition, "evidence_methods",
                    exactRoundTripMethod, method =>
                        EvidenceMethodExists(repository, method))) return false;
        }
        return true;
    }

    private static bool EvidenceMethodExists(string repository, string evidence)
    {
        var parts = evidence.Split('#', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return false;
        var testRoot = Path.Join(repository, "runtime", "tests",
            "AgenticPrReview.Runtime.Tests");
        return Directory.EnumerateFiles(testRoot, "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .Any(source =>
                source.Contains($"class {parts[0]}", StringComparison.Ordinal) &&
                source.Contains($" {parts[1]}(", StringComparison.Ordinal));
    }

    private static bool ValidateW14ResidualScan(JsonElement entry, string repository)
    {
        var scan = entry.GetProperty("w14_residual_scan");
        var forbidden = TextArray(scan, "forbidden_tokens")
            .ToHashSet(StringComparer.Ordinal);
        var evidence = TextArray(scan, "deletion_evidence_paths")
            .ToHashSet(StringComparer.Ordinal);
        var immutable = TextArray(scan, "immutable_provenance_paths")
            .ToHashSet(StringComparer.Ordinal);
        var historical = TextArray(scan, "historical_document_paths")
            .ToHashSet(StringComparer.Ordinal);
        var currentPosition = TextArray(scan, "current_position_document_paths")
            .ToHashSet(StringComparer.Ordinal);
        var derived = TextArray(scan, "derived_bundle_paths")
            .ToHashSet(StringComparer.Ordinal);
        if (!forbidden.SetEquals([
                "src/canonical-json", "canonicalJsonBytes",
                "CANONICAL_JSON_VERSION", "CanonicalJsonValue",
                "CanonicalJsonInputError", "CanonicalJsonByteCapError",
            ]) ||
            !evidence.SetEquals([
                "src/r4-migration-cutover-audit.test.ts",
                "runtime/tests/ActionHostVerifierFixture/FrameworkSupervisor.cs",
                "runtime/tests/AgenticPrReview.Runtime.Tests/Host/Action/ActionHostFrameworkVerifierArchitectureTests.cs",
                "runtime/tests/fixtures/action-host/framework/replacement-record.json",
            ]) ||
            !immutable.SetEquals([
                "protocol/fixtures/prefix-contract/",
                "runtime/tests/fixtures/action-host/framework/e1-base-inventory.json",
            ]) ||
            !historical.SetEquals([
                "docs/20_architecture/session-ledger-and-prefix-contract.md",
                "docs/20_architecture/state-manifest-v2.md",
            ]) ||
            !currentPosition.SetEquals([
                "docs/00_project/project-context.md",
                "docs/20_architecture/agent-runtime-rebaseline.md",
                "docs/20_architecture/r1-legacy-removal-handoff.md",
                "docs/20_architecture/r4-actionhost-wrapper-plan.md",
                "docs/50_ai/agent-context.md",
                "docs/90_roadmap/roadmap-seed.md",
            ]) ||
            !derived.SetEquals([".github/actions/agentic-pr-review/dist/index.js"]))
        {
            return false;
        }

        const string retirementMarker =
            "R4-W14 retired the TypeScript canonical-json family; C# Canonical remains current and the prefix corpus remains immutable evidence.";
        foreach (var path in TrackedPaths(repository).Where(path =>
                     !derived.Contains(path)))
        {
            var fullPath = Path.Join(repository,
                path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath)) continue;
            var text = File.ReadAllText(fullPath);
            if (evidence.Contains(path) ||
                path == "runtime/tests/fixtures/action-host/framework/e1-base-inventory.json" ||
                path.StartsWith("protocol/fixtures/prefix-contract/",
                    StringComparison.Ordinal)) continue;
            if (historical.Contains(path) || currentPosition.Contains(path))
            {
                if (!text.Contains(retirementMarker, StringComparison.Ordinal) ||
                    currentPosition.Contains(path) && text.Split('\n').Any(line =>
                        forbidden.Any(token =>
                            line.Contains(token, StringComparison.Ordinal)) &&
                        !line.Contains(retirementMarker, StringComparison.Ordinal)))
                {
                    return false;
                }
                continue;
            }
            if (forbidden.Any(token => text.Contains(token,
                    StringComparison.Ordinal))) return false;
        }
        return true;
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

    private static bool ValidateW6Ownership(JsonElement entry, string repository) =>
        entry.GetProperty("disposition").GetString() == "removed" &&
        RequiredTextArray(entry, "removed_paths", value =>
            IsClosedPath(value) && !LandingPathExists(repository, value)) &&
        RequiredExactTextArray(entry, "owner_members", ExpectedW6OwnerMembers(),
            member => MemberExists(repository, member)) &&
        RequiredExactTextArray(entry, "retained_evidence_paths",
            ExpectedW6EvidencePaths(), value =>
                IsClosedPath(value) && LandingPathExists(repository, value)) &&
        RequiredExactTextArray(entry, "framework_scenario_ids",
            new HashSet<string>([
                "dispatch-continuation", "dispatch-cross-head-conflict",
                "stale-head", "artifact-upload-outcome-unknown",
                "artifact-delete-outcome-unknown", "crash-mutation",
                "crash-recovery", "cancel-known-commit",
                "cancel-outcome-unknown", "delete-exact"
            ], StringComparer.Ordinal)) &&
        RequiredExactTextArray(entry, "deletion_prerequisites",
            new HashSet<string>([
                "S6 / #156 merged", "P6 / #162 merged",
                "W7 / #169 merged", "E1 / #178 framework evidence green"
            ], StringComparer.Ordinal)) &&
        RequiredTextArray(entry, "retained_owner_groups") &&
        RequiredTextArray(entry, "obsolete_groups") &&
        ValidateW6LegacyManifest(entry, repository) &&
        ValidateW6ReceiptDisposition(entry, repository) &&
        ValidateW6ResidualScan(entry, repository) &&
        !Directory.Exists(Path.Join(repository, "src", "state-acceptance")) &&
        !File.Exists(Path.Join(repository, "protocol", "schemas",
            "candidate-registration.v1.json")) &&
        !File.Exists(Path.Join(repository, "protocol", "schemas",
            "accepted-state-marker.v1.json")) &&
        !File.Exists(Path.Join(repository, "protocol", "schemas",
            "state-selector.v1.json")) &&
        !File.Exists(Path.Join(repository, "protocol", "schemas",
            "state-publication-receipt.v1.json"));

    private static bool ValidateW6LegacyManifest(JsonElement entry,
        string repository)
    {
        return ValidateW6CaseSet(entry.GetProperty("legacy_test_cases")
                .EnumerateArray().ToArray(), ExpectedW6CaseIds(), repository) &&
            ValidateW6CaseSet(entry.GetProperty("legacy_helper_cases")
                .EnumerateArray().ToArray(), ExpectedW6HelperIds(), repository);
    }

    private static bool ValidateW6CaseSet(
        JsonElement[] values,
        IReadOnlySet<string> expected,
        string repository)
    {
        var actual = values.Select(value => value.GetProperty("id").GetString() ?? "")
            .ToArray();
        if (actual.Length != expected.Count || !actual.ToHashSet(StringComparer.Ordinal)
                .SetEquals(expected)) return false;
        foreach (var value in values)
        {
            var retained = value.GetProperty("disposition").GetString() == "retained";
            if (value.GetProperty("disposition").GetString() is not
                    ("retained" or "reviewed_obsolete") ||
                retained != value.TryGetProperty("evidence_path", out var path) ||
                retained && (!IsClosedPath(path.GetString() ?? "") ||
                    !LandingPathExists(repository, path.GetString() ?? "")) ||
                retained != value.TryGetProperty("owner", out var owner) ||
                retained && owner.GetString() is not ("S1" or "S2" or "S3" or "S5" or "S6" or "P6") ||
                !retained && (!value.TryGetProperty("reason", out var reason) ||
                    string.IsNullOrWhiteSpace(reason.GetString()))) return false;
        }
        return true;
    }

    private static IReadOnlySet<string> ExpectedW6CaseIds() => new HashSet<string>([
        "github-git-data.test.ts::non-forced-ref-update", "github-git-data.test.ts::truncated-tree", "github-git-data.test.ts::duplicate-tree-paths", "github-state-paths.test.ts::frozen-m4-paths",
        "contract.test.ts::canonical-records", "contract.test.ts::schema-stage-order", "contract.test.ts::codec-diagnostics", "contract.test.ts::unicode-canonical-diagnostics", "contract.test.ts::state-key-parity", "contract.test.ts::unsafe-unicode", "contract.test.ts::candidate-winner-conflict", "contract.test.ts::aggregate-snapshot-bytes", "contract.test.ts::enumeration-receipt-digest", "contract.test.ts::marker-selector-order", "contract.test.ts::predecessor-byte-hash", "contract.test.ts::cancel-before-mutation", "contract.test.ts::selection-identity-state-key", "contract.test.ts::bootstrap-and-corruption", "contract.test.ts::committed-selector-retry", "contract.test.ts::contract-version-legacy-v1", "contract.test.ts::contract-version-unknown-version", "contract.test.ts::unsafe-candidate-files", "contract.test.ts::unsafe-expected-files", "contract.test.ts::reopen-candidate-bytes", "contract.test.ts::registration-crash-residue", "contract.test.ts::child-process-registration", "contract.test.ts::registration-count-boundary", "contract.test.ts::immutable-target-id", "contract.test.ts::registration-cap", "contract.test.ts::registration-sequence", "contract.test.ts::kernel-lock", "contract.test.ts::lease-selector-cas",
        "github-state-store.test.ts::bootstrap-without-selector", "github-state-store.test.ts::explicit-restore-without-selector", "github-state-store.test.ts::default-branch-identity", "github-state-store.test.ts::counter-transaction", "github-state-store.test.ts::contiguous-registration", "github-state-store.test.ts::global-counter-cutoff", "github-state-store.test.ts::counter-enumeration", "github-state-store.test.ts::moved-global-ref", "github-state-store.test.ts::noncanonical-sentinel", "github-state-store.test.ts::shared-git-data-processes", "github-state-store.test.ts::selector-successor", "github-state-store.test.ts::bootstrap-predecessor", "github-state-store.test.ts::workflow-provenance",
        "schema.test.ts::strict-closed-schemas", "schema.test.ts::nested-key-transition"
    ], StringComparer.Ordinal);

    private static IReadOnlySet<string> ExpectedW6HelperIds() => new HashSet<string>([
        "lock-child.mjs::unix-socket-lock", "store-child.mjs::reference-store-child"
    ], StringComparer.Ordinal);

    private static IReadOnlySet<string> ExpectedW6OwnerMembers() => new HashSet<string>([
        "runtime/src/AgenticPrReview.Runtime/Host/State/OpaqueStore/RestrictedStateOpaqueStoreContracts.cs#OpaqueStoreValidation",
        "runtime/src/AgenticPrReview.Runtime/Host/State/OpaqueStore/LocalRestrictedStateStore.cs#LocalRestrictedStateStore",
        "runtime/src/AgenticPrReview.Runtime/Host/State/GitHubArtifacts/GitHubArtifactRestrictedStateStore.cs#GitHubArtifactRestrictedStateStore",
        "runtime/src/AgenticPrReview.Runtime/Host/State/GitHubArtifacts/PrivateArtifactBridgeClient.cs#PrivateArtifactBridgeClient",
        "runtime/src/AgenticPrReview.Runtime/Host/State/Locator/LocatorRootService.cs#LocatorRootService",
        "runtime/src/AgenticPrReview.Runtime/Host/State/Lineage/LineageService.cs#LineageService",
        "runtime/src/AgenticPrReview.Runtime/Host/State/Lineage/ScopedStateInventory.cs#ScopedStateInventory",
        "runtime/src/AgenticPrReview.Runtime/Host/State/Restore/AuthorizedAcceptedStateComposer.cs#AuthorizedAcceptedStateComposer",
        "runtime/src/AgenticPrReview.Runtime/Host/State/Restore/AcceptedStateSelector.cs#AcceptedStateSelector",
        "runtime/src/AgenticPrReview.Runtime/Host/State/Transactions/RetainedStateTransactionAuthority.cs#RetainedStateTransactionAuthority",
        "runtime/src/AgenticPrReview.Runtime/Host/State/Transactions/RetainedStateTransactionService.cs#RetainedStateTransactionService",
        "runtime/src/AgenticPrReview.Runtime/Host/State/Transactions/RetainedStatePersistence.cs#RetainedStatePersistence",
        "runtime/src/AgenticPrReview.Runtime/Host/Publishing/GitHub/Sticky/StickyCommentPublisher.cs#StickyCommentPublisher",
        "runtime/src/AgenticPrReview.Runtime/Host/Publishing/GitHub/Sticky/StickyCommentPublisher.cs#StickyPublicationReceipt",
        "runtime/src/AgenticPrReview.Runtime/Host/Publishing/Recovery/PublicationRecoveryService.cs#PublicationRecoveryService",
        "runtime/src/AgenticPrReview.Runtime/Host/Action/ActionHostCoordinator.cs#ActionHostCoordinator"
    ], StringComparer.Ordinal);

    private static IReadOnlySet<string> ExpectedW6EvidencePaths() => new HashSet<string>([
        "runtime/tests/AgenticPrReview.Runtime.Tests/Host/State/RestrictedStateStoreConformanceTests.cs",
        "runtime/tests/AgenticPrReview.Runtime.Tests/Host/State/LocalRestrictedStateStoreTests.cs",
        "runtime/tests/AgenticPrReview.Runtime.Tests/Host/State/GitHubArtifacts/GitHubArtifactBridgeConformanceTests.cs",
        "runtime/tests/AgenticPrReview.Runtime.Tests/Host/State/Locator/LocatorCodecAndSelectionTests.cs",
        "runtime/tests/AgenticPrReview.Runtime.Tests/Host/State/Lineage/LineageConcurrencyTests.cs",
        "runtime/tests/AgenticPrReview.Runtime.Tests/Host/State/Restore/AcceptedStateProductionEndToEndTests.cs",
        "runtime/tests/AgenticPrReview.Runtime.Tests/Host/State/Restore/AcceptedStateSelectorTests.cs",
        "runtime/tests/AgenticPrReview.Runtime.Tests/Host/State/Transactions/RetainedStateTransactionEndToEndTests.cs",
        "runtime/tests/AgenticPrReview.Runtime.Tests/Host/Publishing/GitHub/Sticky/StickyCommentPublisherTests.cs",
        "runtime/tests/AgenticPrReview.Runtime.Tests/Host/Publishing/GitHub/Sticky/StickyPublicationContractsTests.cs",
        "runtime/tests/AgenticPrReview.Runtime.Tests/Host/Publishing/Recovery/PublicationRecoveryServiceTests.cs",
        "runtime/tests/AgenticPrReview.Runtime.Tests/Host/Action/ActionHostCoordinatorTests.cs"
    ], StringComparer.Ordinal);

    private static bool ValidateW6ReceiptDisposition(JsonElement entry,
        string repository)
    {
        var receipt = entry.GetProperty("receipt_disposition");
        return receipt.GetProperty("legacy_schema").GetString() ==
                "protocol/schemas/state-publication-receipt.v1.json" &&
            RequiredExactTextArray(receipt, "owners",
                new HashSet<string>(["P2", "P5", "S5", "S6", "P6"], StringComparer.Ordinal)) &&
            RequiredExactTextArray(receipt, "owner_members",
                new HashSet<string>([
                    "runtime/src/AgenticPrReview.Runtime/Host/Publishing/GitHub/Sticky/StickyCommentPublisher.cs#StickyCommentPublisher",
                    "runtime/src/AgenticPrReview.Runtime/Host/Publishing/GitHub/Sticky/StickyCommentPublisher.cs#StickyPublicationReceipt",
                    "runtime/src/AgenticPrReview.Runtime/Host/Publishing/Recovery/PublicationRecoveryService.cs#PublicationRecoveryService",
                    "runtime/src/AgenticPrReview.Runtime/Host/State/Restore/AcceptedStateSelector.cs#AcceptedStateSelector",
                    "runtime/src/AgenticPrReview.Runtime/Host/State/Transactions/RetainedStateTransactionService.cs#RetainedStateTransactionService",
                    "runtime/src/AgenticPrReview.Runtime/Host/State/Transactions/RetainedStatePersistence.cs#RetainedStatePersistence",
                    "runtime/src/AgenticPrReview.Runtime/Host/Action/ActionHostCoordinator.cs#ActionHostCoordinator"
                ], StringComparer.Ordinal), member => MemberExists(repository, member)) &&
            RequiredExactTextArray(receipt, "evidence_paths",
                new HashSet<string>([
                    "runtime/tests/AgenticPrReview.Runtime.Tests/Host/Publishing/GitHub/Sticky/StickyCommentPublisherTests.cs",
                    "runtime/tests/AgenticPrReview.Runtime.Tests/Host/Publishing/GitHub/Sticky/StickyPublicationContractsTests.cs",
                    "runtime/tests/AgenticPrReview.Runtime.Tests/Host/Publishing/Recovery/PublicationRecoveryServiceTests.cs",
                    "runtime/tests/AgenticPrReview.Runtime.Tests/Host/State/Restore/AcceptedStateSelectorTests.cs",
                    "runtime/tests/AgenticPrReview.Runtime.Tests/Host/State/Transactions/RetainedStateTransactionEndToEndTests.cs",
                    "runtime/tests/AgenticPrReview.Runtime.Tests/Host/Action/ActionHostCoordinatorTests.cs"
                ], StringComparer.Ordinal), value => LandingPathExists(repository, value)) &&
            RequiredExactTextArray(receipt, "framework_scenarios",
                new HashSet<string>(["crash-mutation", "crash-recovery", "cancel-known-commit", "cancel-outcome-unknown"], StringComparer.Ordinal));
    }

    private static bool ValidateW6ResidualScan(JsonElement entry,
        string repository)
    {
        var scan = entry.GetProperty("w6_residual_scan");
        var forbidden = new HashSet<string>([
            "GitDataStateTransport", "GitHubGitStateAcceptanceStore",
            "StateAcceptanceStore", "ReferenceStateStore", "OctokitGitDataClient",
            "acceptLocalCandidate", "heads/agentic-pr-review-m4-state-v1",
            "agentic-pr-review-m4-state-v1", "m4-state/v1", "m4-state-v1",
            "candidate-registration.v1.json", "accepted-state-marker.v1.json",
            "state-selector.v1.json", "state-publication-receipt.v1.json",
            "CandidateRegistrationV1", "AcceptedStateMarkerV1", "StateSelectorV1",
            "StatePublicationReceiptV1", "@actions/cache", "actions/cache",
            "restoreCache", "saveCache"
        ], StringComparer.Ordinal);
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in new[] { "w8_marker_paths", "deletion_evidence_paths",
                     "immutable_provenance_paths", "retired_document_paths",
                     "retained_unrelated_policy_paths", "bundled_dependency_artifact_paths" })
        {
            if (!RequiredExactTextArray(scan, property,
                    ExpectedW6ResidualPaths(property))) return false;
            foreach (var path in TextArray(scan, property)) allowed.Add(path);
        }
        if (!TextArray(scan, "forbidden_tokens").ToHashSet(StringComparer.Ordinal)
                .SetEquals(forbidden)) return false;
        foreach (var path in TrackedPaths(repository))
        {
            if (allowed.Contains(path)) continue;
            var fullPath = Path.Join(repository,
                path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath)) continue;
            var text = File.ReadAllText(fullPath);
            if (forbidden.Any(token => text.Contains(token,
                    StringComparison.Ordinal))) return false;
        }
        return true;
    }

    private static IReadOnlySet<string> ExpectedW6ResidualPaths(string property) =>
        property switch
        {
            "w8_marker_paths" => new HashSet<string>([
                "runtime/tests/AgenticPrReview.Runtime.Tests/Host/Publishing/GitHub/Sticky/StickyCommentPublisherTests.cs"
            ], StringComparer.Ordinal),
            "deletion_evidence_paths" => new HashSet<string>([
                "src/residual-reference-allowlist.ts",
                "src/r4-migration-cutover-audit.test.ts",
                "runtime/tests/ActionHostVerifierFixture/FrameworkSupervisor.cs",
                "runtime/tests/AgenticPrReview.Runtime.Tests/Host/Action/ActionHostFrameworkVerifierArchitectureTests.cs",
                "runtime/tests/fixtures/action-host/framework/replacement-record.json"
            ], StringComparer.Ordinal),
            "immutable_provenance_paths" => new HashSet<string>([
                "docs/20_architecture/r1-legacy-removal-handoff.md",
                "runtime/tests/fixtures/action-host/framework/e1-base-inventory.json"
            ], StringComparer.Ordinal),
            "retired_document_paths" => new HashSet<string>([
                "docs/00_project/project-context.md",
                "docs/20_architecture/agent-runtime-rebaseline.md",
                "docs/20_architecture/m4-state-acceptance.md",
                "docs/20_architecture/m4-stateful-action.md",
                "docs/20_architecture/r4-actionhost-wrapper-plan.md",
                "docs/20_architecture/security-boundary.md",
                "docs/90_roadmap/roadmap-seed.md"
            ], StringComparer.Ordinal),
            "retained_unrelated_policy_paths" => new HashSet<string>([
                "scripts/check-r3-live-proof.mjs"
            ], StringComparer.Ordinal),
            "bundled_dependency_artifact_paths" => new HashSet<string>([
                ".github/actions/agentic-pr-review/dist/index.js"
            ], StringComparer.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(property)),
        };

    private static IEnumerable<string> TrackedPaths(string repository)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = repository,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("ls-files");
        using var process = Process.Start(start);
        if (process is null) return [];
        var paths = process.StandardOutput.ReadToEnd().Split('\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        process.WaitForExit();
        return process.ExitCode == 0 ? paths : [];
    }

    private static bool RequiredExactTextArray(JsonElement value,
        string property,
        IReadOnlySet<string> expected,
        Func<string, bool>? additional = null)
    {
        if (!RequiredTextArray(value, property, item =>
                expected.Contains(item) && (additional?.Invoke(item) ?? true)))
            return false;
        var actual = TextArray(value, property).ToHashSet(StringComparer.Ordinal);
        return actual.Count == expected.Count && actual.SetEquals(expected);
    }

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

    private static bool RequiredObjectArray(
        JsonElement value,
        string property,
        int expectedLength,
        params string[] fields) =>
        value.TryGetProperty(property, out var items) &&
        items.ValueKind == JsonValueKind.Array &&
        items.GetArrayLength() == expectedLength &&
        items.EnumerateArray().All(item =>
            item.ValueKind == JsonValueKind.Object &&
            fields.All(field => item.TryGetProperty(field, out var member) &&
                member.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(member.GetString())));

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

    internal static bool EvaluateCanaryRoutes(
        string root,
        string tablePath,
        ICollection<string> failures)
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
        for (var index = 0; index < observations.Length; index++)
        {
            var fields = observations[index];
            if (fields.Length != 2)
            {
                failures.Add("observation-shape:" +
                    index.ToString(CultureInfo.InvariantCulture));
                return false;
            }
            if (!routes.TryGetValue(fields[0], out var route))
            {
                failures.Add("observation-class:sha256-" +
                    Sha256Text(fields[0]));
                return false;
            }
            if (!RouteAllows(route, fields[1]))
            {
                failures.Add("observation-route:" + fields[0] +
                    ":sink-sha256-" + Sha256Text(fields[1]));
                return false;
            }
        }

        foreach (var (canaryClass, route) in routes)
        {
            if (route.ForbiddenSinks.Count == 0 ||
                route.ForbiddenSinks.Any(pattern =>
                    !SinkMatches(pattern, NegativeSink(pattern)) ||
                    RouteAllows(route, NegativeSink(pattern))))
            {
                failures.Add("forbidden-route-model:" + canaryClass);
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
                failures.Add("cardinality:" + canaryClass + ":" +
                    route.Cardinality + ":actual=" +
                    count.ToString(CultureInfo.InvariantCulture) +
                    ":expected=" +
                    expected.ToString(CultureInfo.InvariantCulture));
                return false;
            }
        }

        var negativeInjectionCount = RunNegativeInjectionMatrix(root, routes);
        if (negativeInjectionCount < 1)
        {
            failures.Add("negative-injection-matrix");
            return false;
        }
        File.WriteAllText(Path.Join(root, "canary-negative-injection-count"),
            negativeInjectionCount.ToString(CultureInfo.InvariantCulture));
        return true;
    }

    internal static string[] CanaryRouteViolationFailures(string root)
    {
        var path = Path.Join(root, "canary-route-violation");
        if (!File.Exists(path)) return [];
        return File.ReadAllLines(path)
            .Select((line, index) =>
            {
                var fields = line.Split('\t');
                return fields.Length == 3
                    ? "route-violation:" +
                        (IsKnownCanaryClass(fields[0])
                            ? fields[0]
                            : "unknown-class") + ":" +
                        (IsKnownViolationReason(fields[2])
                            ? fields[2]
                            : "unknown-reason") + ":sha256-" +
                        Sha256Text(line)
                    : "route-violation-shape:" +
                        index.ToString(CultureInfo.InvariantCulture) + ":" +
                        Sha256Text(line);
            })
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Take(64)
            .ToArray();
    }

    private static bool IsKnownCanaryClass(string value) => value is
        "provider-key" or "github-token" or "state-key-current" or
        "state-key-previous" or "actions-runtime-jwt" or "signed-url-sig" or
        "repository" or "reviewed-path" or "workflow-source" or "prompt" or
        "tool-data" or "session-plaintext" or "artifact-ciphertext" or
        "public-result" or "archive";

    private static bool IsKnownViolationReason(string value) => value is
        "expected_missing" or "public_leak" or "forbidden_present" or
        "zip_shape_invalid" or "envelope_shape_invalid" or
        "ciphertext_binding_invalid" or "archive_decode_invalid" or
        "zip_decode_invalid" or "plaintext_in_artifact_archive";

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
        FrameworkJson.SerializeToUtf8Bytes(FrameworkJson.Object(
            ("value", value)));

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

    private static string ReadOptionalJsonString(
        string root,
        string name,
        string property)
    {
        var path = Path.Join(root, name);
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        return document.RootElement.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
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
        var evidence = FrameworkJson.Object(
            ("passed", passed),
            ("payload_sha256", Sha256(payload)),
            ("sdk", RuntimeInformation.FrameworkDescription),
            ("official_artifacts", FrameworkJson.Object(
                ("locator", platform.ArtifactNames.Any(name =>
                    name == "agentic-pr-review-state-root-v1")),
                ("scoped", platform.ArtifactNames.Any(name =>
                    name.StartsWith("apr-state-", StringComparison.Ordinal))))),
            ("cases", FrameworkJson.Array(cases.Select(CaseEvidence))));
        await File.WriteAllTextAsync(Path.Join(root, "evidence.json"),
            FrameworkJson.SerializeIndented(evidence))
            .ConfigureAwait(false);
    }

    private static async Task WriteAotIdentityAsync(
        AotProofContext context,
        string root,
        string repository,
        string record,
        string inventory,
        string canaries)
    {
        var normalizedEvidence = Path.Join(root, "normalized-evidence.json");
        var identity = FrameworkJson.Object(
            ("kind", "apr-r4-e2-action-host-native-aot-identity-v1"),
            ("execution_kind", "native-aot"),
            ("reflection_json_enabled",
                JsonSerializer.IsReflectionEnabledByDefault),
            ("dynamic_code_supported", RuntimeFeature.IsDynamicCodeSupported),
            ("launch_action_source_sha", FrameworkGitHubHandler.ActionSha),
            ("wrapper_build_discriminator",
                FrameworkCanaries.BuildDiscriminator),
            ("payload_sha256", context.PayloadSha256),
            ("managed_intermediate_sha256",
                context.ManagedIntermediateSha256),
            ("runtime_intermediate_sha256",
                context.RuntimeIntermediateSha256),
            ("managed_architecture_sha256",
                context.ManagedArchitectureSha256),
            ("build_pair_sha256", context.BuildPairSha256),
            ("e1_normalized_evidence_sha256", Sha256(normalizedEvidence)),
            ("source_inventory_digest", SourceInventoryDigest(repository)),
            ("replacement_record_digest", Sha256(record)),
            ("base_inventory_digest", JsonProperty(inventory,
                "aggregate_sha256")),
            ("canary_table_digest", Sha256(canaries)));
        var identityBytes = Encoding.UTF8.GetBytes(
            FrameworkJson.Serialize(identity) + "\n");
        if (!FrameworkCanaryCapture.AssertPublicSafe(
                root, "aot.identity", identityBytes))
        {
            throw new InvalidDataException("AOT identity is not public-safe.");
        }
        await File.WriteAllBytesAsync(context.IdentityOutput, identityBytes)
            .ConfigureAwait(false);
    }

    private static JsonObject CaseEvidence(CaseResult result) =>
        FrameworkJson.Object(
            ("Name", result.Name),
            ("ExpectedStatus", result.ExpectedStatus),
            ("ActualStatus", result.ActualStatus),
            ("ExitCode", result.ExitCode),
            ("HostPid", result.HostPid),
            ("ProviderRequests", result.ProviderRequests),
            ("ToolSequence", result.ToolSequence),
            ("GitHubRequests", result.GitHubRequests),
            ("ArtifactRestRequests", result.ArtifactRestRequests),
            ("ArtifactRestNotModified", result.ArtifactRestNotModified),
            ("ArtifactRestPrimary", result.ArtifactRestPrimary),
            ("ArtifactRestSecondaryPoints",
                result.ArtifactRestSecondaryPoints),
            ("AnonymousSignedUploads", result.AnonymousSignedUploads),
            ("AnonymousSignedDownloads", result.AnonymousSignedDownloads),
            ("StateOperations", result.StateOperations),
            ("StickyMutations", result.StickyMutations),
            ("InlineMutations", result.InlineMutations),
            ("ExactEnvironment", result.ExactEnvironment),
            ("OutputUnchanged", result.OutputUnchanged),
            ("ProcessGroupQuiet", result.ProcessGroupQuiet),
            ("PlatformQuiet", result.PlatformQuiet),
            ("CanarySafe", result.CanarySafe),
            ("ContinuationObserved", result.ContinuationObserved),
            ("Passed", result.Passed));

    private static int ReadInt(string root, string name)
    {
        var path = Path.Join(root, name);
        return File.Exists(path) && int.TryParse(File.ReadAllText(path),
            NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }

    private static long ReadLong(string root, string name)
    {
        var path = Path.Join(root, name);
        return File.Exists(path) && long.TryParse(File.ReadAllText(path),
            NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }

    private static JsonObject RoleEvidence(
        FrameworkRequestBudgetReceipt host,
        FrameworkArtifactRestRequestBudgetReceipt artifact,
        FrameworkControlRequestBudgetReceipt embedded,
        IReadOnlyList<FrameworkExternalControlRequestBudgetReceipt> external,
        IReadOnlyList<FrameworkExternalControlRequestBudgetReceipt> cleanup) =>
        FrameworkJson.Object(
            ("node_artifact_rest", artifact.PrimaryRateLimitRequests),
            ("host_head_source_rest", host.HostHeadSourcePrimary),
            ("host_other_github_rest", host.HostOtherGitHubPrimary),
            ("embedded_control", embedded.Primary),
            ("external_control", external.Sum(value => value.Primary)),
            ("cleanup_control", cleanup.Sum(value => value.Primary)));

    private static JsonObject ReceiptEventJoinEvidence(
        string scenario,
        CaseResult result,
        FrameworkRequestBudgetReceipt host,
        FrameworkArtifactRestRequestBudgetReceipt artifact,
        FrameworkControlRequestBudgetReceipt embedded,
        IReadOnlyList<FrameworkExternalControlRequestBudgetReceipt> external,
        IReadOnlyList<FrameworkExternalControlRequestBudgetReceipt> cleanup,
        bool joined)
    {
        var eventsRead = TryReadScenarioEvents(scenario, out var events);
        var eventRoles = eventsRead
            ? FrameworkJson.Object(
                ("node_artifact_rest", events["node_artifact_rest"].Primary),
                ("host_head_source_rest", events["host_head_source_rest"].Primary),
                ("host_other_github_rest", events["host_other_github_rest"].Primary),
                ("trusted_control_rest", events["trusted_control_rest"].Primary))
            : FrameworkJson.Object(
                ("node_artifact_rest", -1),
                ("host_head_source_rest", -1),
                ("host_other_github_rest", -1),
                ("trusted_control_rest", -1));
        return FrameworkJson.Object(
            ("case", result.Name),
            ("joined", joined),
            ("event_roles", eventRoles),
            ("receipt_roles", FrameworkJson.Object(
                ("node_artifact_rest", artifact.PrimaryRateLimitRequests),
                ("host_head_source_rest", host.HostHeadSourcePrimary),
                ("host_other_github_rest", host.HostOtherGitHubPrimary),
                ("trusted_control_rest", embedded.Primary +
                    external.Sum(value => value.Primary) +
                    cleanup.Sum(value => value.Primary)))));
    }

    // Only normalized, secret-free fixture observations are admitted here.
    // The platform records the timestamp at listener dispatch, before route
    // handling, so the half-open rolling-window calculation cannot be
    // satisfied by delaying response serialization.
    private static OperationRequestEventMeasurement ReadOperationRequestEvents(
        string root,
        IReadOnlyList<string> names)
    {
        var fixedNames = new[]
        {
            "dispatch-bootstrap", "dispatch-continuation", "stale-head",
        };
        if (!names.SequenceEqual(fixedNames, StringComparer.Ordinal))
        {
            return OperationRequestEventMeasurement.Invalid;
        }
        var paths = names.Select(name => Path.Join(root, name,
                "trusted-proof-request-events.tsv"))
            .ToArray();
        if (paths.Length != names.Count || paths.Any(path => !File.Exists(path)))
        {
            return OperationRequestEventMeasurement.Invalid;
        }
        var acceptedDomains = new HashSet<string>(StringComparer.Ordinal)
        {
            "node_artifact_rest",
            "host_head_source_rest",
            "host_other_github_rest",
            "trusted_control_rest",
            "actions_results_service",
            "anonymous_transfers",
        };
        var acceptedResponses = new HashSet<string>(StringComparer.Ordinal)
        {
            "success",
            "not_modified",
            "permission_denied",
            "primary_rate_limited",
            "secondary_rate_limited",
            "combined_rate_limited",
            "invalid_rate_headers",
            "other_failure",
        };
        var groups = new List<List<OperationRequestEvent>>();
        var globalOrdinal = 0;
        for (var scenarioIndex = 0; scenarioIndex < paths.Length; scenarioIndex++)
        {
            var path = paths[scenarioIndex];
            var operation = new List<OperationRequestEvent>();
            foreach (var line in File.ReadLines(path))
            {
                if (line.Split('\t') is not [var domain, var route, var method, var points,
                        var timestamp, var response] ||
                    !acceptedDomains.Contains(domain) ||
                    !IsExactEventRoute(domain, route) ||
                    !acceptedResponses.Contains(response) ||
                    !int.TryParse(points, NumberStyles.None,
                        CultureInfo.InvariantCulture, out var secondaryPoints) ||
                    !long.TryParse(timestamp, NumberStyles.None,
                        CultureInfo.InvariantCulture, out var monotonicTimestamp) ||
                    monotonicTimestamp < 0 ||
                    secondaryPoints != (method is "GET" or "HEAD" or "OPTIONS"
                        ? 1 : 5))
                {
                    return OperationRequestEventMeasurement.Invalid;
                }

                operation.Add(new(names[scenarioIndex], scenarioIndex + 1,
                    checked(++globalOrdinal), domain, route, method,
                    secondaryPoints, monotonicTimestamp, response));
            }
            groups.Add(operation);
        }

        var events = groups.SelectMany(group => group).ToArray();
        var authenticated = events.Where(value => value.Domain is
            "node_artifact_rest" or "host_head_source_rest" or
            "host_other_github_rest" or "trusted_control_rest")
            .OrderBy(value => value.MonotonicTimestamp).ToArray();
        var node = authenticated.Where(value =>
            value.Domain == "node_artifact_rest").ToArray();
        var nodeWindowValid = groups.All(group => RollingWindowWithin(group
                .Where(value => value.Domain == "node_artifact_rest")
                .OrderBy(value => value.MonotonicTimestamp).ToArray(), 600,
                inclusive: true));
        var allWindowValid = groups.All(group => RollingWindowWithin(group
                .Where(value => value.Domain is "node_artifact_rest" or
                    "host_head_source_rest" or "host_other_github_rest" or
                    "trusted_control_rest")
                .OrderBy(value => value.MonotonicTimestamp).ToArray(), 900,
                inclusive: false));
        var mutationSpacingValid = groups.All(group => MutationSpacingValid(group));
        var domainTails = DomainFuturePrimaryTails(groups);
        var sequence = string.Concat(events.Select(value =>
            value.Scenario + "\t" + value.ScenarioOrdinal.ToString(
                CultureInfo.InvariantCulture) + "\t" + value.Ordinal.ToString(
                CultureInfo.InvariantCulture) + "\t" + value.Domain + "\t" +
            value.Route + "\t" + value.Method + "\t" + value.SecondaryPoints.ToString(
                CultureInfo.InvariantCulture) + "\t" + value.ResponseClass + "\n"));
        return new(true, nodeWindowValid, allWindowValid, mutationSpacingValid,
            node.Length,
            events.Count(value => value.Domain == "host_head_source_rest"),
            events.Count(value => value.Domain == "host_other_github_rest"),
            events.Count(value => value.Domain == "trusted_control_rest"),
            events.Count(value => value.Domain == "actions_results_service"),
            events.Count(value => value.Domain == "anonymous_transfers"),
            events, domainTails, Sha256Text(sequence));
    }

    internal static IReadOnlyDictionary<string, int> DomainFuturePrimaryTails(
        IEnumerable<IEnumerable<OperationRequestEvent>> scenarioEvents)
    {
        var domains = new[]
        {
            "node_artifact_rest", "host_head_source_rest",
            "host_other_github_rest", "trusted_control_rest",
        };
        var tails = domains.ToDictionary(domain => domain, _ => 0,
            StringComparer.Ordinal);
        // The three runs share one fixed aggregate witness order:
        // bootstrap -> continuation -> stale.  Ordinal is append order from
        // SyntheticOfficialPlatform, not a wall-clock sort key.  A remaining
        // observation in bootstrap must therefore retain the primary work of
        // the following continuation and stale witnesses in its suffix.
        var ordered = scenarioEvents.SelectMany(scenario => scenario)
            .OrderBy(value => value.Ordinal)
            .ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var current = ordered[index];
            if (!tails.ContainsKey(current.Domain)) continue;
            var futurePrimary = ordered.Skip(index + 1).Count(IsPrimaryCharged);
            tails[current.Domain] = Math.Max(tails[current.Domain],
                futurePrimary);
        }

        return tails;
    }

    private static bool IsPrimaryCharged(OperationRequestEvent value) =>
        (value.Domain is "node_artifact_rest" or "host_head_source_rest" or
            "host_other_github_rest" or "trusted_control_rest") &&
        value.ResponseClass != "not_modified";

    private static bool ProtectedEventJoinIsExact(
        string scenario,
        CaseResult result,
        FrameworkRequestBudgetReceipt host,
        FrameworkArtifactRestRequestBudgetReceipt artifact,
        FrameworkControlRequestBudgetReceipt embedded,
        IReadOnlyList<FrameworkExternalControlRequestBudgetReceipt> external,
        IReadOnlyList<FrameworkExternalControlRequestBudgetReceipt> cleanup)
    {
        if (!TryReadScenarioEvents(scenario, out var events)) return false;
        var node = events["node_artifact_rest"];
        var head = events["host_head_source_rest"];
        var other = events["host_other_github_rest"];
        var control = events["trusted_control_rest"];
        var results = events["actions_results_service"];
        var anonymous = events["anonymous_transfers"];
        var controlRaw = embedded.Consumed + external.Sum(value => value.Consumed) +
            cleanup.Sum(value => value.Consumed);
        var controlPrimary = embedded.Primary + external.Sum(value => value.Primary) +
            cleanup.Sum(value => value.Primary);
        var controlPoints = embedded.SecondaryPoints +
            external.Sum(value => value.SecondaryPoints) +
            cleanup.Sum(value => value.SecondaryPoints);
        var controlMutations = embedded.MutationCount +
            external.Sum(value => value.MutationCount) +
            cleanup.Sum(value => value.MutationCount);
        return head.Raw == 180 &&
            ReadInt(scenario, "head-commit-api-count") == 1 &&
            ReadInt(scenario, "head-tree-api-count") == 178 &&
            ReadInt(scenario, "head-archive-api-count") == 1 &&
            ReadInt(scenario, "base-api-count") == 0 &&
            head.Raw == host.HostHeadSourceRaw &&
            head.Primary == host.HostHeadSourcePrimary &&
            head.NotModified == host.HostHeadSourceNotModified &&
            head.Points == host.HostHeadSourceSecondaryPoints &&
            other.Raw == host.HostOtherGitHubRaw &&
            other.Primary == host.HostOtherGitHubPrimary &&
            other.NotModified == host.HostOtherGitHubNotModified &&
            other.Points == host.HostOtherGitHubSecondaryPoints &&
            node.Raw == artifact.TotalAuthenticatedApiRequests &&
            node.Raw == result.ArtifactRestRequests &&
            node.NotModified == artifact.ConditionalNotModifiedRequests &&
            node.Primary == artifact.PrimaryRateLimitRequests &&
            node.Primary == result.ArtifactRestPrimary &&
            node.Points == artifact.SecondaryLimitPoints &&
            node.Points == result.ArtifactRestSecondaryPoints &&
            node.PermissionDenied == artifact.PermissionDenied &&
            control.Raw == controlRaw && control.Primary == controlPrimary &&
            control.Points == controlPoints && control.Mutations == controlMutations &&
            anonymous.SignedDownloads == result.AnonymousSignedDownloads &&
            anonymous.SignedUploads == result.AnonymousSignedUploads &&
            anonymous.Raw == anonymous.SignedUploads + anonymous.SignedDownloads &&
            host.AnonymousCodeloadRequests == ReadInt(scenario,
                "head-archive-anonymous-codeload-count") &&
            host.AnonymousCodeloadRequests == 1 &&
            result.AnonymousSignedDownloads >= 0 &&
            results.Raw > 0;
    }

    private static bool TryReadScenarioEvents(
        string scenario,
        out IReadOnlyDictionary<string, ScenarioRequestEventStats> values)
    {
        values = new Dictionary<string, ScenarioRequestEventStats>();
        var domainsPath = Path.Join(scenario, "trusted-proof-request-domains.tsv");
        var eventsPath = Path.Join(scenario, "trusted-proof-request-events.tsv");
        if (!File.Exists(domainsPath) || !File.Exists(eventsPath)) return false;
        var domains = new[]
        {
            "node_artifact_rest", "host_head_source_rest", "host_other_github_rest",
            "trusted_control_rest", "actions_results_service", "anonymous_transfers",
        };
        var declared = File.ReadLines(domainsPath).Select(line => line.Split('\t'))
            .ToArray();
        if (declared.Length != domains.Length || declared.Any(fields => fields.Length != 2) ||
            !declared.Select(fields => fields[0]).SequenceEqual(domains) ||
            declared.Select(fields => fields[0]).Distinct(StringComparer.Ordinal).Count() != domains.Length)
        {
            return false;
        }
        var mutable = domains.ToDictionary(domain => domain,
            _ => new MutableScenarioRequestEventStats(), StringComparer.Ordinal);
        foreach (var line in File.ReadLines(eventsPath))
        {
            if (line.Split('\t') is not [var domain, var route, var method, var points,
                    var timestamp, var response] || !mutable.TryGetValue(domain,
                    out var stat) || !int.TryParse(points, out var parsedPoints) ||
                !long.TryParse(timestamp, out _) || parsedPoints !=
                    (method is "GET" or "HEAD" or "OPTIONS" ? 1 : 5) ||
                response is not ("success" or "not_modified" or "permission_denied") ||
                !IsExactEventRoute(domain, route))
            {
                return false;
            }
            stat.Raw++;
            stat.Points += parsedPoints;
            if (method is not ("GET" or "HEAD" or "OPTIONS")) stat.Mutations++;
            if (route == "actions_results_signed_upload") stat.SignedUploads++;
            if (route == "actions_results_signed_download") stat.SignedDownloads++;
            if (response == "not_modified") stat.NotModified++;
            else
            {
                stat.Primary++;
                if (response == "permission_denied") stat.PermissionDenied++;
            }
        }
        for (var index = 0; index < domains.Length; index++)
        {
            if (!int.TryParse(declared[index][1], NumberStyles.None,
                    CultureInfo.InvariantCulture, out var declaredRaw) ||
                declaredRaw != mutable[domains[index]].Raw)
            {
                return false;
            }
        }
        values = mutable.ToDictionary(pair => pair.Key,
            pair => new ScenarioRequestEventStats(pair.Value.Raw,
                pair.Value.NotModified, pair.Value.Primary, pair.Value.Points,
                pair.Value.PermissionDenied, pair.Value.Mutations, pair.Value.SignedUploads,
                pair.Value.SignedDownloads),
            StringComparer.Ordinal);
        return true;
    }

    private static bool IsExactEventRoute(string domain, string route) =>
        domain == "actions_results_service"
            ? route == "actions_results_twirp"
            : domain == "anonymous_transfers"
                ? route is "actions_results_signed_upload" or
                    "actions_results_signed_download"
                : route == "github_rest";

    private static bool RollingWindowWithin(
        IReadOnlyList<OperationRequestEvent> events,
        int maximum,
        bool inclusive)
    {
        for (var index = 0; index < events.Count; index++)
        {
            var end = checked(events[index].MonotonicTimestamp +
                Stopwatch.Frequency * 60L);
            var points = events.Where(value => value.MonotonicTimestamp >=
                    events[index].MonotonicTimestamp &&
                value.MonotonicTimestamp < end).Sum(value => value.SecondaryPoints);
            if (inclusive ? points > maximum : points >= maximum) return false;
        }

        return true;
    }

    private static bool MutationSpacingValid(
        IEnumerable<OperationRequestEvent> events)
    {
        var mutations = events.Where(value => value.Domain is
                "node_artifact_rest" or "host_head_source_rest" or
                "host_other_github_rest" or "trusted_control_rest")
            .Where(value => value.Method is not "GET" and not "HEAD" and
                not "OPTIONS")
            .OrderBy(value => value.MonotonicTimestamp)
            .ToArray();
        return mutations.Zip(mutations.Skip(1), (first, second) =>
                second.MonotonicTimestamp - first.MonotonicTimestamp)
            .All(delta => delta >= Stopwatch.Frequency);
    }

    private static bool HeadArchiveTransportEvidenceIsExact(string scenario)
    {
        var path = Path.Join(scenario, "head-archive-served");
        if (!File.Exists(path) || File.ReadAllLines(path) is not [var line] ||
            line.Split('\t') is not [var decodedLargeBlobBytes,
                var compressedArchiveBytes, var decodedArchiveFileBytes,
                var prefix] ||
            !int.TryParse(decodedLargeBlobBytes, NumberStyles.None,
                CultureInfo.InvariantCulture, out var decodedLargeBlob) ||
            !int.TryParse(compressedArchiveBytes, NumberStyles.None,
                CultureInfo.InvariantCulture, out var compressedArchive) ||
            !int.TryParse(decodedArchiveFileBytes, NumberStyles.None,
                CultureInfo.InvariantCulture, out var decodedArchive))
        {
            return false;
        }

        return decodedLargeBlob == FrameworkGitHubHandler
                .ProductionShapedLargeBlobByteCount &&
            compressedArchive > 0 &&
            decodedArchive == decodedLargeBlob +
                Encoding.UTF8.GetByteCount(FrameworkCanaries.ToolData + "\n") &&
            compressedArchive < decodedArchive &&
            prefix == "agentic-pr-review-fixture/" &&
            ReadInt(scenario, "head-archive-api-count") == 1 &&
            ReadInt(scenario, "head-archive-anonymous-codeload-count") == 1 &&
            ReadInt(scenario,
                "head-archive-credential-not-forwarded-count") == 1 &&
            ReadInt(scenario, "head-blob-api-count") == 0;
    }

    private static void CaptureTrustedProofRequestBudgetReceipts(
        string scenario,
        string stderr)
    {
        CapturePrefixedReceipt(scenario, stderr,
            "APR_R4_E2P_GITHUB_REQUEST_BUDGET ",
            "trusted-proof-github-request-budget.json");
        CapturePrefixedReceipt(scenario, stderr,
            "APR_R4_E2P_ARTIFACT_REST_BUDGET ",
            "trusted-proof-artifact-rest-request-budget.json");
        CapturePrefixedReceipt(scenario, stderr,
            "APR_R4_E2P_CONTROL_REQUEST_BUDGET ",
            "trusted-proof-embedded-control-request-budget.json");
    }

    private static void CaptureExternalControlRequestBudgetReceipt(
        string scenario,
        string phase,
        string stderr) =>
        CapturePrefixedReceipt(scenario, stderr,
            "APR_R4_E2P_CONTROL_REQUEST_BUDGET ",
            "trusted-proof-external-control-" + phase +
                "-request-budget.json");

    private static void CapturePrefixedReceipt(
        string scenario,
        string stderr,
        string prefix,
        string name)
    {
        var matches = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith(prefix, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length == 1)
        {
            File.WriteAllText(Path.Join(scenario, name),
                matches[0][prefix.Length..] + "\n");
        }
    }

    private static bool TrustedProofRequestBudgetReceiptsAreExact(
        string scenario,
        int artifactRestRequests,
        int artifactRestNotModified,
        int artifactRestPrimary,
        int artifactRestSecondaryPoints,
        int anonymousSignedDownloads) =>
        TryReadTrustedProofRequestBudgetReceipt(scenario) is { } payload &&
        PayloadRequestBudgetReceiptIsExact(payload,
            payload.AuthenticatedRestLimit) &&
        TryReadArtifactRestRequestBudgetReceipt(scenario) is { } artifact &&
        ArtifactRestRequestBudgetReceiptIsExact(
            artifact,
            scenario,
            artifactRestRequests,
            artifactRestNotModified,
            artifactRestPrimary,
            artifactRestSecondaryPoints,
            anonymousSignedDownloads) &&
        TryReadEmbeddedControlRequestBudgetReceipt(scenario) is { } control &&
        ControlRequestBudgetReceiptIsExact(control);

    private static bool PayloadRequestBudgetReceiptIsExact(
        FrameworkRequestBudgetReceipt receipt,
        int maximum) =>
        receipt.AuthenticatedRestRequests <= maximum &&
        receipt.AuthenticatedRestLimit == maximum &&
        receipt.HostHeadSourceRaw >= 0 && receipt.HostOtherGitHubRaw >= 0 &&
        receipt.HostHeadSourceRaw + receipt.HostOtherGitHubRaw ==
            receipt.AuthenticatedRestRequests &&
        receipt.HostHeadSourcePrimary >= 0 &&
        receipt.HostHeadSourceNotModified >= 0 &&
        receipt.HostHeadSourcePrimary + receipt.HostHeadSourceNotModified ==
            receipt.HostHeadSourceRaw &&
        receipt.HostOtherGitHubPrimary >= 0 &&
        receipt.HostOtherGitHubNotModified >= 0 &&
        receipt.HostOtherGitHubPrimary + receipt.HostOtherGitHubNotModified ==
            receipt.HostOtherGitHubRaw &&
        receipt.HostHeadSourcePermission == 0 &&
        receipt.HostHeadSourcePrimaryRateLimited == 0 &&
        receipt.HostHeadSourceSecondaryRateLimited == 0 &&
        receipt.HostHeadSourceCombinedRateLimited == 0 &&
        receipt.HostHeadSourceInvalidRateHeaders == 0 &&
        receipt.HostHeadSourceSecondaryPoints >= receipt.HostHeadSourceRaw &&
        receipt.HostOtherGitHubPermission == 0 &&
        receipt.HostOtherGitHubPrimaryRateLimited == 0 &&
        receipt.HostOtherGitHubSecondaryRateLimited == 0 &&
        receipt.HostOtherGitHubCombinedRateLimited == 0 &&
        receipt.HostOtherGitHubInvalidRateHeaders == 0 &&
        receipt.HostOtherGitHubSecondaryPoints >= receipt.HostOtherGitHubRaw &&
        !receipt.InvalidRemainingHeader &&
        !receipt.TerminalRateLimited &&
        !receipt.LowRemainingGuard &&
        RemainingTailProfileIsExact(receipt) &&
        receipt.AnonymousCodeloadRequests == 1 &&
        receipt.AnonymousCodeloadLimit == 1 &&
        receipt.RejectedRequests == 0;

    private static bool ArtifactRestRequestBudgetReceiptIsExact(
        FrameworkArtifactRestRequestBudgetReceipt receipt,
        string scenario,
        int artifactRestRequests,
        int artifactRestNotModified,
        int artifactRestPrimary,
        int artifactRestSecondaryPoints,
        int anonymousSignedDownloads) =>
        receipt.Kind == "apr-r4-trusted-proof-artifact-rest-budget-v2" &&
        ArtifactRestReceiptIdentityIsExact(receipt, scenario) &&
        receipt.ProtectedRoute &&
        receipt.MaximumTotalAuthenticatedApiRequests == 32 &&
        receipt.TotalAuthenticatedApiRequests >= 0 &&
        receipt.TotalAuthenticatedApiRequests <=
            receipt.MaximumTotalAuthenticatedApiRequests &&
        receipt.MaximumPrimaryRateLimitRequests == 32 &&
        receipt.PrimaryRateLimitRequests >= 0 &&
        receipt.PrimaryRateLimitRequests <=
            receipt.MaximumPrimaryRateLimitRequests &&
        receipt.ConditionalNotModifiedRequests >= 0 &&
        receipt.ConditionalNotModifiedRequests <=
            receipt.TotalAuthenticatedApiRequests &&
        receipt.PrimaryRateLimitRequests ==
            receipt.TotalAuthenticatedApiRequests -
                receipt.ConditionalNotModifiedRequests &&
        receipt.SecondaryLimitPoints >=
            receipt.TotalAuthenticatedApiRequests &&
        receipt.PermissionDenied >= 0 &&
        receipt.PermissionDenied <= receipt.PrimaryRateLimitRequests &&
        receipt.RemainingTotalAuthenticatedApiRequests ==
            receipt.MaximumTotalAuthenticatedApiRequests -
                receipt.TotalAuthenticatedApiRequests &&
        receipt.RemainingPrimaryRateLimitRequests ==
            receipt.MaximumPrimaryRateLimitRequests -
                receipt.PrimaryRateLimitRequests &&
        receipt.Disposition == "active" &&
        artifactRestRequests == receipt.TotalAuthenticatedApiRequests &&
        artifactRestNotModified == receipt.ConditionalNotModifiedRequests &&
        artifactRestPrimary == receipt.PrimaryRateLimitRequests &&
        artifactRestSecondaryPoints == receipt.SecondaryLimitPoints &&
        anonymousSignedDownloads >= 0;

    private static bool ArtifactRestReceiptIdentityIsExact(
        FrameworkArtifactRestRequestBudgetReceipt receipt,
        string scenario) =>
        StringComparer.Ordinal.Equals(receipt.Repository,
            FrameworkCanaries.Repository) &&
        StringComparer.Ordinal.Equals(receipt.RepositoryId,
            FrameworkGitHubHandler.RepositoryId.ToString(
                CultureInfo.InvariantCulture)) &&
        StringComparer.Ordinal.Equals(receipt.WorkflowSha,
            FrameworkGitHubHandler.WorkflowSha) &&
        StringComparer.Ordinal.Equals(receipt.ActionSourceSha,
            FrameworkGitHubHandler.ActionSha) &&
        StringComparer.Ordinal.Equals(receipt.PayloadSha256,
            ReadOptionalText(scenario, "payload-sha256").Trim()) &&
        StringComparer.Ordinal.Equals(receipt.BuildDiscriminator,
            FrameworkCanaries.BuildDiscriminator) &&
        StringComparer.Ordinal.Equals(receipt.RunId,
            ReadLong(scenario, "run-id").ToString(CultureInfo.InvariantCulture)) &&
        StringComparer.Ordinal.Equals(receipt.RunAttempt,
            ReadInt(scenario, "run-attempt").ToString(CultureInfo.InvariantCulture)) &&
        StringComparer.Ordinal.Equals(receipt.CapProfile,
            "apr-r4-artifact-rest-request-budget-v2");

    private static bool ControlRequestBudgetReceiptIsExact(
        FrameworkControlRequestBudgetReceipt receipt) =>
        receipt.Consumed >= 0 && receipt.Consumed <= receipt.Limit &&
        receipt.Limit == 64 &&
        receipt.Primary >= 0 && receipt.NotModified >= 0 &&
        receipt.Primary + receipt.NotModified == receipt.Consumed &&
        receipt.SecondaryPoints >= receipt.Consumed &&
        receipt.MutationCount >= 0 && receipt.MutationCount <= receipt.Consumed &&
        receipt.PermissionDenied == 0 &&
        receipt.PrimaryRateLimited == 0 &&
        receipt.SecondaryRateLimited == 0 &&
        receipt.CombinedRateLimited == 0 &&
        !receipt.InvalidRemainingHeader &&
        RemainingTailProfileIsExact(receipt) &&
        !receipt.RateLimited;

    private static bool RemainingTailProfileIsExact(
        FrameworkRequestBudgetReceipt receipt) => receipt.MeasurementOnly
        ? receipt.RemainingTailReserve ==
            TrustedProofOperationRequestAccounting.OperationPrimaryReserve &&
            receipt.HostHeadSourceRemainingTailRequired == 0 &&
            receipt.HostOtherGitHubRemainingTailRequired == 0
        : FrozenTailReceiptIsExact(TrustedProofRequestDomain.HostHeadSourceRest,
                receipt.HostHeadSourceRemainingTailRequired,
                receipt.RemainingTailReserve) &&
            FrozenTailReceiptIsExact(TrustedProofRequestDomain.HostOtherGitHubRest,
                receipt.HostOtherGitHubRemainingTailRequired,
                receipt.RemainingTailReserve);

    private static bool RemainingTailProfileIsExact(
        FrameworkControlRequestBudgetReceipt receipt) => receipt.MeasurementOnly
        ? receipt.RemainingTailReserve ==
            TrustedProofOperationRequestAccounting.OperationPrimaryReserve &&
            receipt.RemainingTailRequired == 0
        : FrozenTailReceiptIsExact(TrustedProofRequestDomain.TrustedControlRest,
            receipt.RemainingTailRequired, receipt.RemainingTailReserve);

    private static bool FrozenTailReceiptIsExact(
        TrustedProofRequestDomain domain,
        int requiredTail,
        int reserve) => TrustedProofRequestBudgetProfile.TryGetFrozenTailProfile(
            out var frozen, out var frozenReserve) && reserve == frozenReserve &&
        frozen[domain] == requiredTail;

    private static FrameworkRequestBudgetReceipt?
        TryReadTrustedProofRequestBudgetReceipt(string scenario)
    {
        var path = Path.Join(scenario,
            "trusted-proof-github-request-budget.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var receipt = document.RootElement;
            if (!HasExactProperties(receipt,
                    "authenticated_rest_requests", "authenticated_rest_limit",
                    "anonymous_codeload_requests", "anonymous_codeload_limit",
                    "rejected_requests", "measurement_only",
                    "invalid_remaining_header", "terminal_rate_limited",
                    "low_remaining_guard", "remaining_tail_reserve", "host_head_source_rest",
                    "host_other_github_rest") ||
                !HasExactProperties(receipt.GetProperty("host_head_source_rest"),
                    "raw", "primary", "not_modified", "secondary_points", "permission",
                    "primary_rate_limited", "secondary_rate_limited",
                    "combined_rate_limited", "invalid_rate_headers",
                    "remaining_tail_required") ||
                !HasExactProperties(receipt.GetProperty("host_other_github_rest"),
                    "raw", "primary", "not_modified", "secondary_points", "permission",
                    "primary_rate_limited", "secondary_rate_limited",
                    "combined_rate_limited", "invalid_rate_headers",
                    "remaining_tail_required"))
            {
                return null;
            }
            return new FrameworkRequestBudgetReceipt(
                receipt.GetProperty("authenticated_rest_requests").GetInt32(),
                receipt.GetProperty("authenticated_rest_limit").GetInt32(),
                receipt.GetProperty("anonymous_codeload_requests").GetInt32(),
                receipt.GetProperty("anonymous_codeload_limit").GetInt32(),
                receipt.GetProperty("rejected_requests").GetInt32(),
                receipt.GetProperty("measurement_only").GetBoolean(),
                receipt.GetProperty("invalid_remaining_header").GetBoolean(),
                receipt.GetProperty("terminal_rate_limited").GetBoolean(),
                receipt.GetProperty("low_remaining_guard").GetBoolean(),
                receipt.GetProperty("remaining_tail_reserve").GetInt32(),
                receipt.GetProperty("host_head_source_rest").GetProperty("raw").GetInt32(),
                receipt.GetProperty("host_head_source_rest").GetProperty("primary").GetInt32(),
                receipt.GetProperty("host_head_source_rest").GetProperty("not_modified").GetInt32(),
                receipt.GetProperty("host_head_source_rest").GetProperty("secondary_points").GetInt32(),
                receipt.GetProperty("host_head_source_rest").GetProperty("permission").GetInt32(),
                receipt.GetProperty("host_head_source_rest").GetProperty("primary_rate_limited").GetInt32(),
                receipt.GetProperty("host_head_source_rest").GetProperty("secondary_rate_limited").GetInt32(),
                receipt.GetProperty("host_head_source_rest").GetProperty("combined_rate_limited").GetInt32(),
                receipt.GetProperty("host_head_source_rest").GetProperty("invalid_rate_headers").GetInt32(),
                receipt.GetProperty("host_head_source_rest").GetProperty("remaining_tail_required").GetInt32(),
                receipt.GetProperty("host_other_github_rest").GetProperty("raw").GetInt32(),
                receipt.GetProperty("host_other_github_rest").GetProperty("primary").GetInt32(),
                receipt.GetProperty("host_other_github_rest").GetProperty("not_modified").GetInt32(),
                receipt.GetProperty("host_other_github_rest").GetProperty("secondary_points").GetInt32(),
                receipt.GetProperty("host_other_github_rest").GetProperty("permission").GetInt32(),
                receipt.GetProperty("host_other_github_rest").GetProperty("primary_rate_limited").GetInt32(),
                receipt.GetProperty("host_other_github_rest").GetProperty("secondary_rate_limited").GetInt32(),
                receipt.GetProperty("host_other_github_rest").GetProperty("combined_rate_limited").GetInt32(),
                receipt.GetProperty("host_other_github_rest").GetProperty("invalid_rate_headers").GetInt32(),
                receipt.GetProperty("host_other_github_rest").GetProperty("remaining_tail_required").GetInt32());
        }
        catch (Exception error) when (error is JsonException or
            InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return null;
        }
    }

    private static FrameworkArtifactRestRequestBudgetReceipt?
        TryReadArtifactRestRequestBudgetReceipt(string scenario) =>
        TryReadArtifactRestRequestBudgetReceiptFromPath(Path.Join(scenario,
            "trusted-proof-artifact-rest-request-budget.json"));

    private static FrameworkArtifactRestRequestBudgetReceipt?
        TryReadArtifactRestRequestBudgetReceiptFromPath(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var receipt = document.RootElement;
            if (!HasExactProperties(receipt,
                    "kind", "protected_route",
                    "maximum_total_authenticated_api_requests",
                    "total_authenticated_api_requests",
                    "maximum_primary_rate_limit_requests",
                    "primary_rate_limit_requests",
                    "conditional_not_modified_requests",
                    "secondary_limit_points", "permission_denied",
                    "remaining_total_authenticated_api_requests",
                    "remaining_primary_rate_limit_requests", "disposition",
                    "repository", "repository_id", "workflow_sha",
                    "action_source_sha", "payload_sha256",
                    "build_discriminator", "run_id", "run_attempt",
                    "cap_profile", "measurement_only"))
            {
                return null;
            }
            return new FrameworkArtifactRestRequestBudgetReceipt(
                receipt.GetProperty("kind").GetString() ?? string.Empty,
                receipt.GetProperty("protected_route").GetBoolean(),
                receipt.GetProperty(
                    "maximum_total_authenticated_api_requests")
                    .GetInt32(),
                receipt.GetProperty("total_authenticated_api_requests")
                    .GetInt32(),
                receipt.GetProperty("maximum_primary_rate_limit_requests")
                    .GetInt32(),
                receipt.GetProperty("primary_rate_limit_requests").GetInt32(),
                receipt.GetProperty("conditional_not_modified_requests")
                    .GetInt32(),
                receipt.GetProperty("secondary_limit_points").GetInt32(),
                receipt.GetProperty("permission_denied").GetInt32(),
                receipt.GetProperty(
                    "remaining_total_authenticated_api_requests").GetInt32(),
                receipt.GetProperty(
                    "remaining_primary_rate_limit_requests").GetInt32(),
                receipt.GetProperty("disposition").GetString() ?? string.Empty,
                receipt.GetProperty("repository").GetString() ?? string.Empty,
                receipt.GetProperty("repository_id").GetString() ?? string.Empty,
                receipt.GetProperty("workflow_sha").GetString() ?? string.Empty,
                receipt.GetProperty("action_source_sha").GetString() ?? string.Empty,
                receipt.GetProperty("payload_sha256").GetString() ?? string.Empty,
                receipt.GetProperty("build_discriminator").GetString() ?? string.Empty,
                receipt.GetProperty("run_id").GetString() ?? string.Empty,
                receipt.GetProperty("run_attempt").GetString() ?? string.Empty,
                receipt.GetProperty("cap_profile").GetString() ?? string.Empty,
                receipt.GetProperty("measurement_only").GetBoolean());
        }
        catch (Exception error) when (error is JsonException or
            InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return null;
        }
    }

    private static FrameworkControlRequestBudgetReceipt?
        TryReadEmbeddedControlRequestBudgetReceipt(string scenario) =>
        TryReadControlRequestBudgetReceipt(Path.Join(scenario,
            "trusted-proof-embedded-control-request-budget.json"));

    private static IReadOnlyList<FrameworkExternalControlRequestBudgetReceipt>
        ReadExternalControlRequestBudgetReceipts(string scenario) =>
        Directory.EnumerateFiles(scenario,
                "trusted-proof-external-control-*-request-budget.json")
            .Order(StringComparer.Ordinal)
            .Select(path => TryReadControlRequestBudgetReceipt(path) is { } receipt
                ? new FrameworkExternalControlRequestBudgetReceipt(
                    Path.GetFileName(path)["trusted-proof-external-control-".Length..
                        ^"-request-budget.json".Length],
                    receipt.Consumed,
                    receipt.Limit,
                    receipt.RateLimited,
                    receipt.MeasurementOnly,
                    receipt.Primary,
                    receipt.NotModified,
                    receipt.SecondaryPoints,
                    receipt.MutationCount,
                    receipt.RemainingTailRequired,
                    receipt.RemainingTailReserve,
                    receipt.PermissionDenied,
                    receipt.PrimaryRateLimited,
                    receipt.SecondaryRateLimited,
                    receipt.CombinedRateLimited,
                    receipt.InvalidRemainingHeader)
                : null)
            .Where(receipt => receipt is not null)
            .Cast<FrameworkExternalControlRequestBudgetReceipt>()
            .ToArray();

    private static FrameworkControlRequestBudgetReceipt?
        TryReadControlRequestBudgetReceipt(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var receipt = document.RootElement;
            if (!HasExactProperties(receipt,
                    "consumed", "limit", "primary", "not_modified",
                    "secondary_points", "mutation_count", "remaining_tail_required",
                    "remaining_tail_reserve", "permission_denied",
                    "primary_rate_limited", "secondary_rate_limited",
                    "combined_rate_limited", "invalid_remaining_header",
                    "measurement_only", "rate_limited"))
            {
                return null;
            }
            return new FrameworkControlRequestBudgetReceipt(
                receipt.GetProperty("consumed").GetInt32(),
                receipt.GetProperty("limit").GetInt32(),
                receipt.GetProperty("rate_limited").GetBoolean(),
                receipt.GetProperty("primary").GetInt32(),
                receipt.GetProperty("not_modified").GetInt32(),
                receipt.GetProperty("secondary_points").GetInt32(),
                receipt.GetProperty("mutation_count").GetInt32(),
                receipt.GetProperty("remaining_tail_required").GetInt32(),
                receipt.GetProperty("remaining_tail_reserve").GetInt32(),
                receipt.GetProperty("permission_denied").GetInt32(),
                receipt.GetProperty("primary_rate_limited").GetInt32(),
                receipt.GetProperty("secondary_rate_limited").GetInt32(),
                receipt.GetProperty("combined_rate_limited").GetInt32(),
                receipt.GetProperty("invalid_remaining_header").GetBoolean(),
                receipt.GetProperty("measurement_only").GetBoolean());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool HasExactProperties(JsonElement value,
        params string[] properties) => value.ValueKind == JsonValueKind.Object &&
        value.EnumerateObject().Select(property => property.Name)
            .SequenceEqual(properties, StringComparer.Ordinal);

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
        string? RequiredGlobalEvidence = null,
        bool TrustedProofPayload = false,
        string? BarrierBefore = null,
        string? BarrierAfter = null);

    private sealed record CaseResult(
        string Name,
        string ExpectedStatus,
        string? ActualStatus,
        int ExitCode,
        int HostPid,
        int ProviderRequests,
        int ToolSequence,
        int GitHubRequests,
        int ArtifactRestRequests,
        int ArtifactRestNotModified,
        int ArtifactRestPrimary,
        int ArtifactRestSecondaryPoints,
        int AnonymousSignedUploads,
        int AnonymousSignedDownloads,
        int StateOperations,
        int StickyMutations,
        int InlineMutations,
        bool ExactEnvironment,
        bool OutputUnchanged,
        bool ProcessGroupQuiet,
        bool PlatformQuiet,
        bool CanarySafe,
        bool ContinuationObserved,
        bool Passed);

    private sealed record CompiledPayloadIdentity(
        string ProofKind,
        string SourceCommit,
        string SourceTree);

    private sealed record CompiledPayloadSourceExpectation(
        string SourceCommit,
        string SourceTree);

    private sealed record FrameworkRequestBudgetReceipt(
        int AuthenticatedRestRequests,
        int AuthenticatedRestLimit,
        int AnonymousCodeloadRequests,
        int AnonymousCodeloadLimit,
        int RejectedRequests,
        bool MeasurementOnly,
        bool InvalidRemainingHeader,
        bool TerminalRateLimited,
        bool LowRemainingGuard,
        int RemainingTailReserve,
        int HostHeadSourceRaw,
        int HostHeadSourcePrimary,
        int HostHeadSourceNotModified,
        int HostHeadSourceSecondaryPoints,
        int HostHeadSourcePermission,
        int HostHeadSourcePrimaryRateLimited,
        int HostHeadSourceSecondaryRateLimited,
        int HostHeadSourceCombinedRateLimited,
        int HostHeadSourceInvalidRateHeaders,
        int HostHeadSourceRemainingTailRequired,
        int HostOtherGitHubRaw,
        int HostOtherGitHubPrimary,
        int HostOtherGitHubNotModified,
        int HostOtherGitHubSecondaryPoints,
        int HostOtherGitHubPermission,
        int HostOtherGitHubPrimaryRateLimited,
        int HostOtherGitHubSecondaryRateLimited,
        int HostOtherGitHubCombinedRateLimited,
        int HostOtherGitHubInvalidRateHeaders,
        int HostOtherGitHubRemainingTailRequired);

    private sealed record FrameworkArtifactRestRequestBudgetReceipt(
        string Kind,
        bool ProtectedRoute,
        int MaximumTotalAuthenticatedApiRequests,
        int TotalAuthenticatedApiRequests,
        int MaximumPrimaryRateLimitRequests,
        int PrimaryRateLimitRequests,
        int ConditionalNotModifiedRequests,
        int SecondaryLimitPoints,
        int PermissionDenied,
        int RemainingTotalAuthenticatedApiRequests,
        int RemainingPrimaryRateLimitRequests,
        string Disposition,
        string Repository,
        string RepositoryId,
        string WorkflowSha,
        string ActionSourceSha,
        string PayloadSha256,
        string BuildDiscriminator,
        string RunId,
        string RunAttempt,
        string CapProfile,
        bool MeasurementOnly);

    private sealed record FrameworkControlRequestBudgetReceipt(
        int Consumed,
        int Limit,
        bool RateLimited,
        int Primary,
        int NotModified,
        int SecondaryPoints,
        int MutationCount,
        int RemainingTailRequired,
        int RemainingTailReserve,
        int PermissionDenied,
        int PrimaryRateLimited,
        int SecondaryRateLimited,
        int CombinedRateLimited,
        bool InvalidRemainingHeader,
        bool MeasurementOnly);

    private sealed record FrameworkExternalControlRequestBudgetReceipt(
        string Phase,
        int Consumed,
        int Limit,
        bool RateLimited,
        bool MeasurementOnly,
        int Primary,
        int NotModified,
        int SecondaryPoints,
        int MutationCount,
        int RemainingTailRequired,
        int RemainingTailReserve,
        int PermissionDenied,
        int PrimaryRateLimited,
        int SecondaryRateLimited,
        int CombinedRateLimited,
        bool InvalidRemainingHeader);

    internal sealed record OperationRequestEvent(
        string Scenario,
        int ScenarioOrdinal,
        int Ordinal,
        string Domain,
        string Route,
        string Method,
        int SecondaryPoints,
        long MonotonicTimestamp,
        string ResponseClass);

    private sealed record OperationRequestEventMeasurement(
        bool ShapeValid,
        bool NodeWindowValid,
        bool AllWindowValid,
        bool MutationSpacingValid,
        int NodeArtifactRaw,
        int HostHeadRaw,
        int HostOtherRaw,
        int ControlRaw,
        int ResultsRaw,
        int AnonymousRaw,
        IReadOnlyList<OperationRequestEvent> Events,
        IReadOnlyDictionary<string, int> DomainTails,
        string SequenceDigest)
    {
        internal static readonly OperationRequestEventMeasurement Invalid = new(
            false, false, false, false, 0, 0, 0, 0, 0, 0,
            [], new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["node_artifact_rest"] = 0,
                ["host_head_source_rest"] = 0,
                ["host_other_github_rest"] = 0,
                ["trusted_control_rest"] = 0,
            }, "");
    }

    private sealed record ScenarioRequestEventStats(
        int Raw,
        int NotModified,
        int Primary,
        int Points,
        int PermissionDenied,
        int Mutations,
        int SignedUploads,
        int SignedDownloads);

    private sealed class MutableScenarioRequestEventStats
    {
        internal int Raw { get; set; }
        internal int NotModified { get; set; }
        internal int Primary { get; set; }
        internal int Points { get; set; }
        internal int PermissionDenied { get; set; }
        internal int Mutations { get; set; }
        internal int SignedUploads { get; set; }
        internal int SignedDownloads { get; set; }
    }

    private sealed record UnauthorizedNoLaunchProbe(
        bool Passed,
        int PublicPreflightRequests,
        bool AuthorizationHeaderPresent,
        bool WorkflowRunEligible,
        bool WorkflowDispatchEligible,
        int PayloadStarts,
        int WrapperStarts,
        int ProviderStarts,
        int StateStarts,
        int PublisherStarts,
        int CSharpReceiptStarts,
        int NodeReceiptStarts,
        int EmbeddedControlReceiptStarts,
        int ExternalControlReceiptStarts)
    {
        internal static readonly UnauthorizedNoLaunchProbe Failed = new(
            false, 0, false, false, false, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    private sealed record CanaryRoute(
        IReadOnlySet<string> AllowedSinks,
        IReadOnlySet<string> TerminalSinks,
        IReadOnlySet<string> ForbiddenSinks,
        string Cardinality);
}

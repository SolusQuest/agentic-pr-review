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

    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(2);

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
                File.Exists(inventory) && ValidateInventory(repository,
                    inventory),
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
            "continuation-seed", "wrapper_failure",
            CrashAfterProviderCheckpoint: true), root, repository, payload,
            bundle, node, platform).ConfigureAwait(false));
        cases.Add(await RunCaseAsync(new CaseSpec("dispatch-continuation",
            "continuation", "reviewed", ExpectContinuation: true),
            root, repository, payload, bundle, node, platform)
            .ConfigureAwait(false));
        platform.ResetArtifacts();
        cases.Add(await RunCaseAsync(new CaseSpec("workflow-run",
            "workflow-run", "reviewed", WorkflowRun: true),
            root, repository, payload, bundle, node, platform)
            .ConfigureAwait(false));
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
        cases.Add(await RunCaseAsync(new CaseSpec("cancellation",
            "provider-stall", "cancelled", SignalAfterHostStart: true),
            root, repository, payload, bundle, node, platform)
            .ConfigureAwait(false));
        cases.Add(await RunCaseAsync(new CaseSpec("host-crash", "sticky",
            "wrapper_failure", CrashHost: true), root, repository, payload,
            bundle, node, platform).ConfigureAwait(false));

        if (cases.Any(result => !result.Passed) ||
            cases.Select(result => result.HostPid)
                .Where(value => value > 0).Distinct().Count() < 2 ||
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
            schema = "apr.action-host.framework-evidence.v1",
            scenarios = cases.Select(result => new
            {
                name = result.Name,
                status = result.ExpectedStatus,
            }),
            two_fresh_host_processes = true,
            official_upload_download_delete = true,
            exact_child_environment = true,
            output_file_unchanged = true,
            canary_oracle_passed = true,
        };
        var normalizedBytes = JsonSerializer.SerializeToUtf8Bytes(normalized,
            new JsonSerializerOptions { WriteIndented = true });
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
            spec.CrashHost || spec.SignalAfterHostStart
                ? TimeSpan.FromSeconds(20)
                : TimeSpan.FromSeconds(2)).ConfigureAwait(false);

        if (spec.CrashAfterProviderCheckpoint && hostPid > 0)
        {
            var checkpoint = await WaitForFileAsync(
                Path.Join(scenario, "provider-checkpoint-ready"),
                TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (checkpoint) _ = Kill(hostPid, 9);
        }
        else if (spec.SignalAfterHostStart && hostPid > 0)
        {
            _ = Kill(process.Id, 15);
        }
        else if (spec.CrashHost && hostPid > 0)
        {
            _ = Kill(hostPid, 9);
        }

        var exited = await WaitForExitAsync(process, ProcessTimeout)
            .ConfigureAwait(false);
        if (!exited)
        {
            _ = Kill(-process.Id, 9);
            try { await process.WaitForExitAsync().ConfigureAwait(false); }
            catch { }
        }

        var stdout = await standardOutput.ConfigureAwait(false);
        var stderr = await standardError.ConfigureAwait(false);
        var summary = File.Exists(summaryPath)
            ? await File.ReadAllTextAsync(summaryPath).ConfigureAwait(false)
            : "";
        var output = File.Exists(outputPath)
            ? await File.ReadAllTextAsync(outputPath).ConfigureAwait(false)
            : "";
        var status = ParseStatus(summary);
        var sanitized = SanitizePrivateMaskCommands(stdout);
        var noLeak = PublicCanaryOracle(sanitized + stderr + summary);
        var closedEnvironment = hostPid < 1 ||
            File.Exists(Path.Join(scenario, "host-environment.keys"));
        var outputUnchanged = output == FrameworkCanaries.OutputSentinel;
        var groupQuiet = !(spec.CrashHost ||
                spec.CrashAfterProviderCheckpoint) ||
            await WaitForGroupQuietAsync(
                process.Id, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var expected = spec.ExpectedStatus == "wrapper_failure"
            ? process.ExitCode == 1 && summary.Contains(
                "failed safely", StringComparison.Ordinal)
            : status == spec.ExpectedStatus &&
                process.ExitCode == (spec.ExpectedStatus is "reviewed" or
                    "reviewed_with_inline_warnings" or
                    "skipped_untrusted_event" or "skipped_fork" ? 0 : 1);
        var continuation = !spec.ExpectContinuation || File.Exists(
            Path.Join(scenario, "provider-continuation-observed"));
        var sixTools = spec.ExpectContinuation ||
            spec.ExpectedStatus != "reviewed" &&
                spec.ExpectedStatus != "reviewed_with_inline_warnings" ||
            ReadInt(scenario, "provider-sequence") >= 6;
        var passed = exited && expected && noLeak && closedEnvironment &&
            outputUnchanged && groupQuiet && continuation && sixTools &&
            !File.Exists(Path.Join(scenario, "unexpected-github-request"));
        await File.WriteAllTextAsync(Path.Join(scenario, "case-result.txt"),
            passed ? "pass\n" : "fail\n").ConfigureAwait(false);
        return new CaseResult(spec.Name, spec.ExpectedStatus, status,
            process.ExitCode, hostPid, passed);
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
        info.Environment["TMPDIR"] = scenario;
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
        info.Environment["INPUT_STATE-KEY"] = FrameworkCanaries.StateKey;
        info.Environment["INPUT_PREVIOUS-STATE-KEY"] =
            FrameworkCanaries.PreviousStateKey;
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
        "workflow-run" => 902,
        "inline" => 903,
        "inline-warning" => 904,
        "unsupported" => 905,
        "fork" => 906,
        "permission" => 907,
        "wrong-action" => 908,
        "concurrency" => 909,
        "provider-malformed" => 910,
        "public-result" => 911,
        "credentials-missing" => 912,
        "cancellation" => 913,
        "host-crash" => 914,
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
            FrameworkCanaries.SignedUrl,
            FrameworkCanaries.PublicResult,
        ];
        return forbidden.All(canary => !value.Contains(
            canary, StringComparison.Ordinal));
    }

    private static string? ParseStatus(string summary)
    {
        foreach (var line in summary.Replace("\r\n", "\n",
                     StringComparison.Ordinal).Split('\n'))
        {
            if (line.StartsWith("| Status | ", StringComparison.Ordinal) &&
                line.EndsWith(" |", StringComparison.Ordinal))
            {
                return line[11..^2];
            }
        }

        return null;
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
        var entries = document.RootElement.GetProperty("entries");
        var workPackages = entries.EnumerateArray()
            .Select(entry => entry.GetProperty("work_package").GetString())
            .ToArray();
        return workPackages.Length == 12 &&
            workPackages.SequenceEqual(new[]
            {
                "W3", "W4", "W5", "W6", "W7", "W8", "W9", "W10",
                "W11", "W12", "W14", "W15",
            }, StringComparer.Ordinal);
    }

    private static bool ValidateInventory(string repository, string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        if (root.GetProperty("base_sha").GetString() !=
            "e698fb1df6daf49f393e87fac4f00e3a2ec2c716")
        {
            return false;
        }

        var count = 0;
        foreach (var entry in root.GetProperty("files").EnumerateArray())
        {
            var relative = entry.GetProperty("path").GetString();
            var digest = entry.GetProperty("sha256").GetString();
            if (string.IsNullOrEmpty(relative) || string.IsNullOrEmpty(digest))
            {
                return false;
            }

            var full = Path.GetFullPath(Path.Join(repository, relative));
            if (!full.StartsWith(Path.GetFullPath(repository) +
                    Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                !File.Exists(full) || Sha256(full) != digest)
            {
                return false;
            }

            count++;
        }

        return count > 0;
    }

    private static bool ValidateCanaryTable(string path)
    {
        var text = File.ReadAllText(path);
        string[] classes =
        [
            "provider-key", "github-token", "state-key-current",
            "state-key-previous", "actions-runtime-jwt", "signed-url-sig",
            "repository", "reviewed-path", "workflow-source", "prompt",
            "tool-data", "session-plaintext", "artifact-ciphertext",
            "public-result",
        ];
        return classes.All(value => text.Contains(value + "\t",
            StringComparison.Ordinal));
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
        bool CrashHost = false,
        bool CrashAfterProviderCheckpoint = false);

    private sealed record CaseResult(
        string Name,
        string ExpectedStatus,
        string? ActualStatus,
        int ExitCode,
        int HostPid,
        bool Passed);
}

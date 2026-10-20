using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime.ActionHostVerifierFixture;

// Structural Host work only. Failure modes are explicitly authored Host inputs;
// installed Solus tests independently establish actual runtime failure triggers.
internal sealed class FrameworkAuthoredRunnerFactory(string root) : IActionHostProviderRunnerFactory
{
    public IActionHostProviderRunner Create(ActionHostProviderPolicy policy, ActionHostProviderApiKey key,
        ReviewedSnapshot snapshot, TimeProvider timeProvider) => new Runner(this, snapshot);

    private sealed class Runner(FrameworkAuthoredRunnerFactory owner, ReviewedSnapshot snapshot)
        : IActionHostProviderRunner
    {
        private bool used;
        public Task<AgentRunOutcome> RunAsync(AgentRunRequest run, CancellationToken token)
        {
            if (used) throw new InvalidOperationException("authored_framework_work_already_executed");
            used = true;
            return owner.RunWorkAsync(run, new SnapshotToolExecutor(snapshot, new VerifiedReviewedFileAccess()), token);
        }
        public void Dispose() { }
    }

    internal async Task<AgentRunOutcome> RunWorkAsync(AgentRunRequest run, IAgentToolExecutor executor,
        CancellationToken token)
    {
        var mode = File.ReadAllText(Path.Join(root, "mode")).Trim();
        if (mode == "provider-malformed")
        {
            await BeforeStepAsync(mode, new(run.InitialMessages, AgentToolRegistry.Definitions.ToArray(), null,
                ThinkingRequired: true), 1, token).ConfigureAwait(false);
            return AgentRunOutcome.Failure(AgentFailureCodes.ResponseInvalid, 1, 0, [])
                with { Accounting = ProviderAccounting.Aggregate([]) };
        }
        if (mode == "provider-error")
            return AgentRunOutcome.Failure(AgentFailureCodes.ChatFailed, 1, 0, [])
                with { Accounting = ProviderAccounting.Aggregate([]) };
        var observer = new CurrentFrameworkChatObserver(root);
        return await AuthoredReviewWork.ExecuteAsync(run, executor, Steps(mode == "continuation"),
            observations => Terminal(mode, observations), observer.ObserveWork,
            (request, ordinal, stepToken) => BeforeStepAsync(mode, request, ordinal, stepToken), token).ConfigureAwait(false);
    }

    private async Task BeforeStepAsync(string mode, ProjectChatRequest request, int ordinal, CancellationToken token)
    {
        File.WriteAllText(Path.Join(root, "synthetic-chat-call-count"), ordinal.ToString(CultureInfo.InvariantCulture));
        var body = AgentRequestWriter.Write(request);
        try
        {
            FrameworkCanaryCapture.CaptureAll(root, "provider.request", body);
            foreach (var secret in new[] { ("provider-key", FrameworkCanaries.ProviderKey), ("github-token", FrameworkCanaries.GitHubToken),
                ("state-key-current", FrameworkCanaries.StateKey), ("state-key-previous", FrameworkCanaries.PreviousStateKey) })
                if (!FrameworkCanaryCapture.AssertCanaryAbsent(root, secret.Item1, secret.Item2, "provider.request", body))
                    throw new InvalidOperationException("authored_framework_secret_in_request");
            if (Encoding.UTF8.GetString(body).Contains(FrameworkCanaries.Prompt, StringComparison.Ordinal))
                File.WriteAllText(Path.Join(root, "provider-prompt-observed"), "1");
            if (Encoding.UTF8.GetString(body).Contains(FrameworkCanaries.ToolData, StringComparison.Ordinal))
                File.WriteAllText(Path.Join(root, "provider-tool-data-observed"), "1");
            if (FrameworkSessionCapacity.IsMode(mode))
                FrameworkSessionCapacity.ObserveAuthored(root, request, body, ordinal, mode);
        }
        finally { CryptographicOperations.ZeroMemory(body); }
        if (mode == "provider-malformed") return;
        if (mode == "provider-stall") await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
        File.WriteAllText(Path.Join(root, "synthetic-chat-sequence"), ordinal.ToString(CultureInfo.InvariantCulture));
        if (mode == "continuation-seed" && ordinal == 4 && File.Exists(Path.Join(root, "crash-after-provider-checkpoint")))
        {
            File.WriteAllText(Path.Join(root, "provider-checkpoint-ready"), "1");
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
        }
    }

    private byte[] Terminal(string mode, IReadOnlyList<AgentObservation> observations)
    {
        if (mode == "public-result")
        {
            var rejected = Encoding.UTF8.GetBytes("{\"summary\":\"rejected\",\"findings\":[{\"severity\":\"high\",\"title\":\"" + FrameworkCanaries.PublicResult +
                "\",\"message\":\"untrusted\",\"evidence\":[{\"observation_id\":\"untrusted\",\"path\":\"" +
                FrameworkCanaries.ReviewedPath + "\",\"start_line\":1,\"end_line\":1}]}]}");
            FrameworkCanaryCapture.CaptureAll(root, "agent.validation", rejected);
            return rejected;
        }
        AgentFinding[] findings = mode is "inline" or "inline-warning"
            ? [new("high", "Grounded proof finding", "The exact changed line is reviewed.",
                [new(observations[2].ObservationId, FrameworkCanaries.ReviewedPath, 1, 1)])] : [];
        return AgentToolArguments.WriteFinishReview("Bounded review complete.", [.. findings]);
    }

    private static PreparedAgentToolCall[] Steps(bool successor)
    {
        const string path = FrameworkCanaries.ReviewedPath;
        if (!AgentToolArguments.TryListChangedFilesProvider("{}", out var changed) ||
            !AgentToolArguments.TryListFilesProvider("{}", out var files) ||
            !AgentToolArguments.TryReadDiffProvider("{\"path\":\"" + path + "\",\"start_hunk\":1,\"hunk_count\":20}", out var diff) ||
            !AgentToolArguments.TryReadFileProvider("{\"path\":\"" + path + "\",\"start_line\":1,\"line_count\":20}", out var read) ||
            !AgentToolArguments.TrySearchTextProvider("{\"query\":\"" + FrameworkCanaries.ToolData + "\",\"path\":\"" + path + "\"}", out var search))
            throw new InvalidOperationException("authored_framework_arguments_invalid");
        return successor
            ? [new PreparedReadFileCall("continuation-read-file", read!), new PreparedSearchTextCall("continuation-search-text", search!)]
            : [new PreparedListChangedFilesCall("framework-list-changed", changed!), new PreparedListFilesCall("framework-list-files", files!),
                new PreparedReadDiffCall("framework-read-diff", diff!), new PreparedReadFileCall("framework-read-file", read!),
                new PreparedSearchTextCall("framework-search-text", search!)];
    }
}

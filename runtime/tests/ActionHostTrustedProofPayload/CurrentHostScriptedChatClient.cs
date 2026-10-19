using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.Snapshot;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Execution.DeepSeek;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofPayload;

// Authored current-work inputs only. Neither plan imports or reconstructs a
// previous provider conversation. Actual SDK continuity has a separate proof.
internal sealed class CurrentHostScriptedChatClient(DeepSeekAdapterContext context,
    bool successor, ITrustedProofStaleSignal? staleSignal = null) : IProjectChatClient
{
    internal bool IsSuccessor => successor;
    private int sequence;
    private readonly ITrustedProofStaleSignal stale = staleSignal ?? TrustedProofNoStaleSignal.Instance;
    private static readonly (string Id, string Name, string Arguments)[] Fresh =
    [
        ("e2p-list-changed", "list_changed_files", "{\"after\":null}"),
        ("e2p-list-files", "list_files", "{\"prefix\":null,\"after\":null}"),
        ("e2p-read-diff", "read_diff", "{\"path\":\"proof/apr178-path-canary.txt\",\"start_hunk\":1,\"hunk_count\":20}"),
        ("e2p-read-file", "read_file", "{\"path\":\"proof/apr178-path-canary.txt\",\"start_line\":1,\"line_count\":20}"),
        ("e2p-search", "search_text", "{\"query\":\"trusted-proof\",\"path\":\"proof/apr178-path-canary.txt\"}"),
        ("e2p-finish", "finish_review", "{\"summary\":\"Trusted proof complete.\",\"findings\":[]}"),
    ];
    private static readonly (string Id, string Name, string Arguments)[] Successor =
    [
        ("e2p-successor-read-file", "read_file", "{\"path\":\"proof/apr178-path-canary.txt\",\"start_line\":1,\"line_count\":20}"),
        ("e2p-successor-search", "search_text", "{\"query\":\"trusted-proof\",\"path\":\"proof/apr178-path-canary.txt\"}"),
        ("e2p-successor-finish", "finish_review", "{\"summary\":\"Trusted successor complete.\",\"findings\":[]}"),
    ];

    public async Task<ProjectChatResponse> GetResponseAsync(ProjectChatRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var ordinal = Interlocked.Increment(ref sequence);
        var script = successor ? Successor : Fresh;
        if (!context.IsValid || ordinal > script.Length) throw new ProjectChatNormalizationException();
        ValidateCurrentTranscript(request, script, ordinal);
        if (request.Accounting is { } accounting && !accounting.TryBeginDispatch())
            throw new InvalidOperationException("current_host_script_dispatch_refused");
        if (ordinal == 2) await stale.SignalReadyAndWaitForReleaseAsync(token).ConfigureAwait(false);
        var call = script[ordinal - 1];
        var position = request.Messages.Length;
        var reasoning = "current-host-work-" + ordinal;
        request.Accounting?.RecordUsage(ProviderUsageObservation.Create(3, 2, 1, 2, 5));
        return new(new("assistant", [new ProjectReasoningContent(reasoning, "",
            DeepSeekReasoningContinuationCodec.FramingName, null, position, 0),
            new ProjectToolCallContent(call.Id, call.Name,
                call.Name is "list_changed_files" or "list_files" ? "{}" : call.Arguments)]),
            new(3, 2, new(DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model, DeepSeekAdapterContext.Model, 1, 2)),
            CapturedResponseBodyBytes: 0, Continuation: new(context.ProviderId, context.ModelId,
                context.AdapterId, context.SessionId,
                [new(reasoning, "", DeepSeekReasoningContinuationCodec.FramingName, null, position, 0)]));
    }

    private static void ValidateCurrentTranscript(ProjectChatRequest request,
        (string Id, string Name, string Arguments)[] script, int ordinal)
    {
        var protocol = request.Messages.Where(message => message.Contents.Any(part =>
            part is ProjectToolCallContent or ProjectToolResultContent)).ToArray();
        if (protocol.Length != (ordinal - 1) * 2) throw new ProjectChatNormalizationException();
        for (var index = 0; index < ordinal - 1; index++)
        {
            var expected = script[index];
            if (protocol[index * 2] is not { Role: "assistant" } assistant ||
                assistant.Contents.OfType<ProjectToolCallContent>().ToArray() is not [var call] ||
                call.CallId != expected.Id || call.Name != expected.Name || call.ArgumentsJson != expected.Arguments ||
                protocol[index * 2 + 1] is not { Role: "tool", Contents: [ProjectToolResultContent result] } ||
                result.CallId != call.CallId || string.IsNullOrWhiteSpace(result.Result))
                throw new ProjectChatNormalizationException();
        }
    }
}

internal sealed class CurrentHostScriptedRunnerFactory(TrustedProofPayloadRuntimePorts ports,
    ITrustedProofStaleSignal? staleSignal) : IActionHostProviderRunnerFactory, ICurrentHostTestWorkPlanConsumer
{
    private bool? successor;
    public void SelectCurrentWorkPlan(bool successor)
    {
        if (this.successor is not null) throw new InvalidOperationException("current_work_plan_already_selected");
        this.successor = successor;
    }

    public IActionHostProviderRunner Create(ActionHostProviderPolicy policy, ActionHostProviderApiKey key,
        ReviewedSnapshot snapshot, TimeProvider timeProvider)
    {
        if (successor is not { } selected) throw new InvalidOperationException("current_work_plan_not_admitted");
        return new ActionHostHistoricalChatRunnerFactory(context => ports.WrapHistoricalChatClient(
            new CurrentHostScriptedChatClient(context, selected, staleSignal))).Create(policy, key, snapshot, timeProvider);
    }
}

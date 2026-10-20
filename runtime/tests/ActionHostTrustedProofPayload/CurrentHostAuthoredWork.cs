using System.Text;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofPayload;

// A finite fixture workload, independent of model output or retained history.
// Only the APR tools, result admission and terminal validator execute here.
// SDK execution and continuity are qualified by the managed archive proof.
internal sealed class CurrentHostAuthoredWork(bool successor,
    ITrustedProofStaleSignal? staleSignal = null,
    Action<ProjectChatRequest, int, string>? observe = null)
{
    internal Task<AgentRunOutcome> RunAsync(AgentRunRequest run,
        IAgentToolExecutor executor, CancellationToken token) =>
        AuthoredReviewWork.ExecuteAsync(run, executor, PreparedSteps(successor),
            _ => AgentToolArguments.WriteFinishReview(
                successor ? "Trusted successor complete." : "Trusted proof complete.", []), observe,
            (_, ordinal, stepToken) => ordinal == 2
                ? (staleSignal ?? TrustedProofNoStaleSignal.Instance).SignalReadyAndWaitForReleaseAsync(stepToken).AsTask()
                : Task.CompletedTask, token);

    private static PreparedAgentToolCall[] PreparedSteps(bool successor)
    {
        const string path = "proof/apr178-path-canary.txt";
        if (!AgentToolArguments.TryListChangedFilesProvider("{}", out var changed) ||
            !AgentToolArguments.TryListFilesProvider("{}", out var files) ||
            !AgentToolArguments.TryReadDiffProvider(
                "{\"path\":\"" + path + "\",\"start_hunk\":1,\"hunk_count\":20}", out var diff) ||
            !AgentToolArguments.TryReadFileProvider(
                "{\"path\":\"" + path + "\",\"start_line\":1,\"line_count\":20}", out var read) ||
            !AgentToolArguments.TrySearchTextProvider(
                "{\"query\":\"trusted-proof\",\"path\":\"" + path + "\"}", out var search))
            throw new InvalidOperationException("authored_work_arguments_invalid");
        return successor
            ? [new PreparedReadFileCall("e2p-successor-read-file", read!),
                new PreparedSearchTextCall("e2p-successor-search", search!)]
            : [new PreparedListChangedFilesCall("e2p-list-changed", changed!),
                new PreparedListFilesCall("e2p-list-files", files!),
                new PreparedReadDiffCall("e2p-read-diff", diff!),
                new PreparedReadFileCall("e2p-read-file", read!),
                new PreparedSearchTextCall("e2p-search", search!)];
    }
}

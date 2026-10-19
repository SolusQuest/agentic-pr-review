using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime.ActionHostVerifierFixture;

internal static class FrameworkSessionCapacity
{
    internal const string Prefix = "r7-capacity-";
    internal const int Generations = 20;
    internal static readonly string Reasoning = "r7-retained-中文-" + new string('\u0001', 3072);
    internal static bool IsMode(string mode) => mode.StartsWith(Prefix, StringComparison.Ordinal);
    internal static int Ordinal(string mode) => int.Parse(mode[Prefix.Length..], System.Globalization.CultureInfo.InvariantCulture);

    internal static void Observe(string scenarioRoot, ProjectChatRequest request, byte[] logicalTrace, int ordinal, string mode)
    {
        var assistants = request.Messages.Where(message => message.Role == "assistant").ToArray();
        if (assistants.Length != Ordinal(mode) * 6 + ordinal - 1) throw new InvalidOperationException("r7_history_population");
        for (var index = 0; index < assistants.Length; index++)
        {
            var message = assistants[index];
            var calls = message.Contents.OfType<ProjectToolCallContent>().ToArray();
            if (message.Contents.OfType<ProjectReasoningContent>().ToArray() is not [var reasoning] || reasoning.Text != Reasoning ||
                calls is not [var call] || !call.CallId.StartsWith(Prefix + index / 6 + "-", StringComparison.Ordinal))
                throw new InvalidOperationException("r7_history_association");
            if (index % 6 != 5 && request.Messages.SelectMany(part => part.Contents.OfType<ProjectToolResultContent>())
                    .Count(result => result.CallId == call.CallId) != 1) throw new InvalidOperationException("r7_history_tool_result");
        }
        File.WriteAllText(Path.Join(scenarioRoot, "r7-projection.json"), FrameworkJson.Serialize(FrameworkJson.Object(
            ("observation_domain", "historical-typed-logical-input"), ("messages", request.Messages.Length),
            ("reasoning_associations", assistants.Length), ("logical_request_bytes", logicalTrace.Length))));
    }

    internal sealed class ProviderFactory(string scenarioRoot, IActionHostProviderRunnerFactory inner) : IActionHostProviderRunnerFactory
    {
        public IActionHostProviderRunner Create(ActionHostProviderPolicy policy, ActionHostProviderApiKey key,
            ReviewedSnapshot snapshot, TimeProvider timeProvider) => new Runner(scenarioRoot, inner.Create(policy, key, snapshot, timeProvider));
    }

    private sealed class Runner(string scenarioRoot, IActionHostProviderRunner inner) : IActionHostProviderRunner
    {
        public async Task<AgentRunOutcome> RunAsync(AgentRunRequest run, CancellationToken cancellationToken)
        {
            var plan = run.StablePlan;
            // Test-owned scope metadata only. Actual encrypted artifact bytes come from the platform ZIP transport.
            File.WriteAllText(Path.Join(scenarioRoot, "r7-scope.json"), FrameworkJson.Serialize(FrameworkJson.Object(
                ("repository", plan.RepositoryId), ("workflow", plan.WorkflowIdentity), ("review", plan.ReviewTarget),
                ("session", run.SessionId), ("provider", plan.ProviderId), ("model", plan.ModelId), ("adapter", plan.AdapterId),
                ("policy", plan.PolicySha256), ("limits", plan.LimitsSha256), ("tools", plan.ToolsetSha256), ("build", plan.BuildId))));
            if (File.ReadAllText(Path.Join(scenarioRoot, "mode")).Trim() == Prefix + Generations)
            {
                // Current synthetic review context, without changing the restored prefix or production policy.
                run = run with { InitialMessages = [.. run.InitialMessages[..^1],
                    new("user", [new Agent.Chat.ProjectTextContent(new string('x', AgentLimits.ContentBytes + 1))])] };
            }
            var result = await inner.RunAsync(run, cancellationToken);
            File.WriteAllText(Path.Join(scenarioRoot, "r7-agent-code"), result.Diagnostic?.Code ?? "completed");
            return result;
        }
        public void Dispose() => inner.Dispose();
    }
}

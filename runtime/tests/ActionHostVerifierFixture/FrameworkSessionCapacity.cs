using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Host.State.Lineage;

namespace AgenticPrReview.Runtime.ActionHostVerifierFixture;

internal static class FrameworkSessionCapacity
{
    internal const string Prefix = "r7-capacity-";
    internal const int Generations = LineageFormat.MaximumPhysicalPerClass + 1;
    internal static bool IsMode(string mode) => mode.StartsWith(Prefix, StringComparison.Ordinal);
    internal static int Ordinal(string mode) => int.Parse(mode[Prefix.Length..], System.Globalization.CultureInfo.InvariantCulture);

    // Current Host work observation only. The former cumulative native SESSION
    // population/continuation association is not reconstructed here. Real SDK
    // retention/capacity belongs to the separately qualified V3 route.
    internal static void Observe(string scenarioRoot, ProjectChatRequest request, byte[] logicalTrace, int ordinal, string mode)
    {
        if (!File.Exists(Path.Join(scenarioRoot, "s5-current-framework-work")) ||
            !IsMode(mode) || ordinal is < 1 or > 6 || request.Messages.Length != 2 + (ordinal - 1) * 2 ||
            request.Continuation?.Items.Any(item => item.MessagePosition < 2) == true)
            throw new InvalidOperationException("s5_current_host_work_population");
        File.WriteAllText(Path.Join(scenarioRoot, "r7-projection.json"), FrameworkJson.Serialize(FrameworkJson.Object(
            ("observation_domain", CurrentFrameworkChatObserver.Domain),
            ("sdk_context_import_qualified", false), ("messages", request.Messages.Length),
            ("logical_request_bytes", logicalTrace.Length))));
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
            var result = await inner.RunAsync(run, cancellationToken);
            File.WriteAllText(Path.Join(scenarioRoot, "r7-agent-code"), result.Diagnostic?.Code ?? "completed");
            return result;
        }
        public void Dispose() => inner.Dispose();
    }
}

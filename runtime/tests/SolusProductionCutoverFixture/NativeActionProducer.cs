using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Execution.DeepSeek;

namespace AgenticPrReview.Runtime.SolusProductionCutoverFixture;

// Explicit supporting historical producer. The ordinary executable never
// installs this factory; it exists only to make genuine selected-current SESSION.
internal static class NativeActionProducer
{
    internal static Task<int> RunAsync()
    {
        var original = ActionHostCompositionDependencies.Production();
        var dependencies = new ActionHostCompositionDependencies(original.EventReader, original.AuthorizationFactory,
            original.GitObjectFactory, original.SnapshotFactory, original.StateDependencies, original.PublisherFactory,
            new ActionHostDeepSeekProviderRunnerFactory(credential => DeepSeekTransport.CreateForTesting(credential,
                new Handler(), TimeSpan.FromSeconds(30))), original.TimeProvider,
            original.StagingParentFactory, original.InlineHook, original.WorkflowAdmission,
            original.ReviewedHeadSourceFactory, original.ChangedFileSourceFactory);
        var composition = new ActionHostComposition(dependencies);
        return RuntimeApplication.RunActionHostAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(),
            Console.Error, CancellationToken.None, composition.RunAsync);
    }

    private sealed class Handler : HttpMessageHandler
    {
        private int sends;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (++sends > 2) throw new InvalidOperationException("historical_send_bound");
            var name = sends == 1 ? AgentToolRegistry.ReadFileName : AgentToolRegistry.FinishReviewName;
            var arguments = sends == 1 ? "{\"path\":\"Caller.cs\"}" :
                Encoding.UTF8.GetString(AgentToolArguments.WriteFinishReview("Historical source review.", []));
            var message = new JsonObject {
                ["role"] = "assistant", ["content"] = "", ["reasoning_content"] = "Synthetic historical producer.",
                ["tool_calls"] = new JsonArray { new JsonObject {
                    ["id"] = sends == 1 ? "historical_read" : "historical_finish", ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = name, ["arguments"] = arguments } } }
            };
            var response = new JsonObject {
                ["model"] = DeepSeekAdapterContext.Model,
                ["choices"] = new JsonArray { new JsonObject {
                    ["index"] = 0, ["message"] = message, ["finish_reason"] = "tool_calls" } },
                ["usage"] = new JsonObject { ["prompt_tokens"] = 3, ["completion_tokens"] = 2,
                    ["total_tokens"] = 5, ["prompt_cache_hit_tokens"] = 1, ["prompt_cache_miss_tokens"] = 2 }
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(response.ToJsonString(), Encoding.UTF8, "application/json") });
        }
    }
}

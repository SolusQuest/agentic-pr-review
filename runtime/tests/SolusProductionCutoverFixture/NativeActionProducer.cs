using System.Text;
using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Execution.DeepSeek;

namespace AgenticPrReview.Runtime.SolusProductionCutoverFixture;

// Fixed typed calls produce genuine historical SESSION through the real Host,
// loop and writer. This is not an HTTP/provider qualification or public route.
internal static class NativeActionProducer
{
    internal static Task<int> RunAsync()
    {
        var original = ActionHostCompositionDependencies.Production();
        var dependencies = new ActionHostCompositionDependencies(original.EventReader, original.AuthorizationFactory,
            original.GitObjectFactory, original.SnapshotFactory, original.StateDependencies, original.PublisherFactory,
            new ActionHostHistoricalChatRunnerFactory(context => new Script(context)), original.TimeProvider,
            original.StagingParentFactory, original.InlineHook, original.WorkflowAdmission,
            original.ReviewedHeadSourceFactory, original.ChangedFileSourceFactory);
        var composition = new ActionHostComposition(dependencies);
        return RuntimeApplication.RunActionHostAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(),
            Console.Error, CancellationToken.None, composition.RunAsync);
    }

    private sealed class Script(DeepSeekAdapterContext context) : IProjectChatClient
    {
        private int calls;
        public Task<ProjectChatResponse> GetResponseAsync(ProjectChatRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (++calls > 2 || request.Accounting is { } capture && !capture.TryBeginDispatch())
                throw new InvalidOperationException("historical_script_bound");
            request.Accounting?.RecordUsage(ProviderUsageObservation.Create(3, 2, 1, 2, 5));
            var name = calls == 1 ? AgentToolRegistry.ReadFileName : AgentToolRegistry.FinishReviewName;
            var arguments = calls == 1 ? "{\"path\":\"Caller.cs\"}" :
                Encoding.UTF8.GetString(AgentToolArguments.WriteFinishReview("Historical source review.", []));
            var id = calls == 1 ? "historical_read" : "historical_finish";
            const string reasoning = "Synthetic historical producer.";
            var position = request.Messages.Length;
            // Typed fixture: no serialized provider response body; zero is not a wire measurement.
            return Task.FromResult(new ProjectChatResponse(new("assistant", [
                new ProjectReasoningContent(reasoning, "", DeepSeekReasoningContinuationCodec.FramingName, null, position, 0),
                new ProjectToolCallContent(id, name, arguments)]),
                new(3, 2, new(DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model, DeepSeekAdapterContext.Model, 1, 2)),
                CapturedResponseBodyBytes: 0, Continuation: new(context.ProviderId, context.ModelId, context.AdapterId, context.SessionId,
                    [new(reasoning, "", DeepSeekReasoningContinuationCodec.FramingName, null, position, 0)])));
        }
    }
}

using System.Net;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Execution.DeepSeek;

namespace AgenticPrReview.Runtime.AgentLoopAotFixture;

// Historical authored typed retries exercise generic accounting and loop behavior in AOT.
// Actual provider retry and HTTP qualification belongs to the installed Solus fixture.
internal static class ProviderRetryProof
{
    internal static async Task<int> RunAsync(ProofCommand command)
    {
        var trusted = ProofScenario.Trusted() with { ProviderId = DeepSeekAdapterContext.Provider,
            ModelId = DeepSeekAdapterContext.Model, AdapterId = DeepSeekAdapterContext.Adapter };
        Require(AgentStableRequestMaterializer.TryMaterialize(trusted, null, out var materialized), "materialize");
        var plan = materialized!.StablePlan;
        var identity = ProofScenario.BootstrapIdentity();
        var diff = ProofScenario.BootstrapDiffSource(identity);
        var snapshot = new ReviewedSnapshot(identity, ProofPaths.RepositoryRoot(command), [ProofScenario.ReviewedPath],
            [ProofScenario.BootstrapChangedFile(diff)], [diff]);
        var run = new AgentRunRequest(identity, plan, "r7-retry", [.. materialized.ControlMessages,
            ProofScenario.User("Review this synthetic file.")]);
        foreach (long? failedOutput in new long?[] { null, 458_752, 458_753 })
        {
            var sends = 0;
            byte[]? frozen = null;
            using var client = new Script((request, token) =>
            {
                token.ThrowIfCancellationRequested();
                Require(request.Accounting!.TryBeginDispatch(), "dispatch");
                var body = AgentRequestWriter.Write(request);
                frozen ??= body;
                Require(frozen.AsSpan().SequenceEqual(body), "frozen-logical-request");
                Require(request.MaxOutputTokens == 65_536, "frozen-output");
                if (++sends == 1)
                {
                    request.Accounting.RecordUsage(ProviderUsageObservation.Create(null, failedOutput));
                    request.Accounting.MarkFailed();
                    throw new ProjectChatRetryException();
                }
                return Task.FromResult(Success(request, plan, run.SessionId));
            });
            var loop = new AgentLoop(client,
                new SnapshotToolExecutor(snapshot, new VerifiedReviewedFileAccess()), retryRandom: () => 0);
            var outcome = await loop.RunAsync(run, default);
            var success = failedOutput is null or 458_752;
            Require(outcome.CompletedSessionEligible == success && sends == (success ? 2 : 1), "attempt-result");
            Require(success || outcome.Diagnostic?.Code == AgentFailureCodes.TokenLimit, "headroom-refusal");
            Require(outcome.Accounting is { ModelCalls: 1, ProviderFailedAttempts: 1 } accounting &&
                accounting.ProviderAttempts == sends && accounting.ProviderRetries == (success ? 1 : 0) &&
                accounting.OutputTokens == (failedOutput ?? 0) + (success ? 1 : 0) &&
                accounting.UnknownUsageAttempts == 1 && accounting.UsageCompleteness != AccountingCompleteness.Complete,
                "accounting-known-lower-bound-and-unknown-attempt");
            Require(outcome.Events.OfType<AgentTerminalEvent>().Count() == (success ? 1 : 0), "single-terminal");
        }
        foreach (var connectTimeout in new[] { false, true })
        {
            var sends = 0;
            using var client = new Script((_, _) =>
            {
                sends++;
                throw new ProjectChatRetryException(connectTimeout ? TimeSpan.Zero : null);
            });
            var loop = new AgentLoop(client,
                new SnapshotToolExecutor(snapshot, new VerifiedReviewedFileAccess()), retryRandom: () => 0);
            var outcome = await loop.RunAsync(run, default);
            Require(outcome.Diagnostic?.Code == AgentFailureCodes.ChatFailed && sends == 1 &&
                outcome.Accounting is { ProviderAttempts: 0, ProviderRetries: 0, AttemptCompleteness: AccountingCompleteness.Unavailable },
                "unavailable-dispatch-cannot-retry");
        }
        foreach (var elapsedSeconds in new[] { 12, 13 })
        {
            var clock = new SyntheticTimeProvider(ProofScenario.Now);
            var sends = 0;
            using var client = new Script((request, token) =>
            {
                token.ThrowIfCancellationRequested();
                Require(request.Accounting!.TryBeginDispatch(), "dispatch");
                if (++sends > 1) return Task.FromResult(Success(request, plan, run.SessionId));
                clock.Advance(TimeSpan.FromSeconds(elapsedSeconds));
                request.Accounting.MarkFailed();
                throw new ProjectChatRetryException(TimeSpan.Zero);
            });
            var loop = new AgentLoop(client,
                new SnapshotToolExecutor(snapshot, new VerifiedReviewedFileAccess()), clock, retryRandom: () => 0);
            var outcome = await loop.RunAsync(run with { RemainingHostTime = TimeSpan.FromSeconds(17) }, default);
            Require(outcome.CompletedSessionEligible && sends == 2 && outcome.Accounting?.ProviderRetries == 1,
                "elapsed-script-time-preserves-retry-budget");
        }
        ProofFiles.WriteNew(command.Output, """{"schema":"r7-historical-typed-retry-v1","scenarios":7,"observation_domain":"apr-historical-authored-typed-chat-v1","frozen_logical_request":true,"failed_output_boundary":true}"""u8.ToArray());
        Console.WriteLine("APR_R7_PROVIDER_RETRY_OK");
        return 0;
    }

    private static ProjectChatResponse Success(ProjectChatRequest request, StableAgentPlan plan, string session)
    {
        request.Accounting?.RecordUsage(ProviderUsageObservation.Create(1, 1, 0, 1, 2));
        var position = request.Messages.Length;
        return new(new("assistant", [new ProjectReasoningContent("", "", DeepSeekReasoningContinuationCodec.FramingName,
            null, position, 0), new ProjectToolCallContent("finish", "finish_review", "{\"summary\":\"done\",\"findings\":[]}")]),
            new(1, 1, new(plan.ProviderId, plan.ModelId, plan.ModelId, 0, 1)), CapturedResponseBodyBytes: 0,
            Continuation: new(plan.ProviderId, plan.ModelId, plan.AdapterId, session,
                [new("", "", DeepSeekReasoningContinuationCodec.FramingName, null, position, 0)]));
    }
    private static void Require(bool value, string code)
    {
        if (!value) throw new InvalidOperationException("r7-retry-" + code);
    }
    private sealed class Script(Func<ProjectChatRequest, CancellationToken, Task<ProjectChatResponse>> respond) : IProjectChatClient, IDisposable
    {
        public Task<ProjectChatResponse> GetResponseAsync(ProjectChatRequest request, CancellationToken token) => respond(request, token);
        public void Dispose() { }
    }
}

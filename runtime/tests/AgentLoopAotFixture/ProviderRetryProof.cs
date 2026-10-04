using System.Net;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Execution.DeepSeek;

namespace AgenticPrReview.Runtime.AgentLoopAotFixture;

// Synthetic HTTP only: exercise the actual adapter, accounting and loop in AOT.
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
            var handlers = new List<Handler>();
            byte[]? frozen = null;
            using var transport = DeepSeekTransport.CreateForTesting(DeepSeekCredential.Create("synthetic-r7-retry"), () =>
            {
                Require(handlers.All(handler => handler.Disposed), "dispose-before-next-attempt");
                var handler = new Handler(async (request, token) =>
                {
                    Require(request.RequestUri!.AbsoluteUri == DeepSeekTransportPolicy.Endpoint &&
                        request.Headers.GetValues("Authorization").Single() == "Bearer synthetic-r7-retry" &&
                        request.Content!.Headers.ContentType!.MediaType == "application/json", "wire-policy");
                    var body = await request.Content!.ReadAsByteArrayAsync(token);
                    frozen ??= body;
                    Require(frozen.AsSpan().SequenceEqual(body), "frozen-request");
                    using var json = JsonDocument.Parse(body);
                    Require(json.RootElement.GetProperty("max_tokens").GetInt32() == 65_536, "frozen-output");
                    if (++sends == 1)
                        return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent(failedOutput.HasValue
                            ? "{\"usage\":{\"completion_tokens\":" + failedOutput.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}"
                            : "synthetic-unknown-usage") };
                    return new(HttpStatusCode.OK) { Content = new StringContent("""{"model":"deepseek-flash","choices":[{"index":0,"finish_reason":"tool_calls","message":{"role":"assistant","content":null,"reasoning_content":"","tool_calls":[{"id":"finish","type":"function","function":{"name":"finish_review","arguments":"{\"summary\":\"done\",\"findings\":[]}"}}]}}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2,"prompt_cache_hit_tokens":0,"prompt_cache_miss_tokens":1}}""") };
                });
                handlers.Add(handler);
                return handler;
            }, DeepSeekTransportPolicy.ProviderTimeout, TimeProvider.System);
            var loop = new AgentLoop(DeepSeekChatBackend.CreateClient(new(plan.ProviderId, plan.ModelId, plan.AdapterId, run.SessionId), transport),
                new SnapshotToolExecutor(snapshot, new VerifiedReviewedFileAccess()), retryRandom: () => 0);
            var outcome = await loop.RunAsync(run, default);
            var success = failedOutput is null or 458_752;
            Require(outcome.CompletedSessionEligible == success && sends == (success ? 2 : 1), "attempt-result");
            Require(success || outcome.Diagnostic?.Code == AgentFailureCodes.TokenLimit, "headroom-refusal");
            Require(handlers.All(handler => handler.Disposed), "dispose-final");
            Require(outcome.Accounting is { ModelCalls: 1, ProviderFailedAttempts: 1 } accounting &&
                accounting.ProviderAttempts == sends && accounting.ProviderRetries == (success ? 1 : 0) &&
                accounting.OutputTokens == (failedOutput ?? 0) + (success ? 1 : 0) &&
                accounting.UnknownUsageAttempts == 1 && accounting.UsageCompleteness != AccountingCompleteness.Complete,
                "accounting-known-lower-bound-and-unknown-attempt");
            Require(outcome.Events.OfType<AgentTerminalEvent>().Count() == (success ? 1 : 0), "single-terminal");
        }
        ProofFiles.WriteNew(command.Output, """{"schema":"r7-provider-retry-v1","scenarios":3,"frozen_request":true,"fresh_transport":true,"failed_output_boundary":true}"""u8.ToArray());
        Console.WriteLine("APR_R7_PROVIDER_RETRY_OK");
        return 0;
    }

    private static void Require(bool value, string code)
    {
        if (!value) throw new InvalidOperationException("r7-retry-" + code);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal bool Disposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}

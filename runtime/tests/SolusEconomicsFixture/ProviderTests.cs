using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.SolusFreshHostProof;
using SolusAgent.Api.Usage;
using SolusAgent.Providers.DeepSeek;
using SolusAgent.Runtime.Api.Providers;

namespace AgenticPrReview.Runtime.SolusEconomicsFixture;

internal static class ProviderTests
{
    private const string Key = "synthetic-v2-provider-key";
    private static ProviderRequest Request(Guid? execution = null, Guid? logical = null, int attempt = 1,
        ProviderScope? scope = null) => new(scope ?? new("deepseek", "deepseek-flash"),
            new(execution ?? Guid.NewGuid(), logical ?? Guid.NewGuid(), Guid.NewGuid(), attempt),
            [ProviderInput.Instruction("synthetic trusted instruction"), ProviderInput.Data("synthetic input ☃")],
            requiredCapabilities: ProviderCapabilities.Continuation);
    private static object Usage(long input = 11, long output = 7, long hit = 3, long miss = 8) =>
        new { prompt_tokens = input, completion_tokens = output, prompt_cache_hit_tokens = hit, prompt_cache_miss_tokens = miss };
    private static HttpResponseMessage Response(object? usage = null, HttpStatusCode status = HttpStatusCode.OK, bool invalid = false)
    {
        var value = JsonNode.Parse(JsonSerializer.Serialize(new {
            id = ControlledProvider.ProviderRequestId, @object = "chat.completion", model = "deepseek-flash",
            choices = new[] { new { index = 0, finish_reason = "stop", message = new {
                role = "assistant", content = "{\"findings\":[]}", reasoning_content = ControlledProvider.Reasoning } } }, usage,
        }))!;
        if (invalid) value["choices"]![0]!["message"]!["content"] = new JsonObject();
        return new(status) { Content = new StringContent(value.ToJsonString(), Encoding.UTF8, "application/json") };
    }
    private sealed class Handler(Func<string, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        internal int Sends;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Sends++;
            Proof.Require(request.Headers.Authorization?.Parameter == Key && request.RequestUri?.AbsoluteUri ==
                "https://api.deepseek.com/chat/completions", "v2_supplemental_controlled_transport");
            return await reply(await request.Content!.ReadAsStringAsync(token), token);
        }
    }
    internal static async Task Wire()
    {
        var request = Request(); var observation = new WireObservation { Active = request.Attempt }; string body = "";
        using var handler = new Handler((value, _) => { body = value; observation.Capture(value); return Task.FromResult(Response(Usage())); });
        using var provider = new DeepSeekProvider(new(Key, 1024), handler);
        var result = await provider.ExchangeAsync(request);
        var capture = observation.Freeze().Requests.Single();
        Proof.Require(result.Outcome == ProviderOutcome.Succeeded && capture.Whole.Sha256 == ReferenceHash("whole", body) &&
            capture.Whole.Bytes == Encoding.UTF8.GetByteCount(body), "v2_unit_original_wire_bytes");
        using var parsed = JsonDocument.Parse(body);
        Proof.Require(capture.Messages[1].Token.Sha256 == ReferenceHash("message", parsed.RootElement.GetProperty("messages")[1].GetRawText()),
            "v2_unit_original_message_token");
        var changed = JsonNode.Parse(body)!; changed["messages"]![1]!["content"] = "different suffix";
        var second = new WireObservation { Active = request.Attempt }.Capture(changed.ToJsonString());
        Proof.Require(second.Settings == capture.Settings && second.Messages[0] == capture.Messages[0] &&
            second.Messages[1].Token != capture.Messages[1].Token && second.Whole != capture.Whole, "v2_unit_suffix_only");
        changed["max_tokens"] = 1025;
        var third = new WireObservation { Active = request.Attempt }.Capture(changed.ToJsonString());
        Proof.Require(third.Settings != capture.Settings, "v2_unit_settings_changed");

        foreach (var value in new (object? Body, long? Input, long? Output, long? Hit, long? Miss)[] {
            (null, null, null, null, null), (new { prompt_tokens = 11, completion_tokens = 7 }, 11, 7, null, null),
            (new { prompt_tokens = 11, completion_tokens = 7, prompt_cache_hit_tokens = 3 }, 11, 7, 3, null),
            (new { prompt_tokens = 11, completion_tokens = 7, prompt_cache_miss_tokens = 8 }, 11, 7, null, 8),
            (new { completion_tokens = 7, prompt_cache_hit_tokens = 3 }, null, 7, 3, null),
            (new { prompt_tokens = 11, prompt_cache_hit_tokens = 3, prompt_cache_miss_tokens = 8 }, 11, null, 3, 8),
            (new { prompt_tokens = 11, completion_tokens = 7, prompt_tokens_details = new { cached_tokens = 3 }, prompt_cache_miss_tokens = 8 }, 11, 7, 3, 8),
            (Usage(0, 0, 0, 0), 0, 0, 0, 0) })
        {
            using var child = new Handler((_, _) => Task.FromResult(Response(value.Body)));
            using var selected = new DeepSeekProvider(new(Key, 1024), child);
            var actual = await selected.ExchangeAsync(Request());
            var fact = RunFact.From(new(actual.Observation.ExecutionId, UsageInventoryCoverage.Complete, [actual.Observation]));
            Proof.Require(actual.Outcome == ProviderOutcome.Succeeded && actual.Error == ProviderError.None &&
                Evidence.Run(fact) && child.Sends == 1 && fact.Attempts[0].Exposure == DispatchExposure.Dispatched &&
                fact.Attempts[0].InputTokens == value.Input && fact.Attempts[0].OutputTokens == value.Output &&
                SyntheticPrice.Detail(fact.Attempts[0], ProviderTokenCounterKind.CacheRead) == value.Hit &&
                SyntheticPrice.Detail(fact.Attempts[0], ProviderTokenCounterKind.UncachedInput) == value.Miss,
                "v2_unit_actual_nullable_provider");
            if (value.Body is null) Proof.Require(fact.Input.ObservedTokens is null && SyntheticPrice.Create(fact).Observed.Total is null, "v2_unit_actual_missing_usage");
        }
        using var failedHandler = new Handler((_, _) => Task.FromResult(Response(Usage(), invalid: true)));
        using var failed = new DeepSeekProvider(new(Key, 1024), failedHandler);
        var rejected = await failed.ExchangeAsync(Request());
        Proof.Require(rejected.Outcome == ProviderOutcome.Rejected && rejected.Error == ProviderError.InvalidResponse &&
            rejected.Observation.Usage.InputTokens == 11 &&
            rejected.Observation.Usage.OutputTokens == 7, "v2_unit_usage_before_invalid_response");
        using var invalidUsageHandler = new Handler((_, _) => Task.FromResult(Response(Usage(11, 7, 12, 8))));
        using var invalidUsage = new DeepSeekProvider(new(Key, 1024), invalidUsageHandler);
        var invalid = await invalidUsage.ExchangeAsync(Request());
        Proof.Require(invalid.Outcome == ProviderOutcome.Rejected && invalid.Error == ProviderError.InvalidResponse &&
            invalid.Observation.Usage.InputTokens == 11 && invalid.Observation.Usage.OutputTokens == 7 &&
            SyntheticPrice.Detail(AttemptFact.From(invalid.Observation), ProviderTokenCounterKind.CacheRead) is null,
            "v2_unit_invalid_counter_retains_core_usage");
        using var ambiguousHandler = new Handler((_, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "V2_PRIVATE_EXCEPTION_CANARY"));
        using var ambiguous = new DeepSeekProvider(new(Key, 1024), ambiguousHandler);
        var unknown = await ambiguous.ExchangeAsync(Request());
        Proof.Require(unknown.Outcome == ProviderOutcome.Failed && unknown.Error == ProviderError.ProviderFailed &&
            unknown.Observation.Exposure == DispatchExposure.Unknown && unknown.Observation.Usage.InputTokens is null,
            "v2_unit_unknown_dispatch");
        using var noSendHandler = new Handler((_, _) => throw new ProofFailure("v2_no_send_expected"));
        using var noSend = new DeepSeekProvider(new(Key, 1024), noSendHandler);
        var local = await noSend.ExchangeAsync(Request(scope: new("different-provider", "different-model")));
        Proof.Require(local.Outcome == ProviderOutcome.Rejected && local.Error == ProviderError.InvalidAssociation &&
            local.Observation.Exposure == DispatchExposure.NotDispatched && noSendHandler.Sends == 0 &&
            local.Observation.Usage.InputTokens is null, "v2_unit_local_refusal_no_measurement");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var stopped = await noSend.ExchangeAsync(Request(), cancelled.Token);
        Proof.Require(stopped.Outcome == ProviderOutcome.Cancelled && stopped.Error == ProviderError.Cancelled &&
            stopped.Observation.Exposure == DispatchExposure.NotDispatched && noSendHandler.Sends == 0,
            "v2_unit_provider_cancelled_before_acceptance");
    }
    internal static async Task Freeze()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wire = new WireObservation();
        using var handler = new Handler(async (body, token) => {
            started.SetResult(); await release.Task.WaitAsync(token);
            wire.Capture(body); return Response(Usage());
        });
        using var provider = new DeepSeekProvider(new(Key, 1024), handler);
        var observer = new ObservedProvider(provider, wire);
        var pending = observer.ExchangeAsync(Request()).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var frozen = observer.Freeze(); var frozenWire = wire.Freeze();
        release.SetResult(); await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Proof.Require(frozen.Length == 0 && observer.Freeze().Length == 0 && frozenWire.Requests.Length == 0 &&
            wire.Freeze().Requests.Length == 0, "v2_unit_late_observation_cannot_backfill");
    }
    internal static async Task Retry()
    {
        // Supplemental selected-provider test only. Two explicit exchanges do
        // not alter or qualify production's one-attempt runtime policy.
        var count = 0; var execution = Guid.NewGuid(); var logical = Guid.NewGuid();
        using var handler = new Handler((_, _) => Task.FromResult(++count == 1
            ? Response(Usage(3, 2, 1, 2), HttpStatusCode.ServiceUnavailable) : Response(Usage(7, 4, 3, 4))));
        using var provider = new DeepSeekProvider(new(Key, 1024), handler);
        var first = await provider.ExchangeAsync(Request(execution, logical));
        var second = await provider.ExchangeAsync(Request(execution, logical, 2));
        var run = RunFact.From(new(execution, UsageInventoryCoverage.Complete, [first.Observation, second.Observation]));
        Proof.Require(first.Retry is not null && first.Outcome != ProviderOutcome.Succeeded && second.Outcome == ProviderOutcome.Succeeded &&
            handler.Sends == 2 && run.Attempts.Select(a => a.LogicalCallId).Distinct().Count() == 1 &&
            run.Attempts.Select(a => a.PhysicalAttemptId).Distinct().Count() == 2 && run.Attempts[1].AttemptNumber == 2 &&
            run.Input.ObservedTokens == 10 && run.Output.ObservedTokens == 6 && SyntheticPrice.Create(run).Observed.Total == .000058m,
            "v2_unit_actual_supplemental_retry_identity");
        SelfTests.Throws(() => new AgentRunUsage(execution, UsageInventoryCoverage.Complete, [second.Observation]), "v2_unit_missing_retry_ordinal");
    }
    private static string ReferenceHash(string domain, string text)
    {
        var prefix = Encoding.UTF8.GetBytes("apr.r6.solus." + domain + "\0"); var value = Encoding.UTF8.GetBytes(text);
        var original = new byte[prefix.Length + 4 + value.Length]; prefix.CopyTo(original, 0);
        var offset = prefix.Length; original[offset] = (byte)(value.Length >> 24); original[offset + 1] = (byte)(value.Length >> 16);
        original[offset + 2] = (byte)(value.Length >> 8); original[offset + 3] = (byte)value.Length;
        value.CopyTo(original, offset + 4); return Convert.ToHexStringLower(SHA256.HashData(original));
    }
}

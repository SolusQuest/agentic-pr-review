using System.Net;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Execution.Solus;
using SolusAgent.Providers.DeepSeek;
using SolusAgent.Runtime.Api.Providers;

static class ProviderBridgeTests
{
    internal static async Task Run()
    {
        foreach (var name in new[] { "DeepSeekChatBackend", "DeepSeekRequestWriter", "DeepSeekResponseParser",
            "DeepSeekTransport", "IDeepSeekTransport", "DeepSeekContextAdmission", "DeepSeekUsageReader", "DeepSeekTransportResult" })
            F.Check(typeof(SolusDeepSeekAdapter).Assembly.GetType("AgenticPrReview.Runtime.Execution.DeepSeek." + name, false) is null, "retired_provider_absent_" + name);
        var saved = new[] { "GITHUB_TOKEN", "ACTIONS_RUNTIME_TOKEN", "APR_STATE_KEY" }
            .ToDictionary(k => k, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", "GITHUB_SECRET_CANARY");
            Environment.SetEnvironmentVariable("ACTIONS_RUNTIME_TOKEN", "ACTIONS_SECRET_CANARY");
            Environment.SetEnvironmentVariable("APR_STATE_KEY", "STATE_KEY_CANARY");
            await ContinuationAndPersistence();
            await WireBounds();
            await ParserFailures();
        }
        finally { foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value); }
        F.Throws<ArgumentException>(() => new DeepSeekOptions(F.Credential, 1024, new("https://example.invalid/chat")), "alternate_endpoint");
        using var middleware = new Middleware();
        F.Throws<ArgumentException>(() => new DeepSeekProvider(new(F.Credential, 1024), middleware), "middleware_rejected");
    }

    private static async Task ContinuationAndPersistence()
    {
        using var handler = new ControlledHandler((_, _) => Task.FromResult(F.Http(F.Response(F.Usage()))));
        using var provider = F.Provider(handler);
        var execution = Guid.NewGuid();
        var firstRequest = F.Request(execution, bounds: SolusReviewBridge.Bounds());
        var first = await provider.ExchangeAsync(firstRequest);
        F.Check(first.Outcome == ProviderOutcome.Succeeded && first.Response!.Continuation!.ByteCount > 1, "real_continuation");
        var replay = first.Response!.Continuation!;
        ProviderInput[] history = [.. firstRequest.Inputs, ProviderInput.FromModel(first.Response), ProviderInput.Data("next synthetic task")];
        var second = await provider.ExchangeAsync(F.Request(execution, inputs: history, continuation: replay, bounds: SolusReviewBridge.Bounds()));
        F.Check(second.Outcome == ProviderOutcome.Succeeded && handler.Sends == 2, "exact_replay");
        F.Check(handler.Bodies[1].Contains(F.Reasoning), "restricted_replay_only");
        F.Check(!handler.Bodies[0].Contains(F.Reasoning), "fresh_no_replay");
        F.Throws<ProviderContractException>(() => F.Request(execution, inputs: history), "missing_replay");
        var changed = replay.CopyReplayBytes(); changed[^1] ^= 1;
        F.Throws<ProviderContractException>(() => F.Request(execution, inputs: history,
            continuation: new(replay.Scope, replay.Origin, changed)), "changed_replay");
        F.Throws<ProviderContractException>(() => F.Request(execution, inputs: history,
            continuation: new(new("deepseek", "wrong"), replay.Origin, replay.CopyReplayBytes())), "replay_scope");
        F.Throws<ProviderContractException>(() => F.Request(execution, inputs: history,
            continuation: new(replay.Scope, new(execution, Guid.NewGuid(), Guid.NewGuid()), replay.CopyReplayBytes())), "replay_origin");
        F.Throws<ProviderContractException>(() => F.Request(execution, continuation: replay), "orphan_replay");
        F.Check(handler.Sends == 2, "replay_refusals_no_send");
        var binding = new ProviderContextBinding(provider.Scope, firstRequest.Attempt, replay);
        var state = provider.ExportContext(binding)!;
        F.Check(state is not null && provider.AdmitContext(binding, state), "persistence_witness");
        F.Check(!Encoding.UTF8.GetString(state.CopyRestrictedPayload()).Contains(F.Credential), "context_no_credential");
        var bytes = state.CopyRestrictedPayload(); bytes[^1] ^= 1;
        F.Check(!provider.AdmitContext(binding, new(state.Scope, state.Origin, state.FormatVersion, bytes)), "changed_witness");
        using var freshHandler = new ControlledHandler((_, _) => throw new ProofFailure("unexpected_import_send"));
        using var fresh = F.Provider(freshHandler, credential: "replacement-synthetic-key");
        F.Check(fresh.AdmitContext(binding, state) && freshHandler.Sends == 0, "fresh_credential_not_state");
        using var forwardingHandler = new ControlledHandler((_, _) => throw new ProofFailure("unexpected_forwarding_import_send"));
        using var forwarding = new SolusDeepSeekAdapter(AgenticPrReview.Runtime.Execution.DeepSeek.DeepSeekCredential.Create(F.Credential),
            new SolusReviewFacts(Guid.NewGuid(), new(100, 100, 1024)), forwardingHandler);
        F.Check(forwarding.AdmitContext(binding, state) && forwarding.ExportContext(binding)!.CopyRestrictedPayload().SequenceEqual(state.CopyRestrictedPayload()) &&
            forwardingHandler.Sends == 0, "wrapper_persistence_forwarding");
        var ordinary = first + " " + first.Diagnostic + " " + replay + " " + state + " " + provider;
        F.Check(!ordinary.Contains(F.Reasoning) && !ordinary.Contains(F.Credential), "ordinary_privacy");
        using var wrongHandler = new ControlledHandler((_, _) => throw new ProofFailure("unexpected_scope_send"));
        using var wrongProvider = F.Provider(wrongHandler);
        var wrong = new ProviderRequest(new("deepseek", "wrong"), new(execution, Guid.NewGuid(), Guid.NewGuid()), [ProviderInput.Data("x")]);
        F.Check((await wrongProvider.ExchangeAsync(wrong)).Outcome == ProviderOutcome.Rejected && wrongHandler.Sends == 0, "scope_no_send");
    }

    private static async Task WireBounds()
    {
        using var referenceHandler = new ControlledHandler((_, _) => Task.FromResult(F.Http(F.Response(F.Usage()))));
        using var reference = F.Provider(referenceHandler);
        await reference.ExchangeAsync(F.Request(Guid.NewGuid()));
        var wireBytes = Encoding.UTF8.GetByteCount(referenceHandler.Bodies.Single());
        foreach (var exact in new[] { true, false })
        {
            using var handler = new ControlledHandler((_, _) => Task.FromResult(F.Http(F.Response(F.Usage()))));
            using var provider = F.Provider(handler, requestCap: wireBytes - (exact ? 0 : 1));
            var result = await provider.ExchangeAsync(F.Request(Guid.NewGuid()));
            F.Check(exact ? result.Outcome == ProviderOutcome.Succeeded && handler.Sends == 1
                : result.Error == ProviderError.LimitExceeded && handler.Sends == 0, "escaped_wire_boundary");
        }
        var baseRequest = F.Request(Guid.NewGuid());
        _ = F.Request(Guid.NewGuid(), bounds: new(maximumRequestBytes: baseRequest.PayloadByteCount));
        F.Throws<ProviderContractException>(() => F.Request(Guid.NewGuid(), bounds: new(maximumRequestBytes: baseRequest.PayloadByteCount - 1)), "logical_request_boundary");
        foreach (var stream in new[] { false, true })
        foreach (var exact in new[] { true, false })
        {
            var body = F.Response(F.Usage()); var bytes = Encoding.UTF8.GetBytes(body);
            using var handler = new ControlledHandler((_, _) =>
            {
                var response = F.Http(body);
                if (stream)
                {
                    response.Content.Dispose();
                    response.Content = new UnknownLengthContent(bytes);
                    response.Content.Headers.ContentType = new("application/json");
                }
                return Task.FromResult(response);
            });
            using var provider = F.Provider(handler, responseCap: bytes.Length - (exact ? 0 : 1));
            var result = await provider.ExchangeAsync(F.Request(Guid.NewGuid()));
            F.Check(exact ? result.Outcome == ProviderOutcome.Succeeded : result.Error == ProviderError.LimitExceeded, "response_wire_boundary");
        }
        foreach (var exact in new[] { true, false })
        {
            using var handler = new ControlledHandler((_, _) => Task.FromResult(F.Http(F.Response(F.Usage(), reasoning: new('r', exact ? 2047 : 2048)))));
            using var provider = F.Provider(handler);
            var result = await provider.ExchangeAsync(F.Request(Guid.NewGuid(), bounds: SolusReviewBridge.Bounds()));
            F.Check(exact ? result.Response?.Continuation?.ByteCount == 2048 : result.Error == ProviderError.LimitExceeded, "continuation_byte_boundary");
        }
    }

    private static async Task ParserFailures()
    {
        foreach (var body in new[] { "{", F.Response(F.Usage(), model: "wrong"),
            F.Response(F.Usage()).Replace("\"model\":\"deepseek-flash\"", "\"model\":\"deepseek-flash\",\"model\":\"deepseek-flash\""),
            F.Response(F.Usage(hit: 9, miss: 9)) })
        {
            using var handler = new ControlledHandler((_, _) => Task.FromResult(F.Http(body)));
            using var provider = F.Provider(handler);
            var result = await provider.ExchangeAsync(F.Request(Guid.NewGuid()));
            F.Check(result.Outcome == ProviderOutcome.Rejected && result.Response is null, "invalid_response");
            F.Check(!result.Diagnostic.ToString().Contains(F.Reasoning), "failure_diagnostic_privacy");
        }
        using var invalidHandler = new ControlledHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0xff]) };
            response.Content.Headers.ContentType = new("application/json");
            return Task.FromResult(response);
        });
        using var invalid = F.Provider(invalidHandler);
        F.Check((await invalid.ExchangeAsync(F.Request(Guid.NewGuid()))).Outcome == ProviderOutcome.Rejected, "invalid_utf8");
    }
    private sealed class Middleware : DelegatingHandler { }
}

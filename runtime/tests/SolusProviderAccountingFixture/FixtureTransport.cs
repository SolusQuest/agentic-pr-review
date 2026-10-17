using System.Net;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Execution.Solus;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Execution;
using SolusAgent.Providers.DeepSeek;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Tools.Api;

sealed class ProofFailure(string code) : Exception { internal string Code { get; } = code; }

static class F
{
    internal const string Credential = "synthetic-provider-only-key";
    internal const string Reasoning = "SYNTHETIC_RESTRICTED_REASONING_CANARY";
    internal static int Checks, Sends;
    internal static string Stage = "startup";
    internal static void Check(bool value, string code)
    {
        Checks++;
        if (!value) throw new ProofFailure(code);
    }
    internal static void Throws<T>(Action action, string code) where T : Exception
    {
        try { action(); } catch (T) { Checks++; return; }
        throw new ProofFailure(code);
    }
    internal static async Task ThrowsAsync<T>(Func<Task> action, string code) where T : Exception
    {
        try { await action(); } catch (T) { Checks++; return; }
        throw new ProofFailure(code);
    }
    internal static async Task Wait(Task task) => await task.WaitAsync(TimeSpan.FromSeconds(15));
    internal static object Usage(long input = 10, long output = 5, long hit = 4, long miss = 6) => new
    { prompt_tokens = input, completion_tokens = output, prompt_cache_hit_tokens = hit, prompt_cache_miss_tokens = miss };
    internal static object Call(string id = "call-1") => new
    { id, type = "function", function = new { name = "pause", arguments = "{\"wait\":true}" } };
    internal static string Response(object? usage = null, bool tools = false, string reasoning = Reasoning,
        string model = DeepSeekOptions.Model, string text = "{\"findings\":[]}") => JsonSerializer.Serialize(new
        {
            id = "synthetic", @object = "chat.completion", model,
            choices = new[] { new { index = 0, finish_reason = tools ? "tool_calls" : "stop", message = new
            { role = "assistant", content = tools ? null : text, reasoning_content = reasoning, tool_calls = tools ? new[] { Call() } : null } } },
            usage,
        });
    internal static HttpResponseMessage Http(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    internal static ProviderRequest Request(Guid execution, Guid? logical = null, int ordinal = 1,
        IReadOnlyList<ProviderInput>? inputs = null, ProviderContinuation? continuation = null,
        ProviderExchangeBounds? bounds = null) => new(new("deepseek", DeepSeekOptions.Model),
            new(execution, logical ?? Guid.NewGuid(), Guid.NewGuid(), ordinal),
            inputs ?? [ProviderInput.Instruction("trusted policy"), ProviderInput.Data("synthetic input ☃")],
            continuation: continuation, requiredCapabilities: ProviderCapabilities.Continuation, bounds: bounds);
    internal static DeepSeekProvider Provider(HttpMessageHandler handler, int requestCap = 1_048_576,
        int responseCap = 524_288, string credential = Credential, TimeSpan? timeout = null) => new(new(credential, 1024,
            maximumRequestBodyBytes: requestCap, maximumResponseBodyBytes: responseCap, timeout: timeout ?? TimeSpan.FromSeconds(30)), handler);
    internal static SolusReviewBridge Bridge(ControlledHandler handler, ReviewTokenBudget? budget = null,
        ProofHooks? hooks = null) => new(Guid.NewGuid(), DeepSeekCredential.Create(Credential), hooks ?? new(), budget, handler);
    internal static Task<SolusReviewOutcome> Run(SolusReviewBridge bridge, ProofDomain? domain = null,
        IReadOnlyList<SolusAgent.Runtime.Api.Configuration.RuntimeToolRegistration>? tools = null,
        TimeSpan? remaining = null, CancellationToken token = default,
        IReadOnlyList<AgentInput>? data = null) => bridge.RunAsync("trusted policy", data ?? [new(AgentInputSource.Repository, "synthetic input")],
            tools ?? [], domain ?? new(), remaining, token);
}

sealed class ControlledHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> operation) : HttpMessageHandler
{
    internal int Sends { get; private set; }
    // Synthetic restricted wire witnesses only: never printed or persisted.
    internal List<string> Bodies { get; } = [];
    internal TaskCompletionSource Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken token)
    {
        Sends++; F.Sends++;
        F.Check(message.Method == HttpMethod.Post && message.RequestUri!.OriginalString == DeepSeekOptions.OfficialEndpoint, "endpoint");
        F.Check(message.Version == HttpVersion.Version11 && message.VersionPolicy == HttpVersionPolicy.RequestVersionExact, "protocol");
        F.Check(message.Headers.Authorization?.Scheme == "Bearer" && message.Headers.Authorization.Parameter == F.Credential, "credential_destination");
        var body = await message.Content!.ReadAsStringAsync(token);
        Bodies.Add(body);
        F.Check(!body.Contains(F.Credential), "no_credential_body");
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        F.Check(root.GetProperty("model").GetString() == DeepSeekOptions.Model && !root.GetProperty("stream").GetBoolean() &&
            root.GetProperty("thinking").GetProperty("type").GetString() == "enabled" &&
            root.GetProperty("reasoning_effort").GetString() == "high" && root.GetProperty("max_tokens").GetInt32() == 1024, "identity");
        foreach (var canary in new[] { "GITHUB_SECRET_CANARY", "ACTIONS_SECRET_CANARY", "STATE_KEY_CANARY" })
            F.Check(!body.Contains(canary) && !message.Headers.ToString().Contains(canary), "ambient_isolation");
        try { return await operation(message, token); }
        finally { Returned.TrySetResult(); }
    }
}

sealed class UnknownLengthContent(byte[] bytes) : HttpContent
{
    protected override bool TryComputeLength(out long length) { length = 0; return false; }
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
    protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(bytes, false));
}

sealed class ProofHooks : IRuntimeExposureHooks
{
    internal int Before, After;
    internal bool Deny;
    internal string BeforeMode = "permit", SettlementMode = "continue";
    internal List<SolusAttemptFact> Attempts { get; } = [];
    internal TaskCompletionSource ExposureRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource ExposureReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource SettlementRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource SettlementReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ValueTask<ExposureAcknowledgement?> BeforeDispatchAsync(RuntimeExposure exposure, CancellationToken token)
    {
        Before++;
        if (BeforeMode == "held") return new(HeldExposure(exposure));
        ExposureAcknowledgement? receipt = BeforeMode switch
        {
            "missing" => null,
            "failed" => new(exposure, RuntimeHookStatus.Failed),
            "unknown" => new(exposure, RuntimeHookStatus.Unknown),
            "mismatched" => new(new(new("deepseek", "wrong"), exposure.Attempt, exposure.RequiredAcknowledgement, exposure.Accounting),
                RuntimeHookStatus.Acknowledged, ExposureDecision.Permit, exposure.RequiredAcknowledgement),
            "throw" => throw new InvalidOperationException("SYNTHETIC_HOST_FAILURE"),
            _ => new(exposure, RuntimeHookStatus.Acknowledged, Deny ? ExposureDecision.Deny : ExposureDecision.Permit,
                Deny ? null : exposure.RequiredAcknowledgement),
        };
        return ValueTask.FromResult(receipt);
    }
    public ValueTask<SettlementAcknowledgement?> AfterAttemptAsync(RuntimeSettlement settlement, CancellationToken token)
    {
        After++;
        Attempts.Add(new(settlement.Observation, settlement.ProviderOutcome, settlement.Stop));
        if (SettlementMode == "held") return new(HeldSettlement(settlement));
        SettlementAcknowledgement? receipt = SettlementMode switch
        {
            "missing" => null,
            "failed" => new(settlement.Exposure, RuntimeHookStatus.Failed, accounting: settlement.Accounting),
            "unknown" => new(settlement.Exposure, RuntimeHookStatus.Unknown, accounting: settlement.Accounting),
            "mismatched" => new(settlement.Exposure, RuntimeHookStatus.Acknowledged, RuntimeContinuation.Continue),
            "throw" => throw new InvalidOperationException("SYNTHETIC_HOST_FAILURE"),
            "stop" => new(settlement.Exposure, RuntimeHookStatus.Acknowledged, RuntimeContinuation.Stop, settlement.Accounting),
            _ => new(settlement.Exposure, RuntimeHookStatus.Acknowledged, RuntimeContinuation.Continue, settlement.Accounting),
        };
        return ValueTask.FromResult(receipt);
    }
    private async Task<ExposureAcknowledgement?> HeldExposure(RuntimeExposure exposure)
    {
        await ExposureRelease.Task;
        ExposureReturned.TrySetResult();
        return new(exposure, RuntimeHookStatus.Acknowledged, ExposureDecision.Permit, exposure.RequiredAcknowledgement);
    }
    private async Task<SettlementAcknowledgement?> HeldSettlement(RuntimeSettlement settlement)
    {
        await SettlementRelease.Task;
        SettlementReturned.TrySetResult();
        return new(settlement.Exposure, RuntimeHookStatus.Acknowledged, RuntimeContinuation.Continue, settlement.Accounting);
    }
}

sealed class ProofDomain : ICandidateHost
{
    internal int Calls;
    internal bool Accept = true;
    internal string Mode = "decision";
    public ValueTask<CandidateFeedback?> SubmitAsync(CandidateSubmission submission, CancellationToken token = default)
    {
        Calls++;
        CandidateFeedback? receipt = Mode switch
        {
            "missing" => null,
            "failed" => new(submission.ExecutionId, submission.SubmissionId, CandidateAcknowledgement.Failed),
            "unknown" => new(submission.ExecutionId, submission.SubmissionId, CandidateAcknowledgement.Unknown),
            "wrong_execution" => new(Guid.NewGuid(), submission.SubmissionId, CandidateAcknowledgement.Acknowledged, CandidateDecision.Reject, CandidateContinuation.End),
            "wrong_submission" => new(submission.ExecutionId, Guid.NewGuid(), CandidateAcknowledgement.Acknowledged, CandidateDecision.Reject, CandidateContinuation.End),
            "throw" => throw new InvalidOperationException("SYNTHETIC_CANDIDATE_HOST_FAILURE"),
            _ => new(submission.ExecutionId, submission.SubmissionId, CandidateAcknowledgement.Acknowledged,
                Accept ? CandidateDecision.Accept : CandidateDecision.Reject, CandidateContinuation.End),
        };
        return ValueTask.FromResult(receipt);
    }
}

sealed class PauseCapability(Func<CancellationToken, Task> operation) : IToolCapability
{
    public string CapabilityId => "pause";
    internal int Entries;
    internal async Task Invoke(CancellationToken token) { Entries++; await operation(token); }
}
sealed class PauseTool() : FunctionTool<PauseCapability>(new("pause", "Synthetic bounded pause",
    ToolSchema.Parse("{\"type\":\"object\",\"properties\":{\"wait\":{\"type\":\"boolean\"}},\"required\":[\"wait\"],\"additionalProperties\":false}"),
    ToolSchema.Parse("{\"type\":\"object\",\"properties\":{\"ok\":{\"type\":\"boolean\"}},\"required\":[\"ok\"],\"additionalProperties\":false}"),
    "pause", ToolEffect.ReadOnly, 2048, 4096))
{
    protected override async ValueTask<ToolOutput> InvokeCoreAsync(ToolCall call, PauseCapability capability, CancellationToken token)
    {
        await capability.Invoke(token);
        return ToolOutput.Success(call, "{\"ok\":true}");
    }
}

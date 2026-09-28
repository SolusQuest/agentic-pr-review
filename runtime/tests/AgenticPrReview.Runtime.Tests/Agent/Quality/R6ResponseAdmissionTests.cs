using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Canonical;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;

namespace AgenticPrReview.Runtime.Tests.Agent.Quality;

public sealed class R6ResponseAdmissionTests
{
    private const string Canary = "APR303_PRIVATE_RESPONSE_CANARY";

    [Fact]
    public async Task InboundListFilesArgumentsReachAdmissionWithoutPathRewriting()
    {
        const string arguments = "{ \"prefix\" : \"../APR303_PRIVATE_RESPONSE_CANARY\" }";
        var raw = "{\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\"," +
            "\"content\":\"\",\"reasoning_content\":\"reasoning\",\"tool_calls\":[{" +
            "\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"list_files\"," +
            "\"arguments\":" + JsonSerializer.Serialize(arguments) +
            "}}]},\"finish_reason\":\"tool_calls\"}],\"model\":\"deepseek-v4-flash\"," +
            "\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":2,\"total_tokens\":5," +
            "\"prompt_cache_hit_tokens\":1,\"prompt_cache_miss_tokens\":2}}";
        var response = DeepSeekTransportResult.Success(Encoding.UTF8.GetBytes(raw));
        var parsed = DeepSeekResponseParser.Parse(response);
        Assert.Equal(DeepSeekResponseParseOutcome.Success, parsed.Outcome);
        Assert.Equal(arguments, Assert.IsType<DeepSeekParsedToolResponse>(parsed.Response)
            .Calls.Single().Arguments);

        var transport = new FixedTransport(response);
        var backend = DeepSeekChatBackend.CreateClient(Context(), transport);
        var capturing = new CapturingClient(backend);
        var executor = new UnexpectedExecutor();
        var outcome = await new AgentLoop(capturing, executor).RunAsync(Request(), CancellationToken.None);
        var toolCall = Assert.Single(Assert.IsType<ProjectChatResponse>(capturing.Response)
            .Message!.Contents.OfType<ProjectToolCallContent>());
        Assert.Equal(arguments, toolCall.ArgumentsJson);
        Assert.False(AgentToolArguments.TryListFilesProvider(toolCall.ArgumentsJson,
            out _, out var failure, out var detail));
        Assert.Equal(ListFilesArgumentFailure.Path, failure);
        Assert.Equal(new ListFilesPathRejection(ListFilesPathField.Prefix,
            RepositoryPathFailure.DotSegment), detail);
        Assert.False(outcome.Succeeded);
        // The fixed transport repeats the same call ID after feedback; the
        // second response therefore fails ID admission, with no dispatch.
        Assert.Equal(AgentFailureCodes.ResponseInvalid,
            outcome.Diagnostic?.Code);
        Assert.Equal(0, outcome.Diagnostic?.ToolCalls);
        Assert.Equal(0, executor.Calls);
        Assert.Equal(2, transport.Sends);
        var recovery = Assert.Single(outcome.Events
            .OfType<AgentRecoveryToolCallEvent>());
        Assert.Equal(AgentRecoveryFeedback.RejectedArguments,
            Encoding.UTF8.GetString(recovery.SanitizedArguments.AsSpan()));
    }

    [Fact]
    public async Task SuccessfulTransportWithInvalidUsageKeepsUnknownAccountingAndSafeReason()
    {
        var raw = "{\"choices\":[{}],\"model\":\"deepseek-v4-flash\"," +
            "\"usage\":{\"" + Canary + "\":1}}";
        var transport = new FixedTransport(DeepSeekTransportResult.Success(Encoding.UTF8.GetBytes(raw)));
        var accounting = Accounting();
        using var metered = new LiveMeteredTransport(transport, accounting);
        var backend = DeepSeekChatBackend.CreateClient(Context(), metered);
        var observed = new LiveChatObserver(backend, accounting);
        var executor = new UnexpectedExecutor();

        var outcome = await new AgentLoop(observed, executor).RunAsync(Request(), CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(AgentFailureCodes.ResponseInvalid, outcome.Diagnostic?.Code);
        Assert.Equal(1, outcome.Diagnostic?.ModelCalls);
        Assert.Equal(0, outcome.Diagnostic?.ToolCalls);
        Assert.Equal(1, transport.Sends);
        Assert.Equal(1, accounting.Sends);
        Assert.Equal(1, accounting.UsageUnknownCalls);
        Assert.Equal(0, accounting.KnownInputTokens);
        Assert.Equal(0, accounting.KnownOutputTokens);
        Assert.Equal(1, accounting.Outcomes.NormalizationExceptions);
        Assert.Equal(0, executor.Calls);
        var diagnostic = LiveAgentDiagnostic.Capture(0, outcome.Diagnostic,
            normalizationReason: observed.TakeNormalizationReason());
        Assert.Equal(LiveNormalizationCategories.ProviderUsage, diagnostic.Category);
        Assert.Null(diagnostic.Tool);
        Assert.True(diagnostic.IsCanonical());
        Assert.DoesNotContain(Canary,
            JsonSerializer.Serialize(diagnostic, LiveJsonContext.Default.LiveAgentDiagnostic),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("length", LiveNormalizationCategories.ProviderFinishReasonLength)]
    [InlineData("content_filter", LiveNormalizationCategories.ProviderFinishReasonContentFilter)]
    [InlineData("insufficient_system_resource", LiveNormalizationCategories.ProviderFinishReasonResource)]
    [InlineData("aborted", LiveNormalizationCategories.ProviderFinishReasonAborted)]
    [InlineData(Canary, LiveNormalizationCategories.ProviderFinishReasonOther)]
    public async Task RejectedFinishReasonCrossesAgentAndReportWithoutProviderText(
        string finishReason, string expectedCategory)
    {
        var raw = "{\"choices\":[{\"index\":0,\"message\":{},\"finish_reason\":\"" +
            finishReason + "\"}],\"model\":\"deepseek-v4-flash\",\"usage\":" +
            "{\"prompt_tokens\":3,\"completion_tokens\":2,\"total_tokens\":5," +
            "\"prompt_cache_hit_tokens\":1,\"prompt_cache_miss_tokens\":2}}";
        var transport = new FixedTransport(DeepSeekTransportResult.Success(Encoding.UTF8.GetBytes(raw)));
        var accounting = Accounting();
        using var metered = new LiveMeteredTransport(transport, accounting);
        var backend = DeepSeekChatBackend.CreateClient(Context(), metered);
        var observed = new LiveChatObserver(backend, accounting);

        var outcome = await new AgentLoop(observed, new UnexpectedExecutor())
            .RunAsync(Request(), CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(AgentFailureCodes.ResponseInvalid, outcome.Diagnostic?.Code);
        Assert.Equal(1, transport.Sends);
        Assert.Equal(1, accounting.UsageUnknownCalls);
        Assert.Equal(1, accounting.Outcomes.NormalizationExceptions);
        var diagnostic = LiveAgentDiagnostic.Capture(0, outcome.Diagnostic,
            normalizationReason: observed.TakeNormalizationReason());
        Assert.Equal(expectedCategory, diagnostic.Category);
        Assert.True(diagnostic.IsCanonical());
        var json = JsonSerializer.Serialize(diagnostic, LiveJsonContext.Default.LiveAgentDiagnostic);
        Assert.DoesNotContain(Canary, json, StringComparison.Ordinal);
        Assert.False((diagnostic with { Code = AgentFailureCodes.ChatFailed }).IsCanonical());
    }

    [Fact]
    public async Task SuccessfulBackendWithInvalidProjectProjectionHasDistinctReason()
    {
        var accounting = Accounting();
        var backend = new InvalidProjectBackend();
        var observed = new LiveChatObserver(new MinimalChatClient(backend), accounting);
        await Assert.ThrowsAsync<ProjectChatNormalizationException>(() =>
            observed.GetResponseAsync(new([], [], null), CancellationToken.None));
        Assert.Equal(1, backend.Calls);
        Assert.Equal(1, accounting.UsageUnknownCalls);
        Assert.Equal(1, accounting.Outcomes.NormalizationExceptions);
        Assert.Equal(ProjectChatNormalizationReason.ResponseProjection,
            observed.TakeNormalizationReason());
    }

    [Fact]
    public void ReportCanonicalizationRejectsForgedResponseCategories()
    {
        var valid = LiveAgentDiagnostic.Capture(0,
            new AgentDiagnostic(AgentFailureCodes.ResponseInvalid, 1, 0),
            normalizationReason: ProjectChatNormalizationReason.ProviderMessage);
        Assert.True(valid.IsCanonical());
        Assert.False((valid with { Category = Canary }).IsCanonical());
        Assert.False((valid with { Tool = Canary }).IsCanonical());
        Assert.False((valid with { Code = AgentFailureCodes.ChatFailed }).IsCanonical());
    }

    private static AgentRunRequest Request()
    {
        var identity = new ReviewedIdentity("repo", 1, new string('0', 40), new string('1', 40));
        return new AgentRunRequest(identity,
            new StableAgentPlan(identity.RepositoryId, identity.ReviewTarget,
                "workflow", new string('2', 64),
                AgentCanonical.ToolsetSha256(AgentToolRegistry.Definitions),
                AgentCanonical.LimitsSha256(), "build",
                DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model,
                DeepSeekAdapterContext.Adapter, null),
            "session_0",
            [new ProjectChatMessage("user", [new ProjectTextContent("review")])]);
    }

    private static DeepSeekAdapterContext Context() => new(
        DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model,
        DeepSeekAdapterContext.Adapter, "session_0");

    private static LiveAccounting Accounting() => new(
        new(4, 8, 262144, 32768, 294912, 120, 100000, new(65536, 4096, 1000)));

    private sealed class FixedTransport(DeepSeekTransportResult result) : IDeepSeekTransport
    {
        internal int Sends { get; private set; }
        public Task<DeepSeekTransportResult> SendAsync(ReadOnlyMemory<byte> requestBody,
            CancellationToken cancellationToken)
        {
            Sends++;
            return Task.FromResult(result);
        }
        public void Dispose() { }
    }

    private sealed class CapturingClient(IProjectChatClient inner) : IProjectChatClient
    {
        internal ProjectChatResponse? Response { get; private set; }
        public async Task<ProjectChatResponse> GetResponseAsync(ProjectChatRequest request,
            CancellationToken cancellationToken)
        {
            Response = await inner.GetResponseAsync(request, cancellationToken);
            return Response;
        }
    }

    private sealed class UnexpectedExecutor : IAgentToolExecutor
    {
        internal int Calls { get; private set; }
        public string? Preflight(PreparedAgentToolCall call)
        {
            Calls++;
            throw new InvalidOperationException("Unexpected preflight.");
        }
        public ValueTask<AgentToolExecution> ExecuteAsync(PreparedAgentToolCall call,
            CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("Unexpected execution.");
        }
    }

    private sealed class InvalidProjectBackend : IMinimalChatBackend
    {
        internal int Calls { get; private set; }
        public Task<MinimalChatResponse> GetResponseAsync(MinimalChatRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new MinimalChatResponse(null!, new MinimalChatUsage(3, 2)));
        }
    }
}

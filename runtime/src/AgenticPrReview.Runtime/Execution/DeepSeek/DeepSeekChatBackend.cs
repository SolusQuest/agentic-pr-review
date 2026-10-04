using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using System.Security.Cryptography;
using System.Text;

namespace AgenticPrReview.Runtime.Execution.DeepSeek;

internal enum DeepSeekRequestProfile
{
    Current = 0,
    Output8192 = 1,
    Output65536 = 2,
}

internal sealed class DeepSeekAdapterContext(
    string providerId,
    string modelId,
    string adapterId,
    string sessionId)
{
    internal const string Provider = "deepseek";
    internal const string Model = "deepseek-flash";
    internal const string RetainedAdapterDescriptor =
        "{\"schema_version\":1,\"provider\":\"deepseek\",\"model\":" +
        "\"deepseek-flash\",\"endpoint\":" +
        "\"https://api.deepseek.com/chat/completions\",\"stream\":false," +
        "\"thinking\":\"enabled\",\"reasoning_effort\":\"high\"," +
        "\"max_tokens\":4096,\"tool_choice\":\"omitted\"," +
        "\"request_cap_bytes\":8388608,\"response_cap_bytes\":2097152," +
        "\"context_policy\":\"dsv41-utf8-upper-v1\",\"context_cap_tokens\":1000000," +
        "\"content_rule\":\"zero-or-one-exact\",\"response_rule\":" +
        "\"reasoning,text-if-nonempty,calls\",\"codec_id\":" +
        "\"deepseek-reasoning-content\",\"codec_discriminator\":" +
        "\"deepseek-flash-thinking-v1\",\"encoding\":\"utf8\"," +
        "\"framing\":\"deepseek.reasoning_content.utf8.v1\"}";
    internal static string AdapterDescriptor { get; } = RetainedAdapterDescriptor.Replace(
        "\"max_tokens\":4096,", "\"max_tokens\":65536,\"max_tokens_policy\":\"remaining-known-output-v1\",", StringComparison.Ordinal);
    internal static string Adapter { get; } = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(AdapterDescriptor))).ToLowerInvariant();
    internal static string CandidateAdapterDescriptor { get; } = RetainedAdapterDescriptor.Replace(
        "\"max_tokens\":4096,", "\"max_tokens\":8192,", StringComparison.Ordinal);
    internal static string CandidateAdapter { get; } = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(CandidateAdapterDescriptor))).ToLowerInvariant();
    internal static string Output65536AdapterDescriptor { get; } = RetainedAdapterDescriptor.Replace(
        "\"max_tokens\":4096,", "\"max_tokens\":65536,", StringComparison.Ordinal);
    internal static string Output65536Adapter { get; } = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Output65536AdapterDescriptor))).ToLowerInvariant();

    internal static bool TryResolveProfile(string? adapterId, out DeepSeekRequestProfile profile)
    {
        profile = DeepSeekRequestProfile.Current;
        if (StringComparer.Ordinal.Equals(adapterId, Adapter)) return true;
        if (StringComparer.Ordinal.Equals(adapterId, CandidateAdapter))
        {
            profile = DeepSeekRequestProfile.Output8192;
            return true;
        }
        if (StringComparer.Ordinal.Equals(adapterId, Output65536Adapter))
        {
            profile = DeepSeekRequestProfile.Output65536;
            return true;
        }
        return false;
    }

    internal static string AdapterFor(DeepSeekRequestProfile profile) => profile switch
    {
        DeepSeekRequestProfile.Current => Adapter,
        DeepSeekRequestProfile.Output8192 => CandidateAdapter,
        DeepSeekRequestProfile.Output65536 => Output65536Adapter,
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };

    internal static AgentLimitAuthority LimitAuthorityFor(DeepSeekRequestProfile profile) => profile switch
    {
        DeepSeekRequestProfile.Current => new(Adapter, AgentLimitProfile.Current),
        DeepSeekRequestProfile.Output8192 => new(CandidateAdapter, AgentLimitProfile.Output8192),
        DeepSeekRequestProfile.Output65536 => new(Output65536Adapter, AgentLimitProfile.Output65536),
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };

    internal string ProviderId { get; } = providerId;
    internal string ModelId { get; } = modelId;
    internal string AdapterId { get; } = adapterId;
    internal string SessionId { get; } = sessionId;

    internal bool IsValid =>
        StringComparer.Ordinal.Equals(ProviderId, Provider) &&
        StringComparer.Ordinal.Equals(ModelId, Model) &&
        TryResolveProfile(AdapterId, out _) &&
        AgentValueDomains.IsIdentifier(SessionId);

    internal bool TryProfile(out DeepSeekRequestProfile profile)
    {
        profile = DeepSeekRequestProfile.Current;
        return IsValid && TryResolveProfile(AdapterId, out profile);
    }

    public override string ToString() => "deepseek_adapter_context";
}

internal sealed class DeepSeekChatBackend(
    DeepSeekAdapterContext context,
    IDeepSeekTransport transport) : IMinimalChatBackend
{
    private const string ResponseTooLargeText =
        "provider response omitted: byte cap exceeded";

    internal static IProjectChatClient CreateClient(
        DeepSeekAdapterContext context,
        IDeepSeekTransport transport) =>
        new MinimalChatClient(new DeepSeekChatBackend(context, transport));

    public async Task<MinimalChatResponse> GetResponseAsync(
        MinimalChatRequest request,
        CancellationToken cancellationToken)
    {
        request?.Accounting?.ObserveNoDispatch();
        cancellationToken.ThrowIfCancellationRequested();
        if (request is null || context is null ||
            transport is null ||
            !context.IsValid ||
            !ValidReplay(request))
        {
            throw new ProjectChatNormalizationException(
                AgentFailureCodes.ResponseInvalid,
                ProjectChatNormalizationReason.RequestProjection);
        }

        if (!context.TryProfile(out var profile))
        {
            throw new ProjectChatNormalizationException(
                AgentFailureCodes.ResponseInvalid,
                ProjectChatNormalizationReason.RequestProjection);
        }
        var projection = DeepSeekRequestWriter.Write(request, profile);
        if (projection.Outcome != DeepSeekRequestWriteOutcome.Success ||
            !projection.HasBody)
        {
            throw new ProjectChatNormalizationException(
                AgentFailureCodes.ResponseInvalid,
                ProjectChatNormalizationReason.RequestProjection);
        }

        if (!DeepSeekContextAdmission.TryEstimate(projection.Body.AsSpan(), out var inputUpperBound) ||
            !DeepSeekContextAdmission.Allows(inputUpperBound, request.MaxOutputTokens ?? DeepSeekRequestWriter.MaxTokensFor(profile)))
        {
            throw new ProjectChatNormalizationException(
                AgentFailureCodes.ContextLimit,
                ProjectChatNormalizationReason.RequestProjection);
        }

        DeepSeekTransportResult transportResult;
        if (transport is IAccountedDeepSeekTransport accounted)
        {
            transportResult = await accounted.SendAsync(
                projection.Body.ToArray(), cancellationToken, request.Accounting);
        }
        else
        {
            request.Accounting?.ObserveUnavailableDispatch();
            transportResult = await transport.SendAsync(projection.Body.ToArray(), cancellationToken);
        }
        var parsed = DeepSeekResponseParser.Parse(transportResult, request.Accounting);
        request.Accounting?.RecordUsage(parsed.AccountingUsage);
        if (transportResult?.Outcome == DeepSeekTransportOutcome.ResponseTooLarge)
            request.Accounting?.MarkFailed();
        cancellationToken.ThrowIfCancellationRequested();
        if (transportResult is null)
        {
            throw new ProjectChatNormalizationException(
                AgentFailureCodes.ResponseInvalid,
                ProjectChatNormalizationReason.TransportContract);
        }

        if (transportResult.RetryEligible)
            throw new ProjectChatRetryException(transportResult.RetryAfter);

        return transportResult.Outcome switch
        {
            DeepSeekTransportOutcome.RequestRejected =>
                throw new ProjectChatNormalizationException(
                    AgentFailureCodes.ResponseInvalid,
                    ProjectChatNormalizationReason.TransportContract),
            DeepSeekTransportOutcome.Success => Parse(parsed, request),
            DeepSeekTransportOutcome.ResponseTooLarge =>
                ResponseTooLarge(request.Messages.Length),
            DeepSeekTransportOutcome.HttpFailure or
            DeepSeekTransportOutcome.ConnectTimeout or
            DeepSeekTransportOutcome.ProviderTimeout or
            DeepSeekTransportOutcome.TransportFailure =>
                throw new DeepSeekChatBackendException(),
            _ => throw new ProjectChatNormalizationException(
                AgentFailureCodes.ResponseInvalid,
                ProjectChatNormalizationReason.TransportContract),
        };
    }

    private MinimalChatResponse Parse(
        DeepSeekResponseParseResult parsed,
        MinimalChatRequest request)
    {
        if (parsed.Outcome == DeepSeekResponseParseOutcome.MissingTool)
        {
            throw new ProjectChatNormalizationException(
                AgentFailureCodes.MissingTool);
        }

        if (parsed.Outcome != DeepSeekResponseParseOutcome.Success ||
            parsed.Response is not { } response)
        {
            throw new ProjectChatNormalizationException(
                AgentFailureCodes.ResponseInvalid,
                parsed.InvalidCategory switch
                {
                    DeepSeekResponseInvalidCategory.TransportContract =>
                        ProjectChatNormalizationReason.TransportContract,
                    DeepSeekResponseInvalidCategory.Json =>
                        ProjectChatNormalizationReason.ProviderJson,
                    DeepSeekResponseInvalidCategory.Root =>
                        ProjectChatNormalizationReason.ProviderRoot,
                    DeepSeekResponseInvalidCategory.Usage =>
                        ProjectChatNormalizationReason.ProviderUsage,
                    DeepSeekResponseInvalidCategory.Choice =>
                        ProjectChatNormalizationReason.ProviderChoice,
                    DeepSeekResponseInvalidCategory.ChoiceShape =>
                        ProjectChatNormalizationReason.ProviderChoiceShape,
                    DeepSeekResponseInvalidCategory.ChoiceIndex =>
                        ProjectChatNormalizationReason.ProviderChoiceIndex,
                    DeepSeekResponseInvalidCategory.ChoiceLogprobs =>
                        ProjectChatNormalizationReason.ProviderChoiceLogprobs,
                    DeepSeekResponseInvalidCategory.ChoiceMessageMissing =>
                        ProjectChatNormalizationReason.ProviderChoiceMessageMissing,
                    DeepSeekResponseInvalidCategory.FinishReasonLength =>
                        ProjectChatNormalizationReason.ProviderFinishReasonLength,
                    DeepSeekResponseInvalidCategory.FinishReasonContentFilter =>
                        ProjectChatNormalizationReason.ProviderFinishReasonContentFilter,
                    DeepSeekResponseInvalidCategory.FinishReasonResource =>
                        ProjectChatNormalizationReason.ProviderFinishReasonResource,
                    DeepSeekResponseInvalidCategory.FinishReasonAborted =>
                        ProjectChatNormalizationReason.ProviderFinishReasonAborted,
                    DeepSeekResponseInvalidCategory.FinishReasonOther =>
                        ProjectChatNormalizationReason.ProviderFinishReasonOther,
                    DeepSeekResponseInvalidCategory.Message =>
                        ProjectChatNormalizationReason.ProviderMessage,
                    _ => ProjectChatNormalizationReason.ProviderInternal,
                });
        }

        var messagePosition = request.Messages.Length;
        var contents = new List<MinimalChatContent>(response.Calls.Length + 2)
        {
            new(
                "reasoning",
                null,
                null,
                response.Reasoning,
                string.Empty,
                DeepSeekReasoningContinuationCodec.FramingName,
                null,
                messagePosition,
                0),
        };
        if (response.Content.Length > 0)
        {
            contents.Add(new MinimalChatContent(
                "text",
                null,
                null,
                response.Content,
                null,
                null,
                null,
                messagePosition,
                contents.Count));
        }

        foreach (var call in response.Calls)
        {
            contents.Add(new MinimalChatContent(
                "tool_call",
                call.Id,
                call.Name,
                call.Arguments,
                null,
                null,
                null,
                messagePosition,
                contents.Count));
        }

        var continuationItem = new MinimalChatContinuationItem(
            response.Reasoning,
            string.Empty,
            DeepSeekReasoningContinuationCodec.FramingName,
            null,
            messagePosition,
            0);
        return new MinimalChatResponse(
            new MinimalChatMessage("assistant", contents.ToArray()),
            new MinimalChatUsage(
                response.Usage.InputTokens,
                response.Usage.OutputTokens,
                new ProjectProviderUsage(
                    DeepSeekAdapterContext.Provider,
                    DeepSeekRequestWriter.Model,
                    response.ResponseModel,
                    response.Usage.CacheReadInputTokens,
                    response.Usage.UncachedInputTokens)),
            response.CapturedBytes,
            new MinimalChatContinuation(
                context.ProviderId,
                context.ModelId,
                context.AdapterId,
                context.SessionId,
                [continuationItem]));
    }

    private static MinimalChatResponse ResponseTooLarge(int messagePosition) =>
        new(
            new MinimalChatMessage(
                "assistant",
                [
                    new MinimalChatContent(
                        "text",
                        null,
                        null,
                        ResponseTooLargeText,
                        null,
                        null,
                        null,
                        messagePosition,
                        0),
                ]),
            new MinimalChatUsage(0, 0),
            DeepSeekTransportPolicy.ResponseTooLargeCount,
            Continuation: null);

    private bool ValidReplay(MinimalChatRequest request)
    {
        if (request is null ||
            request.Messages is null ||
            request.Tools is null)
        {
            return false;
        }

        var assistants = request.Messages
            .Select((message, position) => (message, position))
            .Where(entry => entry.message is not null &&
                StringComparer.Ordinal.Equals(
                    entry.message.Role,
                    "assistant"))
            .ToArray();
        if (assistants.Length == 0)
        {
            return request.Continuation is null;
        }

        var continuation = request.Continuation;
        if (continuation is null ||
            continuation.Items is null ||
            continuation.Items.Length != assistants.Length ||
            !StringComparer.Ordinal.Equals(
                continuation.ProviderId,
                context.ProviderId) ||
            !StringComparer.Ordinal.Equals(
                continuation.ModelId,
                context.ModelId) ||
            !StringComparer.Ordinal.Equals(
                continuation.AdapterId,
                context.AdapterId) ||
            !StringComparer.Ordinal.Equals(
                continuation.SessionId,
                context.SessionId))
        {
            return false;
        }

        for (var index = 0; index < assistants.Length; index++)
        {
            var (message, messagePosition) = assistants[index];
            var item = continuation.Items[index];
            if (message.Contents is null ||
                item is null ||
                message.Contents.Length < 2 ||
                message.Contents.Count(content => content is not null &&
                    StringComparer.Ordinal.Equals(
                        content.Kind,
                        "reasoning")) != 1 ||
                message.Contents[0] is not { } reasoning ||
                !StringComparer.Ordinal.Equals(reasoning.Kind, "reasoning") ||
                message.Contents.Count(content => content is not null &&
                    StringComparer.Ordinal.Equals(
                        content.Kind,
                        "tool_call")) is < 1 or >
                    AgentLimits.ToolCallsPerResponse ||
                item.MessagePosition != messagePosition ||
                item.ContentPosition != 0 ||
                item.AssociatedCallId is not null ||
                item.Opaque is not { Length: 0 } ||
                !StringComparer.Ordinal.Equals(
                    item.Framing,
                    DeepSeekReasoningContinuationCodec.FramingName) ||
                !StringComparer.Ordinal.Equals(item.Readable, reasoning.Text) ||
                !StringComparer.Ordinal.Equals(item.Opaque, reasoning.Opaque) ||
                !StringComparer.Ordinal.Equals(item.Framing, reasoning.Framing) ||
                reasoning.AssociatedCallId is not null ||
                reasoning.MessagePosition != messagePosition ||
                reasoning.Position != 0 ||
                !AgentValueDomains.IsUtf8(
                    item.Readable,
                    0,
                    AgentLimits.ContinuationItemBytes))
            {
                return false;
            }
        }

        return true;
    }

    public override string ToString() => "deepseek_chat_backend";
}

internal sealed class DeepSeekChatBackendException : Exception
{
    internal DeepSeekChatBackendException()
        : base("The DeepSeek backend request failed.")
    {
    }

    public override string ToString() => "deepseek_chat_backend_exception";
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Execution.DeepSeek;

namespace AgenticPrReview.Runtime.ActionHostVerifierFixture;

// Authored historical Host/SESSION script. Actual provider wire assertions live
// in the installed Solus fixture; this client has no HTTP or credential input.
internal sealed class FrameworkProviderHandler(string scenarioRoot, DeepSeekAdapterContext context) : IProjectChatClient
{
    public async Task<ProjectChatResponse> GetResponseAsync(ProjectChatRequest request, CancellationToken token)
    {
        var ordinal = Increment("synthetic-chat-call-count");
        token.ThrowIfCancellationRequested();
        File.WriteAllText(Path.Join(scenarioRoot, "typed-chat-observation-domain"), "apr-historical-authored-typed-chat-v1");
        if (!context.IsValid || !request.ThinkingRequired) throw new ProjectChatNormalizationException();
        var mode = ReadMode();
        var body = AgentRequestWriter.Write(request);
        try
        {
            if (FrameworkSessionCapacity.IsMode(mode)) FrameworkSessionCapacity.Observe(scenarioRoot, request, body, ordinal, mode);
            FrameworkCanaryCapture.CaptureAll(scenarioRoot, "provider.request", body);
            foreach (var secret in new[] { ("provider-key", FrameworkCanaries.ProviderKey), ("github-token", FrameworkCanaries.GitHubToken),
                ("state-key-current", FrameworkCanaries.StateKey), ("state-key-previous", FrameworkCanaries.PreviousStateKey) })
                if (!FrameworkCanaryCapture.AssertCanaryAbsent(scenarioRoot, secret.Item1, secret.Item2, "provider.request", body))
                    throw new ProjectChatNormalizationException();
            if (Contains(body, FrameworkCanaries.Prompt)) Record("provider-prompt-observed", 1);
            if (Contains(body, FrameworkCanaries.ToolData)) Record("provider-tool-data-observed", 1);
            if (request.Continuation?.Items.Any(item => item.Readable.Contains(FrameworkCanaries.ContinuationMarker, StringComparison.Ordinal)) == true)
                Increment("provider-session-plaintext-request-count");
            if (File.Exists(Path.Join(scenarioRoot, "expect-continuation"))) ValidateContinuationRequest(request, ordinal);
        }
        finally { CryptographicOperations.ZeroMemory(body); }
        if (request.Accounting is { } accounting && !accounting.TryBeginDispatch())
            throw new InvalidOperationException("historical_script_dispatch_refused");
        if (mode == "provider-error") throw new InvalidOperationException("historical_script_failure");
        if (mode == "provider-malformed") throw new ProjectChatNormalizationException();
        if (mode == "provider-stall") await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
        var call = Increment("synthetic-chat-sequence");
        if (mode == "continuation-seed" && call == 4 && File.Exists(Path.Join(scenarioRoot, "crash-after-provider-checkpoint")))
        {
            Record("provider-checkpoint-ready", 1);
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
        }
        var prefix = FrameworkSessionCapacity.IsMode(mode) ? mode + "-" : mode == "continuation" ? "continuation-" : "framework-";
        var response = mode == "continuation" ? call switch
        {
            1 => Tool(request, "continuation-read-file", "read_file", ReadFileArguments(), call),
            2 => Tool(request, "continuation-search-text", "search_text", SearchArguments(), call),
            3 => Finish(request, mode, call, "continuation-finish"),
            _ => throw new ProjectChatNormalizationException(),
        } : call switch
        {
            1 => Tool(request, prefix + "list-changed", "list_changed_files", "{}", call),
            2 => Tool(request, prefix + "list-files", "list_files", "{}", call),
            3 => Tool(request, prefix + "read-diff", "read_diff", "{\"path\":\"" + FrameworkCanaries.ReviewedPath +
                "\",\"start_hunk\":1,\"hunk_count\":20}", call, mode == "continuation-seed" ? FrameworkCanaries.ContinuationMarker : null),
            4 => Tool(request, prefix + "read-file", "read_file", ReadFileArguments(), call),
            5 => Tool(request, prefix + "search-text", "search_text", SearchArguments(), call),
            6 => Finish(request, mode, call, prefix + "finish"),
            _ => throw new ProjectChatNormalizationException(),
        };
        request.Accounting?.RecordUsage(ProviderUsageObservation.Create(3, 2, 1, 2, 5));
        return response;
    }
    private static string ReadFileArguments() => "{\"path\":\"" + FrameworkCanaries.ReviewedPath + "\",\"start_line\":1,\"line_count\":20}";
    private static string SearchArguments() => "{\"query\":\"" + FrameworkCanaries.ToolData + "\",\"path\":\"" + FrameworkCanaries.ReviewedPath + "\"}";
    private void ValidateContinuationRequest(ProjectChatRequest request, int ordinal)
    {
        var items = request.Continuation?.Items.Where(item => item.Readable.Contains(FrameworkCanaries.ContinuationMarker, StringComparison.Ordinal)).ToArray() ?? [];
        var carriers = items.Where(item => item.MessagePosition >= 0 && item.MessagePosition < request.Messages.Length)
            .Select(item => request.Messages[item.MessagePosition] with { Contents =
                [new ProjectReasoningContent(item.Readable, item.Opaque, item.Framing, item.AssociatedCallId, item.MessagePosition, item.ContentPosition),
                    .. request.Messages[item.MessagePosition].Contents.Where(content => content is not ProjectReasoningContent)] }).ToArray();
        var path = Path.Join(scenarioRoot, "provider-continuation-marker-carrier.sha256");
        string? digest = null;
        if (carriers is [var carrier])
        {
            var bytes = AgentRequestWriter.Write(new([carrier], [], null));
            try { digest = Convert.ToHexStringLower(SHA256.HashData(bytes)); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        if (ordinal == 1 && digest is not null)
        {
            File.WriteAllText(path, digest);
            Record("provider-continuation-observed", 1);
            Record("provider-continuation-first-request-exact", 1);
        }
        else if (digest is not null && File.Exists(path) && File.ReadAllText(path) == digest)
            Record("provider-continuation-carried-history-" + ordinal, 1);
        else Record("provider-continuation-order-violation", 1);
        var id = ordinal == 2 ? "continuation-read-file" : ordinal == 3 ? "continuation-search-text" : null;
        if (id is null) return;
        var calls = request.Messages.SelectMany(message => message.Contents.OfType<ProjectToolCallContent>()).Where(call => call.CallId == id).ToArray();
        var results = request.Messages.SelectMany(message => message.Contents.OfType<ProjectToolResultContent>()).Where(result => result.CallId == id).ToArray();
        Record(calls.Length == 1 && results is [var result] && !string.IsNullOrWhiteSpace(result.Result)
            ? "provider-continuation-relation-" + ordinal : "provider-continuation-order-violation", 1);
    }
    private ProjectChatResponse Finish(ProjectChatRequest request, string mode, int ordinal, string id)
    {
        string arguments;
        if (mode == "public-result")
        {
            arguments = "{\"summary\":\"rejected\",\"findings\":[{\"severity\":\"high\",\"title\":\"" + FrameworkCanaries.PublicResult +
                "\",\"message\":\"untrusted\",\"evidence\":[{\"observation_id\":\"untrusted\",\"path\":\"" +
                FrameworkCanaries.ReviewedPath + "\",\"start_line\":1,\"end_line\":1}]}]}";
            FrameworkCanaryCapture.CaptureAll(scenarioRoot, "agent.validation", arguments);
        }
        else if (mode is "inline" or "inline-warning")
        {
            var result = request.Messages.SelectMany(message => message.Contents.OfType<ProjectToolResultContent>())
                .Single(value => value.CallId == "framework-read-diff");
            using var json = JsonDocument.Parse(result.Result);
            var observation = json.RootElement.GetProperty("observation_id").GetString();
            arguments = "{\"summary\":\"Bounded review complete.\",\"findings\":[{\"severity\":\"high\",\"title\":\"Grounded proof finding\",\"message\":\"The exact changed line is reviewed.\",\"evidence\":[{\"observation_id\":\"" + observation +
                "\",\"path\":\"" + FrameworkCanaries.ReviewedPath + "\",\"start_line\":1,\"end_line\":1}]}]}";
        }
        else arguments = "{\"summary\":\"Bounded review complete.\",\"findings\":[]}";
        return Tool(request, id, "finish_review", arguments, ordinal);
    }
    private ProjectChatResponse Tool(ProjectChatRequest request, string id, string name, string arguments, int ordinal, string? marker = null)
    {
        var reasoning = id.StartsWith(FrameworkSessionCapacity.Prefix, StringComparison.Ordinal) ? FrameworkSessionCapacity.Reasoning :
            "framework-reasoning-" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture) + (marker is null ? "" : " " + marker);
        var position = request.Messages.Length;
        return new(new("assistant", [new ProjectReasoningContent(reasoning, "", DeepSeekReasoningContinuationCodec.FramingName, null, position, 0),
            new ProjectToolCallContent(id, name, arguments)]),
            new(3, 2, new(DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model, DeepSeekAdapterContext.Model, 1, 2)),
            CapturedResponseBodyBytes: 0, Continuation: new(context.ProviderId, context.ModelId, context.AdapterId, context.SessionId,
                [new(reasoning, "", DeepSeekReasoningContinuationCodec.FramingName, null, position, 0)]));
    }
    private string ReadMode() => File.Exists(Path.Join(scenarioRoot, "mode")) ? File.ReadAllText(Path.Join(scenarioRoot, "mode")).Trim() : "sticky";
    private int Increment(string name)
    {
        var path = Path.Join(scenarioRoot, name);
        var value = File.Exists(path) ? int.Parse(File.ReadAllText(path), System.Globalization.CultureInfo.InvariantCulture) + 1 : 1;
        Record(name, value); return value;
    }
    private void Record(string name, int value) => File.WriteAllText(Path.Join(scenarioRoot, name), value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    private static bool Contains(byte[] bytes, string value) => Encoding.UTF8.GetString(bytes).Contains(value, StringComparison.Ordinal);
}

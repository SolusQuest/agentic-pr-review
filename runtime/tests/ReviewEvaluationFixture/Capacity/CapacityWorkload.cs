using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Capacity;

internal sealed class CapacityClock : TimeProvider
{
    internal long Seconds { get; set; }
    public override long TimestampFrequency => 1;
    public override long GetTimestamp() => Seconds;
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(CapacityState.Now + Seconds);
}

internal sealed class CapacityMeasurement
{
    private readonly long start = Stopwatch.GetTimestamp();
    private readonly long allocated = GC.GetTotalAllocatedBytes(precise: true);
    internal int ProjectBytes { get; private set; }
    internal int Messages { get; private set; }
    internal int Parts { get; private set; }
    internal void Observe(ProjectChatRequest request)
    {
        ProjectBytes = Math.Max(ProjectBytes, AgentRequestWriter.Write(request).Length);
        Messages = Math.Max(Messages, request.Messages.Length);
        Parts = Math.Max(Parts, request.Messages.Sum(message => message.Contents.Length));
    }
    internal CapacityMetrics Finish(CapacityTransport? transport, int records, int sessionBytes, int envelopeBytes, long storedBytes, long snapshotBytes)
    {
        using var process = Process.GetCurrentProcess();
        return new((long)Stopwatch.GetElapsedTime(start).TotalMilliseconds,
            GC.GetTotalAllocatedBytes(precise: true) - allocated, process.WorkingSet64, process.PeakWorkingSet64,
            transport?.MaximumRequestBytes ?? 0, transport?.MaximumResponseBytes ?? 0, ProjectBytes,
            Messages, Parts, records, sessionBytes, envelopeBytes, storedBytes, snapshotBytes);
    }
}

internal sealed class CapacityObservedClient(IProjectChatClient inner, CapacityMeasurement measured) : IProjectChatClient
{
    public Task<ProjectChatResponse> GetResponseAsync(ProjectChatRequest request, CancellationToken token)
    { measured.Observe(request); return inner.GetResponseAsync(request, token); }
}

internal sealed record CapacitySnapshot(ReviewedSnapshot Snapshot, IReviewedFileAccess Files, long Bytes)
{
    internal static CapacitySnapshot Create(string root, ReviewedIdentity identity, bool fresh)
    {
        var tool = fresh ? CapacitySpec.FreshTool : CapacitySpec.OldTool;
        var paths = Enumerable.Range(0, 320).Select(index => $"file-{index:D4}.txt").ToArray();
        // The file and its diff describe the same bytes. A short first line bounds read_file
        // observations; the remaining 63 lines still push aggregate canonical diffs above 8 MiB.
        var text = Enumerable.Range(1, 64).Select(line => line == 1 ? tool : tool.PadRight(512, 'x')).ToArray();
        var diffs = paths.Select(path => new ReviewedDiffSource(identity, path, null, "added", false,
            [new ReviewedDiffHunk(0, 0, 1, 64, Enumerable.Range(1, 64).Select(line =>
                new ReviewedDiffLine("addition", null, line, text[line - 1])))])).ToArray();
        var bytes = diffs.Sum(diff => (long)diff.CanonicalBytes.Length);
        CapacitySpec.Require(bytes > 8 * 1024 * 1024 && bytes < AgentLimits.DiffSnapshotBytes, "snapshot_growth");
        var snapshot = new ReviewedSnapshot(identity, root, paths,
            diffs.Select(diff => new ReviewedChangedFile(diff.Path, null, "added", 64, 0, 64,
                "available", diff.PatchSha256, false)), diffs);
        var contents = Encoding.UTF8.GetBytes(string.Join('\n', text) + "\n").ToImmutableArray();
        var files = paths.ToImmutableDictionary(path => path, _ => contents, StringComparer.Ordinal);
        return new(snapshot, new ReplayMemoryFiles(snapshot, files), bytes);
    }
}

internal sealed record CapacityCall(string Id, string Name, string Arguments);

// Unlike R5 replay, this transport never retains the complete repeated request transcript.
// The independently authored history oracle is evaluated on the actual provider wire on every send.
internal sealed class CapacityTransport(CapacityCase selected, CapacityHistory[] history, CapacityClock clock,
    bool fresh, bool host = false, ReviewedIdentity? expectedIdentity = null) : IAccountedDeepSeekTransport
{
    internal int Sends { get; private set; }
    internal int MaximumRequestBytes { get; private set; }
    internal int MaximumResponseBytes { get; private set; }
    internal int VerifiedAssistants { get; private set; }
    internal int VerifiedToolResults { get; private set; }
    internal int ListedFiles { get; private set; }
    internal int DiffReads { get; private set; }
    internal bool OldHistoryAbsent { get; private set; } = true;
    internal string? OracleFailure { get; private set; }
    internal static string Reasoning(string id, int turn) => $"APR_R7_PRIVATE_REASONING_{id}_{turn}_" + new string('r', 256);
    internal static CapacityCall[] Calls(string id, int total, int tools, int turn, bool host = false)
    {
        if (turn == total - 1)
            return [new($"{id}_{turn}_0", "finish_review", "{\"summary\":\"Synthetic capacity review.\",\"findings\":[]}")];
        return Enumerable.Range(0, tools).Select(slot =>
        {
            var callId = $"{id}_{turn}_{slot}";
            if (!host && total > 2 && turn < 4 && slot == 0)
                return new CapacityCall(callId, "list_changed_files", turn == 0 ? "{}" :
                    $"{{\"after\":\"file-{turn * 100 - 1:D4}.txt\"}}");
            if (!host && total > 2 && turn == 0 && slot == 1)
                return new CapacityCall(callId, "read_diff", "{\"path\":\"file-0000.txt\",\"start_hunk\":1,\"hunk_count\":1}");
            return new CapacityCall(callId, "read_file", host
                ? "{\"path\":\"file.txt\",\"start_line\":1,\"line_count\":1}"
                : $"{{\"path\":\"file-{(turn * tools + slot) % 320:D4}.txt\",\"start_line\":1,\"line_count\":1}}");
        }).ToArray();
    }

    public Task<DeepSeekTransportResult> SendAsync(ReadOnlyMemory<byte> body, CancellationToken token) =>
        SendAsync(body, token, null);
    public Task<DeepSeekTransportResult> SendAsync(ReadOnlyMemory<byte> body, CancellationToken token, ProviderAttemptCapture? accounting)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            CapacitySpec.Require(accounting is null || accounting.TryBeginDispatch(), "dispatch");
            MaximumRequestBytes = Math.Max(MaximumRequestBytes, body.Length);
            VerifyWire(body);
        }
        catch (InvalidOperationException failure) { OracleFailure = failure.Message; throw; }
        var turn = Sends++;
        if (selected.Mode == "deadline") clock.Seconds = AgentLimits.DeadlineSeconds;
        if (selected.Mode == "response")
        {
            var oversizedBody = new byte[AgentLimits.ResponseBytes + 1];
            MaximumResponseBytes = oversizedBody.Length;
            CapacitySpec.Require(oversizedBody.Length > AgentLimits.ResponseBytes, "response_body_boundary");
            return Task.FromResult(DeepSeekTransportResult.ResponseTooLarge());
        }
        var reasoning = selected.Mode == "capacity" ? new string('r', AgentLimits.ContentBytes + 1) : Reasoning(selected.Id, turn);
        var calls = Calls(selected.Id, Math.Max(1, selected.Calls), selected.ToolsPerTurn, turn, host);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteString("model", DeepSeekAdapterContext.Model);
            writer.WritePropertyName("choices"); writer.WriteStartArray(); writer.WriteStartObject(); writer.WriteNumber("index", 0);
            writer.WriteString("finish_reason", "tool_calls"); writer.WritePropertyName("message"); writer.WriteStartObject();
            writer.WriteString("role", "assistant"); writer.WriteString("content", ""); writer.WriteString("reasoning_content", reasoning);
            writer.WritePropertyName("tool_calls"); writer.WriteStartArray();
            foreach (var call in calls)
            {
                writer.WriteStartObject(); writer.WriteString("id", call.Id); writer.WriteString("type", "function");
                writer.WritePropertyName("function"); writer.WriteStartObject(); writer.WriteString("name", call.Name);
                writer.WriteString("arguments", call.Arguments); writer.WriteEndObject(); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject(); writer.WriteEndObject(); writer.WriteEndArray();
            var output = selected.Mode == "output" ? AgentLimits.OutputTokens + 1 : 2;
            writer.WritePropertyName("usage"); writer.WriteStartObject(); writer.WriteNumber("prompt_tokens", 3);
            writer.WriteNumber("completion_tokens", output); writer.WriteNumber("total_tokens", 3 + output);
            writer.WriteNumber("prompt_cache_hit_tokens", 0); writer.WriteNumber("prompt_cache_miss_tokens", 3);
            writer.WriteEndObject(); writer.WriteEndObject();
        }
        var response = stream.ToArray();
        MaximumResponseBytes = Math.Max(MaximumResponseBytes, response.Length);
        return Task.FromResult(DeepSeekTransportResult.Success(response));
    }

    private void VerifyWire(ReadOnlyMemory<byte> body)
    {
        using var document = JsonDocument.Parse(body);
        var messages = document.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        var expected = history.SelectMany(run => Enumerable.Range(0, run.Calls)
            .Select(turn => (run.Id, run.Calls, run.ToolsPerTurn, Turn: turn, Identity: run.Identity ?? expectedIdentity ?? CapacityState.Identity)))
            .Concat(Enumerable.Range(0, Sends).Select(turn => (selected.Id, selected.Calls, selected.ToolsPerTurn, Turn: turn,
                Identity: expectedIdentity ?? CapacityState.Identity))).ToArray();
        var assistants = messages.Where(message => message.GetProperty("role").GetString() == "assistant").ToArray();
        CapacitySpec.Require(assistants.Length == expected.Length, "wire_history_count");
        var expectedTools = new Dictionary<string, (CapacityCall Call, ReviewedIdentity Identity)>(StringComparer.Ordinal);
        for (var index = 0; index < expected.Length; index++)
        {
            var item = expected[index];
            var message = assistants[index];
            CapacitySpec.Require(message.GetProperty("reasoning_content").GetString() == Reasoning(item.Id, item.Turn), "wire_reasoning_association");
            var calls = Calls(item.Id, item.Calls, item.ToolsPerTurn, item.Turn, host);
            var actual = message.GetProperty("tool_calls");
            CapacitySpec.Require(actual.GetArrayLength() == calls.Length, "wire_call_count");
            for (var slot = 0; slot < calls.Length; slot++)
            {
                var call = calls[slot]; var value = actual[slot];
                // Production projection omits SESSION's canonical null option again on provider wire.
                using var wanted = JsonDocument.Parse(call.Arguments);
                using var received = JsonDocument.Parse(value.GetProperty("function").GetProperty("arguments").GetString()!);
                CapacitySpec.Require(value.GetProperty("id").GetString() == call.Id, "wire_call_id");
                CapacitySpec.Require(value.GetProperty("function").GetProperty("name").GetString() == call.Name, "wire_call_name");
                CapacitySpec.Require(JsonElement.DeepEquals(wanted.RootElement, received.RootElement), "wire_call_arguments_" + call.Name);
                CapacitySpec.Require(expectedTools.TryAdd(call.Id, (call, item.Identity)), "wire_duplicate_call");
            }
        }
        var toolMessages = messages.Where(message => message.GetProperty("role").GetString() == "tool").ToArray();
        CapacitySpec.Require(toolMessages.Length == expectedTools.Count, "wire_tool_count");
        var listed = new HashSet<string>(StringComparer.Ordinal); var diffReads = 0;
        foreach (var message in toolMessages)
        {
            var id = message.GetProperty("tool_call_id").GetString()!;
            CapacitySpec.Require(expectedTools.Remove(id, out var association), "wire_tool_association");
            var call = association.Call;
            var content = message.GetProperty("content").GetString()!;
            if (call!.Name == "finish_review") CapacitySpec.Require(content == "{}", "wire_terminal_result");
            else
            {
                using var result = JsonDocument.Parse(content);
                var identity = result.RootElement.GetProperty("reviewed_identity");
                var wantedIdentity = association.Identity;
                CapacitySpec.Require(result.RootElement.GetProperty("status").GetString() == "ok" &&
                    identity.GetProperty("repository_id").GetString() == wantedIdentity.RepositoryId &&
                    identity.GetProperty("review_target").GetInt64() == wantedIdentity.ReviewTarget &&
                    identity.GetProperty("base_sha").GetString() == wantedIdentity.BaseSha &&
                    identity.GetProperty("head_sha").GetString() == wantedIdentity.HeadSha, "wire_tool_identity");
                using var arguments = JsonDocument.Parse(call.Arguments);
                if (call.Name is "read_file" or "read_diff")
                    CapacitySpec.Require(result.RootElement.GetProperty("path").GetString() ==
                        arguments.RootElement.GetProperty("path").GetString(), "wire_tool_path");
                if (call.Name == "read_file")
                    CapacitySpec.Require(result.RootElement.GetProperty("lines")[0].GetProperty("text").GetString() ==
                        (fresh ? CapacitySpec.FreshTool : CapacitySpec.OldTool), "wire_tool_data");
                if (call.Name == "list_changed_files")
                {
                    var start = call.Arguments == "{}" ? 0 : int.Parse(arguments.RootElement.GetProperty("after").GetString()!.AsSpan(5, 4),
                        System.Globalization.CultureInfo.InvariantCulture) + 1;
                    var changes = result.RootElement.GetProperty("changes");
                    CapacitySpec.Require(changes.GetArrayLength() == Math.Min(100, 320 - start), "wire_list_count");
                    var offset = 0;
                    foreach (var change in result.RootElement.GetProperty("changes").EnumerateArray())
                    {
                        var path = change.GetProperty("path").GetString()!;
                        CapacitySpec.Require(path == $"file-{start + offset++:D4}.txt" && listed.Add(path), "wire_list_membership");
                    }
                }
                if (call.Name == "read_diff")
                { CapacitySpec.Require(content.Contains(fresh ? CapacitySpec.FreshTool : CapacitySpec.OldTool, StringComparison.Ordinal), "wire_diff_data"); diffReads++; }
            }
        }
        CapacitySpec.Require(expectedTools.Count == 0, "wire_missing_result");
        foreach (var message in messages)
        {
            var role = message.GetProperty("role").GetString();
            var raw = message.GetRawText();
            CapacitySpec.Require(!raw.Contains(fresh ? CapacitySpec.FreshTool : CapacitySpec.OldTool, StringComparison.Ordinal) || role == "tool", "wire_tool_role");
            CapacitySpec.Require(!raw.Contains(fresh ? CapacitySpec.FreshUser : CapacitySpec.OldUser, StringComparison.Ordinal) || role == "user", "wire_user_role");
        }
        if (fresh)
        {
            OldHistoryAbsent &= !Encoding.UTF8.GetString(body.Span).Contains(CapacitySpec.OldUser, StringComparison.Ordinal) &&
                !Encoding.UTF8.GetString(body.Span).Contains(CapacitySpec.OldTool, StringComparison.Ordinal) &&
                !Encoding.UTF8.GetString(body.Span).Contains("default64_", StringComparison.Ordinal) &&
                !Encoding.UTF8.GetString(body.Span).Contains("host_seed_", StringComparison.Ordinal);
            CapacitySpec.Require(OldHistoryAbsent, "reset_history");
        }
        VerifiedAssistants = Math.Max(VerifiedAssistants, assistants.Length);
        VerifiedToolResults = Math.Max(VerifiedToolResults, toolMessages.Length);
        ListedFiles = Math.Max(ListedFiles, listed.Count); DiffReads = Math.Max(DiffReads, diffReads);
    }
    public void Dispose() { }
}

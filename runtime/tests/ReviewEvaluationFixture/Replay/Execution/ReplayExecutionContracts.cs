using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

internal enum ReplayFault { None, Provider, Cancelled, Incomplete, MalformedTerminal, WrongScope, WrongHead, NonCompleted, AfterPrepareCrash, AfterPrepareHang, AfterPrepareOverflow, PartialReply, WrongReply, MissingHistory, ChangedContinuation, MissingContinuation, WrongContinuationPosition }
internal sealed record ReplayChildInput(string Operation, string Root, string Corpus, int Phase, string Session,
    byte[] Key, AcceptedLineage? Predecessor, ReplayFault Fault);

// This is private IPC, never the public result. Sensitive evidence exists only in bounded pipes/memory.
internal sealed record ReplayChildReply(string Operation, string Corpus, int Phase, string Session, string Commit,
    string Tree, bool SourceClean, int ProcessId, string Startup, string Code, PreparedStateReceipt? Prepared,
    byte[] Evaluation, string? LogicalSha256, string? ProviderSha256, int ModelCalls, int ToolCalls,
    byte[]? Plaintext, ImmutableArray<byte[]> Requests, ImmutableArray<string> EnvironmentKeys, byte[] EnvironmentBytes);

internal sealed record ReplayStep(string CaseId, string CaseSha256, string ConfigurationSha256, string SnapshotSha256,
    string Transition, string Code, bool Accepted, long? Generation, string? LogicalSha256, string? ProviderSha256,
    int ModelCalls, int ToolCalls, string? QualityCode, string? EvidenceStatus, string? ScenarioStatus,
    bool PredecessorPreserved);
internal sealed record ReplayObservation(int Phase, int ProcessId, string Startup, string Session,
    string? SessionSha256, string? EnvelopeSha256, long? AcceptedAt, string? ExecutionSha256);
internal sealed record ReplayReport(string Mode, string Code, string Cleanup, string? CorpusSha256,
    string SourceCommit, string SourceTree, bool SourceClean, string Operation,
    ImmutableArray<ReplayStep> Steps, ImmutableArray<ReplayObservation> Observations, string? NormalizedSha256)
{
    internal int ExitCode => Code == "verified" && Cleanup == "cleaned" ? 0 : Code == "input_invalid" ? 2 : 1;
}

[JsonSerializable(typeof(ReplayChildInput))]
[JsonSerializable(typeof(ReplayChildReply))]
[JsonSerializable(typeof(ReplayReport))]
[JsonSerializable(typeof(ImmutableArray<ReplayStep>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UseStringEnumConverter = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false, RespectRequiredConstructorParameters = true, MaxDepth = 20)]
internal sealed partial class ReplayExecutionJson : JsonSerializerContext;

internal static class ReplayWire
{
    internal const int InputLimit = 16 * 1024;
    internal const int EvidenceLimit = 4 * 1024 * 1024;
    internal const int ReplyLimit = 8 * 1024 * 1024;
    internal static T? Read<T>(ReadOnlySpan<byte> bytes, JsonTypeInfo<T> type, int maximum) where T : class
    {
        if (bytes.Length is < 1 || bytes.Length > maximum) return null;
        try
        {
            _ = new UTF8Encoding(false, true).GetCharCount(bytes);
            return JsonSerializer.Deserialize(bytes, type);
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or NotSupportedException) { return null; }
    }

    internal static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximum, CancellationToken token)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, token);
            if (count == 0) return output.ToArray();
            if (output.Length + count > maximum) throw new IOException("replay_stream_limit");
            output.Write(buffer, 0, count);
        }
    }

    internal static byte[] Write(ReplayReport value) => JsonSerializer.SerializeToUtf8Bytes(value, ReplayExecutionJson.Default.ReplayReport);
}

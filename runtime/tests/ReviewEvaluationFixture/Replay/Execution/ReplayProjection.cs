using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

internal static class ReplayProjection
{

    // Comparison only, after actual run admission. This is not an exact input-byte digest.
    // Keep every byte except the fresh world's continuation session-id string token.
    internal static string LogicalInput(IEnumerable<byte[]> requests) => EvaluationAttempt.Hash("replay-historical-normalized-logical-input-v1",
        requests.Select(bytes => AgentCanonical.HashRaw(NormalizeSession(bytes))).ToArray());

    private static byte[] NormalizeSession(byte[] bytes)
    {
        var reader = new Utf8JsonReader(bytes);
        var continuation = false;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1)
                continuation = reader.ValueTextEquals("continuation"u8);
            if (!continuation || reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 2 ||
                !reader.ValueTextEquals("session_id"u8)) continue;
            if (!reader.Read() || reader.TokenType != JsonTokenType.String) throw new InvalidDataException();
            var start = checked((int)reader.TokenStartIndex);
            var end = checked((int)reader.BytesConsumed);
            using var normalized = new MemoryStream();
            normalized.Write(bytes.AsSpan(0, start));
            normalized.Write("\"normalized-session\""u8);
            normalized.Write(bytes.AsSpan(end));
            return normalized.ToArray();
        }
        return bytes;
    }

    internal static string Steps(ImmutableArray<ReplayStep> steps) => AgentCanonical.HashDomain("apr.r5.replay.steps",
        JsonSerializer.SerializeToUtf8Bytes(steps, ReplayExecutionJson.Default.ImmutableArrayReplayStep));
}

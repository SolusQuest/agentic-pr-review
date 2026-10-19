using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

internal static class ReplayProjection
{
    // Normalize only after actual admission. Never restore or persist this comparison artifact.
    internal static string Logical(AgentSessionArtifact artifact, AgentTrustedRequest trusted)
    {
        string? previous = null;
        var runs = ImmutableArray.CreateBuilder<AgentSessionCompletedRun>();
        foreach (var run in artifact.Document.CompletedRuns)
        {
            if (!AgentStableRequestMaterializer.TryMaterialize(trusted, previous, out var stable)) throw new InvalidOperationException();
            runs.Add(run with { StablePlanSha256 = AgentCanonical.StablePlanSha256(stable!.StablePlan) });
            var normalized = artifact.Document with
            {
                SessionId = "normalized-session", Generation = run.RunOrdinal,
                ProducerBaseSha = run.ReviewedIdentity.BaseSha, ProducerHeadSha = run.ReviewedIdentity.HeadSha,
                PredecessorStateSha256 = previous is null ? null : new string('0', 64),
                PriorSessionSha256 = previous, CompletedRuns = runs.ToImmutable(),
            };
            if (!AgentSessionCodec.TryWrite(normalized, out var encoded, out _) || encoded is null) throw new InvalidOperationException();
            previous = encoded.SessionSha256;
        }
        return previous ?? throw new InvalidOperationException();
    }

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

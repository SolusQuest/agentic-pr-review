using System.Collections.Immutable;
using AgenticPrReview.Runtime.Agent.Core;

namespace AgenticPrReview.Runtime.Agent.Tools;

internal abstract record PreparedAgentToolCall(
    string CallId,
    string Name,
    byte[] CanonicalArguments);

internal sealed record AgentObservation(
    string ObservationId,
    ReviewedIdentity Identity,
    ImmutableDictionary<string, ImmutableHashSet<int>> ReturnedLines)
{
    internal bool Grounds(AgentEvidence evidence)
    {
        if (!StringComparer.Ordinal.Equals(ObservationId, evidence.ObservationId) ||
            evidence.StartLine < 1 ||
            evidence.EndLine < evidence.StartLine ||
            !ReturnedLines.TryGetValue(evidence.Path, out var lines))
        {
            return false;
        }

        for (var line = evidence.StartLine; line <= evidence.EndLine; line++)
        {
            if (!lines.Contains(line))
            {
                return false;
            }
        }

        return true;
    }
}

internal sealed record AgentToolExecution(
    bool Succeeded,
    string? FailureCode,
    string? ResultJson,
    byte[]? CanonicalResult,
    AgentObservation? Observation)
{
    internal static AgentToolExecution Failure(string code) =>
        new(false, code, null, null, null);
}

internal interface IAgentToolExecutor
{
    string? Preflight(PreparedAgentToolCall call);

    ValueTask<AgentToolExecution> ExecuteAsync(
        PreparedAgentToolCall call,
        CancellationToken cancellationToken);
}

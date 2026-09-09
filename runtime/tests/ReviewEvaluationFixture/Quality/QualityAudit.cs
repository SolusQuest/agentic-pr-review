using System.Collections.Immutable;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Quality;

internal sealed record QualityAuditResult(bool FactsMatch, ImmutableArray<RequiredObservation> Observations,
    string TargetObservationId);

internal static class QualityAudit
{
    // Independent real-tool preflight. Its observations are never put into the Agent's history.
    internal static async Task<QualityAuditResult> ObserveAsync(AdmittedReplayRun input, QualityCaseSpec spec,
        CancellationToken cancellationToken = default)
    {
        var snapshot = input.CreateSnapshot(Directory.GetCurrentDirectory());
        var executor = new SnapshotToolExecutor(snapshot, input.CreateFileAccess(snapshot));
        var observed = ImmutableArray.CreateBuilder<RequiredObservation>();
        var facts = new HashSet<QualityFact>();
        var targetObservation = string.Empty;
        foreach (var operation in spec.Operations)
        {
            var call = Prepare(operation);
            if (executor.Preflight(call) is not null) return new(false, [], string.Empty);
            var result = await executor.ExecuteAsync(call, cancellationToken);
            if (!AgentToolResultAdmission.TryAdmit(call, snapshot.Identity, result, out _, out var observation))
                return new(false, [], string.Empty);
            observed.Add(new(operation.Name, observation.ObservationId));
            if (call is PreparedReadFileCall read)
            {
                using var document = JsonDocument.Parse(result.CanonicalResult!);
                foreach (var line in document.RootElement.GetProperty("lines").EnumerateArray())
                    facts.Add(new(read.Arguments.Path, line.GetProperty("line").GetInt32(), line.GetProperty("text").GetString()!));
                if (read.Arguments.Path == spec.Target && observation.Grounds(new(observation.ObservationId, spec.Target, spec.Line, spec.Line)))
                    targetObservation = observation.ObservationId;
            }
        }
        return new(spec.Facts.All(facts.Contains) && targetObservation.Length == 64, observed.ToImmutable(), targetObservation);
    }

    internal static bool Matches(AdmittedReplayRun input, QualityCaseSpec spec, QualityAuditResult audit)
    {
        var expected = input.Expected.Input;
        return audit.FactsMatch && input.ExpectedCode == spec.ExpectedCode &&
            expected.RequiredObservations.SequenceEqual(audit.Observations) &&
            expected.Defects.SequenceEqual(Defects(spec, audit)) && expected.ProhibitedFindings.SequenceEqual(Prohibited(spec));
    }

    internal static ImmutableArray<ExpectedDefect> Defects(QualityCaseSpec spec, QualityAuditResult audit) => spec.Safe ? [] :
        [new("defect", "high", audit.TargetObservationId, spec.Target, spec.Line, spec.Line)];
    internal static ImmutableArray<ProhibitedFinding> Prohibited(QualityCaseSpec spec) => spec.Safe ?
        [new(spec.Target, spec.Line, spec.Line)] : [];

    private static PreparedAgentToolCall Prepare(QualityOperation operation) => operation.Name switch
    {
        AgentToolRegistry.ReadFileName when AgentToolArguments.TryReadFile(operation.Arguments, out var value) => new PreparedReadFileCall("audit", value!),
        AgentToolRegistry.ReadDiffName when AgentToolArguments.TryReadDiff(operation.Arguments, out var value) => new PreparedReadDiffCall("audit", value!),
        AgentToolRegistry.ListFilesName when AgentToolArguments.TryListFiles(operation.Arguments, out var value) => new PreparedListFilesCall("audit", value!),
        AgentToolRegistry.ListChangedFilesName when AgentToolArguments.TryListChangedFiles(operation.Arguments, out var value) => new PreparedListChangedFilesCall("audit", value!),
        AgentToolRegistry.SearchTextName when AgentToolArguments.TrySearchText(operation.Arguments, out var value) => new PreparedSearchTextCall("audit", value!),
        _ => throw new InvalidOperationException("quality_operation_invalid"),
    };
}

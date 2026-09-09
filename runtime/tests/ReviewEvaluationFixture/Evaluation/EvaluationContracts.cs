using System.Collections.Immutable;
using System.Text.Json.Serialization;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;

internal static class EvaluationLimits
{
    internal const int InputBytes = 64 * 1024;
    internal const int Depth = 12;
    internal const int Defects = AgentLimits.Findings;
    internal const int Observations = AgentLimits.ToolCalls;
    internal const int Identifier = 64;

    internal static bool Id(string? value) => AgentValueDomains.IsIdentifier(value);
    internal static bool Hash(string? value, int length = 64) =>
        value is not null && value.Length == length &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static bool Path(string? value) => value is not null &&
        value.Length <= AgentLimits.PathBytes && RepositoryPath.IsValid(value);
    internal static bool Severity(string? value) => value is "critical" or "high" or "medium" or "low";
}

// Input DTOs are data, never completed-subject authority. Collections are immutable.
internal sealed record EvaluationReviewedIdentity(
    [property: JsonRequired] string RepositoryId,
    [property: JsonRequired] long ReviewTarget,
    [property: JsonRequired] string BaseSha,
    [property: JsonRequired] string HeadSha)
{
    internal ReviewedIdentity Runtime => new(RepositoryId, ReviewTarget, BaseSha, HeadSha);
    internal bool Valid => RepositoryId is not null &&
        AgentValueDomains.IsUtf8(RepositoryId, 1, 128) && ReviewTarget > 0 &&
        EvaluationLimits.Hash(BaseSha, 40) && EvaluationLimits.Hash(HeadSha, 40);
    public override string ToString() => "evaluation_reviewed_identity";
}

internal sealed record ExpectedDefect(
    [property: JsonRequired] string Id,
    [property: JsonRequired] string Severity,
    [property: JsonRequired] string ObservationId,
    [property: JsonRequired] string Path,
    [property: JsonRequired] int StartLine,
    [property: JsonRequired] int EndLine)
{
    internal bool Valid => EvaluationLimits.Id(Id) && EvaluationLimits.Severity(Severity) &&
        EvaluationLimits.Hash(ObservationId) && EvaluationLimits.Path(Path) &&
        StartLine > 0 && EndLine >= StartLine && (long)EndLine - StartLine < AgentLimits.ReadFileLines;
    internal bool Matches(AgentFinding finding) => finding.Severity == Severity &&
        finding.Evidence.Any(e => e.ObservationId == ObservationId && e.Path == Path &&
            e.StartLine == StartLine && e.EndLine == EndLine);
    public override string ToString() => "evaluation_expected_defect";
}

internal sealed record RequiredObservation(
    [property: JsonRequired] string Tool,
    [property: JsonRequired] string ObservationId)
{
    internal bool Valid => Tool is AgentToolRegistry.ReadFileName or AgentToolRegistry.ReadDiffName or
        AgentToolRegistry.ListFilesName or AgentToolRegistry.ListChangedFilesName or AgentToolRegistry.SearchTextName &&
        EvaluationLimits.Hash(ObservationId);
    public override string ToString() => "evaluation_required_observation";
}

internal sealed record ProhibitedFinding(
    [property: JsonRequired] string Path,
    [property: JsonRequired] int StartLine,
    [property: JsonRequired] int EndLine)
{
    internal bool Valid => EvaluationLimits.Path(Path) && StartLine > 0 && EndLine >= StartLine &&
        (long)EndLine - StartLine < AgentLimits.ReadFileLines;
    public override string ToString() => "evaluation_prohibited_finding";
}

internal sealed record EvaluationCaseInput(
    [property: JsonRequired] string Id,
    [property: JsonRequired] string CorpusSha256,
    [property: JsonRequired] EvaluationReviewedIdentity ReviewedIdentity,
    [property: JsonRequired] ImmutableArray<ExpectedDefect> Defects,
    [property: JsonRequired] ImmutableArray<RequiredObservation> RequiredObservations,
    [property: JsonRequired] ImmutableArray<ProhibitedFinding> ProhibitedFindings)
{
    internal bool Valid => EvaluationLimits.Id(Id) && EvaluationLimits.Hash(CorpusSha256) &&
        ReviewedIdentity is { Valid: true } &&
        !Defects.IsDefault && Defects.Length <= EvaluationLimits.Defects && Defects.All(d => d is { Valid: true }) &&
        Defects.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count() == Defects.Length &&
        !RequiredObservations.IsDefault && RequiredObservations.Length <= EvaluationLimits.Observations &&
        RequiredObservations.All(o => o is { Valid: true }) &&
        RequiredObservations.Distinct().Count() == RequiredObservations.Length &&
        !ProhibitedFindings.IsDefault && ProhibitedFindings.Length <= EvaluationLimits.Defects &&
        ProhibitedFindings.All(p => p is { Valid: true });
    public override string ToString() => "evaluation_case_input";
}

internal sealed class EvaluationCase
{
    private EvaluationCase(EvaluationCaseInput input)
    {
        Input = input;
        Sha256 = AgentCanonical.HashDomain("apr.r5.evaluation.case", EvaluationJson.Write(input));
    }

    internal EvaluationCaseInput Input { get; }
    internal string Sha256 { get; }
    internal static EvaluationCase? Admit(EvaluationCaseInput? input) => input is { Valid: true } &&
        EvaluationJson.Write(input).Length <= EvaluationLimits.InputBytes ? new(input) : null;
    public override string ToString() => "evaluation_case";
}

internal sealed record EvaluationRunInput(
    [property: JsonRequired] string RunId,
    [property: JsonRequired] string Mode,
    [property: JsonRequired] string SourceCommit,
    [property: JsonRequired] string SourceTree,
    [property: JsonRequired] bool SourceClean,
    [property: JsonRequired] string ProviderConfigurationSha256)
{
    internal bool Valid => EvaluationLimits.Id(RunId) && Mode is "deterministic" or "live" &&
        EvaluationLimits.Hash(SourceCommit, 40) && EvaluationLimits.Hash(SourceTree, 40) &&
        EvaluationLimits.Hash(ProviderConfigurationSha256);
    public override string ToString() => "evaluation_run_input";
}

internal sealed record FindingAdjudication(
    [property: JsonRequired] int FindingOrdinal,
    [property: JsonRequired] string Verdict,
    [property: JsonRequired] string? DefectId)
{
    public override string ToString() => "evaluation_finding_adjudication";
}

internal sealed record EvaluationAdjudication(
    [property: JsonRequired] string CorpusSha256,
    [property: JsonRequired] string CaseSha256,
    [property: JsonRequired] string ConfigurationSha256,
    [property: JsonRequired] string ExecutionSha256,
    [property: JsonRequired] ImmutableArray<FindingAdjudication> Findings)
{
    internal bool Valid => EvaluationLimits.Hash(CorpusSha256) && EvaluationLimits.Hash(CaseSha256) &&
        EvaluationLimits.Hash(ConfigurationSha256) && EvaluationLimits.Hash(ExecutionSha256) &&
        !Findings.IsDefault && Findings.Length <= AgentLimits.Findings && Findings.All(f => f is not null &&
            f.FindingOrdinal is >= 0 and < AgentLimits.Findings && f.Verdict is "confirmed" or "rejected" &&
            (f.DefectId is null || EvaluationLimits.Id(f.DefectId)) &&
            (f.Verdict != "rejected" || f.DefectId is null)) &&
        Findings.Select(f => f.FindingOrdinal).Distinct().Count() == Findings.Length;
    public override string ToString() => "evaluation_adjudication";
}

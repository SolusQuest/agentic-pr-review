using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;

internal sealed record R6ProspectiveFindingAnnotation(
    [property: JsonRequired] int FindingOrdinal,
    [property: JsonRequired] string Verdict,
    [property: JsonRequired] string? ExpectedDefectId,
    [property: JsonRequired] string? DefectGroupScope,
    [property: JsonRequired] string? DefectGroupId,
    [property: JsonRequired] ImmutableArray<string> SafeLineUses);

// Private operator input. Origin is deliberately absent and supplied by the
// invoked review path, not by this editable sidecar.
internal sealed record R6ProspectiveAnnotation(
    [property: JsonRequired] string RubricId,
    [property: JsonRequired] string RubricSha256,
    [property: JsonRequired] string CorpusSha256,
    [property: JsonRequired] string CaseSha256,
    [property: JsonRequired] string ConfigurationSha256,
    [property: JsonRequired] string ExecutionSha256,
    [property: JsonRequired] ImmutableArray<R6ProspectiveFindingAnnotation> Findings);

internal sealed record R6ProspectiveFindingReceipt(
    [property: JsonRequired] int FindingOrdinal,
    [property: JsonRequired] string Verdict,
    [property: JsonRequired] string? ExpectedDefectId,
    [property: JsonRequired] string? DefectGroupScope,
    [property: JsonRequired] string? DefectGroupId,
    [property: JsonRequired] string CitationClass,
    [property: JsonRequired] string SafeLineRole,
    [property: JsonRequired] string Reason);

internal sealed record R6ProspectiveCaseReceipt(
    [property: JsonRequired] string RubricId,
    [property: JsonRequired] string RubricSha256,
    [property: JsonRequired] int ScheduleIndex,
    [property: JsonRequired] string CaseId,
    [property: JsonRequired] string CorpusSha256,
    [property: JsonRequired] string CaseSha256,
    [property: JsonRequired] string? ConfigurationSha256,
    [property: JsonRequired] string? ExecutionSha256,
    [property: JsonRequired] string Origin,
    [property: JsonRequired] string Status,
    [property: JsonRequired] int FindingRowCount,
    [property: JsonRequired] ImmutableArray<R6ProspectiveFindingReceipt> Findings);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false, MaxDepth = EvaluationLimits.Depth)]
[JsonSerializable(typeof(R6ProspectiveAnnotation))]
[JsonSerializable(typeof(R6ProspectiveCaseReceipt))]
internal sealed partial class R6ProspectiveJsonContext : JsonSerializerContext;

internal static class R6ProspectiveAssessment
{
    internal const string Expected = "expected";
    internal const string TrueOffFocus = "true_off_focus";
    internal const string FalseUnsafe = "false_unsafe";
    internal const string Unresolved = "unresolved";
    internal const string Authored = "authored";
    internal const string Run = "run";
    internal const string None = "none";
    internal const string Comparison = "comparison";
    internal const string Accusation = "accusation";
    internal const string AiOrigin = "ai-adjudicated";
    internal const string HumanOrigin = "human-confirmed";

    private sealed record Defect(string GroupId, string CaseId, string Path, int Line,
        string Severity, ImmutableArray<(string Path, int Start, int End)> RequiredReads);

    private static readonly ImmutableArray<Defect> Defects =
    [
        new("cs-null-deref", "cs-defect", "src/Caller.cs", 5, "high",
            [("src/Caller.cs", 5, 5), ("src/Lookup.cs", 5, 5)]),
        new("ts-zero-timeout", "ts-defect", "src/client.ts", 2, "medium",
            [("src/client.ts", 2, 2), ("src/config.ts", 1, 2)]),
        new("repository-token-log", "repository-rule", "src/Upload.cs", 5, "high",
            [("src/Upload.cs", 5, 6), ("rules/review.md", 3, 3)]),
    ];

    private static readonly ImmutableHashSet<string> ChangedPaths =
        ["src/Caller.cs", "src/SafeCaller.cs", "src/Upload.cs", "src/client.ts", "src/safe-client.ts"];

    internal static R6ProspectiveAnnotation? ReadAnnotation(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 1 or > EvaluationLimits.InputBytes) return null;
        try
        {
            _ = new UTF8Encoding(false, true).GetCharCount(bytes);
            return JsonSerializer.Deserialize(bytes, R6ProspectiveJsonContext.Default.R6ProspectiveAnnotation);
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or NotSupportedException)
        {
            return null;
        }
    }

    internal static byte[] WriteAnnotation(R6ProspectiveAnnotation annotation) =>
        JsonSerializer.SerializeToUtf8Bytes(annotation,
            R6ProspectiveJsonContext.Default.R6ProspectiveAnnotation);

    internal static R6ProspectiveCaseReceipt? Assess(int scheduleIndex, EvaluationCase testCase,
        EvaluationSubject subject, R6ProspectiveAnnotation? annotation, string origin)
    {
        if (annotation is null || origin is not (AiOrigin or HumanOrigin) ||
            scheduleIndex is < 0 or >= 5 ||
            testCase.Input.CorpusSha256 != R6ProspectiveRubric.CorpusSha256 ||
            testCase.Input.Id != R6ProspectiveRubric.Cases[scheduleIndex] ||
            annotation.RubricId != R6ProspectiveRubric.Id ||
            annotation.RubricSha256 != R6ProspectiveRubric.Sha256 ||
            annotation.CorpusSha256 != testCase.Input.CorpusSha256 ||
            annotation.CaseSha256 != testCase.Sha256 ||
            annotation.ConfigurationSha256 != subject.ConfigurationSha256 ||
            annotation.ExecutionSha256 != subject.ExecutionSha256 ||
            annotation.Findings.IsDefault || annotation.Findings.Length != subject.Findings.Length ||
            annotation.Findings.Length > EvaluationLimits.Defects ||
            testCase.Input.ReviewedIdentity.Runtime != subject.ReviewedIdentity ||
            EvaluationScorer.Evaluate(testCase, subject).EvidenceStatus != AssertionStatus.Passed)
            return null;

        var ordered = annotation.Findings.OrderBy(row => row.FindingOrdinal).ToArray();
        if (ordered.Where((row, index) => row is null || row.FindingOrdinal != index).Any())
            return null;
        var receipts = ImmutableArray.CreateBuilder<R6ProspectiveFindingReceipt>();
        foreach (var row in ordered)
        {
            var finding = subject.Findings[row.FindingOrdinal];
            if (!finding.Evidence.All(e => subject.GroundedObservations.Any(o =>
                    o.Observation.Grounds(e)))) return null;
            var safeRole = DeriveSafeLineRole(finding, row.SafeLineUses);
            if (safeRole is null) return null;

            var defect = Defects.FirstOrDefault(d => d.GroupId == row.DefectGroupId);
            var knownAnchor = Defects.FirstOrDefault(d => finding.Evidence.Any(e =>
                e.Path == d.Path && e.StartLine <= d.Line && e.EndLine >= d.Line));
            var scopeValid = row.DefectGroupScope == Authored && defect is not null ||
                row.DefectGroupScope == Run && defect is null &&
                row.DefectGroupId is not null && EvaluationLimits.Id(row.DefectGroupId) &&
                row.DefectGroupId.StartsWith("run-", StringComparison.Ordinal);
            var isTrue = row.Verdict is Expected or TrueOffFocus;
            if (isTrue != scopeValid ||
                isTrue && knownAnchor is not null &&
                    (row.DefectGroupScope != Authored || row.DefectGroupId != knownAnchor.GroupId) ||
                isTrue && knownAnchor is null && row.DefectGroupScope == Authored ||
                !isTrue && (row.ExpectedDefectId is not null || row.DefectGroupScope is not null ||
                    row.DefectGroupId is not null) ||
                row.Verdict is not (Expected or TrueOffFocus or FalseUnsafe or Unresolved))
                return null;

            var citationClass = "invalid";
            if (isTrue && defect is not null)
                citationClass = CitationClass(finding, subject.GroundedObservations, defect);
            else if (isTrue)
                citationClass = finding.Evidence.Any(e => ChangedPaths.Contains(e.Path) &&
                    e.EndLine - e.StartLine <= 2 &&
                    subject.GroundedObservations.Any(o => o.Observation.Grounds(e)))
                    ? "reviewed_other" : "invalid";

            if (row.Verdict == Expected &&
                (defect?.CaseId != testCase.Input.Id || row.ExpectedDefectId != "defect") ||
                row.Verdict == TrueOffFocus &&
                (defect?.CaseId == testCase.Input.Id || row.ExpectedDefectId is not null))
                return null;

            var reason = row.Verdict switch
            {
                FalseUnsafe => "review_rejected",
                Unresolved => "review_pending",
                _ when safeRole == Accusation => "safe_line_accusation",
                _ when citationClass == "invalid" => "citation_invalid",
                Expected => "confirmed_expected",
                _ => "confirmed_off_focus",
            };
            var publicGroupId = PublicGroupId(row.DefectGroupScope, row.DefectGroupId);
            receipts.Add(new(row.FindingOrdinal, row.Verdict, row.ExpectedDefectId,
                row.DefectGroupScope, publicGroupId, citationClass, safeRole, reason));
        }
        return new(R6ProspectiveRubric.Id, R6ProspectiveRubric.Sha256, scheduleIndex,
            testCase.Input.Id, testCase.Input.CorpusSha256, testCase.Sha256,
            subject.ConfigurationSha256, subject.ExecutionSha256, origin, "assessed",
            receipts.Count, receipts.ToImmutable());
    }

    internal static string KnownCitationClass(string groupId, AgentFinding finding,
        ImmutableArray<EvaluationObservation> observations)
    {
        var defect = Defects.FirstOrDefault(item => item.GroupId == groupId);
        return defect is null ? "invalid" : CitationClass(finding, observations, defect);
    }

    internal static string? PublicGroupId(string? scope, string? id) =>
        scope == Run && id is not null
            ? "run-" + AgentCanonical.HashDomain("apr.r6.v5.run-defect-group",
                Encoding.UTF8.GetBytes(id))
            : id;

    private static string CitationClass(AgentFinding finding,
        ImmutableArray<EvaluationObservation> observations, Defect defect)
    {
        if (finding.Severity != defect.Severity ||
            !defect.RequiredReads.All(read => observations.Any(o =>
                o.Tool == AgentToolRegistry.ReadFileName && o.Observation.Grounds(
                    new(o.Observation.ObservationId, read.Path, read.Start, read.End)))))
            return "invalid";
        foreach (var evidence in finding.Evidence)
        {
            if (evidence.Path != defect.Path || evidence.StartLine > defect.Line ||
                evidence.EndLine < defect.Line ||
                (defect.Line - evidence.StartLine) + (evidence.EndLine - defect.Line) > 2 ||
                !observations.Any(o => o.Observation.Grounds(evidence)))
                continue;
            return evidence.StartLine == defect.Line && evidence.EndLine == defect.Line
                ? "exact" : "bounded_context";
        }
        return "invalid";
    }

    private static bool SafeLineCited(AgentEvidence evidence) =>
        evidence.Path == "src/SafeCaller.cs" && evidence.StartLine <= 5 && evidence.EndLine >= 5 ||
        evidence.Path == "src/safe-client.ts" && evidence.StartLine <= 2 && evidence.EndLine >= 2;

    internal static string? DeriveSafeLineRole(AgentFinding finding,
        ImmutableArray<string> safeLineUses)
    {
        var count = finding.Evidence.Count(SafeLineCited);
        if (safeLineUses.IsDefault || safeLineUses.Length != count ||
            safeLineUses.Any(use => use is not (Comparison or Accusation))) return null;
        return count == 0 ? None : safeLineUses.Contains(Accusation) ? Accusation : Comparison;
    }
}

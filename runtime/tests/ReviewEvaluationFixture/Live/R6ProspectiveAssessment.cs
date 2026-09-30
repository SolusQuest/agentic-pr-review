using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;

internal sealed record R6ProspectiveSafeUse(
    [property: JsonRequired] int EvidenceOrdinal,
    [property: JsonRequired] string Use,
    [property: JsonRequired] string Assessment);

// Diagnostic causal assessment, separate from the observed case outcome.
// Empty causes explicitly mean that available evidence cannot assign one.
internal sealed record R6ProspectiveAttribution(
    [property: JsonRequired] ImmutableArray<string> Causes,
    [property: JsonRequired] string Confidence,
    [property: JsonRequired] string Basis,
    [property: JsonRequired] ImmutableArray<int> ObservationOrdinals)
{
    internal static R6ProspectiveAttribution Undetermined { get; } =
        new([], "undetermined", "insufficient", []);

    internal bool Valid(int observationCount)
    {
        if (Causes.IsDefault || ObservationOrdinals.IsDefault ||
            observationCount is < 0 or > AgentLimits.ToolCalls ||
            Causes.Length > 4 || ObservationOrdinals.Length > observationCount ||
            Causes.Any(cause => cause is not ("model_behavior" or "harness" or
                "test_contract" or "provider_transport")) ||
            !Causes.SequenceEqual(Causes.Order(StringComparer.Ordinal)) ||
            Causes.Distinct(StringComparer.Ordinal).Count() != Causes.Length ||
            ObservationOrdinals.Any(ordinal => ordinal < 0 || ordinal >= observationCount) ||
            !ObservationOrdinals.SequenceEqual(ObservationOrdinals.Order()) ||
            ObservationOrdinals.Distinct().Count() != ObservationOrdinals.Length)
            return false;
        if (Causes.Length == 0)
            return Confidence == "undetermined" && Basis == "insufficient" &&
                ObservationOrdinals.Length == 0;
        return Confidence is "confirmed" or "probable" &&
            Basis is "returned_observation" or "runtime_diagnostic" or
                "test_contract" or "provider_receipt" or "independent_review" &&
            (Basis != "returned_observation" || ObservationOrdinals.Length > 0);
    }
}

internal sealed record R6ProspectiveFindingAnnotation(
    [property: JsonRequired] int FindingOrdinal,
    [property: JsonRequired] string Verdict,
    [property: JsonRequired] string? ExpectedDefectId,
    [property: JsonRequired] string? DefectGroupScope,
    [property: JsonRequired] string? DefectGroupId,
    [property: JsonRequired] ImmutableArray<string> SafeLineUses,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ImmutableArray<R6ProspectiveSafeUse>? SafeLineAssessments = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? AnchorEvidenceOrdinal = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SeverityAssessment = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? AnchorAssessment = null);

// Private operator input. Origin is deliberately absent and supplied by the
// invoked review path, not by this editable sidecar.
internal sealed record R6ProspectiveAnnotation(
    [property: JsonRequired] string RubricId,
    [property: JsonRequired] string RubricSha256,
    [property: JsonRequired] string CorpusSha256,
    [property: JsonRequired] string CaseSha256,
    [property: JsonRequired] string ConfigurationSha256,
    [property: JsonRequired] string ExecutionSha256,
    [property: JsonRequired] ImmutableArray<R6ProspectiveFindingAnnotation> Findings,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    R6ProspectiveAttribution? Attribution = null);

internal sealed record R6ProspectiveFindingReceipt(
    [property: JsonRequired] int FindingOrdinal,
    [property: JsonRequired] string Verdict,
    [property: JsonRequired] string? ExpectedDefectId,
    [property: JsonRequired] string? DefectGroupScope,
    [property: JsonRequired] string? DefectGroupId,
    [property: JsonRequired] string CitationClass,
    [property: JsonRequired] string SafeLineRole,
    [property: JsonRequired] string Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ImmutableArray<R6ProspectiveSafeUse>? SafeLineAssessments = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? AnchorEvidenceOrdinal = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SeverityAssessment = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? AnchorAssessment = null);

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
    [property: JsonRequired] ImmutableArray<R6ProspectiveFindingReceipt> Findings,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    R6ProspectiveAttribution? Attribution = null);

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
    internal const string ComparisonOnly = "comparison_only";
    internal const string ConfirmedOtherIssue = "confirmed_other_issue";
    internal const string ProtectedPropertyAccusation = "protected_property_accusation";
    internal const string SafeUseUnresolved = "unresolved";
    internal const string Justified = "justified";
    internal const string Relevant = "relevant";
    internal const string Rejected = "rejected";
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

    internal static bool EquivalentChangedRead(RequiredObservation required,
        EvaluationSubject subject) => EquivalentChangedRead(required,
            subject.GroundedObservations, subject.ReviewedIdentity);

    internal static bool EquivalentChangedRead(RequiredObservation required,
        ImmutableArray<EvaluationObservation> observations, ReviewedIdentity identity)
    {
        if (required is not { Tool: AgentToolRegistry.ReadFileName,
                Coverage: { } coverage } || !ChangedPaths.Contains(coverage.Path))
            return false;
        return observations.Any(observed =>
            observed.Tool == AgentToolRegistry.ReadDiffName && observed.CompleteDiff &&
            observed.Observation.Identity == identity &&
            observed.Observation.Grounds(new(observed.Observation.ObservationId,
                coverage.Path, coverage.StartLine, coverage.EndLine)));
    }

    private static bool SupportsRead((string Path, int Start, int End) read,
        ImmutableArray<EvaluationObservation> observations, bool equivalentChangedDiff) =>
        observations.Any(observed =>
            (observed.Tool == AgentToolRegistry.ReadFileName ||
             equivalentChangedDiff && ChangedPaths.Contains(read.Path) &&
             observed.Tool == AgentToolRegistry.ReadDiffName && observed.CompleteDiff) &&
            observed.Observation.Grounds(new(observed.Observation.ObservationId,
                read.Path, read.Start, read.End)));

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

    // Only this editable envelope may be corrected. Evidence and semantic
    // eligibility are assessed once after a bound annotation crosses it.
    internal static bool ValidV2EditableEnvelope(int scheduleIndex, EvaluationCase testCase,
        EvaluationSubject subject, R6ProspectiveAnnotation? annotation,
        LivePlanRubric rubric)
    {
        if (!R6ProspectiveRubric.HasIndependentReview(rubric) || annotation is null ||
            scheduleIndex is < 0 or >= 5 ||
            testCase.Input.Id != R6ProspectiveRubric.Cases[scheduleIndex] ||
            testCase.Input.CorpusSha256 != R6ProspectiveRubric.CorpusSha256 ||
            testCase.Input.ReviewedIdentity.Runtime != subject.ReviewedIdentity ||
            annotation.RubricId != rubric.Id ||
            annotation.RubricSha256 != rubric.Sha256 ||
            annotation.CorpusSha256 != testCase.Input.CorpusSha256 ||
            annotation.CaseSha256 != testCase.Sha256 ||
            annotation.ConfigurationSha256 != subject.ConfigurationSha256 ||
            annotation.ExecutionSha256 != subject.ExecutionSha256 ||
            annotation.Findings.IsDefault ||
            annotation.Findings.Length != subject.Findings.Length ||
            annotation.Findings.Length > EvaluationLimits.Defects ||
            annotation.Findings.Any(row => row is null) ||
            (R6ProspectiveRubric.IsV3(rubric)
                ? annotation.Attribution is not { } attribution ||
                    !attribution.Valid(subject.GroundedObservations.Length)
                : annotation.Attribution is not null))
            return false;
        var ordered = annotation.Findings.OrderBy(row => row.FindingOrdinal).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var row = ordered[index];
            if (row is null || row.FindingOrdinal != index ||
                row.Verdict is not (Expected or TrueOffFocus or FalseUnsafe or Unresolved) ||
                row.ExpectedDefectId is not (null or "defect") ||
                row.DefectGroupScope is not (null or Authored or Run))
                return false;
            var finding = subject.Findings[index];
            var isTrue = row.Verdict is Expected or TrueOffFocus;
            if (!ValidV2SafeLineAssessments(finding, row.SafeLineUses,
                    row.SafeLineAssessments) ||
                !ValidV2SemanticAssessments(isTrue, row.SeverityAssessment,
                    row.AnchorAssessment) ||
                isTrue && (row.AnchorEvidenceOrdinal is not { } ordinal ||
                    ordinal < 0 || ordinal >= finding.Evidence.Length) ||
                !isTrue && row.AnchorEvidenceOrdinal is not null)
                return false;
        }
        return true;
    }

    internal static R6ProspectiveCaseReceipt? Assess(int scheduleIndex, EvaluationCase testCase,
        EvaluationSubject subject, R6ProspectiveAnnotation? annotation, string origin,
        LivePlanRubric? selectedRubric = null)
    {
        var rubric = selectedRubric ?? new LivePlanRubric(R6ProspectiveRubric.Id,
            R6ProspectiveRubric.Sha256);
        if (!R6ProspectiveRubric.IsV1(rubric) &&
            !R6ProspectiveRubric.HasIndependentReview(rubric))
            return null;
        var v2 = R6ProspectiveRubric.HasIndependentReview(rubric);
        var v3 = R6ProspectiveRubric.IsV3(rubric);
        if (annotation is null || origin is not (AiOrigin or HumanOrigin) ||
            scheduleIndex is < 0 or >= 5 ||
            testCase.Input.CorpusSha256 != R6ProspectiveRubric.CorpusSha256 ||
            testCase.Input.Id != R6ProspectiveRubric.Cases[scheduleIndex] ||
            annotation.RubricId != rubric.Id ||
            annotation.RubricSha256 != rubric.Sha256 ||
            annotation.CorpusSha256 != testCase.Input.CorpusSha256 ||
            annotation.CaseSha256 != testCase.Sha256 ||
            annotation.ConfigurationSha256 != subject.ConfigurationSha256 ||
            annotation.ExecutionSha256 != subject.ExecutionSha256 ||
            annotation.Findings.IsDefault || annotation.Findings.Length != subject.Findings.Length ||
            annotation.Findings.Length > EvaluationLimits.Defects ||
            (v3 ? annotation.Attribution is not { } attribution ||
                !attribution.Valid(subject.GroundedObservations.Length)
                : annotation.Attribution is not null) ||
            testCase.Input.ReviewedIdentity.Runtime != subject.ReviewedIdentity ||
            EvaluationScorer.Evaluate(testCase, subject, equivalentObservation:
                v3 ? EquivalentChangedRead : null).EvidenceStatus != AssertionStatus.Passed)
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
            if (safeRole is null ||
                v2 && !ValidV2SafeLineAssessments(finding, row.SafeLineUses,
                    row.SafeLineAssessments) ||
                !v2 && (row.SafeLineAssessments is not null ||
                    row.AnchorEvidenceOrdinal is not null ||
                    row.SeverityAssessment is not null ||
                    row.AnchorAssessment is not null)) return null;

            var defect = Defects.FirstOrDefault(d => d.GroupId == row.DefectGroupId);
            var scopeValid = row.DefectGroupScope == Authored && defect is not null ||
                row.DefectGroupScope == Run && defect is null &&
                row.DefectGroupId is not null && EvaluationLimits.Id(row.DefectGroupId) &&
                row.DefectGroupId.StartsWith("run-", StringComparison.Ordinal);
            var isTrue = row.Verdict is Expected or TrueOffFocus;
            if (v2 && !ValidV2SemanticAssessments(isTrue,
                    row.SeverityAssessment, row.AnchorAssessment)) return null;
            if (isTrue != scopeValid ||
                !isTrue && (row.ExpectedDefectId is not null || row.DefectGroupScope is not null ||
                    row.DefectGroupId is not null) ||
                row.Verdict is not (Expected or TrueOffFocus or FalseUnsafe or Unresolved))
                return null;

            var citationClass = "invalid";
            if (isTrue && defect is not null)
                citationClass = v2
                    ? V2CitationClass(finding, subject.GroundedObservations, defect,
                        row.Verdict == TrueOffFocus, row.AnchorEvidenceOrdinal, v3)
                    : CitationClass(finding, subject.GroundedObservations, defect,
                        row.Verdict == TrueOffFocus);
            else if (isTrue)
                citationClass = v2
                    ? V2OtherCitationClass(finding, subject.GroundedObservations,
                        row.AnchorEvidenceOrdinal)
                    : finding.Evidence.Any(e => ChangedPaths.Contains(e.Path) &&
                        e.EndLine - e.StartLine <= 2 &&
                        subject.GroundedObservations.Any(o => o.Observation.Grounds(e)))
                        ? "reviewed_other" : "invalid";
            if (v2 && (isTrue && row.AnchorEvidenceOrdinal is null ||
                !isTrue && row.AnchorEvidenceOrdinal is not null)) return null;

            // Authored off-focus identity must prove its selected frozen anchor.
            // A different reviewed defect may cite that anchor as context without
            // inheriting the authored defect's causal identity.
            if (row.Verdict == TrueOffFocus && defect is not null &&
                citationClass == "invalid") return null;

            if (row.Verdict == Expected &&
                (defect?.CaseId != testCase.Input.Id || row.ExpectedDefectId != "defect") ||
                row.Verdict == TrueOffFocus &&
                (defect?.CaseId == testCase.Input.Id || row.ExpectedDefectId is not null))
                return null;

            var confirmedOtherSafeLine = v2 && ConfirmedOtherSafeLine(
                row.Verdict, row.DefectGroupScope, citationClass, safeRole,
                row.SafeLineAssessments);
            if (v2 && row.SafeLineAssessments!.Value.Any(use =>
                    use.Assessment == ConfirmedOtherIssue) &&
                !confirmedOtherSafeLine) return null;
            var reason = row.Verdict switch
            {
                FalseUnsafe => "review_rejected",
                Unresolved => "review_pending",
                _ when safeRole == Accusation && !confirmedOtherSafeLine =>
                    "safe_line_accusation",
                _ when citationClass == "invalid" => "citation_invalid",
                Expected => "confirmed_expected",
                _ => "confirmed_off_focus",
            };
            var publicGroupId = PublicGroupId(row.DefectGroupScope, row.DefectGroupId);
            receipts.Add(new(row.FindingOrdinal, row.Verdict, row.ExpectedDefectId,
                row.DefectGroupScope, publicGroupId, citationClass, safeRole, reason,
                v2 ? row.SafeLineAssessments : null,
                v2 ? row.AnchorEvidenceOrdinal : null,
                v2 ? row.SeverityAssessment : null,
                v2 ? row.AnchorAssessment : null));
        }
        return new(rubric.Id, rubric.Sha256, scheduleIndex,
            testCase.Input.Id, testCase.Input.CorpusSha256, testCase.Sha256,
            subject.ConfigurationSha256, subject.ExecutionSha256, origin, "assessed",
            receipts.Count, receipts.ToImmutable(), v3 ? annotation.Attribution : null);
    }

    internal static string KnownCitationClass(string groupId, AgentFinding finding,
        ImmutableArray<EvaluationObservation> observations, bool offFocus = false)
    {
        var defect = Defects.FirstOrDefault(item => item.GroupId == groupId);
        return defect is null ? "invalid" : CitationClass(finding, observations, defect, offFocus);
    }

    internal static string? PublicGroupId(string? scope, string? id) =>
        scope == Run && id is not null
            ? "run-" + AgentCanonical.HashDomain("apr.r6.v5.run-defect-group",
                Encoding.UTF8.GetBytes(id))
            : id;

    private static string CitationClass(AgentFinding finding,
        ImmutableArray<EvaluationObservation> observations, Defect defect, bool offFocus)
    {
        if (finding.Severity != defect.Severity ||
            !defect.RequiredReads.Where(read => !offFocus || read.Path != defect.Path)
                .All(read => observations.Any(o =>
                o.Tool == AgentToolRegistry.ReadFileName && o.Observation.Grounds(
                    new(o.Observation.ObservationId, read.Path, read.Start, read.End)))))
            return "invalid";
        foreach (var evidence in finding.Evidence)
        {
            if (evidence.Path != defect.Path || evidence.StartLine > defect.Line ||
                evidence.EndLine < defect.Line ||
                (defect.Line - evidence.StartLine) + (evidence.EndLine - defect.Line) > 2 ||
                offFocus && !defect.RequiredReads.Where(read => read.Path == defect.Path)
                    .All(read => evidence.StartLine <= read.Start && evidence.EndLine >= read.End) ||
                !observations.Any(o => (!offFocus || o.Tool is AgentToolRegistry.ReadFileName or
                    AgentToolRegistry.ReadDiffName) && o.Observation.Grounds(evidence)))
                continue;
            return evidence.StartLine == defect.Line && evidence.EndLine == defect.Line
                ? "exact" : "bounded_context";
        }
        return "invalid";
    }

    private static string V2CitationClass(AgentFinding finding,
        ImmutableArray<EvaluationObservation> observations, Defect defect,
        bool offFocus, int? anchorOrdinal, bool equivalentChangedDiff = false)
    {
        if (anchorOrdinal is not { } ordinal ||
            ordinal < 0 || ordinal >= finding.Evidence.Length ||
            !defect.RequiredReads.Where(read => !offFocus || read.Path != defect.Path)
                .All(read => SupportsRead(read, observations, equivalentChangedDiff)))
            return "invalid";
        var evidence = finding.Evidence[ordinal];
        if (evidence.Path != defect.Path || evidence.StartLine > defect.Line ||
            evidence.EndLine < defect.Line ||
            offFocus && !defect.RequiredReads.Where(read => read.Path == defect.Path)
                .All(read => evidence.StartLine <= read.Start && evidence.EndLine >= read.End) ||
            !observations.Any(o => (!offFocus || o.Tool == AgentToolRegistry.ReadFileName ||
                o.Tool == AgentToolRegistry.ReadDiffName &&
                (!equivalentChangedDiff || o.CompleteDiff)) &&
                o.Observation.Grounds(evidence)))
            return "invalid";
        var extra = defect.Line - evidence.StartLine + evidence.EndLine - defect.Line;
        return extra == 0 ? "exact" : extra <= 2 ? "bounded_context" :
            "reviewed_context";
    }

    internal static string ReviewedCitationClass(string defectGroupId, AgentFinding finding,
        ImmutableArray<EvaluationObservation> observations, bool offFocus,
        int? anchorOrdinal, bool equivalentChangedDiff = false)
    {
        var defect = Defects.FirstOrDefault(item => item.GroupId == defectGroupId);
        return defect is null ? "invalid" : V2CitationClass(finding, observations,
            defect, offFocus, anchorOrdinal, equivalentChangedDiff);
    }

    private static string V2OtherCitationClass(AgentFinding finding,
        ImmutableArray<EvaluationObservation> observations, int? anchorOrdinal)
    {
        if (anchorOrdinal is not { } ordinal || ordinal < 0 ||
            ordinal >= finding.Evidence.Length) return "invalid";
        var evidence = finding.Evidence[ordinal];
        return ChangedPaths.Contains(evidence.Path) &&
            observations.Any(o => o.Observation.Grounds(evidence))
            ? "reviewed_other" : "invalid";
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

    internal static bool ValidV2SafeLineAssessments(AgentFinding finding,
        ImmutableArray<string> uses,
        ImmutableArray<R6ProspectiveSafeUse>? assessments)
    {
        if (uses.IsDefault || assessments is not { IsDefault: false } values)
            return false;
        var safeOrdinals = finding.Evidence.Select((evidence, ordinal) =>
            (evidence, ordinal)).Where(item => SafeLineCited(item.evidence))
            .Select(item => item.ordinal).ToArray();
        if (values.Length != safeOrdinals.Length || uses.Length != values.Length)
            return false;
        for (var index = 0; index < values.Length; index++)
        {
            var item = values[index];
            if (item is null || item.EvidenceOrdinal != safeOrdinals[index] ||
                item.Use != uses[index] ||
                item.Use == Comparison && item.Assessment != ComparisonOnly ||
                item.Use == Accusation && item.Assessment is not
                    (ConfirmedOtherIssue or ProtectedPropertyAccusation or SafeUseUnresolved) ||
                item.Use is not (Comparison or Accusation)) return false;
        }
        return true;
    }

    internal static bool ValidV2SemanticAssessments(bool isTrue,
        string? severity, string? anchor) =>
        (severity is Justified or Rejected or SafeUseUnresolved) &&
        (anchor is Relevant or Rejected or SafeUseUnresolved) &&
        (!isTrue || severity == Justified && anchor == Relevant);

    internal static bool ConfirmedOtherSafeLine(string verdict, string? scope,
        string citationClass, string role,
        ImmutableArray<R6ProspectiveSafeUse>? assessments) =>
        verdict == TrueOffFocus && scope == Run && citationClass == "reviewed_other" &&
        role == Accusation && assessments is { IsDefault: false } values &&
        values.Any(item => item.Use == Accusation) &&
        values.All(item => item.Use != Accusation ||
            item.Assessment == ConfirmedOtherIssue);
}

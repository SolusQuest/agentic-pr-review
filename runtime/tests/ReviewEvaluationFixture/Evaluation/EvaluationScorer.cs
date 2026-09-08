using System.Collections.Immutable;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;

internal enum EvaluationCode
{
    Scored, SubjectInvalid, WrongSnapshot, RequiredToolMissing, RequiredObservationMissing,
    ExpectedFindingMissing, ProhibitedFinding, DuplicateObservation, AdjudicationInvalid,
    ExecutionFailed, InputInvalid,
}
internal enum EvaluationStatus { Completed, Failed, Invalid }
internal enum AssertionStatus { Passed, Failed, NotEvaluated }
internal enum ModelObservationStatus { Adjudicated, Unadjudicated, NotEvaluated }

// Public-safe per-case projection only. No terminal, source, tool-result or exception text.
internal sealed record EvaluationOutcome(
    string CaseId, string CorpusSha256, string CaseSha256,
    string? ConfigurationSha256, string? AttemptSha256, string? ExecutionSha256,
    string? SourceCommit, string? SourceTree, bool? SourceClean, string? Mode,
    EvaluationStatus ExecutionStatus, AssertionStatus EvidenceStatus, AssertionStatus ScenarioStatus,
    ModelObservationStatus ModelStatus, EvaluationCode Code,
    EvaluationFailureSource FailureSource, EvaluationFailureKind FailureKind,
    int FindingCount, int ToolObservationCount, int ExpectedDefects, int StructuralMatches,
    int StructurallyMissingDefects, int DuplicateObservations, int ProhibitedObservations,
    int AdjudicatedTrue, int AdjudicatedFalse, int AdjudicatedDefects, int UnadjudicatedFindings)
{
    public override string ToString() => "evaluation_outcome";
}

internal static class EvaluationScorer
{
    internal static EvaluationOutcome Evaluate(
        EvaluationCase testCase, EvaluationSubject? subject, EvaluationAdjudication? adjudication = null)
    {
        if (subject is null) return Failure(testCase, EvaluationFailure.Invalid, code: EvaluationCode.SubjectInvalid);
        var spec = testCase.Input;
        if (spec.ReviewedIdentity.Runtime != subject.ReviewedIdentity)
            return CompletedRejection(testCase, subject, EvaluationCode.WrongSnapshot);
        foreach (var required in spec.RequiredObservations)
        {
            if (!subject.Observations.Any(o => o.Tool == required.Tool))
                return CompletedRejection(testCase, subject, EvaluationCode.RequiredToolMissing);
            if (!subject.Observations.Contains(required))
                return CompletedRejection(testCase, subject, EvaluationCode.RequiredObservationMissing);
        }

        // Maximum cardinality, not greedy credit: one candidate and one defect each at most once.
        var defects = spec.Defects.OrderBy(d => d.Id, StringComparer.Ordinal).ToArray();
        var owners = Enumerable.Repeat(-1, defects.Length).ToArray();
        bool Augment(int finding, bool[] visited)
        {
            for (var d = 0; d < defects.Length; d++)
            {
                if (visited[d] || !defects[d].Matches(subject.Findings[finding])) continue;
                visited[d] = true;
                if (owners[d] == -1 || Augment(owners[d], visited))
                {
                    owners[d] = finding;
                    return true;
                }
            }
            return false;
        }
        var matches = 0;
        for (var f = 0; f < subject.Findings.Length; f++)
            if (Augment(f, new bool[defects.Length])) matches++;
        var structurallyRelated = subject.Findings.Count(f => defects.Any(d => d.Matches(f)));
        var duplicates = structurallyRelated - matches;
        var prohibited = subject.Findings.Count(f => f.Evidence.Any(e => spec.ProhibitedFindings.Any(p =>
            p.Path == e.Path && e.StartLine <= p.EndLine && e.EndLine >= p.StartLine)));
        var missing = defects.Length - matches;
        var scenarioCode = prohibited > 0 ? EvaluationCode.ProhibitedFinding :
            duplicates > 0 ? EvaluationCode.DuplicateObservation :
            missing > 0 ? EvaluationCode.ExpectedFindingMissing : EvaluationCode.Scored;

        var trueCount = 0;
        var falseCount = 0;
        var credited = new HashSet<string>(StringComparer.Ordinal);
        if (adjudication is not null)
        {
            if (!adjudication.Valid || adjudication.CorpusSha256 != spec.CorpusSha256 ||
                adjudication.CaseSha256 != testCase.Sha256 ||
                adjudication.ConfigurationSha256 != subject.ConfigurationSha256 ||
                adjudication.ExecutionSha256 != subject.ExecutionSha256)
                return CompletedRejection(testCase, subject, EvaluationCode.AdjudicationInvalid);
            foreach (var annotation in adjudication.Findings)
            {
                if (annotation.FindingOrdinal >= subject.Findings.Length)
                    return CompletedRejection(testCase, subject, EvaluationCode.AdjudicationInvalid);
                if (annotation.DefectId is not null)
                {
                    var defect = defects.FirstOrDefault(d => d.Id == annotation.DefectId);
                    if (defect is null || !defect.Matches(subject.Findings[annotation.FindingOrdinal]) ||
                        !credited.Add(annotation.DefectId))
                        return CompletedRejection(testCase, subject, EvaluationCode.AdjudicationInvalid);
                }
                if (annotation.Verdict == "confirmed") trueCount++;
                else falseCount++;
            }
        }
        var pending = subject.Findings.Length - trueCount - falseCount;
        // A failed required assertion cannot be rescued by an otherwise valid annotation.
        var scenarioPassed = scenarioCode == EvaluationCode.Scored;
        return new(spec.Id, spec.CorpusSha256, testCase.Sha256, subject.ConfigurationSha256, subject.Attempt.AttemptSha256, subject.ExecutionSha256,
            subject.Run.SourceCommit, subject.Run.SourceTree, subject.Run.SourceClean, subject.Run.Mode,
            EvaluationStatus.Completed, AssertionStatus.Passed, scenarioPassed ? AssertionStatus.Passed : AssertionStatus.Failed,
            pending == 0 ? ModelObservationStatus.Adjudicated : ModelObservationStatus.Unadjudicated,
            scenarioCode, EvaluationFailureSource.None, EvaluationFailureKind.None,
            subject.Findings.Length, subject.Observations.Length, defects.Length, matches, missing, duplicates, prohibited,
            trueCount, falseCount, credited.Count, pending);
    }

    internal static EvaluationOutcome Failure(EvaluationCase testCase, EvaluationFailure failure,
        EvaluationAttempt? attempt = null,
        EvaluationCode code = EvaluationCode.ExecutionFailed) =>
        new(testCase.Input.Id, testCase.Input.CorpusSha256, testCase.Sha256,
            attempt?.ConfigurationSha256, attempt?.AttemptSha256, null,
            attempt?.Run.SourceCommit, attempt?.Run.SourceTree, attempt?.Run.SourceClean, attempt?.Run.Mode,
            failure.Source == EvaluationFailureSource.Evaluator ? EvaluationStatus.Invalid : EvaluationStatus.Failed,
            AssertionStatus.NotEvaluated, AssertionStatus.NotEvaluated, ModelObservationStatus.NotEvaluated, code,
            failure.Source, failure.Kind, 0, 0, testCase.Input.Defects.Length, 0, 0, 0, 0, 0, 0, 0, 0);

    private static EvaluationOutcome CompletedRejection(EvaluationCase testCase, EvaluationSubject subject, EvaluationCode code) =>
        Failure(testCase, EvaluationFailure.Invalid, subject.Attempt, code) with
        {
            // Evaluation assertions never rewrite the completed execution's actual status.
            ExecutionStatus = EvaluationStatus.Completed,
            FailureSource = code == EvaluationCode.AdjudicationInvalid ? EvaluationFailureSource.Evaluator : EvaluationFailureSource.None,
            FailureKind = code == EvaluationCode.AdjudicationInvalid ? EvaluationFailureKind.InvalidInput : EvaluationFailureKind.None,
            ExecutionSha256 = subject.ExecutionSha256,
            EvidenceStatus = code == EvaluationCode.AdjudicationInvalid ? AssertionStatus.Passed : AssertionStatus.Failed,
            FindingCount = subject.Findings.Length,
            ToolObservationCount = subject.Observations.Length,
        };
}

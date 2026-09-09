using AgenticPrReview.Runtime.Agent;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;

// Admission validates report data, not its origin or any completed Runtime subject.
internal static class EvaluationOutcomeAdmission
{
    internal static bool Valid(EvaluationOutcome value)
    {
        if (!EvaluationLimits.Id(value.CaseId) || !EvaluationLimits.Hash(value.CorpusSha256) ||
            !EvaluationLimits.Hash(value.CaseSha256) || !ValidContext(value) ||
            !Enum.IsDefined(value.ExecutionStatus) || !Enum.IsDefined(value.EvidenceStatus) ||
            !Enum.IsDefined(value.ScenarioStatus) || !Enum.IsDefined(value.ModelStatus) ||
            !Enum.IsDefined(value.Code) || !Enum.IsDefined(value.FailureSource) ||
            !Enum.IsDefined(value.FailureKind) || !ValidFailurePair(value) || !ValidCounts(value)) return false;

        if (value.ExecutionStatus != EvaluationStatus.Completed)
        {
            return value.ExecutionSha256 is null && value.FailureSource != EvaluationFailureSource.None &&
                (value.ExecutionStatus == EvaluationStatus.Invalid) == (value.FailureSource == EvaluationFailureSource.Evaluator) &&
                value.Code is EvaluationCode.ExecutionFailed or EvaluationCode.SubjectInvalid or EvaluationCode.InputInvalid &&
                (value.Code != EvaluationCode.InputInvalid || value.FailureSource == EvaluationFailureSource.Evaluator) &&
                value.EvidenceStatus == AssertionStatus.NotEvaluated && value.ScenarioStatus == AssertionStatus.NotEvaluated &&
                value.ModelStatus == ModelObservationStatus.NotEvaluated && value.FindingCount == 0 && value.ToolObservationCount == 0 &&
                StructuralCountsAreZero(value);
        }

        if (value.AttemptSha256 is null || !EvaluationLimits.Hash(value.ExecutionSha256)) return false;
        if (value.Code is EvaluationCode.WrongSnapshot or EvaluationCode.RequiredToolMissing or EvaluationCode.RequiredObservationMissing)
        {
            return value.EvidenceStatus == AssertionStatus.Failed && value.ScenarioStatus == AssertionStatus.NotEvaluated &&
                value.ModelStatus == ModelObservationStatus.NotEvaluated && value.FailureSource == EvaluationFailureSource.None &&
                StructuralCountsAreZero(value) &&
                (value.Code != EvaluationCode.RequiredObservationMissing || value.ToolObservationCount > 0);
        }

        if (value.EvidenceStatus != AssertionStatus.Passed ||
            value.StructuralMatches + value.StructurallyMissingDefects != value.ExpectedDefects) return false;
        var scenarioCode = value.ProhibitedObservations > 0 ? EvaluationCode.ProhibitedFinding :
            value.DuplicateObservations > 0 ? EvaluationCode.DuplicateObservation :
            value.StructurallyMissingDefects > 0 ? EvaluationCode.ExpectedFindingMissing : EvaluationCode.Scored;
        if (value.ScenarioStatus != (scenarioCode == EvaluationCode.Scored ? AssertionStatus.Passed : AssertionStatus.Failed)) return false;
        if (value.Code == EvaluationCode.AdjudicationInvalid)
        {
            return value.FailureSource == EvaluationFailureSource.Evaluator &&
                value.ModelStatus == ModelObservationStatus.NotEvaluated;
        }
        return value.Code == scenarioCode && value.FailureSource == EvaluationFailureSource.None &&
            value.ModelStatus != ModelObservationStatus.NotEvaluated;
    }

    private static bool ValidContext(EvaluationOutcome value)
    {
        if (value.ConfigurationSha256 is null)
        {
            return value.AttemptSha256 is null && value.ExecutionSha256 is null && value.SourceCommit is null &&
                value.SourceTree is null && value.SourceClean is null && value.Mode is null;
        }
        return EvaluationLimits.Hash(value.ConfigurationSha256) && EvaluationLimits.Hash(value.AttemptSha256) &&
            EvaluationLimits.Hash(value.SourceCommit, 40) && EvaluationLimits.Hash(value.SourceTree, 40) &&
            value.SourceClean is not null && value.Mode is "deterministic" or "live" &&
            (value.ExecutionSha256 is null || EvaluationLimits.Hash(value.ExecutionSha256));
    }

    private static bool ValidCounts(EvaluationOutcome value)
    {
        if (value.FindingCount is < 0 or > AgentLimits.Findings ||
            value.ToolObservationCount is < 0 or > AgentLimits.ToolCalls ||
            value.ExpectedDefects is < 0 or > EvaluationLimits.Defects ||
            value.StructuralMatches < 0 || value.StructuralMatches > Math.Min(value.FindingCount, value.ExpectedDefects) ||
            value.StructurallyMissingDefects < 0 || value.StructurallyMissingDefects > value.ExpectedDefects ||
            value.DuplicateObservations < 0 || value.DuplicateObservations > value.FindingCount - value.StructuralMatches ||
            (value.DuplicateObservations > 0 && value.StructuralMatches == 0) ||
            value.ProhibitedObservations < 0 || value.ProhibitedObservations > value.FindingCount ||
            value.AdjudicatedTrue < 0 || value.AdjudicatedTrue > value.FindingCount ||
            value.AdjudicatedFalse < 0 || value.AdjudicatedFalse > value.FindingCount ||
            value.AdjudicatedDefects < 0 || value.AdjudicatedDefects > Math.Min(value.AdjudicatedTrue, value.StructuralMatches) ||
            value.UnadjudicatedFindings < 0 || value.UnadjudicatedFindings > value.FindingCount) return false;
        if (value.ModelStatus == ModelObservationStatus.NotEvaluated)
        {
            return value.AdjudicatedTrue == 0 && value.AdjudicatedFalse == 0 &&
                value.AdjudicatedDefects == 0 && value.UnadjudicatedFindings == 0;
        }
        return value.AdjudicatedTrue + value.AdjudicatedFalse + value.UnadjudicatedFindings == value.FindingCount &&
            (value.ModelStatus == ModelObservationStatus.Adjudicated ? value.UnadjudicatedFindings == 0 : value.UnadjudicatedFindings > 0);
    }

    private static bool StructuralCountsAreZero(EvaluationOutcome value) =>
        value.StructuralMatches == 0 && value.StructurallyMissingDefects == 0 &&
        value.DuplicateObservations == 0 && value.ProhibitedObservations == 0;

    private static bool ValidFailurePair(EvaluationOutcome value) => value.FailureSource switch
    {
        EvaluationFailureSource.None => value.FailureKind == EvaluationFailureKind.None,
        EvaluationFailureSource.Provider => value.FailureKind == EvaluationFailureKind.ProviderCall,
        EvaluationFailureSource.Agent => value.FailureKind == EvaluationFailureKind.MalformedOutput,
        EvaluationFailureSource.Tool => value.FailureKind == EvaluationFailureKind.ToolOperation,
        EvaluationFailureSource.HostState => value.FailureKind == EvaluationFailureKind.StateAdmission,
        EvaluationFailureSource.Evaluator => value.FailureKind == EvaluationFailureKind.InvalidInput,
        EvaluationFailureSource.Unknown => value.FailureKind is EvaluationFailureKind.Unknown or EvaluationFailureKind.MalformedOutput,
        _ => false,
    };
}

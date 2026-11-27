using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;

namespace AgenticPrReview.Runtime.Solus;

// Fixed in-process evidence only. No text, identity, or receipt escapes the review.
internal enum SolusReviewRejection
{
    None,
    InputCapacity,
    InputContextShape,
    InputData,
    InputText,
    ExecutionAuthority,
    ExecutionCancelled,
    BridgeNoExecution,
    BridgeNoWork,
    BridgeWorkIneligible,
    TerminalAccounting,
    CorrelationExecution,
    CorrelationFresh,
    CorrelationSupplied,
    CorrelationHistory,
    CorrelationHistoryReceipt,
    CorrelationHistoryCheckpoint,
    CorrelationCheckpoint,
    CandidateNotSubmitted,
    CandidateDuplicate,
    CandidateClosed,
    CandidateExecution,
    CandidateRepair,
    CandidatePayloadLimit,
    CandidateFinalSchema,
    TerminalBoundsInvalid,
    FindingFieldsInvalid,
    EvidenceFieldsInvalid,
    EvidenceDuplicate,
    EvidenceObservationUnknown,
    EvidenceIdentityMismatch,
    EvidencePathUnobserved,
    EvidenceLinesUnobserved,
    FindingDuplicate,
    CandidateDomainMissing,
    CompletionReentry,
    CompletionExecution,
    CompletionOutcome,
    CompletionAdmission,
    CompletionCandidateStop,
    CompletionReceiptCount,
    CompletionRepair,
    CompletionContinuation,
    ReceiptExecution,
    ReceiptSubmission,
    ReceiptRejected,
    ReceiptContinuation,
}

// The candidate channel carries untrusted Final data only. It has no publisher,
// storage or accepted-state capability. The only follow-on is one Final format repair.
internal sealed class SolusReviewCandidateHost : ICandidateHost
{
    internal const string FormatCorrection =
        "The final review JSON failed format validation. Return only one JSON object with string summary and array findings. " +
        "Each finding requires severity, title, message and an evidence array. Each evidence item requires " +
        "observation_id, path, start_line and end_line, using only existing observations and integer line numbers. " +
        "Use exactly those fields. Do not include Markdown, commentary or any tool calls.";

    private readonly object gate = new();
    private readonly Guid executionId;
    private readonly SolusSnapshotTools tools;
    private Guid submissionId;
    private Guid formatRepairSource;
    private AgentTerminalReview? accepted;
    private int submissions;
    private SolusReviewRejection submissionRejection;
    private bool completed;

    internal SolusReviewCandidateHost(Guid executionId, SolusSnapshotTools tools)
    {
        if (executionId == Guid.Empty) throw new ArgumentException("APR_SOLUS_EXECUTION_INVALID");
        this.executionId = executionId;
        this.tools = tools ?? throw new ArgumentNullException(nameof(tools));
        if (!tools.TryBindCandidateHost(executionId)) throw new ArgumentException("APR_SOLUS_EXECUTION_INVALID");
    }

    internal static CandidateExecutionBounds Bounds() => new(
        Cr1DevelopmentProfile.CandidateSubmissions, Cr1DevelopmentProfile.CandidateRepairs,
        Cr1DevelopmentProfile.CandidateContinuations, Cr1DevelopmentProfile.ResponsePayloadBytes);

    public ValueTask<CandidateFeedback?> SubmitAsync(CandidateSubmission submission,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var rejection = ValidateSubmissionIdentity(submission);
            var first = submissions++ == 0;
            AgentTerminalReview? review = null;
            if (rejection == SolusReviewRejection.None)
                rejection = ValidateSubmissionPayload(submission, out review);
            if (first && rejection == SolusReviewRejection.CandidateFinalSchema)
            {
                formatRepairSource = submission.SubmissionId;
                return ValueTask.FromResult<CandidateFeedback?>(new(executionId, formatRepairSource,
                    CandidateAcknowledgement.Acknowledged, CandidateDecision.Reject, CandidateContinuation.Continue,
                    FormatCorrection));
            }
            var valid = rejection == SolusReviewRejection.None;
            if (valid)
            {
                submissionId = submission.SubmissionId;
                accepted = review;
            }
            else
            {
                if (submissionRejection == SolusReviewRejection.None) submissionRejection = rejection;
                accepted = null;
            }
            return ValueTask.FromResult<CandidateFeedback?>(new(submission.ExecutionId, submission.SubmissionId,
                CandidateAcknowledgement.Acknowledged, valid ? CandidateDecision.Accept : CandidateDecision.Reject,
                CandidateContinuation.End));
        }
    }

    private SolusReviewRejection ValidateSubmissionIdentity(CandidateSubmission submission)
    {
        if (completed) return SolusReviewRejection.CandidateClosed;
        if (submission.ExecutionId != executionId) return SolusReviewRejection.CandidateExecution;
        if (submissions == 0)
            return submission.RepairsSubmissionId is null ? SolusReviewRejection.None : SolusReviewRejection.CandidateRepair;
        if (submissions != 1 || formatRepairSource == Guid.Empty || submissionRejection != SolusReviewRejection.None ||
            submission.SubmissionId == formatRepairSource)
            return SolusReviewRejection.CandidateDuplicate;
        return submission.RepairsSubmissionId == formatRepairSource
            ? SolusReviewRejection.None : SolusReviewRejection.CandidateRepair;
    }

    private SolusReviewRejection ValidateSubmissionPayload(CandidateSubmission submission, out AgentTerminalReview? review)
    {
        review = null;
        if (System.Text.Encoding.UTF8.GetByteCount(submission.Payload) > Cr1DevelopmentProfile.ResponsePayloadBytes)
            return SolusReviewRejection.CandidatePayloadLimit;
        if (!AgentToolArguments.TryFinishReviewProvider(submission.Payload, out var arguments))
            return SolusReviewRejection.CandidateFinalSchema;
        if (TerminalReviewValidator.TryValidate(arguments!, tools.Identity, tools.CurrentObservations,
                out review, out var reason)) return SolusReviewRejection.None;
        return reason switch
        {
            "terminal_bounds_invalid" => SolusReviewRejection.TerminalBoundsInvalid,
            "finding_fields_invalid" => SolusReviewRejection.FindingFieldsInvalid,
            "evidence_fields_invalid" => SolusReviewRejection.EvidenceFieldsInvalid,
            "evidence_duplicate" => SolusReviewRejection.EvidenceDuplicate,
            "evidence_observation_unknown" => SolusReviewRejection.EvidenceObservationUnknown,
            "evidence_identity_mismatch" => SolusReviewRejection.EvidenceIdentityMismatch,
            "evidence_path_unobserved" => SolusReviewRejection.EvidencePathUnobserved,
            "evidence_lines_unobserved" => SolusReviewRejection.EvidenceLinesUnobserved,
            "finding_duplicate" => SolusReviewRejection.FindingDuplicate,
            _ => SolusReviewRejection.CandidateDomainMissing,
        };
    }

    internal SolusReviewRejection SubmissionRejection
    {
        get
        {
            lock (gate)
                return submissionRejection == SolusReviewRejection.None && RepairPending
                    ? SolusReviewRejection.CandidateFinalSchema : submissionRejection;
        }
    }

    private bool RepairPending => submissions == 1 && formatRepairSource != Guid.Empty;

    internal bool RefuseNonFinalRepair()
    {
        lock (gate)
        {
            if (!RepairPending) return false;
            submissionRejection = SolusReviewRejection.CandidateRepair;
            return true;
        }
    }

    // A callback decision alone does not establish actual runtime completion.
    // Only the final correlated acceptance, with an exact predecessor for a repair, can release the value.
    internal bool TryComplete(ContextCandidateExecutionResult result, out AgentTerminalReview? review) =>
        TryComplete(result, out review, out _);

    internal bool TryComplete(ContextCandidateExecutionResult result, out AgentTerminalReview? review,
        out SolusReviewRejection rejection)
    {
        lock (gate)
        {
            review = null;
            rejection = SolusReviewRejection.CompletionReentry;
            if (completed) return false;
            completed = true;
            rejection = CompletionRejection(result);
            if (rejection == SolusReviewRejection.None) review = accepted;
            accepted = null;
            return rejection == SolusReviewRejection.None;
        }
    }

    private SolusReviewRejection CompletionRejection(ContextCandidateExecutionResult result)
    {
        var context = result.Context;
        var candidates = result.Candidates;
        if (submissionRejection != SolusReviewRejection.None) return submissionRejection;
        if (RepairPending) return SolusReviewRejection.CandidateFinalSchema;
        if (submissions == 0) return SolusReviewRejection.CandidateNotSubmitted;
        if (accepted is null) return SolusReviewRejection.CandidateDomainMissing;
        if (context.ExecutionId != executionId) return SolusReviewRejection.CompletionExecution;
        if (context.Outcome?.Reason != AgentTerminationReason.Completed) return SolusReviewRejection.CompletionOutcome;
        if (!((context.Intent == ContextExecutionIntent.Fresh && context.Admission == ContextAdmission.Fresh) ||
              (context.Intent == ContextExecutionIntent.NewRunFromContext && context.Admission == ContextAdmission.Supplied)))
            return SolusReviewRejection.CompletionAdmission;
        if (candidates is null) return SolusReviewRejection.CompletionCandidateStop;
        var failure = ValidateCompletedReceipts(candidates, executionId);
        if (failure != SolusReviewRejection.None) return failure;
        var expectedCount = formatRepairSource == Guid.Empty ? 1 : 2;
        if (candidates.Receipts.Count != expectedCount) return SolusReviewRejection.CompletionReceiptCount;
        if (candidates.Receipts[^1].SubmissionId != submissionId ||
            expectedCount == 2 && candidates.Receipts[0].SubmissionId != formatRepairSource)
            return SolusReviewRejection.ReceiptSubmission;
        return SolusReviewRejection.None;
    }

    internal static SolusReviewRejection ValidateCompletedReceipts(CandidateExecutionResult candidates, Guid executionId)
    {
        if (candidates.StopReason != CandidateStopReason.Completed) return SolusReviewRejection.CompletionCandidateStop;
        if (candidates.Receipts.Count is not (1 or 2)) return SolusReviewRejection.CompletionReceiptCount;
        if (candidates.RepairsAdmitted != candidates.Receipts.Count - 1) return SolusReviewRejection.CompletionRepair;
        if (candidates.ContinuationsAdmitted != 0) return SolusReviewRejection.CompletionContinuation;
        var accepted = candidates.Receipts[^1];
        if (accepted.ExecutionId != executionId) return SolusReviewRejection.ReceiptExecution;
        if (!accepted.IsAccepted) return SolusReviewRejection.ReceiptRejected;
        if (accepted.Continuation != CandidateContinuation.End) return SolusReviewRejection.ReceiptContinuation;
        if (candidates.Receipts.Count == 2)
        {
            var rejected = candidates.Receipts[0];
            if (rejected.ExecutionId != executionId) return SolusReviewRejection.ReceiptExecution;
            if (rejected.Acknowledgement != CandidateAcknowledgement.Acknowledged ||
                rejected.Decision != CandidateDecision.Reject ||
                rejected.Continuation != CandidateContinuation.Continue)
                return SolusReviewRejection.CompletionRepair;
        }
        return SolusReviewRejection.None;
    }

    public override string ToString() => nameof(SolusReviewCandidateHost);
}

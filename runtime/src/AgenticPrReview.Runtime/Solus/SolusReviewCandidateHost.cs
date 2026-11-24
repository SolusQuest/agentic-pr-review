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
// storage, accepted-state, repair or continuation capability.
internal sealed class SolusReviewCandidateHost : ICandidateHost
{
    private readonly object gate = new();
    private readonly Guid executionId;
    private readonly SolusSnapshotTools tools;
    private Guid submissionId;
    private AgentTerminalReview? accepted;
    private bool submitted;
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
            submitted = true;
            AgentTerminalReview? review = null;
            if (rejection == SolusReviewRejection.None)
                rejection = ValidateSubmissionPayload(submission, out review);
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
        if (submitted) return SolusReviewRejection.CandidateDuplicate;
        if (completed) return SolusReviewRejection.CandidateClosed;
        if (submission.ExecutionId != executionId) return SolusReviewRejection.CandidateExecution;
        return submission.RepairsSubmissionId is not null ? SolusReviewRejection.CandidateRepair : SolusReviewRejection.None;
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
        get { lock (gate) return submissionRejection; }
    }

    // A callback decision alone does not establish actual runtime completion.
    // Only one correlated current receipt can release the validated domain value.
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
        if (!submitted) return SolusReviewRejection.CandidateNotSubmitted;
        if (accepted is null) return SolusReviewRejection.CandidateDomainMissing;
        if (context.ExecutionId != executionId) return SolusReviewRejection.CompletionExecution;
        if (context.Outcome?.Reason != AgentTerminationReason.Completed) return SolusReviewRejection.CompletionOutcome;
        if (!((context.Intent == ContextExecutionIntent.Fresh && context.Admission == ContextAdmission.Fresh) ||
              (context.Intent == ContextExecutionIntent.NewRunFromContext && context.Admission == ContextAdmission.Supplied)))
            return SolusReviewRejection.CompletionAdmission;
        if (candidates?.StopReason != CandidateStopReason.Completed) return SolusReviewRejection.CompletionCandidateStop;
        if (candidates.Receipts.Count != 1) return SolusReviewRejection.CompletionReceiptCount;
        if (candidates.RepairsAdmitted != 0) return SolusReviewRejection.CompletionRepair;
        if (candidates.ContinuationsAdmitted != 0) return SolusReviewRejection.CompletionContinuation;
        var receipt = candidates.Receipts[0];
        if (receipt.ExecutionId != executionId) return SolusReviewRejection.ReceiptExecution;
        if (receipt.SubmissionId != submissionId) return SolusReviewRejection.ReceiptSubmission;
        if (!receipt.IsAccepted) return SolusReviewRejection.ReceiptRejected;
        if (receipt.Continuation != CandidateContinuation.End) return SolusReviewRejection.ReceiptContinuation;
        return SolusReviewRejection.None;
    }

    public override string ToString() => nameof(SolusReviewCandidateHost);
}

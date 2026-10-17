using SolusAgent.Api.Candidates;

namespace AgenticPrReview.Runtime.Execution.Solus;

// Numeric terminal eligibility precedes the separate APR domain validator.
// A provider Final/Completed is never itself permission to accept findings.
internal sealed class SolusTerminalAccountingGate(SolusReviewFacts facts, ICandidateHost domain) : ICandidateHost
{
    internal bool Refused { get; private set; }
    public ValueTask<CandidateFeedback?> SubmitAsync(CandidateSubmission submission, CancellationToken token = default)
    {
        if (token.IsCancellationRequested || !facts.CanAdmitTerminal(submission.ExecutionId))
        {
            Refused = true;
            return ValueTask.FromResult<CandidateFeedback?>(new(submission.ExecutionId, submission.SubmissionId,
                CandidateAcknowledgement.Acknowledged, CandidateDecision.Reject, CandidateContinuation.End));
        }
        // No payload is copied, logged, interpreted or retained in this gate.
        return domain.SubmitAsync(submission, token);
    }
    public override string ToString() => nameof(SolusTerminalAccountingGate);
}

using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;

namespace AgenticPrReview.Runtime.Solus;

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
    private bool failed;
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
            var valid = !submitted && !failed && !completed && submission.ExecutionId == executionId &&
                submission.RepairsSubmissionId is null;
            submitted = true;
            AgentTerminalReview? review = null;
            valid = valid && System.Text.Encoding.UTF8.GetByteCount(submission.Payload) <=
                Cr1DevelopmentProfile.ResponsePayloadBytes &&
                AgentToolArguments.TryFinishReviewProvider(submission.Payload, out var arguments) &&
                TerminalReviewValidator.TryValidate(arguments!, tools.Identity, tools.CurrentObservations, out review);
            if (valid)
            {
                submissionId = submission.SubmissionId;
                accepted = review;
            }
            else
            {
                failed = true;
                accepted = null;
            }
            return ValueTask.FromResult<CandidateFeedback?>(new(submission.ExecutionId, submission.SubmissionId,
                CandidateAcknowledgement.Acknowledged, valid ? CandidateDecision.Accept : CandidateDecision.Reject,
                CandidateContinuation.End));
        }
    }

    // A callback decision alone does not establish actual runtime completion.
    // Only one correlated current receipt can release the validated domain value.
    internal bool TryComplete(ContextCandidateExecutionResult result, out AgentTerminalReview? review)
    {
        lock (gate)
        {
            review = null;
            if (completed) return false;
            completed = true;
            var context = result.Context;
            var candidates = result.Candidates;
            if (failed || accepted is null || context.ExecutionId != executionId ||
                context.Outcome?.Reason != AgentTerminationReason.Completed ||
                !((context.Intent == ContextExecutionIntent.Fresh && context.Admission == ContextAdmission.Fresh) ||
                  (context.Intent == ContextExecutionIntent.NewRunFromContext && context.Admission == ContextAdmission.Supplied)) ||
                candidates?.StopReason != CandidateStopReason.Completed || candidates.Receipts.Count != 1 ||
                candidates.RepairsAdmitted != 0 || candidates.ContinuationsAdmitted != 0)
            { accepted = null; return false; }
            var receipt = candidates.Receipts[0];
            if (receipt.ExecutionId != executionId || receipt.SubmissionId != submissionId ||
                !receipt.IsAccepted || receipt.Continuation != CandidateContinuation.End)
            { accepted = null; return false; }
            review = accepted;
            accepted = null;
            return true;
        }
    }

    public override string ToString() => nameof(SolusReviewCandidateHost);
}

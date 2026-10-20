using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

// Historical fixture identity and authored schedule relation only; no restore authority.
internal static class ReplayFixtureIdentity
{
    internal const long Now = 1_800_000_000;
    internal const string Build = "r5-completed-replay";
    internal static ReviewedHeadTransition Transition(AdmittedReplayRun current, AdmittedReplayRun? previous)
    {
        if (previous is null && current.Input.Transition == "initial") return ReviewedHeadTransition.SameHead;
        if (previous is null || current.Input.PreviousRunId != previous.Input.Id) return ReviewedHeadTransition.Unknown;
        if (current.Input.Transition == "same_head" && current.Input.ReviewedIdentity == previous.Input.ReviewedIdentity)
            return ReviewedHeadTransition.SameHead;
        // Authored synthetic sequence authority, not GitHub ancestry or production restore authority.
        // R1 binds the immediate predecessor and scope; the R2 coverage oracle independently pins its authored head pair.
        return current.Input.Transition == "verified_ahead" &&
            current.Input.ReviewedIdentity.RepositoryId == previous.Input.ReviewedIdentity.RepositoryId &&
            current.Input.ReviewedIdentity.ReviewTarget == previous.Input.ReviewedIdentity.ReviewTarget &&
            current.Input.ReviewedIdentity.HeadSha != previous.Input.ReviewedIdentity.HeadSha
            ? ReviewedHeadTransition.VerifiedAhead : ReviewedHeadTransition.Unknown;
    }

}

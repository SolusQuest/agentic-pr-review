using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Quality;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime.LiveAgentVerifierFixture;

internal static class VerifierContinuationReview
{
    internal static bool IsContinuationOnlyReview(
        AgentRunOutcome outcome,
        R3QualityCase testCase)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(testCase);
        if (testCase.Expectation is not
                R3QualityContinuationExpectation expectation ||
            !outcome.CompletedSessionEligible ||
            outcome.Review is not { } review ||
            review.Findings.Length != 0 ||
            !review.Summary.Contains(
                expectation.PriorOnlyMarker,
                StringComparison.Ordinal))
        {
            return false;
        }

        var calls = outcome.Events.OfType<AgentToolCallEvent>().ToArray();
        var terminals = outcome.Events.OfType<AgentTerminalEvent>().ToArray();
        return calls is
            [
            {
                Name: AgentToolRegistry.FinishReviewName,
            } terminalCall,
            ] &&
            terminalCall.CanonicalArguments.AsSpan().SequenceEqual(
                review.CanonicalBytes) &&
            StringComparer.Ordinal.Equals(
                terminalCall.ArgumentsSha256,
                review.TerminalSha256) &&
            !outcome.Events.OfType<AgentToolResultEvent>().Any() &&
            terminals is [var terminal] &&
            StringComparer.Ordinal.Equals(
                terminal.TerminalSha256,
                review.TerminalSha256);
    }

}

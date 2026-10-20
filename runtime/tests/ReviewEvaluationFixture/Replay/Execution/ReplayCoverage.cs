using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

// R2 assertions are separate from the generic executor and from mutable fixture scripts/expected answers.
internal static class ReplayCoverage
{
    internal const string Fact = "R5_PRIOR_ONLY_ORCHID_246";
    internal const string Current = "Current snapshot contains no prior fact.";
    internal static readonly string[] Cases = ["replay-seed", "replay-same", "replay-ahead"];
    internal static readonly string[] Reasoning = [" seed reasoning \nα ", " seed finish ", " same reasoning ", " same finish ", " ahead reasoning ", " ahead finish "];
}

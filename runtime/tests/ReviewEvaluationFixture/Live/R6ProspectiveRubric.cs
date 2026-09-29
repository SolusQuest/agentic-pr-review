using System.Collections.Immutable;
using System.Text;
using AgenticPrReview.Runtime.Agent.Core;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;

// Internal, opt-in evaluation contract for future R6 V5 populations only.
// Source identity and the admitted plan bind implementation and selection.
internal static class R6ProspectiveRubric
{
    internal const string Id = "r6-v5-semantic-v1";
    internal const string CorpusSha256 = R6V5QualityGate.CorpusSha256;

    private const string Specification =
        "r6-v5-semantic-v1|anchor-extra<=2|grounded-one-observation|three-frozen-defects|" +
        "reviewed-off-focus|within-case-duplicate-block|cross-case-deduplicate|" +
        "one-completed-argument-recovery|five-case-complete|known-usage|cleaned";

    internal static readonly string Sha256 = AgentCanonical.HashDomain(
        "apr.r6.v5.prospective-rubric", Encoding.UTF8.GetBytes(Specification));

    internal static readonly ImmutableArray<string> Cases =
        ["cs-defect", "cs-safe", "ts-defect", "ts-safe", "repository-rule"];

    internal static bool ValidSelection(LivePlanRubric? rubric, string corpusSha256,
        ImmutableArray<string> schedule) => rubric is null ||
        rubric.Id == Id && rubric.Sha256 == Sha256 && corpusSha256 == CorpusSha256 &&
        schedule.SequenceEqual(Cases);
}

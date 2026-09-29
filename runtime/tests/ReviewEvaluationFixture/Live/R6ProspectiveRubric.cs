using System.Collections.Immutable;
using System.Text;
using AgenticPrReview.Runtime.Agent.Core;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;

// Internal, opt-in evaluation contract for future R6 V5 populations only.
// Source identity and the admitted plan bind implementation and selection.
internal static class R6ProspectiveRubric
{
    internal const string Id = "r6-v5-semantic-v1";
    internal const string V2Id = "r6-v5-semantic-v2";
    internal const string CorpusSha256 = R6V5QualityGate.CorpusSha256;

    private const string Specification =
        "r6-v5-semantic-v1|anchor-extra<=2|grounded-one-observation|three-frozen-defects|" +
        "reviewed-off-focus|within-case-duplicate-block|cross-case-deduplicate|" +
        "one-completed-argument-recovery|five-case-complete|known-usage|cleaned";

    // V2 moves semantic model-output judgments to the bound independent review
    // while preserving the declared observation and execution requirements.
    private const string V2Specification =
        "r6-v5-semantic-v2|grounded-one-observation|declared-read-file-facts|" +
        "reviewed-focal-anchor-and-severity|reviewed-protected-property|" +
        "evidence-ordinal-safe-use|three-scheduled-focal-defects|" +
        "reviewed-off-focus|within-case-duplicate-block|cross-case-deduplicate|" +
        "one-completed-argument-recovery|five-case-complete|known-usage|cleaned";

    internal static readonly string Sha256 = AgentCanonical.HashDomain(
        "apr.r6.v5.prospective-rubric", Encoding.UTF8.GetBytes(Specification));

    internal static readonly string V2Sha256 = AgentCanonical.HashDomain(
        "apr.r6.v5.prospective-rubric", Encoding.UTF8.GetBytes(V2Specification));

    internal static readonly ImmutableArray<string> Cases =
        ["cs-defect", "cs-safe", "ts-defect", "ts-safe", "repository-rule"];

    internal static bool IsV1(LivePlanRubric? rubric) =>
        rubric is { Id: Id } && rubric.Sha256 == Sha256;

    internal static bool IsV2(LivePlanRubric? rubric) =>
        rubric is { Id: V2Id } && rubric.Sha256 == V2Sha256;

    internal static bool ValidSelection(LivePlanRubric? rubric, string corpusSha256,
        ImmutableArray<string> schedule) => rubric is null ||
        (IsV1(rubric) || IsV2(rubric)) && corpusSha256 == CorpusSha256 &&
        schedule.SequenceEqual(Cases);
}

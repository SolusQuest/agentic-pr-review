using System.Collections.Immutable;
using System.Text.Json;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;

namespace AgenticPrReview.Runtime.Tests.Agent.Quality;

public sealed class R6ProspectiveRubricTests
{
    private static LivePlanDigestInput Plan() => new(
        LiveLimits.PlanFormat,
        new(new string('a', 40), new string('b', 40), true),
        R6ProspectiveRubric.CorpusSha256,
        new(DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model,
            DeepSeekAdapterContext.AdapterFor(DeepSeekRequestProfile.Output65536),
            LivePlanAdmission.ProviderConfigurationSha256(DeepSeekRequestProfile.Output65536)),
        R6ProspectiveRubric.Cases,
        new(5, 40, 327680, 2621440, 2949120, 1800, 4000000,
            new(8192, 65536, 100000)));

    [Fact]
    public void ProspectiveSelectionChangesPlanDigestWithoutChangingOldProjection()
    {
        var old = Plan();
        var oldJson = JsonSerializer.Serialize(old, LiveJsonContext.Default.LivePlanDigestInput);
        Assert.DoesNotContain("\"rubric\"", oldJson, StringComparison.Ordinal);
        Assert.True(LivePlanAdmission.ValidProjection(old));

        var selected = old with
        {
            Rubric = new(R6ProspectiveRubric.Id, R6ProspectiveRubric.Sha256),
        };
        Assert.True(LivePlanAdmission.ValidProjection(selected));
        Assert.NotEqual(LivePlanAdmission.Digest(old), LivePlanAdmission.Digest(selected));
        Assert.Contains("\"rubric\"", JsonSerializer.Serialize(selected,
            LiveJsonContext.Default.LivePlanDigestInput), StringComparison.Ordinal);
        Assert.False(LivePlanAdmission.ValidProjection(selected with
        { Rubric = selected.Rubric! with { Sha256 = new string('0', 64) } }));
        Assert.False(LivePlanAdmission.ValidProjection(selected with
        { CorpusSha256 = new string('0', 64) }));
        Assert.False(LivePlanAdmission.ValidProjection(selected with
        { Schedule = R6ProspectiveRubric.Cases.Reverse().ToImmutableArray() }));
    }
}

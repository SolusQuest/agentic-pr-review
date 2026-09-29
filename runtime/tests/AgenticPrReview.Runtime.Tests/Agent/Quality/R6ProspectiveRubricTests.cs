using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
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
        Assert.Equal("598542ebdcb437e7d98f38573fc55ac32465f9216713f7433f4290488e899b47",
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(oldJson))));
        Assert.Equal("152021a5a113961fb6125f8aeb15f5fcd06394383264424c1388b19c34c72a25",
            LivePlanAdmission.Digest(old));
        Assert.Equal("b73cdcea42672134400a4eab4e77607e1311f1003d9c8bb7b1ee9e654f6adcab",
            R6ProspectiveRubric.Sha256);
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

    [Fact]
    public void V2SelectionUsesDistinctBoundDigestAndPreservesV1()
    {
        var old = Plan();
        var v1 = old with { Rubric = new(R6ProspectiveRubric.Id,
            R6ProspectiveRubric.Sha256) };
        var v2 = old with { Rubric = new(R6ProspectiveRubric.V2Id,
            R6ProspectiveRubric.V2Sha256) };
        Assert.True(LivePlanAdmission.ValidProjection(old));
        Assert.True(LivePlanAdmission.ValidProjection(v1));
        Assert.True(LivePlanAdmission.ValidProjection(v2));
        Assert.NotEqual(R6ProspectiveRubric.Sha256, R6ProspectiveRubric.V2Sha256);
        Assert.Equal("18a9fda0819fd58e8caae97b1be68226df74b1e03a5e5f6b60a4d8d47c7056fd",
            R6ProspectiveRubric.V2Sha256);
        Assert.NotEqual(LivePlanAdmission.Digest(v1), LivePlanAdmission.Digest(v2));
        Assert.False(LivePlanAdmission.ValidProjection(v2 with
        { Rubric = v2.Rubric! with { Sha256 = R6ProspectiveRubric.Sha256 } }));
    }
}

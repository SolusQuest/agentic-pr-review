using System.Text;
using AgenticPrReview.Runtime.Agent;

namespace AgenticPrReview.Runtime.Tests.Agent;

public sealed class SolusReviewCapacityTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"review\":{}}")]
    public void OmittedValuesSelectManagedDefaultsWithoutChangingCurrent(string json)
    {
        Assert.True(Select(json, out var selected));
        Assert.Equal(new SolusReviewCapacity(32, 1800, new(1179648, 1179648, 32768)), selected);
        Assert.Equal(64, AgentLimits.ModelCalls);
        Assert.Equal(900, AgentLimits.DeadlineSeconds);
        Assert.Equal(ReviewTokenBudget.Default, new(2_000_000, 38_000_000, 524_288));
    }

    [Fact]
    public void EveryExplicitLowerAxisSurvivesAndOmittedPeersKeepTheirDefaults()
    {
        Assert.True(Select("{\"review\":{\"maxModelCalls\":2,\"timeoutSeconds\":3," +
            "\"maxUncachedInputTokens\":1,\"maxCachedInputTokens\":8191,\"maxOutputTokens\":1024}}", out var selected));
        Assert.Equal(new SolusReviewCapacity(2, 3, new(1, 8191, 1024)), selected);
        Assert.Equal(8192, selected!.InputAllowance);
        Assert.True(Select("{\"review\":{\"maxUncachedInputTokens\":1}}", out var mixed));
        Assert.Equal(new SolusReviewCapacity(32, 1800, new(1, 1179648, 32768)), mixed);
        Assert.Equal(1179648, mixed!.InputAllowance);
    }

    [Theory]
    [InlineData("maxModelCalls", 32)]
    [InlineData("timeoutSeconds", 1800)]
    [InlineData("maxUncachedInputTokens", 1179648)]
    [InlineData("maxCachedInputTokens", 1179648)]
    [InlineData("maxOutputTokens", 32768)]
    public void ExactMaximumSelectsAndOneOverRejectsTheWholeSelection(string field, long maximum)
    {
        Assert.True(Select("{\"review\":{\"" + field + "\":" + maximum + "}}", out _));
        Assert.False(Select("{\"review\":{\"" + (field == "timeoutSeconds" ? "maxModelCalls" : "timeoutSeconds") + "\":1,\"" + field + "\":" +
            (maximum + 1) + "}}", out var over));
        Assert.Null(over);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"review\":null}")]
    [InlineData("{\"review\":[]}")]
    [InlineData("{\"review\":{\"maxModelCalls\":0}}")]
    [InlineData("{\"review\":{\"maxModelCalls\":-1}}")]
    [InlineData("{\"review\":{\"maxModelCalls\":1.5}}")]
    [InlineData("{\"review\":{\"maxModelCalls\":\"1\"}}")]
    [InlineData("{\"review\":{\"maxModelCalls\":9223372036854775808}}")]
    [InlineData("{\"review\":{\"maxModelCalls\":1,\"maxOutputTokens\":null}}")]
    [InlineData("{\"review\":{\"maxOutputTokens\":1024,\"maxOutputTokens\":8192}}")]
    [InlineData("{\"review\":{\"unknown\":1}}")]
    public void MalformedOrMixedUnsupportedSelectionNeverReturnsAPartialProfile(string json)
    {
        Assert.False(Select(json, out var selected));
        Assert.Null(selected);
    }

    [Fact]
    public void PositiveBudgetsBelowAReservationAreNotRaised()
    {
        Assert.True(Select("{\"review\":{\"maxUncachedInputTokens\":1," +
            "\"maxCachedInputTokens\":1,\"maxOutputTokens\":1}}", out var selected));
        Assert.Equal(new ReviewTokenBudget(1, 1, 1), selected!.Tokens);
        Assert.Equal(2, selected.InputAllowance);
    }

    private static bool Select(string json, out SolusReviewCapacity? selected) =>
        SolusReviewCapacity.TrySelect(Encoding.UTF8.GetBytes(json), out selected);
}

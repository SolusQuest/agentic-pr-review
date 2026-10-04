using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Loop;

namespace AgenticPrReview.Runtime.Tests.Agent.Loop;

public sealed class AgentToolResultBudgetTests
{
    [Theory]
    [InlineData(0L, 65_536, true, 65_536L)]
    [InlineData(0L, 65_537, false, 0L)]
    [InlineData(8_323_072L, 65_536, true, 8_388_608L)]
    [InlineData(8_388_608L, 1, false, 8_388_608L)]
    [InlineData(8_388_607L, 2, false, 8_388_607L)]
    [InlineData(long.MaxValue, 1, false, long.MaxValue)]
    [InlineData(-1L, 1, false, -1L)]
    [InlineData(0L, -1, false, 0L)]
    public void AdmissionIsExactAndFailureLeavesTheCounterIntact(
        long current, int resultBytes, bool admitted, long expected)
    {
        Assert.Equal(admitted, AgentToolResultBudget.TryAdd(current, resultBytes, out var next));
        Assert.Equal(expected, next);
    }

    [Fact]
    public void MaximumResultsReachTheAggregateCeilingBeforeTheCallLimit()
    {
        long bytes = 0;
        const int results = 128;
        for (var call = 0; call < results; call++)
        {
            Assert.True(AgentToolResultBudget.TryAdd(bytes, AgentLimits.ToolResultBytes, out bytes));
        }
        Assert.Equal(8_388_608, bytes);
        Assert.True(results < AgentLimits.ToolCalls);
        Assert.False(AgentToolResultBudget.TryAdd(bytes, 1, out var next));
        Assert.Equal(bytes, next);
        Assert.False(AgentToolResultBudget.TryAdd(bytes, AgentLimits.ToolResultBytes, out next));
        Assert.Equal(bytes, next);
    }
}

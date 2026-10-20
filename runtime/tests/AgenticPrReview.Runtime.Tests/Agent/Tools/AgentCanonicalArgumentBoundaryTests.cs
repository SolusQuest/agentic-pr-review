using System.Text;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime.Tests.Agent.Tools;

public sealed class AgentCanonicalArgumentBoundaryTests
{
    [Fact]
    public void ProviderDefaultsAndExplicitCanonicalNullsRemainDistinct()
    {
        const string canonicalGlobalSearch =
            "{\"query\":\"line\",\"path\":null}";
        Assert.False(AgentToolArguments.TrySearchTextCanonical(
            "{\"query\":\"line\"}",
            out _));
        Assert.True(AgentToolArguments.TrySearchTextCanonical(
            canonicalGlobalSearch,
            out var canonicalSearch));
        Assert.Equal(
            canonicalGlobalSearch,
            Encoding.UTF8.GetString(canonicalSearch!.CanonicalBytes));

        Assert.True(AgentToolArguments.TryReadFile(
            "{\"path\":\"src/a.cs\"}",
            out var providerRead));
        const string canonicalRead =
            "{\"path\":\"src/a.cs\",\"start_line\":1,\"line_count\":800}";
        Assert.Equal(
            canonicalRead,
            Encoding.UTF8.GetString(providerRead!.CanonicalBytes));
    }
}

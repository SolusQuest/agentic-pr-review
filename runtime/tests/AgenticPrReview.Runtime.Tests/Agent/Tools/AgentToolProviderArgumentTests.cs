using System.Text;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime.Tests.Agent.Tools;

public sealed class AgentToolProviderArgumentTests
{
    public static TheoryData<string, string, string> EquivalentSpellings => new()
    {
        {
            AgentToolRegistry.ListFilesName,
            "{ \"after\" : \"src/\\u0061.cs\" , \"prefix\" : \"src\" }",
            "{\"prefix\":\"src\",\"after\":\"src/a.cs\"}"
        },
        {
            AgentToolRegistry.ListChangedFilesName,
            "{ \"after\" : \"src/\\u0061.cs\" }",
            "{\"after\":\"src/a.cs\"}"
        },
        {
            AgentToolRegistry.ReadDiffName,
            "{ \"hunk_count\" : 3 , \"path\" : \"src/\\u0061.cs\" , \"start_hunk\" : 2 }",
            "{\"path\":\"src/a.cs\",\"start_hunk\":2,\"hunk_count\":3}"
        },
        {
            AgentToolRegistry.ReadFileName,
            "{ \"line_count\" : 3 , \"path\" : \"src/\\u0061.cs\" , \"start_line\" : 2 }",
            "{\"path\":\"src/a.cs\",\"start_line\":2,\"line_count\":3}"
        },
        {
            AgentToolRegistry.SearchTextName,
            "{ \"path\" : \"src/\\u0061.cs\" , \"query\" : \"ne\\u0065dle\" }",
            "{\"query\":\"needle\",\"path\":\"src/a.cs\"}"
        },
        {
            AgentToolRegistry.FinishReviewName,
            "{ \"findings\" : [ ] , \"summary\" : \"d\\u006fne\" }",
            "{\"summary\":\"done\",\"findings\":[]}"
        },
    };

    public static TheoryData<string, string> InvalidSpellings => new()
    {
        {
            AgentToolRegistry.ListFilesName,
            "{\"prefix\":\"src\",\"pr\\u0065fix\":\"src\"}"
        },
        {
            AgentToolRegistry.ListFilesName,
            "{\"prefix\":null}"
        },
        {
            AgentToolRegistry.ListChangedFilesName,
            "{\"after\":\"src/a.cs\",\"unknown\":false}"
        },
        {
            AgentToolRegistry.ListChangedFilesName,
            "{\"after\":null}"
        },
        {
            AgentToolRegistry.ReadDiffName,
            "{\"path\":\"src/a.cs\",\"start_hunk\":null}"
        },
        {
            AgentToolRegistry.ReadDiffName,
            "{\"start_hunk\":1,\"hunk_count\":20}"
        },
        {
            AgentToolRegistry.ReadFileName,
            "{\"path\":null}"
        },
        {
            AgentToolRegistry.ReadFileName,
            "{\"path\":\"../outside\"}"
        },
        {
            AgentToolRegistry.SearchTextName,
            "{\"query\":1}"
        },
        {
            AgentToolRegistry.SearchTextName,
            "{\"query\":\"needle\",\"path\":null}"
        },
        {
            AgentToolRegistry.FinishReviewName,
            "{\"summary\":\"done\"}"
        },
        {
            AgentToolRegistry.FinishReviewName,
            "{\"summary\":\"done\",\"summary\":\"done\",\"findings\":[]}"
        },
    };

    [Theory]
    [MemberData(nameof(EquivalentSpellings))]
    public void CurrentProviderSpellingsNormalizeToExistingCanonicalBytes(
        string tool,
        string input,
        string expectedCanonical)
    {
        var canonical = TryProvider(tool, input);

        Assert.NotNull(canonical);
        Assert.Equal(expectedCanonical, Encoding.UTF8.GetString(canonical));
        Assert.False(TryStrict(tool, input));
        Assert.True(TryStrict(tool, expectedCanonical));
    }

    [Theory]
    [MemberData(nameof(InvalidSpellings))]
    public void CurrentProviderAdmissionRemainsClosed(string tool, string input)
    {
        Assert.Null(TryProvider(tool, input));
    }

    [Theory]
    [InlineData(AgentToolRegistry.ListFilesName)]
    [InlineData(AgentToolRegistry.ListChangedFilesName)]
    [InlineData(AgentToolRegistry.ReadDiffName)]
    [InlineData(AgentToolRegistry.ReadFileName)]
    [InlineData(AgentToolRegistry.SearchTextName)]
    [InlineData(AgentToolRegistry.FinishReviewName)]
    public void CurrentProviderAdmissionRejectsTrailingJson(string tool)
    {
        var canonical = tool switch
        {
            AgentToolRegistry.ListFilesName => "{}",
            AgentToolRegistry.ListChangedFilesName => "{}",
            AgentToolRegistry.ReadDiffName => "{\"path\":\"src/a.cs\"}",
            AgentToolRegistry.ReadFileName => "{\"path\":\"src/a.cs\"}",
            AgentToolRegistry.SearchTextName => "{\"query\":\"needle\"}",
            AgentToolRegistry.FinishReviewName =>
                "{\"summary\":\"done\",\"findings\":[]}",
            _ => throw new InvalidOperationException(),
        };

        Assert.Null(TryProvider(tool, string.Concat(canonical, "{}")));
    }

    [Fact]
    public void CurrentProviderAdmissionAppliesStructuralAndByteCaps()
    {
        var excessiveProperties = string.Concat(
            "{\"prefix\":\"src\"",
            string.Concat(Enumerable.Range(0, 65).Select(index =>
                $",\"x{index}\":{index}")),
            "}");
        var excessiveTerminal = string.Concat(
            "{\"summary\":\"",
            new string('x', AgentLimits.TerminalBytes),
            "\",\"findings\":[]}");

        Assert.Null(TryProvider(
            AgentToolRegistry.ListFilesName,
            excessiveProperties));
        Assert.Null(TryProvider(
            AgentToolRegistry.FinishReviewName,
            excessiveTerminal));
    }

    private static byte[]? TryProvider(string tool, string input) => tool switch
    {
        AgentToolRegistry.ListFilesName =>
            AgentToolArguments.TryListFilesProvider(input, out var listFiles)
                ? listFiles!.CanonicalBytes
                : null,
        AgentToolRegistry.ListChangedFilesName =>
            AgentToolArguments.TryListChangedFilesProvider(
                input,
                out var listChangedFiles)
                ? listChangedFiles!.CanonicalBytes
                : null,
        AgentToolRegistry.ReadDiffName =>
            AgentToolArguments.TryReadDiffProvider(input, out var readDiff)
                ? readDiff!.CanonicalBytes
                : null,
        AgentToolRegistry.ReadFileName =>
            AgentToolArguments.TryReadFileProvider(input, out var readFile)
                ? readFile!.CanonicalBytes
                : null,
        AgentToolRegistry.SearchTextName =>
            AgentToolArguments.TrySearchTextProvider(input, out var searchText)
                ? searchText!.CanonicalBytes
                : null,
        AgentToolRegistry.FinishReviewName =>
            AgentToolArguments.TryFinishReviewProvider(input, out var finishReview)
                ? finishReview!.CanonicalBytes
                : null,
        _ => throw new InvalidOperationException(),
    };

    private static bool TryStrict(string tool, string input) => tool switch
    {
        AgentToolRegistry.ListFilesName =>
            AgentToolArguments.TryListFiles(input, out _),
        AgentToolRegistry.ListChangedFilesName =>
            AgentToolArguments.TryListChangedFiles(input, out _),
        AgentToolRegistry.ReadDiffName =>
            AgentToolArguments.TryReadDiff(input, out _),
        AgentToolRegistry.ReadFileName =>
            AgentToolArguments.TryReadFile(input, out _),
        AgentToolRegistry.SearchTextName =>
            AgentToolArguments.TrySearchText(input, out _),
        AgentToolRegistry.FinishReviewName =>
            AgentToolArguments.TryFinishReview(input, out _),
        _ => throw new InvalidOperationException(),
    };
}

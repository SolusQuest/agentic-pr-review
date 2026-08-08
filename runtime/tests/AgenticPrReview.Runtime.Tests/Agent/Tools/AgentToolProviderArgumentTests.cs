using System.Text;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime.Tests.Agent.Tools;

public sealed class AgentToolProviderArgumentTests
{
    private const string ObservationId =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

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
            AgentToolRegistry.ReadFileName,
            "{\"line_count\":4e0,\"path\":\"src/a.cs\",\"start_line\":1.0}",
            "{\"path\":\"src/a.cs\",\"start_line\":1,\"line_count\":4}"
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
        {
            AgentToolRegistry.FinishReviewName,
            "{\"findings\":[{\"evidence\":[{\"end_line\":20.00," +
                "\"observation_id\":\"" + ObservationId + "\"," +
                "\"path\":\"src/a.cs\",\"start_line\":2e1}]," +
                "\"message\":\"message\",\"severity\":\"high\"," +
                "\"title\":\"title\"}],\"summary\":\"done\"}",
            "{\"summary\":\"done\",\"findings\":[{" +
                "\"severity\":\"high\",\"title\":\"title\"," +
                "\"message\":\"message\",\"evidence\":[{" +
                "\"observation_id\":\"" + ObservationId + "\"," +
                "\"path\":\"src/a.cs\",\"start_line\":20," +
                "\"end_line\":20}]}]}"
        },
    };

    public static TheoryData<string, int> EquivalentInt32Spellings => new()
    {
        { "1.0", 1 },
        { "1e0", 1 },
        { "20.00", 20 },
        { "2e1", 20 },
        { "100e-2", 1 },
        { "-2147483648.0", int.MinValue },
        { "214748364700e-2", int.MaxValue },
    };

    public static TheoryData<string> InvalidInt32Spellings => new()
    {
        "1.5",
        "2147483648",
        "-2147483649",
        "1.0000000000000000000000000000000000001",
        "1e2147483647",
        "1e-2147483647",
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

    [Theory]
    [MemberData(nameof(EquivalentInt32Spellings))]
    public void ProviderInt32NormalizationUsesExactDecimalSemantics(
        string input,
        int expected)
    {
        Assert.True(AgentToolArguments.TryProviderInt32(
            Encoding.UTF8.GetBytes(input),
            out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(InvalidInt32Spellings))]
    public void ProviderInt32NormalizationRejectsNonIntegralOrOutOfRangeValues(
        string input)
    {
        Assert.False(AgentToolArguments.TryProviderInt32(
            Encoding.UTF8.GetBytes(input),
            out _));
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

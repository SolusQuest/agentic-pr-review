using AgenticPrReview.Runtime.ActionHostVerifierFixture;

namespace AgenticPrReview.Runtime.Tests.Host.Action;

public sealed class ActionHostOutputOracleTests
{
    private const string Sentinel = "APR178_OUTPUT_SENTINEL\n";
    private static readonly (string Name, string Label)[] Rows =
    [
        ("status", "Status"), ("termination-reason", "Review termination"),
        ("model-calls", "Model calls"), ("provider-attempts", "Provider attempts"),
        ("provider-retries", "Provider retries"), ("provider-failed-attempts", "Failed provider attempts"),
        ("provider-unknown-usage-attempts", "Attempts with unknown usage"),
        ("provider-unknown-cache-partition-attempts", "Attempts with unknown cache partition"),
        ("attempt-accounting-completeness", "Attempt accounting completeness"), ("usage-completeness", "Usage completeness"),
        ("input-tokens", "Known input token sum"), ("input-cache-hit-tokens", "Known cache hit token sum"),
        ("input-cache-miss-tokens", "Known cache miss token sum"), ("output-tokens", "Known output token sum"),
    ];
    private static string Frame(string name, string value)
    {
        const string delimiter = "ghadelimiter_00000000-0000-0000-0000-000000000001";
        return $"{name}<<{delimiter}\n{value}\n{delimiter}\n";
    }
    private static (string Output, string Summary) Fixture(string? input = "9223372036854775807")
    {
        var values = Rows.ToDictionary(row => row.Name, row => row.Name switch
        {
            "status" => "reviewed", "termination-reason" => "review_completed",
            "attempt-accounting-completeness" => "complete", "usage-completeness" => "partial",
            "input-tokens" => input ?? "Not available", _ => "0",
        });
        return (Sentinel + string.Concat(Rows.Where(row => values[row.Name] != "Not available").Select(row => Frame(row.Name, values[row.Name]))),
            string.Join("\n", Rows.Select(row => $"| {row.Label} | {values[row.Name]} |")));
    }
    [Theory]
    [InlineData("9223372036854775807")]
    [InlineData("9007199254740993")]
    [InlineData("0")]
    [InlineData(null)]
    public void NativeFramesMatchExactKnownSummaryStrings(string? input)
    {
        var fixture = Fixture(input);
        Assert.True(FrameworkSupervisor.OutputsMatchSummary(fixture.Output, fixture.Summary, false));
        Assert.True(FrameworkSupervisor.OutputsMatchSummary(fixture.Output.Replace("\n", "\r\n", StringComparison.Ordinal).Replace("APR178_OUTPUT_SENTINEL\r\n", Sentinel, StringComparison.Ordinal), fixture.Summary, false));
    }
    [Theory]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("missing")]
    [InlineData("wrong")]
    [InlineData("malformed")]
    [InlineData("trailing")]
    [InlineData("sentinel")]
    [InlineData("oversize")]
    [InlineData("duplicate-row")]
    public void RejectsUntrustedOrIncompleteNativeProof(string mutation)
    {
        var (output, summary) = Fixture();
        switch (mutation)
        {
            case "duplicate": output += Frame("status", "reviewed"); break;
            case "unknown": output += Frame("private-canary", "PRIVATE_CANARY"); break;
            case "missing": output = output.Replace(Frame("model-calls", "0"), "", StringComparison.Ordinal); break;
            case "wrong": output = output.Replace(Frame("model-calls", "0"), Frame("model-calls", "1"), StringComparison.Ordinal); break;
            case "malformed": output = output.Replace("<<ghadelimiter_", "<<bad_", StringComparison.Ordinal); break;
            case "trailing": output += "junk"; break;
            case "sentinel": output = output[Sentinel.Length..]; break;
            case "oversize": output += new string('x', 16384); break;
            case "duplicate-row": summary += "\n| Model calls | 0 |"; break;
        }
        Assert.False(FrameworkSupervisor.OutputsMatchSummary(output, summary, true));
    }
    [Fact]
    public void UnavailableNeverAcceptsFabricatedZeroAndAbsenceRequiresControlledKill()
    {
        var fixture = Fixture(null);
        Assert.False(FrameworkSupervisor.OutputsMatchSummary(fixture.Output + Frame("input-tokens", "0"), fixture.Summary, false));
        Assert.False(FrameworkSupervisor.OutputsMatchSummary(Sentinel, "", false));
        Assert.True(FrameworkSupervisor.OutputsMatchSummary(Sentinel, "", true));
        Assert.False(FrameworkSupervisor.OutputsMatchSummary(Sentinel + Frame("status", "reviewed"), "", true));
    }
    [Fact]
    public void FixedWrapperSummaryRequiresOnlyFourConstants()
    {
        const string summary = "## Agentic PR Review\n\nThe private review wrapper failed safely.\n\nStatus: failed. Review termination: host_failure. Attempt accounting completeness: unavailable. Usage completeness: unavailable. Provider counts, token sums and state disposition: Not available.\n";
        var output = Sentinel + Frame("status", "failed") + Frame("termination-reason", "host_failure") + Frame("attempt-accounting-completeness", "unavailable") + Frame("usage-completeness", "unavailable");
        Assert.True(FrameworkSupervisor.OutputsMatchSummary(output, summary, false));
        Assert.False(FrameworkSupervisor.OutputsMatchSummary(output + Frame("model-calls", "0"), summary, false));
    }
}

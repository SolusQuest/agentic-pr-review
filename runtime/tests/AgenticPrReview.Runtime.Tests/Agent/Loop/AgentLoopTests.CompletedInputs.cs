using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Quality;

namespace AgenticPrReview.Runtime.Tests.Agent.Loop;

public sealed partial class AgentLoopTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(AgentLimits.PartsPerMessage)]
    public async Task MultipartCurrentInputRetainsGenericLoopCompletion(int partCount)
    {
        var trusted = CompletedInputTrusted();
        Assert.True(AgentStableRequestMaterializer.TryMaterialize(trusted, null, out var materialized));
        var current = new ProjectChatMessage("user",
            Enumerable.Range(0, partCount)
                .Select(index => (ProjectChatContent)new ProjectTextContent(string.Concat("part-", index)))
                .ToArray());
        var run = new AgentRunRequest(Identity, materialized!.StablePlan, "session0",
            [.. materialized.ControlMessages, current]);
        var loop = new AgentLoop(new ScriptedChatClient([Response(TerminalCall("finish0", "done"), 0, 0)]),
            new ScriptedToolExecutor());
        var outcome = await loop.RunAsync(run, CancellationToken.None);

        Assert.True(outcome.CompletedSessionEligible);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task GenericLoopCompletionDoesNotAdmitThePolicyPrefix(int prefixCase)
    {
        var trusted = CompletedInputTrusted();
        Assert.True(AgentStableRequestMaterializer.TryMaterialize(trusted, null,
            out var materialized));
        ProjectChatMessage[][] prefixes =
        [
            [],
            [new("system", [new ProjectTextContent("altered")])],
            [.. materialized!.ControlMessages, new("system", [new ProjectTextContent("extra")])],
            [new("system", [new ProjectTextContent("extra")]), .. materialized.ControlMessages],
        ];
        var run = new AgentRunRequest(Identity, materialized!.StablePlan, "session0",
            [.. prefixes[prefixCase], new("user", [new ProjectTextContent("review")])]);
        var loop = new AgentLoop(new ScriptedChatClient([Response(TerminalCall("finish0", "done"), 0, 0)]),
            new ScriptedToolExecutor());
        var outcome = await loop.RunAsync(run, CancellationToken.None);

        // Completion alone grants no persistence, state admission or policy authority.
        Assert.True(outcome.CompletedSessionEligible);
        var quality = R3QualitySubject.TryCreateFreshCompleted(
            new(run, outcome, trusted, run.InitialMessages.Length - 1),
            R3QualityFreshProcessTwoInputSet.Capture([]));
        Assert.False(quality.Succeeded);
        Assert.Equal("quality_projection_invalid", quality.FailureCode);
    }

    private static AgentTrustedRequest CompletedInputTrusted() => new(
        "repo", 1, "workflow@trusted-sha", "trusted policy A"u8.ToArray(),
        "build", "provider", "model", "adapter");
}

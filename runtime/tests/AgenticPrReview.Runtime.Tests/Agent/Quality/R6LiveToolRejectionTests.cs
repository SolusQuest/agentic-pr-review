using System.Text.Json;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;

namespace AgenticPrReview.Runtime.Tests.Agent.Quality;

public sealed class R6LiveToolRejectionTests
{
    private const string Canary = "APR299_PRIVATE_ARGUMENT_CANARY";
    private static string Corpus => Path.Combine(AppContext.BaseDirectory,
        "fixtures", "agent", "r6", "quality-sandbox");

    private static SnapshotToolExecutor Executor(string caseId)
    {
        var fixture = Assert.IsType<AdmittedReplayFixture>(ReplayAdmission.Load(Corpus).Fixture);
        var run = fixture.Runs.Single(value => value.Input.CaseId == caseId);
        var snapshot = run.CreateSnapshot(Path.GetTempPath());
        return new SnapshotToolExecutor(snapshot, run.CreateFileAccess(snapshot));
    }

    private static ProjectToolCallContent Call(string id, string name, string arguments) =>
        new(id, name, arguments);

    private static ProjectChatResponse Response(params ProjectToolCallContent[] calls) =>
        new(new ProjectChatMessage("assistant", calls), new ProjectChatUsage(1, 1), 1);

    [Fact]
    public void FrozenSnapshotDistinguishesMalformedArgumentsFromUntrackedPath()
    {
        var executor = Executor("repository-rule");
        Assert.Null(LiveToolRejectionProjector.Project(Response(Call("a", AgentToolRegistry.ReadFileName,
            "{\"path\":\"src/Upload.cs\"}")), executor));

        var malformed = LiveToolRejectionProjector.Project(Response(Call("a", AgentToolRegistry.ReadFileName,
            "{\"path\":null}")), executor);
        Assert.Equal(AgentFailureCodes.ToolArgumentsInvalid, malformed?.FailureCode);
        Assert.Equal(AgentToolRegistry.ReadFileName, malformed?.Tool);
        Assert.Equal("arguments_contract_invalid", malformed?.Category);

        var untracked = LiveToolRejectionProjector.Project(Response(Call("a", AgentToolRegistry.ReadFileName,
            "{\"path\":\"src/NotTracked.cs\"}")), executor);
        Assert.Equal(AgentFailureCodes.ToolPathNotTracked, untracked?.FailureCode);
        Assert.Equal(AgentToolRegistry.ReadFileName, untracked?.Tool);
        Assert.Equal("path_not_tracked", untracked?.Category);
    }

    [Fact]
    public void CompleteArgumentPhasePrecedesAnyPreflightProjection()
    {
        var executor = Executor("repository-rule");
        var untracked = Call("one", AgentToolRegistry.ReadFileName,
            "{\"path\":\"src/NotTracked.cs\"}");
        var malformed = Call("two", AgentToolRegistry.SearchTextName, "{\"query\":null}");
        foreach (var response in new[] { Response(untracked, malformed), Response(malformed, untracked) })
        {
            var projection = LiveToolRejectionProjector.Project(response, executor);
            Assert.Equal(AgentFailureCodes.ToolArgumentsInvalid, projection?.FailureCode);
            Assert.Equal(AgentToolRegistry.SearchTextName, projection?.Tool);
            Assert.Equal("arguments_contract_invalid", projection?.Category);
        }
    }

    [Fact]
    public void AmbiguousBatchesAndUnknownNamesNeverInventAttribution()
    {
        var executor = Executor("cs-safe");
        var multipleMalformed = LiveToolRejectionProjector.Project(Response(
            Call("one", AgentToolRegistry.ReadFileName, "{bad"),
            Call("two", AgentToolRegistry.SearchTextName, "{bad")), executor);
        Assert.Equal(AgentFailureCodes.ToolArgumentsInvalid, multipleMalformed?.FailureCode);
        Assert.Equal("unknown", multipleMalformed?.Tool);
        Assert.Equal("unknown", multipleMalformed?.Category);

        var multipleUntracked = LiveToolRejectionProjector.Project(Response(
            Call("one", AgentToolRegistry.ReadFileName, "{\"path\":\"missing-a.cs\"}"),
            Call("two", AgentToolRegistry.ReadFileName, "{\"path\":\"missing-b.cs\"}")), executor);
        Assert.Equal(AgentFailureCodes.ToolPathNotTracked, multipleUntracked?.FailureCode);
        Assert.Equal("unknown", multipleUntracked?.Tool);

        var unknownName = LiveToolRejectionProjector.Project(Response(
            Call("one", Canary, "{\"path\":\"src/SafeCaller.cs\"}")), executor);
        Assert.Null(unknownName);
    }

    [Fact]
    public void ProjectionAndReportNeverSerializeProviderControlledText()
    {
        var projection = LiveToolRejectionProjector.Project(Response(
            Call("one", AgentToolRegistry.ReadFileName, "{\"path\":\"" + Canary + "\"}")),
            Executor("cs-safe"));
        Assert.Equal(AgentFailureCodes.ToolPathNotTracked, projection?.FailureCode);
        var diagnostic = LiveAgentDiagnostic.Capture(1,
            new AgentDiagnostic(AgentFailureCodes.ToolPathNotTracked, 1, 0), projection);
        var json = JsonSerializer.Serialize(diagnostic, LiveJsonContext.Default.LiveAgentDiagnostic);
        Assert.Equal(AgentToolRegistry.ReadFileName, diagnostic.Tool);
        Assert.DoesNotContain(Canary, json, StringComparison.Ordinal);
        Assert.DoesNotContain("path", json.Replace("path_not_tracked", "", StringComparison.Ordinal),
            StringComparison.Ordinal);

        var forged = LiveAgentDiagnostic.Capture(1,
            new AgentDiagnostic(AgentFailureCodes.ToolArgumentsInvalid, 1, 0),
            new LiveToolRejectionProjection(AgentFailureCodes.ToolArgumentsInvalid, Canary,
                "arguments_contract_invalid"));
        Assert.Equal("unknown", forged.Tool);
        Assert.Equal("unknown", forged.Category);
        Assert.DoesNotContain(Canary,
            JsonSerializer.Serialize(forged, LiveJsonContext.Default.LiveAgentDiagnostic),
            StringComparison.Ordinal);
    }
}

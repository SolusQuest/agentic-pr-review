using AgenticPrReview.Runtime.ActionHostVerifierFixture;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Execution.DeepSeek;

namespace AgenticPrReview.Runtime.Tests.Host.Action;

public sealed class CurrentFrameworkTraceOracleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualCurrentFrameworkAuthoredWorkHasIndependentClosedTrace(bool successor)
    {
        var root = Directory.CreateTempSubdirectory("apr-s5-framework-trace-").FullName;
        try
        {
            File.WriteAllText(Path.Join(root, "mode"), successor ? "continuation" : "continuation-seed");
            File.WriteAllText(Path.Join(root, "s5-current-framework-work"), "host-only-structural-records");
            var context = new DeepSeekAdapterContext(DeepSeekAdapterContext.Provider,
                DeepSeekAdapterContext.Model, DeepSeekAdapterContext.Adapter, "s5-framework-proof");
            var trusted = new AgentTrustedRequest("repo", 409, "trusted-workflow", "fixed policy"u8.ToArray(),
                "test-build", context.ProviderId, context.ModelId, context.AdapterId);
            Assert.True(AgentStableRequestMaterializer.TryMaterialize(trusted, null, out var materialized));
            var run = new AgentRunRequest(new("repo", 409, new('a', 40), new('b', 40)),
                materialized!.StablePlan, context.SessionId,
                [.. materialized.ControlMessages, new("user", [new ProjectTextContent("Review only the current fixture.")])]);
            const string path = "proof/apr178-path-canary.txt";
            Directory.CreateDirectory(Path.Join(root, "proof"));
            const string content = "APR178_TOOL_DATA_CANARY";
            File.WriteAllText(Path.Join(root, path), content + "\n");
            var diff = new ReviewedDiffSource(run.ReviewedIdentity, path, null, "modified", false,
                [new ReviewedDiffHunk(0, 0, 1, 1, [new ReviewedDiffLine("addition", null, 1, content)])]);
            var snapshot = new ReviewedSnapshot(run.ReviewedIdentity, root, [path],
                [new ReviewedChangedFile(path, null, "modified", 1, 0, 1, "available", diff.PatchSha256, false)], [diff]);
            var outcome = await new FrameworkAuthoredRunnerFactory(root).RunWorkAsync(run,
                new SnapshotToolExecutor(snapshot, new VerifiedReviewedFileAccess()), default);
            Assert.True(outcome.Succeeded, outcome.Diagnostic?.Code);
            var count = successor ? 3 : 6;
            Assert.True(FrameworkSupervisor.CurrentFrameworkTraceIsValid(root, count, successor));
            Assert.False(FrameworkSupervisor.CurrentHostTraceIsValid(root, count, successor));
            Assert.False(FrameworkSupervisor.TypedTraceIsValid(root, count, successor));
            var tracePath = Path.Join(root, "current-framework-chat-trace.tsv");
            var original = File.ReadAllLines(tracePath);
            foreach (var mutation in new[] { "domain", "ordinal", "outcome", "source-plan", "prior-history",
                "message-count", "result-count", "argument-digest", "request-digest", "missing-row", "duplicate-row" })
            {
                var lines = original.ToArray();
                var fields = lines[0].Split('\t');
                switch (mutation)
                {
                    case "domain": fields[0] = "apr-s5-current-host-typed-work-v1"; break;
                    case "ordinal": fields[1] = "2"; break;
                    case "outcome": fields[2] = "rejected"; break;
                    case "source-plan": fields[3] = successor ? "fresh" : "successor"; break;
                    case "prior-history": fields[4] = "1"; break;
                    case "message-count": fields[5] = "4"; break;
                    case "result-count": fields[6] = "6"; break;
                    case "argument-digest": fields[7] = new string('a', 64); break;
                    case "request-digest": fields[8] = "invalid"; break;
                    case "missing-row": lines = lines[..^1]; break;
                    case "duplicate-row": lines = [.. lines, lines[0]]; break;
                }
                lines[0] = string.Join('\t', fields);
                File.WriteAllLines(tracePath, lines);
                Assert.False(FrameworkSupervisor.CurrentFrameworkTraceIsValid(root, count, successor), mutation);
            }
        }
        finally { Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData(false, "result-id")]
    [InlineData(true, "result-id")]
    [InlineData(false, "call-id")]
    [InlineData(true, "call-id")]
    [InlineData(false, "arguments")]
    [InlineData(true, "arguments")]
    [InlineData(false, "message-order")]
    [InlineData(true, "message-order")]
    public async Task ActualObserverRejectsMalformedCurrentExchangeBeforeNextScriptCall(bool successor, string mutation)
    {
        var root = Directory.CreateTempSubdirectory("apr-s5-framework-input-").FullName;
        try
        {
            File.WriteAllText(Path.Join(root, "mode"), successor ? "continuation" : "continuation-seed");
            File.WriteAllText(Path.Join(root, "s5-current-framework-work"), "host-only-structural-records");
            var context = new DeepSeekAdapterContext(DeepSeekAdapterContext.Provider,
                DeepSeekAdapterContext.Model, DeepSeekAdapterContext.Adapter, "s5-framework-input-proof");
            var observer = new CurrentFrameworkChatObserver(root, new FrameworkProviderHandler(root, context));
            ProjectChatMessage[] initial = [new("system", [new ProjectTextContent("trusted policy")]),
                new("user", [new ProjectTextContent("current review")])];
            var response = await observer.GetResponseAsync(new(initial, [], null, ThinkingRequired: true), default);
            var call = Assert.Single(response.Message.Contents.OfType<ProjectToolCallContent>());
            if (!successor) call = call with { ArgumentsJson = "{\"after\":null}" };
            var result = new ProjectToolResultContent(call.CallId, "{\"current_observation\":true}");
            if (mutation == "call-id") call = call with { CallId = "foreign-call" };
            if (mutation == "result-id") result = result with { CallId = "foreign-result" };
            if (mutation == "arguments") call = call with { ArgumentsJson = "{}" };
            ProjectChatMessage assistant = response.Message with { Contents = [call] };
            ProjectChatMessage tool = new("tool", [result]);
            ProjectChatMessage[] messages = [.. initial, assistant, tool];
            if (mutation == "message-order") (messages[2], messages[3]) = (messages[3], messages[2]);
            await Assert.ThrowsAsync<ProjectChatNormalizationException>(() =>
                observer.GetResponseAsync(new(messages, [], null, ThinkingRequired: true), default));
            Assert.Equal("1", File.ReadAllText(Path.Join(root, "synthetic-chat-call-count")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void CurrentCarrierAbsenceCannotRelabelHistoricalCanaryObservationsOrWaiveOtherRoutes()
    {
        var root = Directory.CreateTempSubdirectory("apr-s5-framework-carrier-").FullName;
        try
        {
            var table = Path.Join(root, "routes.tsv");
            const string header = "class\tsource\tapproved_private_sinks\tterminal_sinks\tforbidden_sinks\tcardinality\n";
            const string row = "session-plaintext\tagent.session\tstate.encryption-input\tprovider.request\tartifact.raw;github.*;public.*;host.environment;evidence.raw\tequals-session-plaintext-provider-requests\n";
            File.WriteAllText(table, header + row);
            Assert.True(FrameworkSupervisor.EvaluateCurrentFrameworkCanaryRoutes(root, table, []));
            Assert.False(FrameworkSupervisor.EvaluateCanaryRoutes(root, table, []));
            var scenario = Directory.CreateDirectory(Path.Join(root, "scenario")).FullName;
            File.WriteAllText(Path.Join(scenario, "provider-session-plaintext-request-count"), "1");
            var observations = Path.Join(root, "canary-observations.tsv");
            File.WriteAllText(observations, "session-plaintext\tprovider.request\n");
            Assert.True(FrameworkSupervisor.EvaluateCanaryRoutes(root, table, []));
            Assert.False(FrameworkSupervisor.EvaluateCurrentFrameworkCanaryRoutes(root, table, []));
            File.WriteAllText(Path.Join(scenario, "provider-session-plaintext-request-count"), "0");
            foreach (var sink in new[] { "artifact.raw", "github.authorization", "public.output", "host.environment", "evidence.raw" })
            {
                File.WriteAllText(observations, "session-plaintext\t" + sink + "\n");
                Assert.False(FrameworkSupervisor.EvaluateCurrentFrameworkCanaryRoutes(root, table, []), sink);
            }
            File.Delete(observations);
            File.WriteAllText(table, header + row + "public-result\tprovider.response\tagent.validation\t-\tpublic.*\texactly-one\n");
            Assert.False(FrameworkSupervisor.EvaluateCurrentFrameworkCanaryRoutes(root, table, []));
            File.WriteAllText(table, header + row.Replace("equals-session-plaintext-provider-requests", "at-least-one"));
            Assert.False(FrameworkSupervisor.EvaluateCurrentFrameworkCanaryRoutes(root, table, []));
        }
        finally { Directory.Delete(root, true); }
    }

}

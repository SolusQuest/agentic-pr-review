using System.Reflection;
using AgenticPrReview.Runtime.ActionHostTrustedProofPayload;
using AgenticPrReview.Runtime.ActionHostVerifierFixture;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Execution.DeepSeek;

namespace AgenticPrReview.Runtime.Tests.Host.Action;

public sealed class CurrentHostTraceOracleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualCurrentLoopTraceIsAdmittedAndIndependentMutationsAreRejected(bool successor)
    {
        var root = Directory.CreateTempSubdirectory("apr-s5-current-trace-").FullName;
        try
        {
            var context = new DeepSeekAdapterContext(DeepSeekAdapterContext.Provider,
                DeepSeekAdapterContext.Model, DeepSeekAdapterContext.Adapter, "s5-current-host-proof");
            var trusted = new AgentTrustedRequest("repo", 409, "trusted-workflow", "fixed policy"u8.ToArray(),
                "test-build", context.ProviderId, context.ModelId, context.AdapterId);
            Assert.True(AgentStableRequestMaterializer.TryMaterialize(trusted, null, out var materialized));
            var run = new AgentRunRequest(new("repo", 409, new('a', 40), new('b', 40)),
                materialized!.StablePlan, context.SessionId,
                [.. materialized.ControlMessages, new("user", [new ProjectTextContent("Review the fixed current fixture.")])]);
            const string path = "proof/apr178-path-canary.txt";
            Directory.CreateDirectory(Path.Join(root, "proof"));
            File.WriteAllText(Path.Join(root, path), "trusted-proof\n");
            var diff = new ReviewedDiffSource(run.ReviewedIdentity, path, null, "modified", false,
                [new ReviewedDiffHunk(0, 0, 1, 1, [new ReviewedDiffLine("addition", null, 1, "trusted-proof")])]);
            var snapshot = new ReviewedSnapshot(run.ReviewedIdentity, root, [path],
                [new ReviewedChangedFile(path, null, "modified", 1, 0, 1, "available", diff.PatchSha256, false)], [diff]);
            var outcome = await new AgentLoop(Observer(root, new(context, successor)),
                new SnapshotToolExecutor(snapshot, new VerifiedReviewedFileAccess())).RunAsync(run, default);
            Assert.True(outcome.Succeeded, outcome.Diagnostic?.Code);
            var count = successor ? 3 : 6;
            Assert.True(FrameworkSupervisor.CurrentHostTraceIsValid(root, count, successor));
            Assert.False(FrameworkSupervisor.TypedTraceIsValid(root, count, successor));
            var tracePath = Path.Join(root, "verifier-typed-chat-trace.tsv");
            var original = File.ReadAllLines(tracePath);
            foreach (var mutation in new[] { "domain", "ordinal", "outcome", "source-plan", "prior-history",
                "message-count", "result-count", "argument-digest", "request-digest", "missing-row", "duplicate-row" })
            {
                var lines = original.ToArray();
                var fields = lines[0].Split('\t');
                switch (mutation)
                {
                    case "domain": fields[0] = "apr-historical-authored-typed-chat-v1"; break;
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
                Assert.False(FrameworkSupervisor.CurrentHostTraceIsValid(root, count, successor), mutation);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static IProjectChatClient Observer(string root, CurrentHostScriptedChatClient inner)
    {
        const string name = "AgenticPrReview.Runtime.ActionHostTrustedProofVerifier.dll";
        var configuration = typeof(CurrentHostTraceOracleTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        var sibling = Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "..", "..",
            "AgenticPrReview.Runtime.ActionHostTrustedProofVerifier", configuration.ToLowerInvariant(), name));
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Join(repository.FullName, "AGENTS.md"))) repository = repository.Parent;
        Assert.NotNull(repository);
        var standard = Path.Join(repository.FullName, "runtime", "tests", "ActionHostTrustedProofVerifier",
            "bin", configuration, "net10.0", name);
        var assembly = Assembly.LoadFrom(File.Exists(sibling) ? sibling : standard);
        var type = assembly.GetType("AgenticPrReview.Runtime.ActionHostTrustedProofVerifier.CurrentHostRecordingChatClient", true)!;
        return Assert.IsAssignableFrom<IProjectChatClient>(Activator.CreateInstance(type,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [root, inner], null));
    }
}

using System.Text;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Session;

namespace AgenticPrReview.Runtime.Tests.Agent.Session;

public sealed partial class AgentSessionRoundTripTests
{
    private static async Task AssertRecoveryHistoryRebuildsAsync(
        AgentSessionArtifact original,
        AgentSessionTrustedRequest trusted,
        string privateCanary)
    {
        var previous = original;
        for (var generation = 1; generation <= 2; generation++)
        {
            var restored = Restore(previous, trusted, AgentSessionHeadTransition.SameHead);
            Assert.True(restored.Succeeded, restored.Code);
            var run = restored.RunRequest!;
            var outcome = await new AgentLoop(
                new OneResponseChatClient(_ => new ProjectChatResponse(
                    new ProjectChatMessage("assistant", [
                        new ProjectToolCallContent($"finish{generation}", "finish_review", FinishJson),
                    ]),
                    new ProjectChatUsage(1, 1),
                    1)),
                new NeverToolExecutor()).RunAsync(run, CancellationToken.None);
            Assert.True(outcome.CompletedSessionEligible);
            var input = new AgentSessionBuildInput(
                run,
                outcome,
                trusted,
                run.InitialMessages.Length - 1,
                SyntheticContinuationCodec.Instance,
                new AgentSessionPredecessor(
                    previous.Plaintext,
                    previous.SessionSha256,
                    new string('e', 64),
                    previous.Document.Generation,
                    previous.Document.ProducerBaseSha,
                    previous.Document.ProducerHeadSha,
                    previous.Document.PredecessorStateSha256),
                AgentSessionHeadTransition.SameHead);
            var rebuilt = AgentSessionBuilder.Build(input);
            Assert.True(rebuilt.Succeeded, rebuilt.FailureCode);
            var artifact = Assert.IsType<AgentSessionArtifact>(rebuilt.Artifact);
            Assert.Equal(generation, artifact.Document.Generation);
            Assert.Equal(generation + 1, artifact.Document.CompletedRuns.Length);
            Assert.Equal(previous.SessionSha256, artifact.Document.PriorSessionSha256);
            for (var record = 0; record < original.Document.CompletedRuns[0].Records.Length; record++)
            {
                Assert.Equal(
                    AgentSessionCodec.WriteRecordBytes(original.Document.CompletedRuns[0].Records[record]),
                    AgentSessionCodec.WriteRecordBytes(artifact.Document.CompletedRuns[0].Records[record]));
            }
            Assert.DoesNotContain(privateCanary, Encoding.UTF8.GetString(artifact.Plaintext), StringComparison.Ordinal);
            AssertRecoveryPrefixRejectsAlteredEvents(input);
            previous = artifact;
        }

        var finalRestore = Restore(previous, trusted, AgentSessionHeadTransition.SameHead);
        Assert.True(finalRestore.Succeeded, finalRestore.Code);
        Assert.Equal(2, finalRestore.RunRequest!.InitialMessages
            .SelectMany(message => message.Contents).OfType<ProjectRecoveryToolCallContent>().Count());
        Assert.Equal(2, finalRestore.RunRequest.InitialMessages
            .SelectMany(message => message.Contents).OfType<ProjectToolErrorContent>().Count());
    }

    private static void AssertRecoveryPrefixRejectsAlteredEvents(AgentSessionBuildInput input)
    {
        var checkedMutations = 0;
        for (var index = 0; index < input.Run.InitialMessages.Length; index++)
        {
            // The initial event prefix is the stable plan followed by one event per message.
            var eventIndex = index + 1;
            var message = Assert.IsType<AgentMessageEvent>(input.Outcome.Events[eventIndex]);
            for (var partIndex = 0; partIndex < message.Contents.Length; partIndex++)
            {
                foreach (var alteredPart in AlterRecoveryPart(message.Contents[partIndex]))
                {
                    var alteredMessage = message with
                    {
                        Contents = message.Contents.SetItem(partIndex, alteredPart),
                    };
                    var alteredOutcome = input.Outcome with
                    {
                        Events = input.Outcome.Events.SetItem(eventIndex, alteredMessage),
                    };
                    var rejected = AgentSessionBuilder.Build(input with { Outcome = alteredOutcome });
                    Assert.False(rejected.Succeeded);
                    Assert.Equal("session_record_invalid", rejected.FailureCode);
                    Assert.Null(rejected.Artifact);
                    checkedMutations++;
                }
            }
        }

        Assert.Equal(16, checkedMutations);
    }

    private static IEnumerable<AgentMessagePart> AlterRecoveryPart(AgentMessagePart part)
    {
        switch (part)
        {
            case AgentRecoveryToolCallReferencePart call:
                yield return call with { CallId = "substituted" };
                yield return call with { Name = "read_diff" };
                yield return call with { ArgumentsSha256 = new string('0', 64) };
                yield return call with { Rejected = !call.Rejected };
                yield return new AgentToolCallReferencePart(call.CallId, call.Name, call.ArgumentsSha256);
                break;
            case AgentToolErrorReferencePart error:
                yield return error with { CallId = "substituted" };
                yield return error with { ResultSha256 = new string('0', 64) };
                yield return new AgentToolResultReferencePart(error.CallId, error.ResultSha256);
                break;
        }
    }
}

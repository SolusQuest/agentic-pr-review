using System.Collections.Immutable;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Quality;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;

namespace AgenticPrReview.Runtime.Tests.Agent.Quality;

public sealed class FreshQualityProjectionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task AuthoredCompletionCannotAdmitAnAlteredTrustedPolicyPrefix(int prefixCase)
    {
        var fixture = await EvaluationSelfTest.CreateAsync();
        var input = fixture.Input;
        Assert.True(input.Outcome.CompletedSessionEligible);
        Assert.True(AgentStableRequestMaterializer.TryMaterialize(input.TrustedRequest, null, out var materialized));
        ProjectChatMessage[][] prefixes =
        [
            [],
            [new("system", [new ProjectTextContent("altered")])],
            [.. materialized!.ControlMessages, new("system", [new ProjectTextContent("extra")])],
            [new("system", [new ProjectTextContent("extra")]), .. materialized.ControlMessages],
        ];
        var run = input.Run with { InitialMessages = [.. prefixes[prefixCase], input.Run.InitialMessages[^1]] };
        var quality = R3QualitySubject.TryCreateFreshCompleted(
            new(run, input.Outcome, input.TrustedRequest, run.InitialMessages.Length - 1),
            R3QualityFreshProcessTwoInputSet.Capture([]));
        Assert.False(quality.Succeeded);
        Assert.Equal("quality_projection_invalid", quality.FailureCode);
    }

    [Fact]
    public async Task FreshProjectionRejectsResumedInputsAndDetachedExecutionEvents()
    {
        var fixture = await EvaluationSelfTest.CreateAsync();
        Assert.NotNull(fixture.Admit());
        var input = fixture.Input;
        var events = input.Outcome.Events;
        var tool = events.OfType<AgentToolResultEvent>().Single();
        var call = events.OfType<AgentToolCallEvent>().First();
        var mutations = new[]
        {
            input with { CurrentReviewContextIndex = -1 },
            input with { Run = input.Run with { StablePlan = input.Run.StablePlan with { PriorSessionSha256 = new string('a', 64) } } },
            input with { Run = input.Run with { InitialMessages = [..input.Run.InitialMessages, new("user", [new AgenticPrReview.Runtime.Agent.Chat.ProjectTextContent("Historical context")])] } },
            input with { Outcome = input.Outcome with { ReviewedIdentity = input.Run.ReviewedIdentity with { HeadSha = new string('f', 40) } } },
            input with { Outcome = input.Outcome with { Events = events.Remove(tool) } },
            input with { Outcome = input.Outcome with { Events = events.Replace(call, call with { ArgumentsSha256 = new string('f', 64) }) } },
            input with { Outcome = input.Outcome with { Events = events.Replace(tool, tool with { ResultSha256 = new string('f', 64) }) } },
            input with { Outcome = input.Outcome with { Events = events.Add(new AgentTerminalEvent(input.Outcome.Review!.TerminalSha256)) } },
            input with { Outcome = input.Outcome with { Events = events.SetItem(0, new AgentPlanEvent(new string('f', 64))) } },
        };
        foreach (var invalid in mutations) Assert.Null(EvaluationSubject.Admit(invalid, fixture.Run));
    }

    [Fact]
    public async Task CurrentFailedExecutionRejectsNegativeAndOverBudgetDiagnosticCounts()
    {
        var fixture = await EvaluationSelfTest.CreateAsync(ungrounded: true);
        var actual = fixture.Input.Outcome;
        Assert.False(actual.Succeeded);
        Assert.NotNull(actual.Diagnostic);
        Assert.Equal(EvaluationFailureSource.Agent, EvaluationFailure.FromAgentOutcome(actual).Source);
        var diagnostics = new[]
        {
            actual.Diagnostic with { ToolCalls = -1 },
            actual.Diagnostic with { ToolCalls = 513 },
            actual.Diagnostic with { ModelCalls = -1 },
            actual.Diagnostic with { ModelCalls = 65 },
        };
        foreach (var diagnostic in diagnostics)
        {
            var failure = EvaluationFailure.FromAgentOutcome(actual with { Diagnostic = diagnostic });
            Assert.Same(EvaluationFailure.Invalid, failure);
            var report = EvaluationScorer.Failure(fixture.Case, failure, fixture.Attempt);
            Assert.Equal(EvaluationFailureSource.Evaluator, report.FailureSource);
            Assert.Equal(EvaluationFailureKind.InvalidInput, report.FailureKind);
            Assert.Equal(ModelObservationStatus.NotEvaluated, report.ModelStatus);
            Assert.Equal(AssertionStatus.NotEvaluated, report.ScenarioStatus);
            Assert.NotEqual(EvaluationStatus.Completed, report.ExecutionStatus);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ActualFreshThinkingRunRejectsDetachedOrResumedMetadata(bool associated, bool recovery)
    {
        var fixture = await EvaluationSelfTest.CreateAsync(reasoning: true, associateReasoning: associated, recovery: recovery);
        var input = fixture.Input;
        Assert.True(input.Outcome.Succeeded);
        Assert.Null(input.Run.Continuation);
        var candidate = Assert.IsType<AgentContinuationCandidate>(input.Outcome.Continuation);
        Assert.Equal(recovery ? 3 : 2, candidate.Items.Length);
        var emitted = input.Outcome.Events.OfType<AgentContinuationEvent>().ToArray();
        Assert.Equal(recovery ? 3 : 2, emitted.Length);
        var subject = Assert.IsType<EvaluationSubject>(fixture.Admit());
        Assert.Single(subject.GroundedObservations);
        Assert.Single(subject.Findings);
        Assert.Equal(EvaluationStatus.Completed, EvaluationScorer.Evaluate(fixture.Case, subject).ExecutionStatus);
        var first = candidate.Items[0];
        var eventIndex = input.Outcome.Events.IndexOf(emitted[0]);
        var referenceMessage = input.Outcome.Events.OfType<AgentMessageEvent>()
            .First(message => message.Contents.OfType<AgentReasoningReferencePart>().Any());
        var reference = referenceMessage.Contents.OfType<AgentReasoningReferencePart>().Single();
        var mutations = new List<AgenticPrReview.Runtime.Agent.Quality.FreshQualityRun>
        {
            input with { Run = input.Run with { Continuation = new(candidate.ProviderId, candidate.ModelId, candidate.AdapterId,
                candidate.SessionId, [new(first.Readable, first.Opaque, first.Framing, first.AssociatedCallId, first.MessagePosition, first.ContentPosition)]) } },
            input with { Outcome = input.Outcome with { Continuation = null } },
            input with { Outcome = input.Outcome with { Events = input.Outcome.Events.Remove(emitted[0]) } },
            input with { Outcome = input.Outcome with { Events = input.Outcome.Events.Insert(eventIndex, emitted[0]) } },
            input with { Outcome = input.Outcome with { Events = input.Outcome.Events.SetItem(eventIndex, emitted[1]) } },
            input with { Outcome = input.Outcome with { Events = input.Outcome.Events.Replace(referenceMessage, referenceMessage with {
                Contents = referenceMessage.Contents.Replace(reference, reference with { ReadableSha256 = new string('f', 64) }) }) } },
        };
        foreach (var altered in new[]
        {
            candidate with { ProviderId = "wrong-provider" }, candidate with { ModelId = "wrong-model" },
            candidate with { AdapterId = "wrong-adapter" }, candidate with { SessionId = "wrong-session" },
            candidate with { Items = candidate.Items.RemoveAt(0) }, candidate with { Items = candidate.Items.Add(first) },
            candidate with { Items = [.. candidate.Items.Reverse()] },
            candidate with { Items = candidate.Items.Add(first with { ContentPosition = 2 }) },
            candidate with { Items = candidate.Items.Add(first with { MessagePosition = 0 }) },
            candidate with { Items = candidate.Items.SetItem(0, first with { Readable = "detached readable" }) },
            candidate with { Items = candidate.Items.SetItem(0, first with { Opaque = "detached opaque" }) },
            candidate with { Items = candidate.Items.SetItem(0, first with { Framing = "detached framing" }) },
            candidate with { Items = candidate.Items.SetItem(0, first with { MessagePosition = first.MessagePosition + 1 }) },
            candidate with { Items = candidate.Items.SetItem(0, first with { ContentPosition = first.ContentPosition + 1 }) },
            candidate with { Items = candidate.Items.SetItem(0, first with { AssociatedCallId = "unseen-call" }) },
        }) mutations.Add(input with { Outcome = input.Outcome with { Continuation = altered } });
        if (recovery)
        {
            var call = Assert.Single(input.Outcome.Events.OfType<AgentRecoveryToolCallEvent>(), item => item.Rejected);
            var error = Assert.Single(input.Outcome.Events.OfType<AgentToolErrorEvent>(), item => item.CallId == call.CallId);
            Assert.Single(input.Outcome.Events.OfType<AgentToolResultEvent>());
            var skipped = Assert.Single(input.Outcome.Events.OfType<AgentRecoveryToolCallEvent>(), item => !item.Rejected);
            var invalidArguments = System.Text.Encoding.UTF8.GetBytes("{\"path\":null}").ToImmutableArray();
            var invalidHash = AgentCanonical.HashRaw(invalidArguments.AsSpan());
            var skippedReference = referenceMessage.Contents.OfType<AgentRecoveryToolCallReferencePart>()
                .Single(item => item.CallId == skipped.CallId);
            mutations.Add(input with { Outcome = input.Outcome with { Events = input.Outcome.Events
                .Replace(skipped, skipped with { SanitizedArguments = invalidArguments, ArgumentsSha256 = invalidHash })
                .Replace(referenceMessage, referenceMessage with { Contents = referenceMessage.Contents.Replace(skippedReference,
                    skippedReference with { ArgumentsSha256 = invalidHash }) }) } });
            foreach (var events in new[]
            {
                input.Outcome.Events.Remove(call), input.Outcome.Events.Remove(error),
                input.Outcome.Events.Replace(call, call with { Rejected = false }),
                input.Outcome.Events.Replace(call, call with { ArgumentsSha256 = new string('f', 64) }),
                input.Outcome.Events.Replace(call, call with { CallId = "detached-recovery" }),
                input.Outcome.Events.Replace(error, error with { Name = "detached-tool" }),
                input.Outcome.Events.Replace(error, error with { ResultSha256 = new string('f', 64) }),
                input.Outcome.Events.Insert(input.Outcome.Events.IndexOf(error), error),
            }) mutations.Add(input with { Outcome = input.Outcome with { Events = events } });
        }
        foreach (var invalid in mutations) Assert.Null(EvaluationSubject.AdmitFresh(invalid, fixture.Run));
    }

}

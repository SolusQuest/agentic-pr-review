using System.Collections.Immutable;
using System.Text;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Growth.Reset;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;

// A finite admitted corpus plan for report/accounting fixtures. Replies may
// reject the plan or supply its terminal data, but cannot select further work.
// No retry, runtime profile, continuation import or generic correction loop.
// The default source remains unavailable; SDK execution has separate owners.
internal static class AuthoredLiveWork
{
    internal static async Task<AgentRunOutcome> RunAsync(AgentRunRequest run, ReplayScript script,
        IAuthoredResponseSource samples, SnapshotToolExecutor executor, CancellationToken token)
    {
        var events = ImmutableArray.CreateBuilder<AgentLogicalEvent>();
        var messages = run.InitialMessages.ToList();
        var observations = new List<AgentObservation>();
        var calls = 0;
        var tools = 0;
        AgentRunOutcome Failed(string code)
        {
            events.Add(new AgentFailureEvent(code));
            return AgentRunOutcome.Failure(code, calls, tools, events.ToImmutable());
        }
        if (run.Continuation is not null || messages.Count != 2 || messages[0].Role != "system" || messages[1].Role != "user")
            return Failed(AgentFailureCodes.ResponseInvalid);
        events.Add(new AgentPlanEvent(AgentCanonical.StablePlanSha256(run.StablePlan)));
        for (var index = 0; index < messages.Count; index++)
            events.Add(new AgentMessageEvent(index, messages[index].Role,
                [.. messages[index].Contents.Cast<ProjectTextContent>().Select(part => new AgentTextPart(part.Text))]));
        try
        {
            foreach (var turn in script.Turns)
            {
                token.ThrowIfCancellationRequested();
                calls++;
                var request = new ProjectChatRequest(messages.ToArray(), AgentToolRegistry.Definitions.ToArray(), null, ThinkingRequired: true);
                var response = await samples.GetResponseAsync(request, token).ConfigureAwait(false);
                if (response.CapturedResponseBodyBytes > AgentLimits.ResponseBytes) return Failed(AgentFailureCodes.ResponseTooLarge);
                if (response.Usage is not { InputTokens: >= 0, OutputTokens: >= 0 }) return Failed(AgentFailureCodes.UsageInvalid);
                if (response.Message is not { Role: "assistant", Contents: { } contents }) return Failed(AgentFailureCodes.ResponseInvalid);
                var returned = contents.OfType<ProjectToolCallContent>().ToArray();
                if (returned.Any(call => call.Name == "finish_review"))
                {
                    if (returned.Length != 1) return Failed(AgentFailureCodes.TerminalSequenceInvalid);
                    tools++;
                    if (!AgentToolArguments.TryFinishReviewProvider(returned[0].ArgumentsJson, out var finish) ||
                        !TerminalReviewValidator.TryValidate(finish!, run.ReviewedIdentity, observations, out var review))
                        return Failed(AgentFailureCodes.TerminalInvalid);
                    if (turn.ToolCalls is not [{ Name: "finish_review" }] || returned[0].CallId != turn.ToolCalls[0].Id)
                        return Failed(AgentFailureCodes.ResponseInvalid);
                    var id = returned[0].CallId;
                    events.Add(new AgentMessageEvent(messages.Count, "assistant",
                        [new AgentToolCallReferencePart(id, "finish_review", review!.TerminalSha256)]));
                    events.Add(new AgentToolCallEvent(id, "finish_review", review.TerminalSha256, [.. review.CanonicalBytes]));
                    events.Add(new AgentTerminalEvent(review.TerminalSha256));
                    return AgentRunOutcome.Success(review, run.ReviewedIdentity, events.ToImmutable(), null);
                }
                if (returned.Any(call => !AgentToolRegistry.Definitions.Any(tool => tool.Name == call.Name)))
                    return Failed(AgentFailureCodes.UnknownTool);
                var rejection = LiveToolRejectionProjector.Project(response, executor);
                if (rejection is not null) return Failed(rejection.FailureCode);
                if (returned.Length != turn.ToolCalls.Length || returned.Where((value, index) =>
                    value.CallId != turn.ToolCalls[index].Id || value.Name != turn.ToolCalls[index].Name).Any())
                    return Failed(AgentFailureCodes.ResponseInvalid);
                var prepared = turn.ToolCalls.Select(call => AuthoredReviewWork.PrepareCall(call.Id, call.Name, call.ArgumentsJson)).ToArray();
                for (var index = 0; index < prepared.Length; index++)
                {
                    var actual = AuthoredReviewWork.PrepareCall(returned[index].CallId, returned[index].Name, returned[index].ArgumentsJson);
                    if (!actual.CanonicalArguments.AsSpan().SequenceEqual(prepared[index].CanonicalArguments))
                        return Failed(AgentFailureCodes.ResponseInvalid);
                    if (executor.Preflight(prepared[index]) is { } code) return Failed(code);
                }
                var parts = prepared.Select(call => (AgentMessagePart)new AgentToolCallReferencePart(call.CallId,
                    call.Name, AgentCanonical.HashRaw(call.CanonicalArguments))).ToImmutableArray();
                if (turn.ReasoningContent.Length > 0) parts = [new AgentTextPart(turn.ReasoningContent), .. parts];
                events.Add(new AgentMessageEvent(messages.Count, "assistant", parts));
                var messageParts = prepared.Select(call => (ProjectChatContent)new ProjectToolCallContent(call.CallId,
                    call.Name, Encoding.UTF8.GetString(call.CanonicalArguments))).ToArray();
                messages.Add(new("assistant", turn.ReasoningContent.Length > 0
                    ? [new ProjectTextContent(turn.ReasoningContent), .. messageParts] : messageParts));
                foreach (var call in prepared)
                {
                    tools++;
                    var execution = await executor.ExecuteAsync(call, token).ConfigureAwait(false);
                    if (!execution.Succeeded) return Failed(execution.FailureCode ?? AgentFailureCodes.ToolIoFailed);
                    if (!AgentToolResultAdmission.TryAdmit(call, run.ReviewedIdentity, execution, out var json, out var observation))
                        return Failed(AgentFailureCodes.ToolIoFailed);
                    observations.Add(observation);
                    events.Add(new AgentToolCallEvent(call.CallId, call.Name, AgentCanonical.HashRaw(call.CanonicalArguments), [.. call.CanonicalArguments]));
                    events.Add(new AgentToolResultEvent(call.CallId, call.Name, observation.ObservationId,
                        AgentCanonical.HashRaw(execution.CanonicalResult!), [.. execution.CanonicalResult!]));
                    messages.Add(new("tool", [new ProjectToolResultContent(call.CallId, json)]));
                }
            }
            return Failed(AgentFailureCodes.MissingTool);
        }
        catch (OperationCanceledException) { return Failed(AgentFailureCodes.Cancelled); }
        catch (ProjectChatNormalizationException) { return Failed(AgentFailureCodes.ResponseInvalid); }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException)
        { return Failed(AgentFailureCodes.ChatFailed); }
    }
}

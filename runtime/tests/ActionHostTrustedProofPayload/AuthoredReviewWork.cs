using System.Text;
using System.Collections.Immutable;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Tools;

#if S5_FRAMEWORK_HOST
namespace AgenticPrReview.Runtime.ActionHostVerifierFixture;
#elif S5_EVALUATION_HOST
namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Growth.Reset;
#else
namespace AgenticPrReview.Runtime.ActionHostTrustedProofPayload;
#endif

// Executes a finite fixture-owned list, never model-selected work. Every result
// and terminal crosses the actual APR domain validators. No retry, continuation
// import or runtime limit profile is implemented by this test utility.
internal static class AuthoredReviewWork
{
    internal static async Task<AgentRunOutcome> ExecuteAsync(AgentRunRequest run,
        IAgentToolExecutor executor, PreparedAgentToolCall[] steps,
        Func<IReadOnlyList<AgentObservation>, byte[]> buildTerminal,
        Action<ProjectChatRequest, int, string>? observe,
        Func<ProjectChatRequest, int, CancellationToken, Task>? beforeStep,
        CancellationToken token, IReadOnlyList<string>? annotations = null,
        string terminalCallId = "authored-finish", bool recordAuthoredAccounting = true)
    {
        if (run.Continuation is not null || run.InitialMessages is not
            [{ Role: "system" }, { Role: "user" }] ||
            run.InitialMessages.Any(message => message.Contents.Any(part =>
                part is ProjectToolCallContent or ProjectToolResultContent or ProjectReasoningContent)))
            throw new InvalidOperationException("authored_work_cannot_import_history");
        var messages = run.InitialMessages.ToList();
        var events = ImmutableArray.CreateBuilder<AgentLogicalEvent>();
        events.Add(new AgentPlanEvent(AgentCanonical.StablePlanSha256(run.StablePlan)));
        for (var index = 0; index < messages.Count; index++)
            events.Add(new AgentMessageEvent(index, messages[index].Role,
                [.. messages[index].Contents.Cast<ProjectTextContent>().Select(part => new AgentTextPart(part.Text))]));
        var observations = new List<AgentObservation>();
        var attempts = new List<ProviderAttemptObservation>();
        for (var index = 0; index < steps.Length; index++)
        {
            var request = Request(messages);
            var ordinal = index + 1;
            var phase = "failed";
            try
            {
                observe?.Invoke(request, ordinal, "before");
                token.ThrowIfCancellationRequested();
                if (beforeStep is not null) await beforeStep(request, ordinal, token).ConfigureAwait(false);
                var call = steps[index];
                if (executor.Preflight(call) is not null)
                    throw new InvalidOperationException("authored_work_preflight_rejected");
                var execution = await executor.ExecuteAsync(call, token).ConfigureAwait(false);
                if (!AgentToolResultAdmission.TryAdmit(call, run.ReviewedIdentity, execution,
                        out var json, out var observation))
                    throw new InvalidOperationException("authored_work_result_rejected");
                observations.Add(observation);
                var argumentsHash = AgentCanonical.HashRaw(call.CanonicalArguments);
                var annotation = annotations is not null && index < annotations.Count ? annotations[index] : string.Empty;
                var parts = ImmutableArray.CreateBuilder<AgentMessagePart>();
                if (annotation.Length > 0) parts.Add(new AgentTextPart(annotation));
                parts.Add(new AgentToolCallReferencePart(call.CallId, call.Name, argumentsHash));
                events.Add(new AgentMessageEvent(messages.Count, "assistant", parts.ToImmutable()));
                events.Add(new AgentToolCallEvent(call.CallId, call.Name, argumentsHash, [.. call.CanonicalArguments]));
                events.Add(new AgentToolResultEvent(call.CallId, call.Name, observation.ObservationId,
                    AgentCanonical.HashRaw(execution.CanonicalResult!), [.. execution.CanonicalResult!]));
                ProjectChatContent[] contents = annotation.Length > 0
                    ? [new ProjectTextContent(annotation), new ProjectToolCallContent(call.CallId, call.Name, Encoding.UTF8.GetString(call.CanonicalArguments))]
                    : [new ProjectToolCallContent(call.CallId, call.Name, Encoding.UTF8.GetString(call.CanonicalArguments))];
                messages.Add(new("assistant", contents));
                messages.Add(new("tool", [new ProjectToolResultContent(call.CallId, json)]));
                if (recordAuthoredAccounting) attempts.Add(AuthoredObservation(index));
                phase = "succeeded";
            }
            catch (OperationCanceledException)
            {
                phase = "cancelled";
                return AgentRunOutcome.Failure(AgentFailureCodes.Cancelled, ordinal, index, events.ToImmutable())
                    with { Accounting = recordAuthoredAccounting ? ProviderAccounting.Aggregate(attempts) : null };
            }
            finally { observe?.Invoke(request, ordinal, phase); }
        }

        var terminalRequest = Request(messages);
        var terminalOrdinal = steps.Length + 1;
        var terminalPhase = "failed";
        try
        {
            observe?.Invoke(terminalRequest, terminalOrdinal, "before");
            token.ThrowIfCancellationRequested();
            if (beforeStep is not null) await beforeStep(terminalRequest, terminalOrdinal, token).ConfigureAwait(false);
            var terminal = buildTerminal(observations);
            if (recordAuthoredAccounting) attempts.Add(AuthoredObservation(steps.Length));
            if (!AgentToolArguments.TryFinishReviewProvider(Encoding.UTF8.GetString(terminal), out var finish) ||
                !TerminalReviewValidator.TryValidate(finish!, run.ReviewedIdentity, observations, out var review))
                return AgentRunOutcome.Failure(AgentFailureCodes.TerminalInvalid, terminalOrdinal,
                    terminalOrdinal, events.ToImmutable()) with { Accounting = recordAuthoredAccounting ? ProviderAccounting.Aggregate(attempts) : null };
            var finishId = terminalCallId;
            events.Add(new AgentMessageEvent(messages.Count, "assistant",
                [new AgentToolCallReferencePart(finishId, AgentToolRegistry.FinishReviewName, review!.TerminalSha256)]));
            events.Add(new AgentToolCallEvent(finishId, AgentToolRegistry.FinishReviewName,
                review.TerminalSha256, [.. review.CanonicalBytes]));
            events.Add(new AgentTerminalEvent(review.TerminalSha256));
            terminalPhase = "succeeded";
            return AgentRunOutcome.Success(review, run.ReviewedIdentity, events.ToImmutable(), null)
                with { Accounting = recordAuthoredAccounting ? ProviderAccounting.Aggregate(attempts) : null };
        }
        catch (OperationCanceledException)
        {
            terminalPhase = "cancelled";
            return AgentRunOutcome.Failure(AgentFailureCodes.Cancelled, terminalOrdinal, steps.Length, events.ToImmutable())
                with { Accounting = recordAuthoredAccounting ? ProviderAccounting.Aggregate(attempts) : null };
        }
        finally { observe?.Invoke(terminalRequest, terminalOrdinal, terminalPhase); }
    }

    internal static PreparedAgentToolCall PrepareCall(string id, string name, string json) => name switch
    {
        AgentToolRegistry.ReadFileName when AgentToolArguments.TryReadFileProvider(json, out var value) => new PreparedReadFileCall(id, value!),
        AgentToolRegistry.ReadDiffName when AgentToolArguments.TryReadDiffProvider(json, out var value) => new PreparedReadDiffCall(id, value!),
        AgentToolRegistry.ListFilesName when AgentToolArguments.TryListFilesProvider(json, out var value) => new PreparedListFilesCall(id, value!),
        AgentToolRegistry.ListChangedFilesName when AgentToolArguments.TryListChangedFilesProvider(json, out var value) => new PreparedListChangedFilesCall(id, value!),
        AgentToolRegistry.SearchTextName when AgentToolArguments.TrySearchTextProvider(json, out var value) => new PreparedSearchTextCall(id, value!),
        _ => throw new InvalidOperationException("authored_tool_plan_invalid"),
    };

    private static ProjectChatRequest Request(List<ProjectChatMessage> messages) =>
        new(messages.ToArray(), AgentToolRegistry.Definitions.ToArray(), null, ThinkingRequired: true);

    private static ProviderAttemptObservation AuthoredObservation(int ordinal)
    {
        // These numeric inputs are authored fixture facts. No provider send is
        // observed and the aggregate retains unavailable dispatch completeness.
        var capture = new ProviderAttemptCapture(ordinal, 0);
        capture.ObserveUnavailableDispatch();
        capture.RecordUsage(ProviderUsageObservation.Create(3, 2, 1, 2, 5));
        return capture.Freeze(operationCompleted: true, succeeded: true);
    }

}

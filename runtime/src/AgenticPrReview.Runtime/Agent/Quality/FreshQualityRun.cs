using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime.Agent.Quality;

// An in-memory evaluation input, never a continuation or durable state artifact.
internal sealed record FreshQualityRun(AgentRunRequest Run, AgentRunOutcome Outcome,
    AgentTrustedRequest TrustedRequest, int CurrentReviewContextIndex);

internal static class FreshQualityProjection
{
    internal static bool TryValidate(FreshQualityRun input, out string? context)
    {
        context = null;
        var run = input.Run;
        var outcome = input.Outcome;
        if (run is null || outcome is null || !outcome.CompletedSessionEligible || outcome.Diagnostic is not null ||
            run.ReviewedIdentity is null || outcome.ReviewedIdentity != run.ReviewedIdentity || !run.ReviewedIdentity.IsValid() ||
            !AgentValueDomains.IsIdentifier(run.SessionId) || run.Continuation is not null || outcome.Continuation is not null ||
            !AgentStableRequestMaterializer.TryMaterialize(input.TrustedRequest, null, out var stable) ||
            run.StablePlan != stable!.StablePlan || run.ReviewedIdentity.RepositoryId != stable.StablePlan.RepositoryId ||
            run.ReviewedIdentity.ReviewTarget != stable.StablePlan.ReviewTarget ||
            run.InitialMessages is null || run.InitialMessages.Length != stable.ControlMessages.Length + 1 ||
            input.CurrentReviewContextIndex != stable.ControlMessages.Length ||
            run.InitialMessages[^1] is not { Role: "user", Contents: [ProjectTextContent current] } ||
            !AgentValueDomains.IsUtf8(current.Text, 1, AgentLimits.ContentBytes)) return false;
        var expected = stable.ControlMessages.Append(new ProjectChatMessage("user", [new ProjectTextContent(current.Text)])).ToArray();
        for (var i = 0; i < expected.Length; i++)
        {
            if (run.InitialMessages[i] is not { Contents: [ProjectTextContent text] } message ||
                expected[i].Contents is not [ProjectTextContent expectedText] ||
                message.Role != expected[i].Role || text.Text != expectedText.Text) return false;
        }
        var events = outcome.Events;
        if (events.IsDefaultOrEmpty || events.Length > 1 + AgentLimits.Messages + 2 * AgentLimits.ToolCalls ||
            events[0] is not AgentPlanEvent plan || plan.StablePlanSha256 != AgentCanonical.StablePlanSha256(run.StablePlan)) return false;
        var position = 1;
        for (var i = 0; i < expected.Length; i++, position++)
        {
            if (position >= events.Length || events[position] is not AgentMessageEvent
                { Contents: [AgentTextPart part] } message || message.MessageIndex != i || message.Role != expected[i].Role ||
                part.Text != ((ProjectTextContent)expected[i].Contents[0]).Text) return false;
        }
        var nextMessage = expected.Length;
        var callIds = new HashSet<string>(StringComparer.Ordinal);
        var terminalSeen = false;
        while (position < events.Length)
        {
            if (terminalSeen || events[position++] is not AgentMessageEvent { Role: "assistant" } message ||
                message.MessageIndex != nextMessage++ || nextMessage > AgentLimits.Messages || message.Contents.IsDefaultOrEmpty ||
                message.Contents.Length > AgentLimits.PartsPerMessage) return false;
            var references = new List<AgentToolCallReferencePart>();
            foreach (var part in message.Contents)
            {
                if (part is AgentToolCallReferencePart reference) references.Add(reference);
                else if (part is not AgentTextPart text || !AgentValueDomains.IsUtf8(text.Text, 1, AgentLimits.ContentBytes)) return false;
            }
            if (references.Count is < 1 or > AgentLimits.ToolCallsPerResponse) return false;
            foreach (var reference in references)
            {
                if (!AgentValueDomains.IsIdentifier(reference.CallId) || !callIds.Add(reference.CallId) || callIds.Count > AgentLimits.ToolCalls ||
                    position >= events.Length || events[position++] is not AgentToolCallEvent call ||
                    call.CallId != reference.CallId || call.Name != reference.Name || call.ArgumentsSha256 != reference.ArgumentsSha256 ||
                    call.CanonicalArguments.IsDefaultOrEmpty) return false;
                var terminal = call.Name == AgentToolRegistry.FinishReviewName;
                var digest = terminal ? AgentCanonical.HashDomain(AgentCanonical.TerminalDomain, call.CanonicalArguments.AsSpan())
                    : AgentCanonical.HashRaw(call.CanonicalArguments.AsSpan());
                if (digest != call.ArgumentsSha256 || position >= events.Length) return false;
                if (terminal)
                {
                    if (references.Count != 1 || events[position++] is not AgentTerminalEvent completed ||
                        completed.TerminalSha256 != outcome.Review!.TerminalSha256 ||
                        !call.CanonicalArguments.AsSpan().SequenceEqual(outcome.Review.CanonicalBytes)) return false;
                    terminalSeen = true;
                }
                else
                {
                    if (events[position++] is not AgentToolResultEvent result || result.CallId != call.CallId || result.Name != call.Name ||
                        result.CanonicalResult.IsDefaultOrEmpty || result.ResultSha256 != AgentCanonical.HashRaw(result.CanonicalResult.AsSpan())) return false;
                    nextMessage++;
                }
            }
        }
        if (!terminalSeen) return false;
        context = current.Text;
        return true;
    }
}

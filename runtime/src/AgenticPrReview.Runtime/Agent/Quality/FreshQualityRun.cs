using System.Security.Cryptography;
using System.Text;
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
            !AgentValueDomains.IsIdentifier(run.SessionId) || run.Continuation is not null ||
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
        // This run has no input history. Its newly emitted typed reasoning is
        // correlated only as current evidence; it grants no restore authority.
        if (!TryCurrentReasoning(outcome.Continuation, run, expected.Length, out var reasoning)) return false;
        var reasoningSlots = reasoning.ToDictionary(item => (item.MessagePosition, item.ContentPosition));
        var consumedReasoning = new HashSet<(int Message, int Content)>();
        var reasoningPosition = 0;
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
            references.AddRange(message.Contents.OfType<AgentToolCallReferencePart>());
            var recoveries = message.Contents.OfType<AgentRecoveryToolCallReferencePart>().ToArray();
            if (references.Count > 0 && recoveries.Length > 0) return false;
            for (var contentPosition = 0; contentPosition < message.Contents.Length; contentPosition++)
            {
                var part = message.Contents[contentPosition];
                if (part is AgentToolCallReferencePart or AgentRecoveryToolCallReferencePart) continue;
                if (part is AgentReasoningReferencePart reference)
                {
                    var slot = (message.MessageIndex, contentPosition);
                    var emitted = new AgentContinuationEvent(reference.ReadableSha256, reference.OpaqueSha256,
                        reference.FramingSha256, reference.AssociatedCallId, reference.MessagePosition, reference.ContentPosition);
                    if (!reasoningSlots.TryGetValue(slot, out var actual) || emitted != actual ||
                        !consumedReasoning.Add(slot) || reference.AssociatedCallId is { } associated &&
                        !callIds.Contains(associated) && !references.Any(call => call.CallId == associated) &&
                        !recoveries.Any(call => call.CallId == associated)) return false;
                }
                else if (part is not AgentTextPart text || !AgentValueDomains.IsUtf8(text.Text, 1, AgentLimits.ContentBytes)) return false;
            }
            while (reasoningPosition < reasoning.Count && reasoning[reasoningPosition].MessagePosition == message.MessageIndex)
            {
                var item = reasoning[reasoningPosition++];
                if (!consumedReasoning.Contains((item.MessagePosition, item.ContentPosition)) ||
                    position >= events.Length || events[position++] != item) return false;
            }
            if (recoveries.Length > 0)
            {
                if (recoveries.Length > AgentLimits.ToolCallsPerResponse || !recoveries.Any(call => call.Rejected)) return false;
                foreach (var reference in recoveries)
                {
                    if (!AgentValueDomains.IsIdentifier(reference.CallId) || !callIds.Add(reference.CallId) ||
                        callIds.Count > AgentLimits.ToolCalls || reference.Name == AgentToolRegistry.FinishReviewName ||
                        !AgentToolRegistry.Definitions.Any(tool => tool.Name == reference.Name) || position + 1 >= events.Length ||
                        events[position++] is not AgentRecoveryToolCallEvent call || events[position++] is not AgentToolErrorEvent error ||
                        call.CallId != reference.CallId || call.Name != reference.Name || call.Rejected != reference.Rejected ||
                        call.ArgumentsSha256 != reference.ArgumentsSha256 || call.SanitizedArguments.IsDefaultOrEmpty ||
                        call.SanitizedArguments.Length > AgentLimits.ToolArgumentsBytes ||
                        call.ArgumentsSha256 != AgentCanonical.HashRaw(call.SanitizedArguments.AsSpan()) ||
                        error.CallId != call.CallId || error.Name != call.Name || error.CanonicalResult.IsDefaultOrEmpty ||
                        error.CanonicalResult.Length > AgentLimits.ToolResultBytes ||
                        error.ResultSha256 != AgentCanonical.HashRaw(error.CanonicalResult.AsSpan())) return false;
                    var arguments = new UTF8Encoding(false, true).GetString(call.SanitizedArguments.AsSpan());
                    var feedback = new UTF8Encoding(false, true).GetString(error.CanonicalResult.AsSpan());
                    if (call.Rejected ? arguments != AgentRecoveryFeedback.RejectedArguments ||
                        !AgentRecoveryFeedback.IsRejectedError(call.Name, feedback) :
                        !ValidSkippedArguments(call.Name, arguments) || feedback != AgentRecoveryFeedback.BatchNotExecuted)
                        return false;
                    nextMessage++;
                }
                continue;
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
        if (!terminalSeen || reasoningPosition != reasoning.Count || consumedReasoning.Count != reasoning.Count) return false;
        context = current.Text;
        return true;
    }
    private static bool ValidSkippedArguments(string name, string arguments) => name switch
    {
        AgentToolRegistry.ListFilesName => AgentToolArguments.TryListFilesCanonical(arguments, out _),
        AgentToolRegistry.ListChangedFilesName => AgentToolArguments.TryListChangedFilesCanonical(arguments, out _),
        AgentToolRegistry.ReadDiffName => AgentToolArguments.TryReadDiff(arguments, out _),
        AgentToolRegistry.ReadFileName => AgentToolArguments.TryReadFile(arguments, out _),
        AgentToolRegistry.SearchTextName => AgentToolArguments.TrySearchTextCanonical(arguments, out _),
        _ => false,
    };

    private static bool TryCurrentReasoning(AgentContinuationCandidate? candidate, AgentRunRequest run,
        int firstMessage, out List<AgentContinuationEvent> references)
    {
        references = [];
        if (candidate is null) return true;
        if (candidate.ProviderId != run.StablePlan.ProviderId || candidate.ModelId != run.StablePlan.ModelId ||
            candidate.AdapterId != run.StablePlan.AdapterId || candidate.SessionId != run.SessionId ||
            candidate.Items.IsDefault || candidate.Items.Length > AgentLimits.PartsTotal) return false;
        var slots = new HashSet<(int Message, int Content)>();
        long bytes = 0;
        var previousMessage = firstMessage;
        foreach (var item in candidate.Items)
        {
            if (item is null || item.MessagePosition < firstMessage || item.MessagePosition < previousMessage ||
                item.MessagePosition >= AgentLimits.Messages || item.ContentPosition is < 0 or >= AgentLimits.PartsPerMessage ||
                !slots.Add((item.MessagePosition, item.ContentPosition)) ||
                !AgentValueDomains.IsUtf8(item.Readable, 0, AgentLimits.ContentBytes) ||
                !AgentValueDomains.IsUtf8(item.Opaque, 0, AgentLimits.ContentBytes) ||
                !AgentValueDomains.IsUtf8(item.Framing, 1, AgentLimits.ContentBytes) ||
                item.AssociatedCallId is not null && !AgentValueDomains.IsIdentifier(item.AssociatedCallId)) return false;
            previousMessage = item.MessagePosition;
            // Reuse the unchanged generic producer's writer solely for its
            // byte bounds. Discard/zero the bytes; no state is produced here.
            var encoded = AgentRequestWriter.WriteContinuationItem(new(item.Readable, item.Opaque, item.Framing,
                item.AssociatedCallId, item.MessagePosition, item.ContentPosition));
            try
            {
                bytes += encoded.Length;
                if (encoded.Length > AgentLimits.ContinuationItemBytes || bytes > AgentLimits.ContinuationTotalBytes) return false;
            }
            finally { CryptographicOperations.ZeroMemory(encoded); }
            references.Add(new(Hash(item.Readable), Hash(item.Opaque), Hash(item.Framing), item.AssociatedCallId,
                item.MessagePosition, item.ContentPosition));
        }
        return true;
    }

    private static string Hash(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try { return AgentCanonical.HashRaw(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

}

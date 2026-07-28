using AgenticPrReview.Runtime.Agent.Chat;

namespace AgenticPrReview.Runtime.AiAbstractionFixture;

internal static class FixtureScript
{
    internal static ProjectChatResponse ResponseFor(
        FixturePhase phase,
        int turn,
        string scenario)
    {
        if (phase == FixturePhase.Resume)
        {
            return BuildTerminal(
                FixtureConstants.ResumeFinishCallId,
                FixtureConstants.ResumeTerminalSummary);
        }

        return turn switch
        {
            0 => BuildToolResponse(
                FixtureConstants.ReadCallId,
                "read_file",
                ReadArguments(scenario),
                scenario),
            1 => BuildToolResponse(
                FixtureConstants.SearchCallId,
                "search_text",
                """{"path":"src/Widget.cs","query":"PRIOR_ONLY_FACT_81_9f6d3a"}""",
                scenario),
            2 => BuildTerminal(
                FixtureConstants.FinishCallId,
                FixtureConstants.TerminalSummary),
            _ => throw new FixtureFailure("APR_AI_FIXTURE"),
        };
    }

    private static ProjectChatResponse BuildToolResponse(
        string callId,
        string toolName,
        string arguments,
        string scenario)
    {
        if (scenario == "unknown-tool")
        {
            toolName = "unapproved_tool";
        }

        var readable = scenario == "continuation-altered"
            ? FixtureConstants.ReadableContinuation + "-altered"
            : scenario == "continuation-oversized"
                ? new string('r', FixtureConstants.MaxContinuationBytes + 1)
                : FixtureConstants.ReadableContinuation;
        var opaque = scenario == "continuation-altered"
            ? FixtureConstants.OpaqueContinuation + "-altered"
            : FixtureConstants.OpaqueContinuation;
        var framing = scenario == "continuation-wrong-framing"
            ? "user_data"
            : FixtureConstants.ReasoningFraming;
        var associatedCallId = scenario == "continuation-wrong-association"
            ? "call-other-999"
            : callId;
        var messagePosition = scenario == "continuation-misplaced" ? 3 : 2;
        var contentPosition = scenario == "continuation-misplaced" ? 2 : 1;
        var role = scenario == "continuation-wrong-role" ? "user" : "assistant";

        var reasoning = new ProjectReasoningContent(
            readable,
            opaque,
            framing,
            associatedCallId,
            messagePosition,
            contentPosition);
        var toolCall = new ProjectToolCallContent(callId, toolName, arguments);
        ProjectChatContent[] contents = scenario switch
        {
            "continuation-missing" =>
            [
                new ProjectTextContent("Synthetic assistant response."),
                toolCall,
            ],
            "response-ordering" or "continuation-misplaced" =>
            [
                new ProjectTextContent("Synthetic assistant response."),
                toolCall,
                reasoning,
            ],
            _ =>
            [
                new ProjectTextContent("Synthetic assistant response."),
                reasoning,
                toolCall,
            ],
        };
        return new ProjectChatResponse(new ProjectChatMessage(role, contents));
    }

    private static ProjectChatResponse BuildTerminal(string callId, string summary)
    {
        var reasoning = new ProjectReasoningContent(
            FixtureConstants.ReadableContinuation,
            FixtureConstants.OpaqueContinuation,
            FixtureConstants.ReasoningFraming,
            callId,
            2,
            1);
        return new ProjectChatResponse(new ProjectChatMessage(
            "assistant",
            [
                new ProjectTextContent("Synthetic terminal response."),
                reasoning,
                new ProjectToolCallContent(
                    callId,
                    "finish_review",
                    $$"""{"summary":"{{summary}}","findings":[]}"""),
            ]));
    }

    private static string ReadArguments(string scenario) => scenario switch
    {
        "unknown-argument-field" =>
            """{"path":"src/Widget.cs","startLine":1,"endLine":3,"extra":true}""",
        "duplicate-argument-field" =>
            """{"path":"src/Widget.cs","path":"src/Other.cs","startLine":1,"endLine":3}""",
        "malformed-argument" =>
            """{"path":"","startLine":0,"endLine":3}""",
        _ => """{"path":"src/Widget.cs","startLine":1,"endLine":3}""",
    };
}

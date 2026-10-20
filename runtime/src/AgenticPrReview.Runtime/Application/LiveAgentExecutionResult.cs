namespace AgenticPrReview.Runtime;

internal static class R3LiveAgentCodes
{
    internal const string Completed = "r3_live_completed";
    internal const string InputInvalid = "r3_live_input_invalid";
    internal const string SecretInvalid = "r3_live_secret_invalid";
    internal const string CompositionFailed = "r3_live_composition_failed";
    internal const string HandoffUnavailable =
        "r3_live_handoff_unavailable";
    internal const string HandoffCleanupFailed =
        "r3_live_handoff_cleanup_failed";
}

internal sealed class R3LiveAgentResult(
    string code,
    int modelCalls,
    int toolCalls,
    string? stablePlanSha256,
    string? terminalSha256,
    long? acceptedGeneration,
    string? acceptedSessionSha256,
    string? acceptedEnvelopeSha256,
    bool handoffReady)
{
    internal string Code { get; } = code;

    internal int ModelCalls { get; } = modelCalls;

    internal int ToolCalls { get; } = toolCalls;

    internal string? StablePlanSha256 { get; } = stablePlanSha256;

    internal string? TerminalSha256 { get; } = terminalSha256;

    internal long? AcceptedGeneration { get; } = acceptedGeneration;

    internal string? AcceptedSessionSha256 { get; } = acceptedSessionSha256;

    internal string? AcceptedEnvelopeSha256 { get; } = acceptedEnvelopeSha256;

    internal bool HandoffReady { get; } = handoffReady;

    public override string ToString() => "r3_live_agent_result";
}

internal static class R3LiveAgentDiagnosticCodes
{
    internal const string PreparationFailed =
        "r3_live_composition_preparation_failed";
    internal const string StateRestoreFailed =
        "r3_live_composition_state_restore_failed";
    internal const string SnapshotFailed =
        "r3_live_composition_snapshot_failed";
    internal const string TransportFailed =
        "r3_live_composition_transport_failed";
    internal const string AgentRunFailed =
        "r3_live_composition_agent_run_failed";
    internal const string StateCommitFailed =
        "r3_live_composition_state_commit_failed";
    internal const string ResultFailed =
        "r3_live_composition_result_failed";
}

internal sealed class R3LiveAgentExecution(
    R3LiveAgentResult result,
    string? diagnosticCode = null)
{
    internal R3LiveAgentResult Result { get; } = result;

    internal string? DiagnosticCode { get; } = diagnosticCode;

    public override string ToString() => "r3_live_agent_execution";
}


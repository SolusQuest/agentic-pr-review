using System.Collections.Immutable;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Quality;

internal sealed record QualityOperation(string Name, string Arguments);
internal sealed record QualityFact(string Path, int Line, string Text);
internal sealed record QualityCaseSpec(string Id, string Category, string? PositiveCase,
    EvaluationCode ExpectedCode, string? AgentFailure, string Target, int Line,
    bool Safe, bool Sticky, ImmutableArray<QualityOperation> Operations, ImmutableArray<QualityFact> Facts);

// This small acceptance inventory is independent of the mutable corpus manifest and provider answers.
internal static class QualityCoverage
{
    internal static QualityOperation Read(string path) => new(AgentToolRegistry.ReadFileName,
        "{\"path\":\"" + path + "\",\"start_line\":1,\"line_count\":20}");
    private static readonly ImmutableArray<QualityOperation> Caller =
    [new(AgentToolRegistry.ListChangedFilesName, "{}"),
        new(AgentToolRegistry.ReadDiffName, "{\"path\":\"src/Caller.cs\"}"), Read("src/Caller.cs"), Read("src/Lookup.cs")];
    private static readonly ImmutableArray<QualityOperation> Client =
    [Read("src/config.ts"), new(AgentToolRegistry.SearchTextName, "{\"query\":\"timeoutMs\",\"path\":\"src/client.ts\"}"), Read("src/client.ts")];
    private static readonly ImmutableArray<QualityOperation> Rule =
    [new(AgentToolRegistry.ListFilesName, "{\"prefix\":\"rules\"}"), Read("rules/review.md"),
        new(AgentToolRegistry.SearchTextName, "{\"query\":\"Log\",\"path\":\"src/Upload.cs\"}"), Read("src/Upload.cs")];
    private static readonly ImmutableArray<QualityFact> CallerFacts =
    [new("src/Lookup.cs", 3, "    public static string? Find(string key) => null;"),
        new("src/Caller.cs", 3, "    public static string Label(string key) => Lookup.Find(key).Trim();")];
    private static readonly ImmutableArray<QualityFact> ClientFacts =
    [new("src/config.ts", 2, "export const config = { timeoutMs: 0 }; // zero disables the timeout"),
        new("src/client.ts", 2, "export const timeout = config.timeoutMs || 3000;")];
    private static readonly ImmutableArray<QualityFact> RuleFacts =
    [new("rules/review.md", 2, "Never log an upload access token, including during failure handling."),
        new("src/Upload.cs", 3, "    public static void Failed(string accessToken) => Log(accessToken);")];

    internal static ImmutableArray<QualityCaseSpec> Cases { get; } =
    [
        new("cs-defect", "csharp-caller-callee", null, EvaluationCode.Scored, null, "src/Caller.cs", 3, false, false, Caller, CallerFacts),
        new("cs-safe", "csharp-safe-control", "cs-defect", EvaluationCode.Scored, null, "src/SafeCaller.cs", 3, true, false,
            [Read("src/SafeCaller.cs"), Read("src/Lookup.cs")],
            [CallerFacts[0], new("src/SafeCaller.cs", 3, "    public static string Label(string key) => Lookup.Find(key)?.Trim() ?? string.Empty;")]),
        new("ts-defect", "typescript-data-flow", null, EvaluationCode.Scored, null, "src/client.ts", 2, false, false, Client, ClientFacts),
        new("ts-safe", "typescript-safe-control", "ts-defect", EvaluationCode.Scored, null, "src/safe-client.ts", 2, true, false,
            [Read("src/config.ts"), Read("src/safe-client.ts")],
            [ClientFacts[0], new("src/safe-client.ts", 2, "export const timeout = config.timeoutMs ?? 3000;")]),
        new("repository-rule", "context-required-rule", null, EvaluationCode.Scored, null, "src/Upload.cs", 3, false, false, Rule, RuleFacts),
        new("sticky-only", "grounded-no-inline-location", "cs-defect", EvaluationCode.Scored, null, "src/Lookup.cs", 3, false, true,
            [Read("src/Lookup.cs"), Read("src/Caller.cs")], CallerFacts),
        Fault("no-required-tool", EvaluationCode.RequiredToolMissing),
        Fault("irrelevant-tool", EvaluationCode.RequiredObservationMissing),
        Fault("wrong-evidence", EvaluationCode.ExecutionFailed, AgentFailureCodes.TerminalInvalid),
        Fault("wrong-location", EvaluationCode.ExpectedFindingMissing),
        new("safe-invention", "invented-safe-defect", "cs-safe", EvaluationCode.ProhibitedFinding, null, "src/SafeCaller.cs", 3, true, false,
            [Read("src/SafeCaller.cs"), Read("src/Lookup.cs")],
            [CallerFacts[0], new("src/SafeCaller.cs", 3, "    public static string Label(string key) => Lookup.Find(key)?.Trim() ?? string.Empty;")]),
        Fault("duplicate-proposal", EvaluationCode.DuplicateObservation),
        Fault("pathless-proposal", EvaluationCode.ExecutionFailed, AgentFailureCodes.TerminalInvalid),
    ];

    private static QualityCaseSpec Fault(string id, EvaluationCode code, string? failure = null) =>
        new(id, id, "cs-defect", code, failure, "src/Caller.cs", 3, false, false, Caller, CallerFacts);
}

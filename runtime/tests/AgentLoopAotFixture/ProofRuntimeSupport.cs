using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime.AgentLoopAotFixture;

internal static class ProofScenario
{
    internal const string RepositoryId = "solusquest/agentic-pr-review";
    internal const long ReviewTarget = 88;
    internal const string WorkflowIdentity = "runtime-agent-proof";
    internal const string BuildId = "r2-agent-proof";
    internal const string ProviderId = "synthetic-loopback";
    internal const string ModelId = "synthetic-r2";
    internal const string AdapterId = "minimal-http-r2";
    internal const string SessionId = "issue88";
    internal const string ReviewedPath = "reviewed/fact.txt";
    internal const string PriorOnlyFact =
        "NEBULA-7F4C-91A2-R2-PRIOR-ONLY";
    internal const string ContinuationReadable =
        "Synthetic public reasoning marker.";
    internal const string ContinuationOpaque =
        "opaque-r2-7f4c91a2";
    internal const string ContinuationFraming =
        "{\"kind\":\"synthetic-r2\",\"ordinal\":1}";
    internal const long Now = 1_900_000_000;

    private static readonly byte[] Policy =
        "synthetic public R2 agent proof policy"u8.ToArray();

    internal static ReviewedIdentity BootstrapIdentity() => new(
        RepositoryId,
        ReviewTarget,
        new string('1', 40),
        new string('2', 40));

    internal static ReviewedIdentity ContinueIdentity() => new(
        RepositoryId,
        ReviewTarget,
        new string('2', 40),
        new string('3', 40));

    internal static ReviewedDiffSource BootstrapDiffSource(
        ReviewedIdentity identity) => new(
        identity,
        ReviewedPath,
        previousPath: null,
        "modified",
        sourceTruncated: false,
        [
            new ReviewedDiffHunk(
                oldStart: 0,
                oldCount: 0,
                newStart: 1,
                newCount: 1,
                [
                    new ReviewedDiffLine(
                        "addition",
                        OldLine: null,
                        NewLine: 1,
                        PriorOnlyFact),
                ]),
        ]);

    internal static ReviewedChangedFile BootstrapChangedFile(
        ReviewedDiffSource source) => new(
        ReviewedPath,
        PreviousPath: null,
        "modified",
        Additions: 1,
        Deletions: 0,
        Changes: 1,
        "available",
        source.PatchSha256,
        SourceTruncated: false);

    internal static AgentTrustedRequest Trusted() => new(
        RepositoryId,
        ReviewTarget,
        WorkflowIdentity,
        Policy.ToArray(),
        BuildId,
        ProviderId,
        ModelId,
        AdapterId);

    internal static ProjectChatMessage User(string text) =>
        new("user", [new ProjectTextContent(text)]);

}

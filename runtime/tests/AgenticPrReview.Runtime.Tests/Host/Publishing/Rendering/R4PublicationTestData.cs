using System.Collections.Immutable;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Publishing.Rendering;

internal static class R4PublicationTestData
{
    internal const string RepositoryId = "123456789";
    internal const ulong RepositoryIdValue = 123456789;
    internal const long PullRequestNumber = 42;
    internal const string BaseSha =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    internal const string HeadSha =
        "cccccccccccccccccccccccccccccccccccccccc";
    internal const string PolicySha256 =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    internal const string H1Identity =
        "action-contract/v1+payload-build:2026.08";

    internal static R4PublicationScopeV1 Scope { get; } = new(
        RepositoryIdValue,
        987654321,
        ".github/workflows/review.yml",
        "refs/heads/main",
        PullRequestNumber,
        PolicySha256,
        H1Identity);

    internal static ReviewedIdentity Identity(string? headSha = null) => new(
        RepositoryId,
        PullRequestNumber,
        BaseSha,
        headSha ?? HeadSha);

    internal static AgentEvidence Evidence(
        char observation = 'a',
        string path = "src/app.ts",
        int startLine = 7,
        int endLine = 9) =>
        new(new string(observation, 64), path, startLine, endLine);

    internal static AgentFinding Finding(
        string severity = "high",
        string title = "Title",
        string message = "Message",
        ImmutableArray<AgentEvidence>? evidence = null) =>
        new(
            severity,
            title,
            message,
            evidence ?? [Evidence()]);

    internal static AgentRunOutcome Outcome(
        string summary = "Review complete",
        ImmutableArray<AgentFinding>? findings = null,
        ReviewedIdentity? identity = null,
        string? terminalSha256 = null) =>
        AgentRunOutcome.Success(
            new AgentTerminalReview(
                summary,
                findings ?? [],
                terminalSha256 ?? new string('d', 64),
                [1, 2, 3]),
            identity ?? Identity(),
            ImmutableArray<AgentLogicalEvent>.Empty,
            continuation: null);

    internal static R4ValidatedPublicationReview Validated(
        string summary = "Review complete",
        ImmutableArray<AgentFinding>? findings = null,
        ReviewedIdentity? identity = null,
        R4PublicationScopeV1? scope = null)
    {
        Assert.True(R4ValidatedPublicationReview.TryCreate(
            Outcome(summary, findings, identity),
            scope ?? Scope,
            out var validated));
        return Assert.IsType<R4ValidatedPublicationReview>(validated);
    }

    internal static R4RenderedStickyComment Render(
        string summary = "Review complete",
        ImmutableArray<AgentFinding>? findings = null,
        ReviewedIdentity? identity = null,
        R4PublicationScopeV1? scope = null) =>
        R4StickyRenderer.Render(Validated(summary, findings, identity, scope));
}

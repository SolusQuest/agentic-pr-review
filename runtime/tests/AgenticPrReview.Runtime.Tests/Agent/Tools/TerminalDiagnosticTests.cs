using System.Collections.Immutable;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Live;

namespace AgenticPrReview.Runtime.Tests.Agent.Tools;

public sealed class TerminalDiagnosticTests
{
    [Theory]
    [InlineData("valid", null)]
    [InlineData("summary", "terminal_bounds_invalid")]
    [InlineData("severity", "finding_fields_invalid")]
    [InlineData("range", "evidence_fields_invalid")]
    [InlineData("duplicate-evidence", "evidence_duplicate")]
    [InlineData("ungrounded", "evidence_lines_unobserved")]
    [InlineData("observation", "evidence_observation_unknown")]
    [InlineData("identity", "evidence_identity_mismatch")]
    [InlineData("path", "evidence_path_unobserved")]
    [InlineData("duplicate-finding", "finding_duplicate")]
    public void FixedReasonPreservesAdmission(string mutation, string? expected)
    {
        var identity = new ReviewedIdentity("repo", 1, new('a', 40), new('b', 40));
        var observationId = new string('c', 64);
        var evidence = new AgentEvidence(mutation == "observation" ? new('d', 64) : observationId,
            mutation == "path" ? "other.txt" : "a.txt", 1, mutation == "range" ? 0 : mutation == "ungrounded" ? 2 : 1);
        var finding = new AgentFinding(mutation == "severity" ? "PRIVATE_CANARY" : "high", "title", "message",
            mutation == "duplicate-evidence" ? [evidence, evidence] : [evidence]);
        ImmutableArray<AgentFinding> findings = mutation == "duplicate-finding" ? [finding, finding] : [finding];
        var summary = mutation == "summary" ? "" : "done";
        var arguments = new FinishReviewArguments(summary, findings, AgentToolArguments.WriteFinishReview(summary, findings));
        var observations = new[] { new AgentObservation(observationId, mutation == "identity" ? identity with { ReviewTarget = 2 } : identity,
            ImmutableDictionary<string, ImmutableHashSet<int>>.Empty.Add("a.txt", [1])) };
        var accepted = TerminalReviewValidator.TryValidate(arguments, identity, observations, out var review, out var reason);
        Assert.Equal(expected is null, accepted);
        Assert.Equal(expected, reason);
        Assert.Equal(accepted, TerminalReviewValidator.TryValidate(arguments, identity, observations, out _));
        Assert.Equal(accepted, review is not null);
        Assert.True(EconomicsJournal.ValidTerminalReason(AgentFailureCodes.TerminalInvalid, reason));
        if (reason is not null) Assert.False(EconomicsJournal.ValidTerminalReason(AgentFailureCodes.ChatFailed, reason));
    }

    [Fact]
    public void DiagnosticVocabularyCannotCarryPrivateText()
    {
        Assert.False(EconomicsJournal.ValidTerminalReason(AgentFailureCodes.TerminalInvalid, "PRIVATE_CANARY"));
        Assert.True(EconomicsJournal.ValidTerminalReason(AgentFailureCodes.TerminalInvalid, "arguments_invalid"));
        Assert.True(EconomicsJournal.ValidTerminalReason(AgentFailureCodes.TerminalInvalid, null));
    }
}

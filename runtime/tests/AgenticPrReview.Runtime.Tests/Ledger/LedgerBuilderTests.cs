using System.Collections.Immutable;
using System.Text;
using AgenticPrReview.Runtime.Ledger;

namespace AgenticPrReview.Runtime.Tests.Ledger;

public sealed class LedgerBuilderTests
{
    private static readonly ExpectedIdentities Identities = new(
        Repository: "owner/repo",
        HeadRepository: "owner/repo",
        PullRequest: 1,
        WorkflowIdentity: "ci",
        TrustedExecutionDomain: "trusted",
        ProviderId: "provider",
        ModelId: "model-2024-01-01",
        AdapterId: "adapter",
        TemplateId: "template",
        PolicyId: "policy",
        ToolDefinitionId: "tools",
        CacheConfigId: "cacheconfig");

    [Fact]
    public void BuildBootstrapCandidateSucceeds()
    {
        var context = BuildContext(interactionOrdinal: 0);
        var outcome = BuildOutcome(interactionOrdinal: 0);
        var expected = new BootstrapTransition(Identities, "aaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbb", 0);

        var candidateOutcome = LedgerBuilder.CreateBootstrap(expected, context.Value!, outcome.Value!);

        Assert.NotNull(candidateOutcome.Candidate);
        Assert.Empty(candidateOutcome.Diagnostics);
        Assert.Equal("bootstrap", candidateOutcome.Candidate!.Model.Header.Kind);
    }

    [Fact]
    public void BuildOverBoundAppendInteractionsFails()
    {
        // Predecessor at maxItems (64 records = 32 pairs). Appending one more pair exceeds the bound.
        var predecessor = ParseLedger(BuildMaxedPredecessorJson());
        var expected = new ContinuationTransition(
            Identities, "aaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbb",
            predecessor.ContentSha256, "bbbbbbbbbbbbbbbbbbbbbb", 0, 1);

        var context = BuildContextRecord(32, "0000000000000000000000000000000000000000000000000000000000000000");
        var outcome = BuildOutcomeRecord(32, "0000000000000000000000000000000000000000000000000000000000000000");
        var candidateOutcome = LedgerBuilder.AppendContinuation(expected, predecessor, context, outcome);

        Assert.Null(candidateOutcome.Candidate);
        Assert.Single(candidateOutcome.Diagnostics);
        Assert.Equal(LedgerDiagnosticCodes.OverBoundAppend, candidateOutcome.Diagnostics[0].Code);
        Assert.Equal(LedgerDiagnosticCodes.InteractionLimitExceeded, candidateOutcome.Diagnostics[0].CauseCode);
    }

    [Fact]
    public void BuildReviewContextRejectsLoneSurrogate()
    {
        var source = new ValidatedContextSource
        {
            SubjectDigest = "1111111111111111111111111111111111111111111111111111111111111111",
            ReviewedHeadSha = "0000000000000000000000000000000000000000",
            ReviewedBaseSha = "1111111111111111111111111111111111111111",
            ChangedFiles = ImmutableArray<LedgerChangedFile>.Empty
        };
        var interaction = new InteractionIdentity("\uD8000000000000000000000000000000000000000000000000000000000000", 0);

        var outcome = LedgerBuilder.BuildReviewContext(source, Identities, interaction);

        Assert.Null(outcome.Value);
        Assert.Single(outcome.Diagnostics);
        Assert.Equal(LedgerDiagnosticCodes.InvalidUnicode, outcome.Diagnostics[0].Code);
    }

    [Fact]
    public void CreateRecoveryRootRejectsInvalidUnicodeRecord()
    {
        var context = BuildContextRecord(0, "\uD8000000000000000000000000000000000000000000000000000000000000");
        var outcome = BuildOutcomeRecord(0, "0000000000000000000000000000000000000000000000000000000000000000");
        var expected = new RecoveryRootTransition(Identities, "dddddddddddddddddddddd", "eeeeeeeeeeeeeeeeeeeeee", 0, "integrity_mismatch");

        var candidateOutcome = LedgerBuilder.CreateRecoveryRoot(expected, context, outcome);

        Assert.Null(candidateOutcome.Candidate);
        Assert.Single(candidateOutcome.Diagnostics);
        Assert.Equal(LedgerDiagnosticCodes.InvalidUnicode, candidateOutcome.Diagnostics[0].Code);
    }

    private static BuildOutcome<ReviewContextRecord> BuildContext(long interactionOrdinal)
    {
        var source = new ValidatedContextSource
        {
            SubjectDigest = "1111111111111111111111111111111111111111111111111111111111111111",
            ReviewedHeadSha = "0000000000000000000000000000000000000000",
            ReviewedBaseSha = "1111111111111111111111111111111111111111",
            ChangedFiles = ImmutableArray<LedgerChangedFile>.Empty
        };
        var interaction = new InteractionIdentity("0000000000000000000000000000000000000000000000000000000000000000", interactionOrdinal);
        return LedgerBuilder.BuildReviewContext(source, Identities, interaction);
    }

    private static BuildOutcome<ReviewOutcomeRecord> BuildOutcome(long interactionOrdinal)
    {
        var source = new ValidatedOutcomeSource
        {
            Summary = "Summary text.",
            Findings = ImmutableArray<LedgerFinding>.Empty,
            Limitations = ImmutableArray<string>.Empty
        };
        var interaction = new InteractionIdentity("0000000000000000000000000000000000000000000000000000000000000000", interactionOrdinal);
        return LedgerBuilder.BuildReviewOutcome(source, interaction);
    }

    private static ReviewContextRecord BuildContextRecord(long ordinal, string interactionId)
    {
        return new ReviewContextRecord
        {
            Role = "review_context",
            InteractionId = interactionId,
            InteractionOrdinal = ordinal,
            SubjectDigest = "1111111111111111111111111111111111111111111111111111111111111111",
            CacheContractDigest = LedgerCanonicalizer.ComputeCacheContractDigest(Identities),
            ReviewedHeadSha = "0000000000000000000000000000000000000000",
            ReviewedBaseSha = "1111111111111111111111111111111111111111",
            ChangedFiles = ImmutableArray<LedgerChangedFile>.Empty
        };
    }

    private static ReviewOutcomeRecord BuildOutcomeRecord(long ordinal, string interactionId)
    {
        return new ReviewOutcomeRecord
        {
            Role = "review_outcome",
            InteractionId = interactionId,
            InteractionOrdinal = ordinal,
            Summary = "Summary text.",
            Findings = ImmutableArray<LedgerFinding>.Empty,
            Limitations = ImmutableArray<string>.Empty
        };
    }

    private static ValidatedLedger ParseLedger(string json)
    {
        var outcome = LedgerParser.ParseAndValidate(Encoding.UTF8.GetBytes(json));
        Assert.NotNull(outcome.Ledger);
        return outcome.Ledger!;
    }

    private static string BuildMaxedPredecessorJson()
    {
        var sb = new StringBuilder();
        sb.AppendLine("""
{
  "header": {
    "adapterId": "adapter",
    "cacheConfigId": "cacheconfig",
    "headRepository": "owner/repo",
    "kind": "bootstrap",
    "ledgerEpoch": "bbbbbbbbbbbbbbbbbbbbbb",
    "modelId": "model-2024-01-01",
    "policyId": "policy",
    "predecessorLedgerSha256": "bootstrap",
    "providerId": "provider",
    "pullRequest": 1,
    "repository": "owner/repo",
    "sessionEpoch": "aaaaaaaaaaaaaaaaaaaaaa",
    "stateGeneration": 0,
    "templateId": "template",
    "toolDefinitionId": "tools",
    "trustedExecutionDomain": "trusted",
    "workflowIdentity": "ci"
  },
  "prefixContractVersion": 1,
  "records": [
""");

        var digest = LedgerCanonicalizer.ComputeCacheContractDigest(Identities);
        for (var i = 0; i < 32; i++)
        {
            if (i > 0) sb.AppendLine(",");
            var interactionId = $"{i:x64}";
            sb.AppendLine($$"""
    {
      "interactionId": "{{interactionId}}",
      "interactionOrdinal": {{i}},
      "role": "review_context",
      "cacheContractDigest": "{{digest}}",
      "changedFiles": [],
      "reviewedBaseSha": "1111111111111111111111111111111111111111",
      "reviewedHeadSha": "0000000000000000000000000000000000000000",
      "subjectDigest": "1111111111111111111111111111111111111111111111111111111111111111"
    },
    {
      "interactionId": "{{interactionId}}",
      "interactionOrdinal": {{i}},
      "role": "review_outcome",
      "findings": [],
      "limitations": [],
      "summary": "Summary text."
    }
""");
        }

        sb.AppendLine("""
  ],
  "schemaVersion": 1
}
""");
        return sb.ToString();
    }

    private static string MinimalBootstrapJson()
    {
        return """
{
  "header": {
    "adapterId": "adapter",
    "cacheConfigId": "cacheconfig",
    "headRepository": "owner/repo",
    "kind": "bootstrap",
    "ledgerEpoch": "bbbbbbbbbbbbbbbbbbbbbb",
    "modelId": "model-2024-01-01",
    "policyId": "policy",
    "predecessorLedgerSha256": "bootstrap",
    "providerId": "provider",
    "pullRequest": 1,
    "repository": "owner/repo",
    "sessionEpoch": "aaaaaaaaaaaaaaaaaaaaaa",
    "stateGeneration": 0,
    "templateId": "template",
    "toolDefinitionId": "tools",
    "trustedExecutionDomain": "trusted",
    "workflowIdentity": "ci"
  },
  "prefixContractVersion": 1,
  "records": [
    {
      "interactionId": "0000000000000000000000000000000000000000000000000000000000000000",
      "interactionOrdinal": 0,
      "role": "review_context",
      "cacheContractDigest": "c67bf2569b74a5699f670791f30c731d728703d8ce2b6201866175526cd52a85",
      "changedFiles": [],
      "reviewedBaseSha": "1111111111111111111111111111111111111111",
      "reviewedHeadSha": "0000000000000000000000000000000000000000",
      "subjectDigest": "1111111111111111111111111111111111111111111111111111111111111111"
    },
    {
      "interactionId": "0000000000000000000000000000000000000000000000000000000000000000",
      "interactionOrdinal": 0,
      "role": "review_outcome",
      "findings": [],
      "limitations": [],
      "summary": "Summary text."
    }
  ],
  "schemaVersion": 1
}
""";
    }
}

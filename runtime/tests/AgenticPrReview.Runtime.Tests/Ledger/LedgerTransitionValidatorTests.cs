using System.Text;
using AgenticPrReview.Runtime.Ledger;

namespace AgenticPrReview.Runtime.Tests.Ledger;

public sealed class LedgerTransitionValidatorTests
{
    private static readonly ExpectedIdentities Identities = LedgerTestBaseline.Identities;

    [Fact]
    public void BootstrapValidates()
    {
        var ledger = ParseLedger(MinimalBootstrapJson());
        var expected = new BootstrapTransition(Identities, "aaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbb", 0);
        var outcome = LedgerTransitionValidator.ValidateBootstrap(expected, ledger);

        Assert.Empty(outcome.Diagnostics);
    }

    [Fact]
    public void BootstrapWrongKindFails()
    {
        var ledger = ParseLedger(RecoveryRootJson());
        var expected = new BootstrapTransition(
            Identities, "aaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbb", 0);
        var outcome = LedgerTransitionValidator.ValidateBootstrap(expected, ledger);

        Assert.Single(outcome.Diagnostics);
        Assert.Equal(LedgerDiagnosticCodes.TransitionKindMismatch, outcome.Diagnostics[0].Code);
    }

    [Fact]
    public void ContinuationValidates()
    {
        var predecessor = ParseLedger(MinimalBootstrapJson());
        var candidate = ParseLedger(ContinuationJson(predecessor.ContentSha256));
        var expected = new ContinuationTransition(
            Identities, "aaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbb",
            predecessor.ContentSha256, "bbbbbbbbbbbbbbbbbbbbbb", 0, 1);
        var outcome = LedgerTransitionValidator.ValidateContinuation(expected, predecessor, candidate);

        Assert.Empty(outcome.Diagnostics);
    }

    [Fact]
    public void ContinuationWrongPredecessorHashFails()
    {
        var predecessor = ParseLedger(MinimalBootstrapJson());
        var candidate = ParseLedger(ContinuationJson("0000000000000000000000000000000000000000000000000000000000000000"));
        var expected = new ContinuationTransition(
            Identities, "aaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbb",
            predecessor.ContentSha256, "bbbbbbbbbbbbbbbbbbbbbb", 0, 1);
        var outcome = LedgerTransitionValidator.ValidateContinuation(expected, predecessor, candidate);

        Assert.Single(outcome.Diagnostics);
        Assert.Equal(LedgerDiagnosticCodes.PredecessorHashMismatch, outcome.Diagnostics[0].Code);
    }

    [Fact]
    public void ResetValidates()
    {
        var predecessor = ParseLedger(MinimalBootstrapJson());
        var candidate = ParseLedger(ResetJson(predecessor.ContentSha256));
        var expected = new ResetTransition(
            Identities, "aaaaaaaaaaaaaaaaaaaaaa", "cccccccccccccccccccccc",
            predecessor.ContentSha256, "0000000000000000000000000000000000000000000000000000000000000000",
            "bbbbbbbbbbbbbbbbbbbbbb", 0, 1, "base_change");
        var outcome = LedgerTransitionValidator.ValidateReset(expected, predecessor, candidate);

        Assert.Empty(outcome.Diagnostics);
    }

    [Fact]
    public void ResetSameEpochFails()
    {
        var predecessor = ParseLedger(MinimalBootstrapJson());
        var candidate = ParseLedger(ResetJson(predecessor.ContentSha256, ledgerEpoch: "bbbbbbbbbbbbbbbbbbbbbb"));
        var expected = new ResetTransition(
            Identities, "aaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbb",
            predecessor.ContentSha256, "0000000000000000000000000000000000000000000000000000000000000000",
            "bbbbbbbbbbbbbbbbbbbbbb", 0, 1, "base_change");
        var outcome = LedgerTransitionValidator.ValidateReset(expected, predecessor, candidate);

        Assert.Single(outcome.Diagnostics);
        Assert.Equal(LedgerDiagnosticCodes.ResetEpochNotFresh, outcome.Diagnostics[0].Code);
    }

    [Fact]
    public void RecoveryRootValidates()
    {
        var ledger = ParseLedger(RecoveryRootJson());
        var expected = new RecoveryRootTransition(Identities, "aaaaaaaaaaaaaaaaaaaaaa", "eeeeeeeeeeeeeeeeeeeeee", 0, "integrity_mismatch");
        var outcome = LedgerTransitionValidator.ValidateRecoveryRoot(expected, ledger);

        Assert.Empty(outcome.Diagnostics);
    }

    [Fact]
    public void RecoveryRootWrongReasonFails()
    {
        var ledger = ParseLedger(RecoveryRootJson());
        var expected = new RecoveryRootTransition(Identities, "aaaaaaaaaaaaaaaaaaaaaa", "eeeeeeeeeeeeeeeeeeeeee", 0, "unsafe_provenance");
        var outcome = LedgerTransitionValidator.ValidateRecoveryRoot(expected, ledger);

        Assert.Single(outcome.Diagnostics);
        Assert.Equal(LedgerDiagnosticCodes.RecoveryRootReasonMismatch, outcome.Diagnostics[0].Code);
    }

    private static ValidatedLedger ParseLedger(string json)
    {
        var outcome = LedgerParser.ParseAndValidate(Encoding.UTF8.GetBytes(json));
        Assert.NotNull(outcome.Ledger);
        return outcome.Ledger!;
    }

    // Inline ledgers are byte-level canonical (compact, RFC 8785 key order): the parser's
    // canonical-form stage compares raw bytes against the re-serialized model.
    private static string MinimalBootstrapJson()
    {
        return $$"""{"header":{"adapterId":"{{LedgerTestBaseline.AdapterId}}","cacheConfigId":"{{LedgerTestBaseline.CacheConfigId}}","headRepository":"owner/repo","kind":"bootstrap","ledgerEpoch":"bbbbbbbbbbbbbbbbbbbbbb","modelId":"{{LedgerTestBaseline.ModelId}}","policyId":"{{LedgerTestBaseline.PolicyId}}","predecessorLedgerSha256":"bootstrap","providerId":"provider","pullRequest":1,"repository":"owner/repo","sessionEpoch":"aaaaaaaaaaaaaaaaaaaaaa","stateGeneration":0,"templateId":"{{LedgerTestBaseline.TemplateId}}","toolDefinitionId":"{{LedgerTestBaseline.ToolDefinitionId}}","trustedExecutionDomain":"trusted","workflowIdentity":"ci"},"prefixContractVersion":1,"records":[{"cacheContractDigest":"{{LedgerTestBaseline.CacheContractDigest}}","changedFiles":[],"interactionId":"0000000000000000000000000000000000000000000000000000000000000000","interactionOrdinal":0,"reviewedBaseSha":"1111111111111111111111111111111111111111","reviewedHeadSha":"0000000000000000000000000000000000000000","role":"review_context","subjectDigest":"1111111111111111111111111111111111111111111111111111111111111111"},{"findings":[],"interactionId":"0000000000000000000000000000000000000000000000000000000000000000","interactionOrdinal":0,"limitations":[],"role":"review_outcome","summary":"Summary text."}],"schemaVersion":1}""";
    }

    private static string ContinuationJson(string predecessorHash)
    {
        return $$"""{"header":{"adapterId":"{{LedgerTestBaseline.AdapterId}}","cacheConfigId":"{{LedgerTestBaseline.CacheConfigId}}","headRepository":"owner/repo","kind":"continuation","ledgerEpoch":"bbbbbbbbbbbbbbbbbbbbbb","modelId":"{{LedgerTestBaseline.ModelId}}","policyId":"{{LedgerTestBaseline.PolicyId}}","predecessorLedgerEpoch":"bbbbbbbbbbbbbbbbbbbbbb","predecessorLedgerSha256":"{{predecessorHash}}","predecessorStateGeneration":0,"providerId":"provider","pullRequest":1,"repository":"owner/repo","sessionEpoch":"aaaaaaaaaaaaaaaaaaaaaa","stateGeneration":1,"templateId":"{{LedgerTestBaseline.TemplateId}}","toolDefinitionId":"{{LedgerTestBaseline.ToolDefinitionId}}","trustedExecutionDomain":"trusted","workflowIdentity":"ci"},"prefixContractVersion":1,"records":[{"cacheContractDigest":"{{LedgerTestBaseline.CacheContractDigest}}","changedFiles":[],"interactionId":"0000000000000000000000000000000000000000000000000000000000000000","interactionOrdinal":0,"reviewedBaseSha":"1111111111111111111111111111111111111111","reviewedHeadSha":"0000000000000000000000000000000000000000","role":"review_context","subjectDigest":"1111111111111111111111111111111111111111111111111111111111111111"},{"findings":[],"interactionId":"0000000000000000000000000000000000000000000000000000000000000000","interactionOrdinal":0,"limitations":[],"role":"review_outcome","summary":"Summary text."},{"cacheContractDigest":"{{LedgerTestBaseline.CacheContractDigest}}","changedFiles":[],"interactionId":"1111111111111111111111111111111111111111111111111111111111111111","interactionOrdinal":1,"reviewedBaseSha":"1111111111111111111111111111111111111111","reviewedHeadSha":"0000000000000000000000000000000000000000","role":"review_context","subjectDigest":"1111111111111111111111111111111111111111111111111111111111111111"},{"findings":[],"interactionId":"1111111111111111111111111111111111111111111111111111111111111111","interactionOrdinal":1,"limitations":[],"role":"review_outcome","summary":"Summary text."}],"schemaVersion":1}""";
    }

    private static string ResetJson(string predecessorHash, string ledgerEpoch = "cccccccccccccccccccccc")
    {
        return $$"""{"header":{"adapterId":"{{LedgerTestBaseline.AdapterId}}","cacheConfigId":"{{LedgerTestBaseline.CacheConfigId}}","headRepository":"owner/repo","kind":"reset","ledgerEpoch":"{{ledgerEpoch}}","modelId":"{{LedgerTestBaseline.ModelId}}","policyId":"{{LedgerTestBaseline.PolicyId}}","predecessorLedgerEpoch":"bbbbbbbbbbbbbbbbbbbbbb","predecessorLedgerSha256":"{{predecessorHash}}","predecessorManifestSha256":"0000000000000000000000000000000000000000000000000000000000000000","predecessorStateGeneration":0,"providerId":"provider","pullRequest":1,"repository":"owner/repo","resetReason":"base_change","sessionEpoch":"aaaaaaaaaaaaaaaaaaaaaa","stateGeneration":1,"templateId":"{{LedgerTestBaseline.TemplateId}}","toolDefinitionId":"{{LedgerTestBaseline.ToolDefinitionId}}","trustedExecutionDomain":"trusted","workflowIdentity":"ci"},"prefixContractVersion":1,"records":[{"cacheContractDigest":"{{LedgerTestBaseline.CacheContractDigest}}","changedFiles":[],"interactionId":"1111111111111111111111111111111111111111111111111111111111111111","interactionOrdinal":0,"reviewedBaseSha":"1111111111111111111111111111111111111111","reviewedHeadSha":"0000000000000000000000000000000000000000","role":"review_context","subjectDigest":"1111111111111111111111111111111111111111111111111111111111111111"},{"findings":[],"interactionId":"1111111111111111111111111111111111111111111111111111111111111111","interactionOrdinal":0,"limitations":[],"role":"review_outcome","summary":"Summary text."}],"schemaVersion":1}""";
    }

    private static string RecoveryRootJson()
    {
        return $$"""{"header":{"adapterId":"{{LedgerTestBaseline.AdapterId}}","cacheConfigId":"{{LedgerTestBaseline.CacheConfigId}}","headRepository":"owner/repo","kind":"recovery_root","ledgerEpoch":"eeeeeeeeeeeeeeeeeeeeee","modelId":"{{LedgerTestBaseline.ModelId}}","policyId":"{{LedgerTestBaseline.PolicyId}}","predecessorLedgerSha256":"bootstrap","providerId":"provider","pullRequest":1,"recoveryReason":"integrity_mismatch","repository":"owner/repo","sessionEpoch":"aaaaaaaaaaaaaaaaaaaaaa","stateGeneration":0,"templateId":"{{LedgerTestBaseline.TemplateId}}","toolDefinitionId":"{{LedgerTestBaseline.ToolDefinitionId}}","trustedExecutionDomain":"trusted","workflowIdentity":"ci"},"prefixContractVersion":1,"records":[{"cacheContractDigest":"{{LedgerTestBaseline.CacheContractDigest}}","changedFiles":[],"interactionId":"0000000000000000000000000000000000000000000000000000000000000000","interactionOrdinal":0,"reviewedBaseSha":"1111111111111111111111111111111111111111","reviewedHeadSha":"0000000000000000000000000000000000000000","role":"review_context","subjectDigest":"1111111111111111111111111111111111111111111111111111111111111111"},{"findings":[],"interactionId":"0000000000000000000000000000000000000000000000000000000000000000","interactionOrdinal":0,"limitations":[],"role":"review_outcome","summary":"Summary text."}],"schemaVersion":1}""";
    }
}

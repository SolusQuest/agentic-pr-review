using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticPrReview.Runtime.Ledger;

namespace AgenticPrReview.Runtime.Tools.LedgerFixtureGen;

/// <summary>
/// JSON DTOs for the ledger-transition / ledger-build manifest entries (issue #49 §12).
/// Property names serialize camelCase via <see cref="ManifestJson.Options"/>, aligned
/// with the C# ExpectedTransition record parameter names (sessionEpoch, ledgerEpoch,
/// stateGeneration, predecessorLedgerSha256, predecessorLedgerEpoch,
/// predecessorStateGeneration, predecessorManifestSha256, resetReason, recoveryReason,
/// and the twelve ExpectedIdentities fields).
/// </summary>
internal sealed record ManifestIdentities(
    string Repository,
    string HeadRepository,
    int PullRequest,
    string WorkflowIdentity,
    string TrustedExecutionDomain,
    string ProviderId,
    string ModelId,
    string AdapterId,
    string TemplateId,
    string PolicyId,
    string ToolDefinitionId,
    string CacheConfigId);

internal sealed record ManifestExpected(
    ManifestIdentities Identities,
    string SessionEpoch,
    string LedgerEpoch,
    long StateGeneration,
    string? PredecessorLedgerSha256,
    string? PredecessorLedgerEpoch,
    long? PredecessorStateGeneration,
    string? PredecessorManifestSha256,
    string? ResetReason,
    string? RecoveryReason);

internal sealed record ManifestTransition(string Kind, ManifestExpected Expected);

internal sealed record ManifestParseExpectation(bool Valid, string ContentSha256);

internal sealed record ManifestTransitionExpectation(bool Valid, string? CandidateContentSha256, string? Code);

internal sealed record ManifestBuildExpectation(
    bool Valid,
    string? CandidateContentSha256,
    string? ExpectedCandidateFile,
    string? Code,
    string? CauseCode);

internal sealed record TransitionManifestEntry(
    string Type,
    string File,
    ManifestTransition Transition,
    string? Predecessor,
    ManifestParseExpectation ParseExpectation,
    ManifestTransitionExpectation TransitionExpectation);

internal sealed record BuildManifestEntry(
    string Type,
    string File,
    ManifestTransition Transition,
    string? Predecessor,
    ManifestBuildExpectation BuildExpectation);

internal static class ManifestJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    internal static TransitionManifestEntry ToEntry(TransitionFixture fixture)
    {
        return new TransitionManifestEntry(
            "ledger-transition",
            "provider-session-ledger/" + fixture.Artifact.FileName,
            new ManifestTransition(fixture.Kind, ToManifest(fixture.Expected)),
            fixture.PredecessorFile,
            new ManifestParseExpectation(true, fixture.Artifact.ContentSha256!),
            fixture.ExpectValid
                ? new ManifestTransitionExpectation(true, fixture.Artifact.ContentSha256, null)
                : new ManifestTransitionExpectation(false, null, fixture.ExpectCode));
    }

    internal static BuildManifestEntry ToEntry(BuildFixture fixture)
    {
        return new BuildManifestEntry(
            "ledger-build",
            "provider-session-ledger/" + fixture.Artifact.FileName,
            new ManifestTransition(fixture.Kind, ToManifest(fixture.Expected)),
            fixture.PredecessorFile,
            fixture.ExpectValid
                ? new ManifestBuildExpectation(true, fixture.CandidateContentSha256, fixture.ExpectedCandidateFile, null, null)
                : new ManifestBuildExpectation(false, null, null, fixture.ExpectCode, fixture.ExpectCauseCode));
    }

    private static ManifestExpected ToManifest(ExpectedTransition expected)
    {
        return expected switch
        {
            BootstrapTransition b => new ManifestExpected(
                ToManifest(b.Identities), b.SessionEpoch, b.LedgerEpoch, b.StateGeneration,
                null, null, null, null, null, null),
            ContinuationTransition c => new ManifestExpected(
                ToManifest(c.Identities), c.SessionEpoch, c.LedgerEpoch, c.StateGeneration,
                c.PredecessorLedgerSha256, c.PredecessorLedgerEpoch, c.PredecessorStateGeneration, null, null, null),
            ResetTransition r => new ManifestExpected(
                ToManifest(r.Identities), r.SessionEpoch, r.LedgerEpoch, r.StateGeneration,
                r.PredecessorLedgerSha256, r.PredecessorLedgerEpoch, r.PredecessorStateGeneration, r.PredecessorManifestSha256, r.ResetReason, null),
            RecoveryRootTransition rr => new ManifestExpected(
                ToManifest(rr.Identities), rr.SessionEpoch, rr.LedgerEpoch, rr.StateGeneration,
                null, null, null, null, null, rr.RecoveryReason),
            _ => throw new InvalidOperationException("Unknown expected transition type.")
        };
    }

    private static ManifestIdentities ToManifest(ExpectedIdentities identities)
    {
        return new ManifestIdentities(
            identities.Repository,
            identities.HeadRepository,
            identities.PullRequest,
            identities.WorkflowIdentity,
            identities.TrustedExecutionDomain,
            identities.ProviderId,
            identities.ModelId,
            identities.AdapterId,
            identities.TemplateId,
            identities.PolicyId,
            identities.ToolDefinitionId,
            identities.CacheConfigId);
    }
}

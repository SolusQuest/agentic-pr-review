// LedgerFixtureGen: regenerates the ProviderSessionLedgerV1 fixtures under
// protocol/fixtures/v1/provider-session-ledger/ from the LedgerBuilder public API.
//
// Usage:
//   dotnet run --project runtime/tools/LedgerFixtureGen -- --artifacts-path <output-dir> [--manifest-fragment <file>]
//   (the -- separator keeps dotnet run from consuming the options itself)
//
// The tool writes 112 generated fixtures (16 valid restores, 61 invalid restores, 25
// transition candidates, 10 build scenarios), verifies each one (restore re-read,
// transition validator self-check, builder pipeline self-check), and prints the
// contentSha256 / code oracles that protocol/fixtures/v1/manifest.json must declare.
// With --manifest-fragment it also writes the 35 ledger-transition / ledger-build
// manifest entries as a JSON array for splicing into the manifest. The one §13 row not
// generated is invalid-json.json, which is maintained by hand. Output is deterministic:
// rerunning with the same library produces byte-identical files.

using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Ledger;
using AgenticPrReview.Runtime.Tools.LedgerFixtureGen;

var artifactsPath = ParseArtifactsPath(args, out var manifestFragmentPath);
Directory.CreateDirectory(artifactsPath);

// ---- Valid restores (issue #49 §13 "Valid restores" order) ----
var bootstrap = RestoreScenarios.BootstrapMinimal(out var bootstrapLedger);
var continuation = RestoreScenarios.ContinuationOneAppend(bootstrapLedger, out _);
var resetCacheContractChange = RestoreScenarios.ResetCacheContractChange(bootstrapLedger);
var resetBaseChange = RestoreScenarios.ResetBaseChange(bootstrapLedger);
var resetHeadHistoryDiscontinuity = RestoreScenarios.ResetHeadHistoryDiscontinuity(bootstrapLedger);
var recoveryRootIntegrityMismatch = RestoreScenarios.RecoveryRootIntegrityMismatch();
var continuationMaxInteractions = RestoreScenarios.ContinuationMaxInteractions(bootstrapLedger);
var continuationNearByteLimit = RestoreScenarios.ContinuationNearByteLimit(bootstrapLedger);

var artifacts = new List<FixtureArtifact>
{
    bootstrap,
    continuation,
    resetCacheContractChange,
    resetBaseChange,
    resetHeadHistoryDiscontinuity,
    RestoreScenarios.RecoveryRootUnavailableAcceptedArtifact(),
    RestoreScenarios.RecoveryRootCorruptAcceptedArtifact(),
    recoveryRootIntegrityMismatch,
    RestoreScenarios.RecoveryRootUnsafeProvenance(),
    RestoreScenarios.RecoveryRootStateKeyMismatch(),
    RestoreScenarios.RecoveryRootContractVersionIncompatible(),
    RestoreScenarios.RecoveryRootOverBoundLedger(),
    continuationMaxInteractions,
    continuationNearByteLimit,
    RestoreScenarios.Sha256HeadSha(),
    RestoreScenarios.BootstrapWithPatch()
};

var bootstrapBytes = bootstrap.Content;
var bootstrapText = Text(bootstrap);
var continuationText = Text(continuation);
var resetText = Text(resetBaseChange);
var recoveryRootText = Text(recoveryRootIntegrityMismatch);

// ---- Restore-time invalid (issue #49 §13 "Restore-time invalid" order).
// invalid-json.json is maintained by hand and intentionally not generated. ----
artifacts.Add(InvalidRestoreScenarios.RawOversize());
artifacts.Add(InvalidRestoreScenarios.InvalidUtf8(bootstrapBytes));
artifacts.Add(InvalidRestoreScenarios.BomLeading(bootstrapBytes));
artifacts.Add(InvalidRestoreScenarios.DuplicateJsonProperty(bootstrapText));
artifacts.Add(InvalidRestoreScenarios.DepthExceeded());
artifacts.Add(InvalidRestoreScenarios.ArrayLengthExceeded());
artifacts.Add(InvalidRestoreScenarios.PropertyCountExceeded());
artifacts.Add(InvalidRestoreScenarios.RawMultiDefect());
artifacts.Add(InvalidRestoreScenarios.NulInSummary(bootstrapText));
artifacts.Add(InvalidRestoreScenarios.LoneSurrogateInString(bootstrapText));
artifacts.Add(InvalidRestoreScenarios.LoneSurrogateInPropertyName(bootstrapText));
artifacts.Add(InvalidRestoreScenarios.DuplicateEscapedSurrogateProperty());
artifacts.Add(InvalidRestoreScenarios.UnicodeSurrogateKeySortPrecedence());
artifacts.Add(InvalidRestoreScenarios.RootScalarLoneSurrogate());
artifacts.Add(InvalidRestoreScenarios.RootScalarNul());
artifacts.Add(InvalidRestoreScenarios.NulInPropertyName(bootstrapText));
artifacts.Add(InvalidRestoreScenarios.UnsupportedSchemaVersion(bootstrapText));
artifacts.Add(InvalidRestoreScenarios.MissingSchemaVersion(bootstrapText));
artifacts.Add(InvalidRestoreScenarios.WrongTypeSchemaVersion(bootstrapText));
artifacts.Add(InvalidRestoreScenarios.UnsupportedPrefixContractVersion(bootstrapText));
artifacts.Add(InvalidRestoreScenarios.MissingPrefixContractVersion(bootstrapText));
artifacts.Add(InvalidRestoreScenarios.WrongTypePrefixContractVersion(bootstrapText));
artifacts.Add(InvalidRestoreScenarios.UnknownTopLevelField(bootstrapText));
artifacts.Add(InvalidRestoreScenarios.UnknownHeaderField(bootstrapText));
artifacts.Add(InvalidRestoreScenarios.UnknownHeaderKind(bootstrapText));
artifacts.Add(InvalidRestoreScenarios.OverlongSummary(bootstrapText));
artifacts.Add(InvalidRestoreScenarios.ChangedFileStatOutOfRange(bootstrapText));
artifacts.Add(InvalidRestoreScenarios.ChangedFileNegativeStat(bootstrapText));
artifacts.Add(InvalidRestoreScenarios.WhitespaceSummary(bootstrapText));
artifacts.Add(InvalidRestoreScenarios.AbsolutePathInFinding(bootstrapText));
artifacts.Add(InvalidRestoreScenarios.FindingLineOverCap(bootstrapText));
artifacts.Add(InvalidRestoreScenarios.IdentityByteLengthExceeded(bootstrapText));
artifacts.Add(InvalidRestoreScenarios.ControlCharacterInIdentity(bootstrapText));
artifacts.Add(RestoreScenarios.ModelAliasLatest(bootstrapLedger));
artifacts.Add(InvalidRestoreScenarios.UnsupportedChangeStatus(bootstrapText));
artifacts.Add(InvalidRestoreScenarios.NonCanonicalKeyOrder(bootstrapText));
artifacts.Add(InvalidRestoreScenarios.NonCanonicalStringEscape(bootstrapText));
artifacts.Add(InvalidRestoreScenarios.CanonicalByteLimitExceeded(bootstrapText));
artifacts.Add(InvalidRestoreScenarios.RecordsEmpty(bootstrapText));
artifacts.Add(InvalidRestoreScenarios.RecordsOddLength(bootstrapText));
artifacts.Add(InvalidRestoreScenarios.OrdinalGap(continuationText));
artifacts.Add(InvalidRestoreScenarios.DuplicateInteraction(continuationText));
artifacts.Add(InvalidRestoreScenarios.PairOrderSwapped(continuationText));
artifacts.Add(InvalidRestoreScenarios.PairInteractionIdMismatch(bootstrapText));
artifacts.Add(InvalidRestoreScenarios.DigestMismatch(bootstrapText));
artifacts.Add(InvalidRestoreScenarios.InteractionLimitExceeded(bootstrapText));
artifacts.Add(InvalidRestoreScenarios.ChangedFileLimitExceeded(bootstrapText));
artifacts.Add(InvalidRestoreScenarios.FindingLimitExceeded(bootstrapText));
artifacts.Add(InvalidRestoreScenarios.LimitationsLimitExceeded(bootstrapText));
artifacts.Add(InvalidRestoreScenarios.BootstrapNonzeroGeneration(bootstrapText));
artifacts.Add(InvalidRestoreScenarios.RecoveryRootNonzeroGeneration(recoveryRootText));
artifacts.Add(InvalidRestoreScenarios.RecoveryRootMissingReason(recoveryRootText));
artifacts.Add(InvalidRestoreScenarios.ResetMissingReason(resetText));
artifacts.Add(InvalidRestoreScenarios.ResetForbiddenField(resetText));
artifacts.Add(InvalidRestoreScenarios.ContinuationForbiddenField(continuationText));
artifacts.Add(InvalidRestoreScenarios.RecordRoleMismatch(bootstrapText));
artifacts.Add(InvalidRestoreScenarios.FindingLineRangeInvalid(bootstrapText));
artifacts.Add(InvalidRestoreScenarios.FindingLocationMismatch(bootstrapText));
artifacts.Add(InvalidRestoreScenarios.FindingLocationMissingPath(bootstrapText));
artifacts.Add(InvalidRestoreScenarios.LedgerDeepPathNoTruncation(bootstrapText));
artifacts.Add(InvalidRestoreScenarios.LedgerDeepPathTruncation(bootstrapText));

// ---- Transitions (issue #49 §13 valid 4 + invalid 21, matrix order) ----
var transitionFixtures = new List<TransitionFixture>
{
    TransitionScenarios.ValidBootstrap(),
    TransitionScenarios.ValidContinuation(bootstrapLedger),
    TransitionScenarios.ValidResetCacheContractChange(bootstrapLedger),
    TransitionScenarios.ValidRecoveryRootIntegrityMismatch(),
    TransitionScenarios.ContinuationModifiedHistory(continuationText, bootstrapLedger),
    TransitionScenarios.ContinuationWrongPredecessorHash(continuationText, bootstrapLedger),
    TransitionScenarios.ContinuationCacheContractChanged(continuationText, bootstrapLedger),
    TransitionScenarios.ContinuationCandidateOnlyIdentityDrift(continuationText, bootstrapLedger),
    TransitionScenarios.ContinuationSessionEpochChanged(continuationText, bootstrapLedger),
    TransitionScenarios.ContinuationLedgerEpochChanged(continuationText, bootstrapLedger),
    TransitionScenarios.ContinuationPredecessorLedgerEpochMismatch(continuationText, bootstrapLedger),
    TransitionScenarios.ContinuationPredecessorGenerationMismatch(continuationText, bootstrapLedger),
    TransitionScenarios.ContinuationMultiPredecessorDefect(continuationText, bootstrapLedger),
    TransitionScenarios.ContinuationStateGenerationMismatch(continuationText, bootstrapLedger),
    TransitionScenarios.ResetWithPredecessorRecords(resetText, bootstrapLedger),
    TransitionScenarios.ResetSameEpoch(resetText, bootstrapLedger),
    TransitionScenarios.ResetWrongManifestHash(resetText, bootstrapLedger),
    TransitionScenarios.ResetWrongReason(resetText, bootstrapLedger),
    TransitionScenarios.ResetSessionScopeChanged(resetText, bootstrapLedger),
    TransitionScenarios.ResetPredecessorGenerationMismatch(resetText, bootstrapLedger),
    TransitionScenarios.ResetBaseChangeCacheContractDrift(resetText, bootstrapLedger),
    TransitionScenarios.RecoveryRootWrongReason(recoveryRootText),
    TransitionScenarios.BootstrapMultiPair(bootstrapText),
    TransitionScenarios.RecoveryRootMultiPair(recoveryRootText),
    TransitionScenarios.BootstrapWithExpectedContinuation(bootstrapText, bootstrapLedger)
};

// ---- Builder scenarios (issue #49 §13 valid 4 + invalid 6, matrix order) ----
var maxedPredecessor = ParseLedger(continuationMaxInteractions);
var nearByteLimitPredecessor = ParseLedger(continuationNearByteLimit);
var buildFixtures = new List<BuildFixture>
{
    BuildScenarios.BuildValidBootstrap(transitionFixtures[0].Artifact.Content),
    BuildScenarios.BuildValidContinuation(bootstrapLedger, transitionFixtures[1].Artifact.Content),
    BuildScenarios.BuildValidReset(bootstrapLedger, transitionFixtures[2].Artifact.Content),
    BuildScenarios.BuildValidRecoveryRoot(transitionFixtures[3].Artifact.Content),
    BuildScenarios.BuildMismatchedInteractionIds(),
    BuildScenarios.BuildContinuationWrongPredecessorHash(bootstrapLedger),
    BuildScenarios.OverBoundAppendCanonicalByte(nearByteLimitPredecessor),
    BuildScenarios.OverBoundAppendInteractions(maxedPredecessor),
    BuildScenarios.OverBoundRootCanonicalByte(),
    BuildScenarios.OverBoundMultiDefect(maxedPredecessor)
};

foreach (var artifact in artifacts)
{
    var path = WriteArtifact(artifactsPath, artifact);
    VerifyWritten(path, artifact);
    Print(artifact);
}

foreach (var fixture in transitionFixtures)
{
    var path = WriteArtifact(artifactsPath, fixture.Artifact);
    VerifyWritten(path, fixture.Artifact);
    Print(fixture.Artifact);
}

foreach (var fixture in buildFixtures)
{
    // Scenario files are not ledgers; their oracle is the builder pipeline self-check
    // already executed when the fixture was constructed.
    WriteArtifact(artifactsPath, fixture.Artifact);
    Console.WriteLine($"{fixture.Artifact.FileName}: {fixture.Artifact.Content.Length} bytes, scenario");
}

if (manifestFragmentPath is not null)
{
    var entries = new List<object>();
    foreach (var fixture in transitionFixtures)
    {
        entries.Add(ManifestJson.ToEntry(fixture));
    }

    foreach (var fixture in buildFixtures)
    {
        entries.Add(ManifestJson.ToEntry(fixture));
    }

    File.WriteAllText(manifestFragmentPath, JsonSerializer.Serialize(entries, ManifestJson.Options) + "\n");
    Console.WriteLine($"wrote {entries.Count} manifest entries to {manifestFragmentPath}");
}

static string ParseArtifactsPath(string[] args, out string? manifestFragmentPath)
{
    manifestFragmentPath = null;
    if (args.Length == 2 && args[0] == "--artifacts-path" && !string.IsNullOrWhiteSpace(args[1]))
    {
        return args[1];
    }

    if (args.Length == 4 && args[0] == "--artifacts-path" && !string.IsNullOrWhiteSpace(args[1]) &&
        args[2] == "--manifest-fragment" && !string.IsNullOrWhiteSpace(args[3]))
    {
        manifestFragmentPath = args[3];
        return args[1];
    }

    throw new ArgumentException("usage: LedgerFixtureGen --artifacts-path <output-dir> [--manifest-fragment <file>]");
}

static string Text(FixtureArtifact artifact) => Encoding.UTF8.GetString(artifact.Content);

static ValidatedLedger ParseLedger(FixtureArtifact artifact)
{
    var outcome = LedgerParser.ParseAndValidate(artifact.Content);
    if (outcome.Ledger is null)
    {
        throw new InvalidOperationException($"{artifact.FileName}: re-parse failed.");
    }

    return outcome.Ledger;
}

static string WriteArtifact(string artifactsPath, FixtureArtifact artifact)
{
    var path = Path.Combine(artifactsPath, artifact.FileName);
    File.WriteAllBytes(path, artifact.Content);
    return path;
}

static void Print(FixtureArtifact artifact)
{
    var oracle = artifact.ContentSha256 is not null
        ? $"contentSha256 {artifact.ContentSha256}"
        : $"code {artifact.ExpectedCode}";
    Console.WriteLine($"{artifact.FileName}: {artifact.Content.Length} bytes, {oracle}");
}

static void VerifyWritten(string path, FixtureArtifact artifact)
{
    var bytes = File.ReadAllBytes(path);
    var outcome = LedgerParser.ParseAndValidate(bytes);

    if (artifact.ContentSha256 is not null)
    {
        if (outcome.Ledger is null || outcome.Ledger.ContentSha256 != artifact.ContentSha256)
        {
            throw new InvalidOperationException($"{artifact.FileName}: re-read restore verification failed.");
        }
    }
    else if (outcome.Ledger is not null ||
             outcome.Diagnostics.IsEmpty ||
             outcome.Diagnostics[0].Code != artifact.ExpectedCode)
    {
        throw new InvalidOperationException(
            $"{artifact.FileName}: expected a first diagnostic of {artifact.ExpectedCode} on re-read.");
    }
}

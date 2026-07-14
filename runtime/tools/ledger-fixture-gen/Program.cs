using System.Collections.Immutable;
using System.IO;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Ledger;

namespace AgenticPrReview.Runtime.LedgerFixtureGen;

internal static partial class Program
{
    private static string root = default!;

    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: LedgerFixtureGen <fixture-root>");
            return 2;
        }
        root = args[0];
        Directory.CreateDirectory(root);

        var bootstrap = BuildBootstrap();
        Write("bootstrap-minimal.json", bootstrap);
        var continuation = BuildContinuation(bootstrap);
        Write("continuation-one-append.json", continuation);
        var resetCc = BuildResetCacheContract(bootstrap);
        Write("reset-cache-contract-changed.json", resetCc);
        var resetBase = BuildResetBase(bootstrap);
        Write("reset-base-changed.json", resetBase);
        var recovery = BuildRecovery();
        Write("recovery-predecessor-unavailable.json", recovery);
        var maxInter = BuildMaxInteractions();
        Write("continuation-max-interactions.json", maxInter);
        var nearByte = BuildNearByteLimit();
        Write("continuation-near-byte-limit.json", nearByte);

        WriteRaw("raw-oversize.json", MakeRawOversize());
        WriteRaw("duplicate-json-property.json", MakeDuplicateJsonProperty());
        WriteRaw("invalid-utf8.json", MakeInvalidUtf8());
        WriteRaw("nul-in-summary.json", MakeNulInSummary(bootstrap));
        WriteRaw("lone-surrogate.json", MakeLoneSurrogate(bootstrap));
        WriteRaw("depth-exceeded.json", MakeDepthExceeded());
        WriteRaw("array-length-exceeded.json", MakeArrayLengthExceeded());
        WriteRaw("property-count-exceeded.json", MakePropertyCountExceeded());
        WriteRaw("unknown-top-level-field.json", MakeUnknownTopLevel(bootstrap));
        WriteRaw("unknown-header-field.json", MakeUnknownHeaderField(bootstrap));
        WriteRaw("overlong-summary.json", MakeOverlongSummary(bootstrap));
        WriteRaw("unsupported-schema-version.json", MakeUnsupportedSchemaVersion(bootstrap));
        WriteRaw("unsupported-prefix-contract-version.json", MakeUnsupportedPrefixContractVersion(bootstrap));
        WriteRaw("unsafe-reference-shape.json", MakeUnsafeReferenceShape(bootstrap));
        WriteRaw("absolute-path-in-finding.json", MakeAbsolutePathInFinding(bootstrap));
        WriteRaw("identity-byte-length-exceeded.json", MakeIdentityByteLengthExceeded(bootstrap));
        WriteRaw("control-character-in-identity.json", MakeControlCharInIdentity(bootstrap));
        WriteRaw("unsupported-change-status.json", MakeUnsupportedChangeStatus(bootstrap));
        WriteRaw("non-canonical-key-order.json", MakeNonCanonicalKeyOrder());
        WriteRaw("non-canonical-string-escape.json", MakeNonCanonicalStringEscape(bootstrap));
        WriteRaw("records-empty.json", MakeRecordsEmpty(bootstrap));
        WriteRaw("records-odd-length.json", MakeRecordsOddLength(bootstrap));
        WriteRaw("ordinal-gap.json", MakeOrdinalGap(bootstrap));
        WriteRaw("duplicate-interaction.json", MakeDuplicateInteraction(bootstrap));
        WriteRaw("pair-order-swapped.json", MakePairOrderSwapped(bootstrap));
        WriteRaw("digest-mismatch.json", MakeDigestMismatch(bootstrap));
        WriteRaw("interaction-limit-exceeded.json", MakeInteractionLimitExceeded());
        WriteRaw("changed-file-limit-exceeded.json", MakeChangedFileLimitExceeded());
        WriteRaw("finding-limit-exceeded.json", MakeFindingLimitExceeded());
        WriteRaw("limitations-limit-exceeded.json", MakeLimitationsLimitExceeded());
        WriteRaw("canonical-byte-limit-exceeded.json", MakeCanonicalByteLimitExceeded());
        WriteRaw("bootstrap-nonzero-generation.json", MakeBootstrapNonzeroGeneration(bootstrap));
        WriteRaw("recovery-missing-reason.json", MakeRecoveryMissingReason(recovery));
        WriteRaw("reset-missing-reason.json", MakeResetMissingReason(resetCc));
        WriteRaw("reset-forbidden-field.json", MakeResetForbiddenField(resetCc));
        WriteRaw("continuation-forbidden-field.json", MakeContinuationForbiddenField(continuation));
        WriteRaw("recovery-forbidden-field.json", MakeRecoveryForbiddenField(recovery));
        WriteRaw("record-role-tool.json", MakeRecordRoleTool(bootstrap));
        WriteRaw("invalid-json-truncated.json", System.Text.Encoding.UTF8.GetBytes("{\"header\":{\"kind\":\"bootstrap\""));
        WriteRaw("finding-line-range-invalid.json", MakeFindingLineRangeInvalid());
        WriteRaw("finding-location-mismatch.json", MakeFindingLocationMismatch());
        WriteRaw("finding-location-missing-path.json", MakeFindingLocationMissingPath());

        var badContHistory = MutateModifyHistory(continuation);
        Write("continuation-modified-history.json", badContHistory);
        var badContHash = MutatePredecessorHash(continuation);
        Write("continuation-wrong-predecessor-hash.json", badContHash);
        var badContIdentity = MutateCacheContract(continuation);
        Write("continuation-cache-contract-changed.json", badContIdentity);
        var badContEpoch = MutateLedgerEpoch(continuation);
        Write("continuation-epoch-changed.json", badContEpoch);
        var badContGen = MutatePredecessorStateGeneration(continuation);
        Write("continuation-predecessor-generation-mismatch.json", badContGen);
        var badResetRecs = MutateResetWithPredecessorRecords(resetCc, bootstrap);
        Write("reset-with-predecessor-records.json", badResetRecs);
        var badResetSameEpoch = MutateResetSameEpoch(resetCc);
        Write("reset-same-epoch.json", badResetSameEpoch);
        var badResetManifestHash = MutateResetManifestHash(resetCc);
        Write("reset-wrong-manifest-hash.json", badResetManifestHash);
        Write("bootstrap-with-expected-continuation.json", bootstrap);

        WriteScenario("over-bound-append-byte.json", BuildOverBoundByteScenario());
        WriteScenario("over-bound-append-interactions.json", BuildOverBoundInteractionsScenario());

        return 0;
    }

    private static void Write(string name, ValidatedLedger v)
    {
        var path = Path.Combine(root, name);
        File.WriteAllBytes(path, v.ToCanonicalByteArray());
        Console.WriteLine($"{name}: {v.ContentSha256}");
    }

    private static void WriteRaw(string name, byte[] bytes)
    {
        var path = Path.Combine(root, name);
        File.WriteAllBytes(path, bytes);
        Console.WriteLine($"{name}: (raw, {bytes.Length} bytes)");
    }

    private static void WriteScenario(string name, object scenario)
    {
        var path = Path.Combine(root, name);
        var json = JsonSerializer.Serialize(scenario, new JsonSerializerOptions { WriteIndented = false });
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(json));
        Console.WriteLine($"{name}: scenario");
    }
}

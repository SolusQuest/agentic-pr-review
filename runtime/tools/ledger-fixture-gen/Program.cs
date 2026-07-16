using System.IO;
using System.Text;
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

        // Positive baselines (canonical ledger bytes).
        var bootstrap = BuildBootstrap();
        Write("bootstrap-minimal.json", bootstrap);

        var continuation = BuildContinuation(bootstrap);
        Write("continuation-one-append.json", continuation);

        var resetCache = BuildResetCacheContract(bootstrap);
        Write("reset-cache-contract-change.json", resetCache);

        var resetBase = BuildResetBase(bootstrap);
        Write("reset-base-change.json", resetBase);

        var recoveryRoot = BuildRecoveryRoot();
        Write("recovery-root-unavailable-accepted-artifact.json", recoveryRoot);

        var maxInter = BuildMaxInteractions();
        Write("continuation-max-interactions.json", maxInter);

        // ---------- Raw-transport stage ----------
        WriteRaw("raw-oversize.bin", MakeRawOversize());
        WriteRaw("invalid-utf8.bin", MakeInvalidUtf8());
        WriteRaw("invalid-json.json", MakeInvalidJson());
        WriteRaw("duplicate-json-property.json", MakeDuplicateJsonProperty(bootstrap));
        WriteRaw("depth-exceeded.json", MakeDepthExceeded());
        WriteRaw("array-length-exceeded.json", MakeArrayLengthExceeded());
        WriteRaw("property-count-exceeded.json", MakePropertyCountExceeded());
        WriteRaw("raw-multi-defect.json", MakeRawMultiDefect());

        // ---------- Unicode-safety stage ----------
        WriteRaw("nul-in-summary.json", MakeNulInSummary(bootstrap));
        WriteRaw("lone-surrogate-in-string.json", MakeLoneSurrogateInString(bootstrap));
        WriteRaw("lone-surrogate-in-property-name.json", MakeLoneSurrogateInPropertyName(bootstrap));
        WriteRaw("nul-in-property-name.json", MakeNulInPropertyName(bootstrap));
        WriteRaw("root-scalar-lone-surrogate.json", MakeRootScalarLoneSurrogate());
        WriteRaw("root-scalar-nul.json", MakeRootScalarNul());

        // ---------- Version routing ----------
        WriteRaw("unsupported-schema-version.json", MakeUnsupportedSchemaVersion(bootstrap));
        WriteRaw("missing-schema-version.json", MakeMissingSchemaVersion(bootstrap));
        WriteRaw("wrong-type-schema-version.json", MakeWrongTypeSchemaVersion(bootstrap));
        WriteRaw("unsupported-prefix-contract-version.json", MakeUnsupportedPrefixContractVersion(bootstrap));
        WriteRaw("missing-prefix-contract-version.json", MakeMissingPrefixContractVersion(bootstrap));
        WriteRaw("wrong-type-prefix-contract-version.json", MakeWrongTypePrefixContractVersion(bootstrap));

        // ---------- Schema-stage negatives ----------
        WriteRaw("unknown-top-level-field.json", MakeUnknownTopLevel(bootstrap));
        WriteRaw("unknown-header-field.json", MakeUnknownHeaderField(bootstrap));
        WriteRaw("unknown-header-kind.json", MakeUnknownHeaderKind(bootstrap));
        WriteRaw("overlong-summary.json", MakeOverlongSummary(bootstrap));
        WriteRaw("whitespace-summary.json", MakeWhitespaceSummary(bootstrap));
        WriteRaw("absolute-path-in-finding.json", MakeAbsolutePathInFinding(bootstrap));
        WriteRaw("unsupported-change-status.json", MakeUnsupportedChangeStatus(bootstrap));

        // ---------- Structural bounds (identity) ----------
        WriteRaw("identity-byte-length-exceeded.json", MakeIdentityByteLengthExceeded(bootstrap));
        WriteRaw("control-character-in-identity.json", MakeControlCharInIdentity(bootstrap));

        // ---------- Semantic invariants ----------
        WriteRaw("pair-order-swapped.json", MakePairOrderSwapped(bootstrap));
        WriteRaw("records-odd-length.json", MakeRecordsOddLength(bootstrap));
        WriteRaw("records-empty.json", MakeRecordsEmpty(bootstrap));
        WriteRaw("model-alias-latest.json", MakeModelAliasLatest(bootstrap));

        // ---------- Canonical form ----------
        WriteRaw("non-canonical-key-order.json", MakeNonCanonicalKeyOrder());
        WriteRaw("non-canonical-string-escape.json", MakeNonCanonicalStringEscape(bootstrap));

        return 0;
    }

    internal static void Write(string name, ValidatedLedger v)
    {
        var path = Path.Combine(root, name);
        File.WriteAllBytes(path, v.ToCanonicalByteArray());
        Console.WriteLine($"{name}: {v.ContentSha256}");
    }

    internal static void WriteRaw(string name, byte[] bytes)
    {
        var path = Path.Combine(root, name);
        File.WriteAllBytes(path, bytes);
        Console.WriteLine($"{name}: (raw, {bytes.Length} bytes)");
    }
}

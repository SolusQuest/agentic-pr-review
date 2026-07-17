using System.Text;
using AgenticPrReview.Runtime.Ledger;

namespace AgenticPrReview.Runtime.Tools.LedgerFixtureGen;

/// <summary>
/// Invalid restore-fixture scenarios for protocol/fixtures/v1/provider-session-ledger/.
/// Construction pattern: start from the canonical bytes of a valid scenario and apply
/// the minimal text/structure mutation that lands on the §13 target stage and code.
/// Every artifact self-checks through LedgerParser.ParseAndValidate before it is
/// written: the first diagnostic's code must equal the §13 expectation exactly.
/// invalid-json.json is maintained by hand and is not produced here.
/// </summary>
internal static class InvalidRestoreScenarios
{
    // Frozen oracle literals from the contract's "Concrete deep-path golden vectors"
    // (ledger-deep-path-no-truncation / ledger-deep-path-truncation); stored, never
    // re-derived from the production implementation.
    private const string DeepPathNoTruncationMessage =
        "ledger_invalid_unicode:/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>";

    private const string DeepPathTruncationMessage =
        "ledger_invalid_unicode:/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<path-truncated>/<untrusted-property>";

    internal const string SchemaVersionTail = "],\"schemaVersion\":1}";

    // ---- Raw-transport stage -------------------------------------------------

    internal static FixtureArtifact RawOversize()
    {
        var bytes = new byte[LedgerParser.LedgerRawByteLimit + 1];
        Array.Fill<byte>(bytes, 0x20);
        return Invalid("raw-oversize.bin", bytes, LedgerDiagnosticCodes.RawByteLimitExceeded);
    }

    internal static FixtureArtifact InvalidUtf8(byte[] bootstrapBytes)
    {
        var bytes = (byte[])bootstrapBytes.Clone();
        bytes[100] = 0xFF; // never valid in UTF-8, regardless of position
        return Invalid("invalid-utf8.bin", bytes, LedgerDiagnosticCodes.InvalidUtf8);
    }

    internal static FixtureArtifact BomLeading(byte[] bootstrapBytes)
    {
        var bytes = new byte[bootstrapBytes.Length + 3];
        bytes[0] = 0xEF;
        bytes[1] = 0xBB;
        bytes[2] = 0xBF;
        Array.Copy(bootstrapBytes, 0, bytes, 3, bootstrapBytes.Length);
        return Invalid("bom-leading.json", bytes, LedgerDiagnosticCodes.InvalidUtf8);
    }

    internal static FixtureArtifact DuplicateJsonProperty(string bootstrap)
    {
        var text = Mutate(bootstrap, "\"prefixContractVersion\":1,", "\"prefixContractVersion\":1,\"schemaVersion\":1,");
        return InvalidJson("duplicate-json-property.json", text, LedgerDiagnosticCodes.DuplicateJsonProperty);
    }

    internal static FixtureArtifact DepthExceeded()
    {
        return InvalidJson("depth-exceeded.json", NestedObjectDocument(65), LedgerDiagnosticCodes.JsonDepthExceeded);
    }

    internal static FixtureArtifact ArrayLengthExceeded()
    {
        return InvalidJson("array-length-exceeded.json", ArrayDocument(4097), LedgerDiagnosticCodes.JsonArrayLengthExceeded);
    }

    internal static FixtureArtifact PropertyCountExceeded()
    {
        return InvalidJson("property-count-exceeded.json", PropertiesDocument(513), LedgerDiagnosticCodes.JsonPropertyCountExceeded);
    }

    internal static FixtureArtifact RawMultiDefect()
    {
        // Combines a duplicate property, a 65-deep chain, a 4097-item array, and 513
        // root properties; the raw stage precedence reports the duplicate first.
        var sb = new StringBuilder();
        sb.Append("{\"k0\":0,\"k1\":0,\"k0\":1,\"deep\":");
        sb.Append(NestedObjectDocument(65));
        sb.Append(",\"big\":");
        sb.Append(ArrayDocument(4097));
        for (var i = 2; i <= 510; i++)
        {
            sb.Append(",\"k").Append(i).Append("\":0");
        }

        sb.Append('}');
        return InvalidJson("raw-multi-defect.json", sb.ToString(), LedgerDiagnosticCodes.DuplicateJsonProperty);
    }

    // ---- Unicode-safety stage ------------------------------------------------

    internal static FixtureArtifact NulInSummary(string bootstrap)
    {
        var text = Mutate(bootstrap, "\"summary\":\"Summary text.\"", "\"summary\":\"Sum\\u0000mary text.\"");
        return InvalidJson("nul-in-summary.json", text, LedgerDiagnosticCodes.InvalidUnicode);
    }

    internal static FixtureArtifact LoneSurrogateInString(string bootstrap)
    {
        var text = Mutate(bootstrap, "\"summary\":\"Summary text.\"", "\"summary\":\"\\uD800\"");
        return InvalidJson("lone-surrogate-in-string.json", text, LedgerDiagnosticCodes.InvalidUnicode);
    }

    internal static FixtureArtifact LoneSurrogateInPropertyName(string bootstrap)
    {
        var text = Mutate(bootstrap, "{\"header\":", "{\"\\uD800\":1,\"header\":");
        return InvalidJson("lone-surrogate-in-property-name.json", text, LedgerDiagnosticCodes.InvalidUnicode);
    }

    internal static FixtureArtifact RootScalarLoneSurrogate()
    {
        return InvalidJson("root-scalar-lone-surrogate.json", "\"\\uD800\"", LedgerDiagnosticCodes.InvalidUnicode);
    }

    internal static FixtureArtifact RootScalarNul()
    {
        return InvalidJson("root-scalar-nul.json", "\"\\u0000\"", LedgerDiagnosticCodes.InvalidUnicode);
    }

    internal static FixtureArtifact NulInPropertyName(string bootstrap)
    {
        var text = Mutate(bootstrap, "{\"header\":", "{\"\\u0000\":1,\"header\":");
        return InvalidJson("nul-in-property-name.json", text, LedgerDiagnosticCodes.InvalidUnicode);
    }

    // ---- Version-routing stage -----------------------------------------------

    internal static FixtureArtifact UnsupportedSchemaVersion(string bootstrap)
    {
        var text = Mutate(bootstrap, "\"schemaVersion\":1", "\"schemaVersion\":2");
        return InvalidJson("unsupported-schema-version.json", text, LedgerDiagnosticCodes.UnsupportedSchemaVersion);
    }

    internal static FixtureArtifact UnsupportedPrefixContractVersion(string bootstrap)
    {
        var text = Mutate(bootstrap, "\"prefixContractVersion\":1", "\"prefixContractVersion\":2");
        return InvalidJson("unsupported-prefix-contract-version.json", text, LedgerDiagnosticCodes.UnsupportedPrefixContractVersion);
    }

    // ---- Schema stage ----------------------------------------------------------

    internal static FixtureArtifact MissingSchemaVersion(string bootstrap)
    {
        var text = Mutate(bootstrap, ",\"schemaVersion\":1}", "}");
        return InvalidJson("missing-schema-version.json", text, LedgerDiagnosticCodes.SchemaViolation);
    }

    internal static FixtureArtifact WrongTypeSchemaVersion(string bootstrap)
    {
        var text = Mutate(bootstrap, "\"schemaVersion\":1", "\"schemaVersion\":\"1\"");
        return InvalidJson("wrong-type-schema-version.json", text, LedgerDiagnosticCodes.SchemaViolation);
    }

    internal static FixtureArtifact MissingPrefixContractVersion(string bootstrap)
    {
        var text = Mutate(bootstrap, "\"prefixContractVersion\":1,", "");
        return InvalidJson("missing-prefix-contract-version.json", text, LedgerDiagnosticCodes.SchemaViolation);
    }

    internal static FixtureArtifact WrongTypePrefixContractVersion(string bootstrap)
    {
        var text = Mutate(bootstrap, "\"prefixContractVersion\":1", "\"prefixContractVersion\":\"1\"");
        return InvalidJson("wrong-type-prefix-contract-version.json", text, LedgerDiagnosticCodes.SchemaViolation);
    }

    internal static FixtureArtifact UnknownTopLevelField(string bootstrap)
    {
        var text = Mutate(bootstrap, "{\"header\":", "{\"extraField\":1,\"header\":");
        return InvalidJson("unknown-top-level-field.json", text, LedgerDiagnosticCodes.UnknownField);
    }

    internal static FixtureArtifact UnknownHeaderField(string bootstrap)
    {
        var text = Mutate(bootstrap, "\"kind\":\"bootstrap\",", "\"kind\":\"bootstrap\",\"unknownField\":1,");
        return InvalidJson("unknown-header-field.json", text, LedgerDiagnosticCodes.UnknownField);
    }

    internal static FixtureArtifact UnknownHeaderKind(string bootstrap)
    {
        var text = Mutate(bootstrap, "\"kind\":\"bootstrap\"", "\"kind\":\"mystery\"");
        return InvalidJson("unknown-header-kind.json", text, LedgerDiagnosticCodes.SchemaViolation);
    }

    internal static FixtureArtifact OverlongSummary(string bootstrap)
    {
        var text = Mutate(bootstrap, "\"summary\":\"Summary text.\"", "\"summary\":\"" + new string('x', 4001) + "\"");
        return InvalidJson("overlong-summary.json", text, LedgerDiagnosticCodes.OverlongValue);
    }

    internal static FixtureArtifact ChangedFileStatOutOfRange(string bootstrap)
    {
        var text = Mutate(bootstrap, "\"changedFiles\":[]", "\"changedFiles\":[" + ChangedFile("src/a.cs", "modified", "1000001") + "]");
        return InvalidJson("changed-file-stat-out-of-range.json", text, LedgerDiagnosticCodes.SchemaViolation);
    }

    internal static FixtureArtifact ChangedFileNegativeStat(string bootstrap)
    {
        var text = Mutate(bootstrap, "\"changedFiles\":[]", "\"changedFiles\":[" + ChangedFile("src/a.cs", "modified", "-1") + "]");
        return InvalidJson("changed-file-negative-stat.json", text, LedgerDiagnosticCodes.SchemaViolation);
    }

    internal static FixtureArtifact WhitespaceSummary(string bootstrap)
    {
        var text = Mutate(bootstrap, "\"summary\":\"Summary text.\"", "\"summary\":\"   \"");
        return InvalidJson("whitespace-summary.json", text, LedgerDiagnosticCodes.SchemaViolation);
    }

    internal static FixtureArtifact AbsolutePathInFinding(string bootstrap)
    {
        var finding = Finding("\"/etc/passwd\"", "null", "null");
        var text = Mutate(bootstrap, "\"findings\":[]", "\"findings\":[" + finding + "]");
        return InvalidJson("absolute-path-in-finding.json", text, LedgerDiagnosticCodes.SchemaViolation);
    }

    internal static FixtureArtifact FindingLineOverCap(string bootstrap)
    {
        var finding = Finding("\"src/a.cs\"", "2147483648", "1");
        var text = Mutate(bootstrap, "\"findings\":[]", "\"findings\":[" + finding + "]");
        return InvalidJson("finding-line-over-cap.json", text, LedgerDiagnosticCodes.SchemaViolation);
    }

    internal static FixtureArtifact UnsupportedChangeStatus(string bootstrap)
    {
        var text = Mutate(bootstrap, "\"changedFiles\":[]", "\"changedFiles\":[" + ChangedFile("src/a.cs", "patched", "0") + "]");
        return InvalidJson("unsupported-change-status.json", text, LedgerDiagnosticCodes.UnsupportedChangeStatus);
    }

    internal static FixtureArtifact RecordsEmpty(string bootstrap)
    {
        var recordsStart = Anchor(bootstrap, "\"records\":[") + "\"records\":[".Length;
        var recordsEnd = bootstrap.Length - SchemaVersionTail.Length;
        var text = bootstrap[..recordsStart] + bootstrap[recordsEnd..];
        return InvalidJson("records-empty.json", text, LedgerDiagnosticCodes.RecordsEmpty);
    }

    internal static FixtureArtifact InteractionLimitExceeded(string bootstrap)
    {
        // 33 pairs = 66 records, one past the schema maxItems cap of 64.
        var text = HandRolledLedger(bootstrap, pairCount: 33, limitationsPerOutcome: 0, limitationLength: 0);
        return InvalidJson("interaction-limit-exceeded.json", text, LedgerDiagnosticCodes.InteractionLimitExceeded);
    }

    internal static FixtureArtifact ChangedFileLimitExceeded(string bootstrap)
    {
        // 201 changed files, one past the maxItems cap of 200.
        var sb = new StringBuilder("\"changedFiles\":[");
        for (var i = 0; i < 201; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(ChangedFile("src/f" + i + ".cs", "modified", "0"));
        }

        sb.Append(']');
        var text = Mutate(bootstrap, "\"changedFiles\":[]", sb.ToString());
        return InvalidJson("changed-file-limit-exceeded.json", text, LedgerDiagnosticCodes.ChangedFileLimitExceeded);
    }

    internal static FixtureArtifact FindingLimitExceeded(string bootstrap)
    {
        // 51 findings, one past the maxItems cap of 50.
        var sb = new StringBuilder("\"findings\":[");
        for (var i = 0; i < 51; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(Finding("null", "null", "null"));
        }

        sb.Append(']');
        var text = Mutate(bootstrap, "\"findings\":[]", sb.ToString());
        return InvalidJson("finding-limit-exceeded.json", text, LedgerDiagnosticCodes.FindingLimitExceeded);
    }

    internal static FixtureArtifact LimitationsLimitExceeded(string bootstrap)
    {
        // 17 limitations, one past the maxItems cap of 16.
        var sb = new StringBuilder("\"limitations\":[");
        for (var i = 0; i < 17; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append("\"x\"");
        }

        sb.Append(']');
        var text = Mutate(bootstrap, "\"limitations\":[]", sb.ToString());
        return InvalidJson("limitations-limit-exceeded.json", text, LedgerDiagnosticCodes.LimitationsLimitExceeded);
    }

    internal static FixtureArtifact BootstrapNonzeroGeneration(string bootstrap)
    {
        var text = Mutate(bootstrap, "\"stateGeneration\":0", "\"stateGeneration\":1");
        return InvalidJson("bootstrap-nonzero-generation.json", text, LedgerDiagnosticCodes.BootstrapShapeViolation);
    }

    internal static FixtureArtifact RecoveryRootNonzeroGeneration(string recoveryRoot)
    {
        var text = Mutate(recoveryRoot, "\"stateGeneration\":0", "\"stateGeneration\":1");
        return InvalidJson("recovery-root-nonzero-generation.json", text, LedgerDiagnosticCodes.RecoveryRootShapeViolation);
    }

    internal static FixtureArtifact RecoveryRootMissingReason(string recoveryRoot)
    {
        var text = Mutate(recoveryRoot, "\"recoveryReason\":\"integrity_mismatch\",", "");
        return InvalidJson("recovery-root-missing-reason.json", text, LedgerDiagnosticCodes.RecoveryRootReasonMissing);
    }

    internal static FixtureArtifact ResetMissingReason(string reset)
    {
        var text = Mutate(reset, "\"resetReason\":\"base_change\",", "");
        return InvalidJson("reset-missing-reason.json", text, LedgerDiagnosticCodes.ResetReasonMissing);
    }

    internal static FixtureArtifact ResetForbiddenField(string reset)
    {
        // recoveryReason is declared in the header oneOf union but forbidden by the reset variant.
        var text = Mutate(reset, "\"kind\":\"reset\",", "\"kind\":\"reset\",\"recoveryReason\":\"integrity_mismatch\",");
        return InvalidJson("reset-forbidden-field.json", text, LedgerDiagnosticCodes.ResetShapeViolation);
    }

    internal static FixtureArtifact ContinuationForbiddenField(string continuation)
    {
        // resetReason is declared in the header oneOf union but forbidden by the continuation variant.
        var text = Mutate(continuation, "\"kind\":\"continuation\",", "\"kind\":\"continuation\",\"resetReason\":\"base_change\",");
        return InvalidJson("continuation-forbidden-field.json", text, LedgerDiagnosticCodes.ContinuationShapeViolation);
    }

    internal static FixtureArtifact RecordRoleMismatch(string bootstrap)
    {
        var text = Mutate(bootstrap, "\"role\":\"review_outcome\"", "\"role\":1");
        return InvalidJson("record-role-mismatch.json", text, LedgerDiagnosticCodes.RecordRoleMismatch);
    }

    // ---- Structural bounds stage -----------------------------------------------

    internal static FixtureArtifact IdentityByteLengthExceeded(string bootstrap)
    {
        // 256 characters that encode to 512 UTF-8 bytes: passes the schema character cap,
        // fails the structural 256-byte identity cap.
        var text = Mutate(bootstrap, "\"workflowIdentity\":\"ci\"", "\"workflowIdentity\":\"" + new string('é', 256) + "\"");
        return InvalidJson("identity-byte-length-exceeded.json", text, LedgerDiagnosticCodes.IdentityByteLengthExceeded);
    }

    internal static FixtureArtifact ControlCharacterInIdentity(string bootstrap)
    {
        // A non-NUL control character clears the Unicode-safety stage and the schema
        // (identityString has no pattern), then fails the structural control check.
        var text = Mutate(bootstrap, "\"workflowIdentity\":\"ci\"", "\"workflowIdentity\":\"ci\\u0001\"");
        return InvalidJson("control-character-in-identity.json", text, LedgerDiagnosticCodes.ControlCharacterInIdentity);
    }

    internal static FixtureArtifact CanonicalByteLimitExceeded(string bootstrap)
    {
        // 32 padded pairs: canonical bytes above the 256 KiB cap, raw bytes still under
        // the 512 KiB raw cap.
        var text = HandRolledLedger(bootstrap, pairCount: 32, limitationsPerOutcome: 8, limitationLength: 1200);
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > LedgerParser.LedgerRawByteLimit)
        {
            throw new InvalidOperationException(
                $"canonical-byte-limit-exceeded self-check failed: raw bytes {bytes.Length} exceed the raw cap.");
        }

        return Invalid("canonical-byte-limit-exceeded.json", bytes, LedgerDiagnosticCodes.CanonicalByteLimitExceeded);
    }

    // ---- Semantic invariants stage ---------------------------------------------

    internal static FixtureArtifact RecordsOddLength(string bootstrap)
    {
        // Append a third record (outcome at ordinal 1) to the minimal two-record bootstrap.
        var outcomeRecord = ExtractFirstOutcomeRecord(bootstrap)
            .Replace("\"interactionOrdinal\":0", "\"interactionOrdinal\":1", StringComparison.Ordinal);
        var text = bootstrap[..^SchemaVersionTail.Length] + "," + outcomeRecord + SchemaVersionTail;
        return InvalidJson("records-odd-length.json", text, LedgerDiagnosticCodes.RecordsLengthNotEven);
    }

    internal static FixtureArtifact OrdinalGap(string continuation)
    {
        // Second pair jumps from ordinal 1 to 2 while the first pair stays at 0.
        var text = MutateAll(continuation, "\"interactionOrdinal\":1", "\"interactionOrdinal\":2", expectedOccurrences: 2);
        return InvalidJson("ordinal-gap.json", text, LedgerDiagnosticCodes.OrdinalGap);
    }

    internal static FixtureArtifact DuplicateInteraction(string continuation)
    {
        // Both pairs at ordinal 0: the (role, ordinal) tuple repeats.
        var text = MutateAll(continuation, "\"interactionOrdinal\":1", "\"interactionOrdinal\":0", expectedOccurrences: 2);
        return InvalidJson("duplicate-interaction.json", text, LedgerDiagnosticCodes.DuplicateInteraction);
    }

    internal static FixtureArtifact PairOrderSwapped(string continuation)
    {
        var recordsStart = Anchor(continuation, "\"records\":[") + "\"records\":[".Length;
        var recordsEnd = continuation.Length - SchemaVersionTail.Length;
        var records = continuation[recordsStart..recordsEnd].Split("},{");
        if (records.Length != 4)
        {
            throw new InvalidOperationException("pair-order-swapped self-check failed: expected 4 records.");
        }

        // Split yields the record bodies; the first body keeps its leading '{' and the
        // last body keeps its trailing '}'. Strip both before recombining.
        var bodies = new[]
        {
            records[0].Substring(1),
            records[1],
            records[2],
            records[3].Substring(0, records[3].Length - 1)
        };
        var text = continuation[..recordsStart]
            + "{" + bodies[1] + "},{" + bodies[0] + "},{" + bodies[2] + "},{" + bodies[3] + "}"
            + continuation[recordsEnd..];
        return InvalidJson("pair-order-swapped.json", text, LedgerDiagnosticCodes.PairOrderMismatch);
    }

    internal static FixtureArtifact PairInteractionIdMismatch(string bootstrap)
    {
        var text = MutateSecond(
            bootstrap,
            "\"interactionId\":\"" + LedgerFixtureBaseline.InteractionId + "\"",
            "\"interactionId\":\"" + new string('2', 64) + "\"");
        return InvalidJson("pair-interaction-id-mismatch.json", text, LedgerDiagnosticCodes.PairInteractionIdMismatch);
    }

    internal static FixtureArtifact DigestMismatch(string bootstrap)
    {
        // Well-formed 64-hex cacheContractDigest that does not match the header identities.
        var text = Mutate(bootstrap, "\"cacheContractDigest\":\"" + ExtractBaselineDigest(bootstrap) + "\"", "\"cacheContractDigest\":\"" + new string('f', 64) + "\"");
        return InvalidJson("digest-mismatch.json", text, LedgerDiagnosticCodes.DigestMismatch);
    }

    internal static FixtureArtifact FindingLineRangeInvalid(string bootstrap)
    {
        var finding = Finding("\"src/a.cs\"", "5", "3");
        var text = Mutate(bootstrap, "\"findings\":[]", "\"findings\":[" + finding + "]");
        return InvalidJson("finding-line-range-invalid.json", text, LedgerDiagnosticCodes.FindingLineRangeInvalid);
    }

    internal static FixtureArtifact FindingLocationMismatch(string bootstrap)
    {
        var finding = Finding("\"src/a.cs\"", "5", "null");
        var text = Mutate(bootstrap, "\"findings\":[]", "\"findings\":[" + finding + "]");
        return InvalidJson("finding-location-mismatch.json", text, LedgerDiagnosticCodes.FindingLocationMismatch);
    }

    internal static FixtureArtifact FindingLocationMissingPath(string bootstrap)
    {
        var finding = Finding("null", "5", "7");
        var text = Mutate(bootstrap, "\"findings\":[]", "\"findings\":[" + finding + "]");
        return InvalidJson("finding-location-missing-path.json", text, LedgerDiagnosticCodes.FindingLocationMissingPath);
    }

    // ---- Canonical-form stage ----------------------------------------------------

    internal static FixtureArtifact NonCanonicalKeyOrder(string bootstrap)
    {
        // Move schemaVersion to the front: same model, different bytes.
        var text = Mutate(bootstrap, ",\"schemaVersion\":1}", "}");
        text = Mutate(text, "{\"header\":", "{\"schemaVersion\":1,\"header\":");
        return InvalidJson("non-canonical-key-order.json", text, LedgerDiagnosticCodes.NonCanonical);
    }

    internal static FixtureArtifact NonCanonicalStringEscape(string bootstrap)
    {
        // Escape a space: same parsed string, non-canonical bytes.
        var text = Mutate(bootstrap, "\"summary\":\"Summary text.\"", "\"summary\":\"Summary\\u0020text.\"");
        return InvalidJson("non-canonical-string-escape.json", text, LedgerDiagnosticCodes.NonCanonical);
    }

    // ---- Ledger deep-path golden vectors (byte-exact message oracles) ------------

    internal static FixtureArtifact LedgerDeepPathNoTruncation(string bootstrap)
    {
        // 8 unknown ancestors plus the terminal leaf property = 9 sanitized segments.
        var text = Mutate(bootstrap, "{\"header\":", "{" + DeepPathChain(ancestorCount: 8) + ",\"header\":");
        return InvalidJson("ledger-deep-path-no-truncation.json", text, LedgerDiagnosticCodes.InvalidUnicode, DeepPathNoTruncationMessage);
    }

    internal static FixtureArtifact LedgerDeepPathTruncation(string bootstrap)
    {
        // 12 unknown ancestors plus the terminal leaf property = 13 sanitized segments.
        var text = Mutate(bootstrap, "{\"header\":", "{" + DeepPathChain(ancestorCount: 12) + ",\"header\":");
        return InvalidJson("ledger-deep-path-truncation.json", text, LedgerDiagnosticCodes.InvalidUnicode, DeepPathTruncationMessage);
    }

    // ---- Helpers -----------------------------------------------------------------

    private static FixtureArtifact InvalidJson(string fileName, string text, string expectedCode, string? expectedMessage = null)
    {
        return Invalid(fileName, Encoding.UTF8.GetBytes(text), expectedCode, expectedMessage);
    }

    private static FixtureArtifact Invalid(string fileName, byte[] bytes, string expectedCode, string? expectedMessage = null)
    {
        var outcome = LedgerParser.ParseAndValidate(bytes);
        var actualCode = outcome.Diagnostics.IsEmpty ? "<none>" : outcome.Diagnostics[0].Code;
        if (outcome.Ledger is not null || outcome.Diagnostics.IsEmpty || actualCode != expectedCode)
        {
            throw new InvalidOperationException(
                $"{fileName} self-check failed: expected first diagnostic {expectedCode}, got {(outcome.Ledger is not null ? "<valid>" : actualCode)}.");
        }

        if (expectedMessage is not null && outcome.Diagnostics[0].Message != expectedMessage)
        {
            throw new InvalidOperationException(
                $"{fileName} self-check failed: message mismatch.\nexpected: {expectedMessage}\nactual:   {outcome.Diagnostics[0].Message}");
        }

        return new FixtureArtifact(fileName, bytes, null, expectedCode);
    }

    internal static string Mutate(string text, string search, string replacement)
    {
        if (!text.Contains(search, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"mutation anchor not found: {search}");
        }

        return text.Replace(search, replacement, StringComparison.Ordinal);
    }

    internal static string MutateAll(string text, string search, string replacement, int expectedOccurrences)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }

        if (count != expectedOccurrences)
        {
            throw new InvalidOperationException($"mutation anchor occurrence mismatch: expected {expectedOccurrences}, found {count}: {search}");
        }

        return text.Replace(search, replacement, StringComparison.Ordinal);
    }

    internal static string MutateSecond(string text, string search, string replacement)
    {
        var first = Anchor(text, search);
        var second = text.IndexOf(search, first + search.Length, StringComparison.Ordinal);
        if (second < 0)
        {
            throw new InvalidOperationException($"mutation anchor has no second occurrence: {search}");
        }

        return text[..second] + replacement + text[(second + search.Length)..];
    }

    internal static int Anchor(string text, string search)
    {
        var index = text.IndexOf(search, StringComparison.Ordinal);
        if (index < 0)
        {
            throw new InvalidOperationException($"anchor not found: {search}");
        }

        return index;
    }

    private static string ExtractFirstOutcomeRecord(string bootstrap)
    {
        var start = Anchor(bootstrap, "{\"findings\"");
        return bootstrap.Substring(start, bootstrap.Length - SchemaVersionTail.Length - start);
    }

    internal static string ExtractBaselineDigest(string bootstrap)
    {
        const string anchor = "\"cacheContractDigest\":\"";
        var start = Anchor(bootstrap, anchor) + anchor.Length;
        return bootstrap.Substring(start, 64);
    }

    private static string Finding(string pathJson, string startLineJson, string endLineJson)
    {
        return "{\"body\":\"b\",\"category\":\"correctness\",\"confidence\":\"high\",\"endLine\":" + endLineJson
            + ",\"path\":" + pathJson + ",\"severity\":\"high\",\"startLine\":" + startLineJson + ",\"title\":\"t\"}";
    }

    private static string ChangedFile(string path, string status, string additions)
    {
        return "{\"additions\":" + additions + ",\"changes\":0,\"deletions\":0,\"path\":\"" + path + "\",\"status\":\"" + status + "\"}";
    }

    private static string DeepPathChain(int ancestorCount)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < ancestorCount; i++)
        {
            sb.Append("\"a").Append(i).Append("\":{");
        }

        sb.Append("\"leaf\":\"\\uD800\"");
        for (var i = 0; i < ancestorCount; i++)
        {
            sb.Append('}');
        }

        return sb.ToString();
    }

    private static string NestedObjectDocument(int depth)
    {
        var sb = new StringBuilder("{");
        for (var i = 1; i < depth; i++)
        {
            sb.Append("\"a\":{");
        }

        sb.Append("\"a\":0");
        for (var i = 0; i < depth; i++)
        {
            sb.Append('}');
        }

        return sb.ToString();
    }

    private static string ArrayDocument(int length)
    {
        var sb = new StringBuilder("{\"a\":[");
        for (var i = 0; i < length; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append('0');
        }

        sb.Append("]}");
        return sb.ToString();
    }

    private static string PropertiesDocument(int count)
    {
        var sb = new StringBuilder("{");
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append("\"k").Append(i).Append("\":0");
        }

        sb.Append('}');
        return sb.ToString();
    }

    // Composes a ledger with the same canonical layout as the writer (sorted keys,
    // compact separators), reusing the bootstrap header and baseline digests. Used only
    // for invalid artifacts whose defect the builder would refuse to mint.
    private static string HandRolledLedger(string bootstrap, int pairCount, int limitationsPerOutcome, int limitationLength)
    {
        var headerEnd = Anchor(bootstrap, ",\"prefixContractVersion\"");
        var digest = ExtractBaselineDigest(bootstrap);
        var limitation = limitationLength > 0 ? new string('x', limitationLength) : null;

        var sb = new StringBuilder();
        sb.Append(bootstrap, 0, headerEnd);
        sb.Append(",\"prefixContractVersion\":1,\"records\":[");
        for (var i = 0; i < pairCount; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            var interactionId = $"{i:x64}";
            sb.Append("{\"cacheContractDigest\":\"").Append(digest)
                .Append("\",\"changedFiles\":[],\"interactionId\":\"").Append(interactionId)
                .Append("\",\"interactionOrdinal\":").Append(i)
                .Append(",\"reviewedBaseSha\":\"").Append(LedgerFixtureBaseline.ReviewedBaseSha)
                .Append("\",\"reviewedHeadSha\":\"").Append(LedgerFixtureBaseline.ReviewedHeadSha)
                .Append("\",\"role\":\"review_context\",\"subjectDigest\":\"").Append(LedgerFixtureBaseline.SubjectDigest)
                .Append("\"},{\"findings\":[],\"interactionId\":\"").Append(interactionId)
                .Append("\",\"interactionOrdinal\":").Append(i)
                .Append(",\"limitations\":[");
            for (var j = 0; j < limitationsPerOutcome; j++)
            {
                if (j > 0)
                {
                    sb.Append(',');
                }

                sb.Append('\"').Append(limitation).Append('\"');
            }

            sb.Append("],\"role\":\"review_outcome\",\"summary\":\"Summary text.\"}");
        }

        sb.Append(SchemaVersionTail);
        return sb.ToString();
    }
}

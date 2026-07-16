using System.Collections.Immutable;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using AgenticPrReview.Runtime.Ledger;

namespace AgenticPrReview.Runtime.LedgerFixtureGen;

/// <summary>
/// Byte-level and JSON-text mutations that produce negative fixtures from a
/// valid canonical ledger seed. Every mutation function returns the raw bytes
/// that should be written to disk; callers must NOT re-canonicalize the output
/// because most mutations intentionally break canonical form.
/// </summary>
internal static partial class Program
{
    internal static byte[] MakeUnsupportedSchemaVersion(ValidatedLedger seed)
        => ReplaceUtf8(seed, "\"schemaVersion\":1", "\"schemaVersion\":2");

    internal static byte[] MakeMissingSchemaVersion(ValidatedLedger seed)
    {
        var text = Utf8(seed);
        text = text.Replace(",\"schemaVersion\":1", "");
        return Encoding.UTF8.GetBytes(text);
    }

    internal static byte[] MakeWrongTypeSchemaVersion(ValidatedLedger seed)
        => ReplaceUtf8(seed, "\"schemaVersion\":1", "\"schemaVersion\":\"1\"");

    internal static byte[] MakeUnsupportedPrefixContractVersion(ValidatedLedger seed)
        => ReplaceUtf8(seed, "\"prefixContractVersion\":1", "\"prefixContractVersion\":99");

    internal static byte[] MakeMissingPrefixContractVersion(ValidatedLedger seed)
    {
        var text = Utf8(seed);
        text = text.Replace("\"prefixContractVersion\":1,", "");
        return Encoding.UTF8.GetBytes(text);
    }

    internal static byte[] MakeWrongTypePrefixContractVersion(ValidatedLedger seed)
        => ReplaceUtf8(seed, "\"prefixContractVersion\":1", "\"prefixContractVersion\":\"1\"");

    internal static byte[] MakeUnknownTopLevel(ValidatedLedger seed)
    {
        var text = Utf8(seed);
        // Insert an unknown top-level property alphabetically after "header".
        text = text.Replace("\"prefixContractVersion\":1,", "\"prefixContractVersion\":1,\"unknownTopLevel\":\"x\",");
        return Encoding.UTF8.GetBytes(text);
    }

    internal static byte[] MakeUnknownHeaderField(ValidatedLedger seed)
    {
        var text = Utf8(seed);
        // Insert an unknown header property before workflowIdentity.
        text = text.Replace("\"workflowIdentity\":", "\"unknownHeader\":\"x\",\"workflowIdentity\":");
        return Encoding.UTF8.GetBytes(text);
    }

    internal static byte[] MakeUnknownHeaderKind(ValidatedLedger seed)
        => ReplaceUtf8(seed, "\"kind\":\"bootstrap\"", "\"kind\":\"unknown-kind\"");

    internal static byte[] MakeOverlongSummary(ValidatedLedger seed)
    {
        var text = Utf8(seed);
        var over = new string('x', LedgerLimits.MaxSummaryChars + 1);
        text = Regex.Replace(text, "\"summary\":\"[^\"]+\"", "\"summary\":\"" + over + "\"");
        return Encoding.UTF8.GetBytes(text);
    }

    internal static byte[] MakeWhitespaceSummary(ValidatedLedger seed)
    {
        var text = Utf8(seed);
        text = Regex.Replace(text, "\"summary\":\"[^\"]+\"", "\"summary\":\"   \"");
        return Encoding.UTF8.GetBytes(text);
    }

    internal static byte[] MakeControlCharInIdentity(ValidatedLedger seed)
    {
        // Inject a control character into the workflowIdentity field.
        var text = Utf8(seed);
        text = text.Replace("\"workflowIdentity\":\"acme/example/.github/workflows/ci.yml\"",
            "\"workflowIdentity\":\"acme/example/.github/workflows/ci.yml\\u0001\"");
        return Encoding.UTF8.GetBytes(text);
    }

    internal static byte[] MakeIdentityByteLengthExceeded(ValidatedLedger seed)
    {
        // Schema maxLength on workflowIdentity is 256 UTF-16 code units. To trigger
        // ledger_identity_byte_length_exceeded (the structural-bounds stage,
        // post-schema) rather than ledger_overlong_value (schema stage), we
        // craft an identity that fits in <= 256 UTF-16 code units but exceeds
        // 256 UTF-8 bytes. Use CJK ideographs (each 1 UTF-16 code unit, 3 UTF-8
        // bytes): 128 chars = 384 UTF-8 bytes.
        var text = Utf8(seed);
        var over = new string('\u4e2d', 128); // "中" repeated 128 times
        text = text.Replace("\"workflowIdentity\":\"acme/example/.github/workflows/ci.yml\"",
            "\"workflowIdentity\":\"" + over + "\"");
        return Encoding.UTF8.GetBytes(text);
    }

    internal static byte[] MakeModelAliasLatest(ValidatedLedger seed)
    {
        // Substitute modelId=latest and re-emit both the header modelId and
        // the derived cacheContractDigest so the parser reaches the semantic
        // stage's model-alias-literal check rather than short-circuiting on
        // digest_mismatch.
        var identitiesLatest = Program.Ident with { ModelId = "latest" };
        var newDigest = LedgerDigests.ComputeCacheContractDigest(identitiesLatest);
        var text = Utf8(seed);
        text = text.Replace("\"modelId\":\"model-2026-01\"", "\"modelId\":\"latest\"");
        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            "\"cacheContractDigest\":\"[a-f0-9]{64}\"",
            "\"cacheContractDigest\":\"" + newDigest + "\"");
        return Encoding.UTF8.GetBytes(text);
    }

    internal static byte[] MakeUnsupportedChangeStatus(ValidatedLedger seed)
        => ReplaceUtf8(seed, "\"status\":\"modified\"", "\"status\":\"weirdstatus\"");

    internal static byte[] MakeAbsolutePathInFinding(ValidatedLedger seed)
    {
        // Inject a finding with an absolute-style path.
        var text = Utf8(seed);
        var findings = "\"findings\":[{\"body\":\"bad\",\"category\":\"maintainability\",\"confidence\":\"medium\",\"endLine\":null,\"path\":\"/etc/passwd\",\"severity\":\"low\",\"startLine\":null,\"title\":\"t\"}]";
        text = Regex.Replace(text, "\"findings\":\\[\\]", findings);
        return Encoding.UTF8.GetBytes(text);
    }

    internal static byte[] MakeDuplicateJsonProperty(ValidatedLedger seed)
    {
        var text = Utf8(seed);
        // Duplicate a top-level key by inserting another "schemaVersion".
        text = text.Replace("\"schemaVersion\":1}", "\"schemaVersion\":1,\"schemaVersion\":1}");
        return Encoding.UTF8.GetBytes(text);
    }

    internal static byte[] MakeInvalidUtf8()
    {
        // A UTF-8 byte sequence that is a valid partial code point but truncated.
        return new byte[] { 0xC3, 0x28 };
    }

    internal static byte[] MakeInvalidJson()
    {
        return Encoding.UTF8.GetBytes("{\"header\":{\"kind\":\"bootstrap\"");
    }

    internal static byte[] MakeRawOversize()
    {
        var pad = new string('x', LedgerLimits.MaxRawBytes + 100);
        return Encoding.UTF8.GetBytes("{\"padding\":\"" + pad + "\"}");
    }

    internal static byte[] MakeNonCanonicalKeyOrder()
    {
        // A minimal document with schemaVersion coming before header alphabetically-wise
        // reversed to break the sorted-property canonical requirement.
        return Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":1,\"prefixContractVersion\":1,\"records\":[],\"header\":{}}");
    }

    internal static byte[] MakeNonCanonicalStringEscape(ValidatedLedger seed)
    {
        // Introduce an unnecessary escape sequence.
        var text = Utf8(seed);
        text = text.Replace("\"role\":\"review_context\"", "\"role\":\"review\\u005fcontext\"");
        return Encoding.UTF8.GetBytes(text);
    }

    // -----------------------------------------------------------------
    // Structural cap violations (raw text output; unbounded documents)

    internal static byte[] MakeDepthExceeded()
    {
        var sb = new StringBuilder();
        var depth = LedgerLimits.MaxJsonDepth + 2;
        sb.Append('{');
        for (var i = 0; i < depth - 1; i++) sb.Append("\"a\":{");
        sb.Append("\"a\":1");
        for (var i = 0; i < depth - 1; i++) sb.Append('}');
        sb.Append('}');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    internal static byte[] MakeArrayLengthExceeded()
    {
        var count = LedgerLimits.MaxArrayLength + 1;
        var sb = new StringBuilder();
        sb.Append("{\"a\":[");
        for (var i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('0');
        }
        sb.Append("]}");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    internal static byte[] MakePropertyCountExceeded()
    {
        // Emit MaxTotalProperties+1 flat properties packed into a JSON object
        // small enough that the raw-byte cap does NOT fire first. We use every
        // JSON-safe ASCII printable character (excluding '"' and '\\'), which
        // yields a 92-character alphabet. Optimally-packed keys:
        //   * 92 single-char keys ("a":0 = 5 bytes each)
        //   * 8464 double-char keys ("ab":0 = 6 bytes each)
        //   * 56981 three-char keys ("abc":0 = 7 bytes each)
        // Plus 65536 commas and 2 braces. Total = 515649 bytes < 524288 cap.
        var alphabet = "!#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[]^_`abcdefghijklmnopqrstuvwxyz{|}~";
        // The above string intentionally omits '"' and '\\' but includes every
        // other ASCII printable character. Sanity check via length assertion.
        if (alphabet.Length != 92)
            throw new InvalidOperationException($"alphabet must be 92 chars; got {alphabet.Length}");
        var count = LedgerLimits.MaxTotalProperties + 1;
        var sb = new StringBuilder(count * 8);
        sb.Append('{');
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var emitted = 0;
        // 1-char keys.
        foreach (var c in alphabet)
        {
            if (emitted == count) break;
            EmitKey(sb, seenKeys, c.ToString(), emitted); emitted++;
        }
        // 2-char keys.
        for (var i = 0; i < alphabet.Length && emitted < count; i++)
        {
            for (var j = 0; j < alphabet.Length && emitted < count; j++)
            {
                var key = new string(new[] { alphabet[i], alphabet[j] });
                EmitKey(sb, seenKeys, key, emitted); emitted++;
            }
        }
        // 3-char keys.
        for (var i = 0; i < alphabet.Length && emitted < count; i++)
        {
            for (var j = 0; j < alphabet.Length && emitted < count; j++)
            {
                for (var k = 0; k < alphabet.Length && emitted < count; k++)
                {
                    var key = new string(new[] { alphabet[i], alphabet[j], alphabet[k] });
                    EmitKey(sb, seenKeys, key, emitted); emitted++;
                }
            }
        }
        sb.Append('}');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static void EmitKey(StringBuilder sb, HashSet<string> seen, string key, int emittedSoFar)
    {
        if (!seen.Add(key)) throw new InvalidOperationException($"duplicate key: {key}");
        if (emittedSoFar > 0) sb.Append(',');
        sb.Append('"'); sb.Append(key); sb.Append("\":0");
    }

    internal static byte[] MakeRawMultiDefect()
    {
        // Duplicate top-level property AND a nested array exceeding cap.
        var count = LedgerLimits.MaxArrayLength + 1;
        var sb = new StringBuilder();
        sb.Append("{\"a\":1,\"a\":1,\"b\":[");
        for (var i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('0');
        }
        sb.Append("]}");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    // -----------------------------------------------------------------
    // Unicode-safety fixtures

    internal static byte[] MakeNulInSummary(ValidatedLedger seed)
    {
        var text = Utf8(seed);
        text = Regex.Replace(text, "\"summary\":\"[^\"]+\"", "\"summary\":\"a\\u0000b\"");
        return Encoding.UTF8.GetBytes(text);
    }

    internal static byte[] MakeLoneSurrogateInString(ValidatedLedger seed)
    {
        var text = Utf8(seed);
        text = Regex.Replace(text, "\"summary\":\"[^\"]+\"", "\"summary\":\"a\\uD800b\"");
        return Encoding.UTF8.GetBytes(text);
    }

    internal static byte[] MakeLoneSurrogateInPropertyName(ValidatedLedger seed)
    {
        // Inject an unknown top-level property whose name contains a lone surrogate.
        var text = Utf8(seed);
        text = text.Replace("\"schemaVersion\":1}", "\"\\uD800\":\"x\",\"schemaVersion\":1}");
        return Encoding.UTF8.GetBytes(text);
    }

    internal static byte[] MakeNulInPropertyName(ValidatedLedger seed)
    {
        var text = Utf8(seed);
        text = text.Replace("\"schemaVersion\":1}", "\"a\\u0000b\":\"x\",\"schemaVersion\":1}");
        return Encoding.UTF8.GetBytes(text);
    }

    internal static byte[] MakeRootScalarLoneSurrogate()
    {
        // A scalar-root JSON document whose value is a lone high surrogate.
        return Encoding.UTF8.GetBytes("\"\\uD800\"");
    }

    internal static byte[] MakeRootScalarNul()
    {
        return Encoding.UTF8.GetBytes("\"\\u0000\"");
    }

    // -----------------------------------------------------------------
    // Semantic invariants (post-schema)

    internal static byte[] MakePairOrderSwapped(ValidatedLedger seed)
    {
        // Swap the two records' order in the array. Each record keeps its
        // own schema; the pair (outcome, context) violates the section 9
        // pair-order invariant.
        var text = Utf8(seed);
        var recordsStart = text.IndexOf("\"records\":[", StringComparison.Ordinal) + "\"records\":[".Length;
        // Find first record (context) end: locate the matching outer '}'.
        var depth = 0;
        var i = recordsStart;
        if (text[i] != '{') throw new InvalidOperationException("records[0] not an object");
        var r0Start = i;
        while (i < text.Length)
        {
            var ch = text[i];
            if (ch == '{') depth++;
            else if (ch == '}') { depth--; if (depth == 0) { i++; break; } }
            i++;
        }
        var r0End = i;
        // Expect ','
        if (text[i] != ',') throw new InvalidOperationException("expected comma between records");
        i++;
        var r1Start = i;
        depth = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (ch == '{') depth++;
            else if (ch == '}') { depth--; if (depth == 0) { i++; break; } }
            i++;
        }
        var r1End = i;
        var record0 = text.Substring(r0Start, r0End - r0Start);
        var record1 = text.Substring(r1Start, r1End - r1Start);
        var swapped = text.Substring(0, r0Start) + record1 + "," + record0 + text.Substring(r1End);
        return Encoding.UTF8.GetBytes(swapped);
    }

    internal static byte[] MakeRecordsOddLength(ValidatedLedger seed)
    {
        // Duplicate the trailing outcome record so records.length == 3 (odd,
        // > schema minItems, < schema maxItems). This ensures the parser
        // reaches the semantic-invariants stage instead of stopping at the
        // schema-level records.minItems check.
        var text = Utf8(seed);
        var recStart = text.IndexOf("\"records\":[", StringComparison.Ordinal);
        if (recStart < 0) throw new InvalidOperationException("records array not found");
        var scan = recStart + "\"records\":[".Length;
        // Skip the first record.
        var depth = 0;
        var i = scan;
        while (i < text.Length)
        {
            var ch = text[i];
            if (ch == '{') depth++;
            else if (ch == '}') { depth--; if (depth == 0) { i++; break; } }
            i++;
        }
        // Now at ',' between record[0] and record[1].
        var commaAt = i;
        i++;
        var record1Start = i;
        depth = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (ch == '{') depth++;
            else if (ch == '}') { depth--; if (depth == 0) { i++; break; } }
            i++;
        }
        var record1End = i;
        var record1Text = text.Substring(record1Start, record1End - record1Start);
        // Duplicate the outcome record to make records.length == 3.
        var newText = text.Substring(0, record1End) + "," + record1Text + text.Substring(record1End);
        return Encoding.UTF8.GetBytes(newText);
    }

    internal static byte[] MakeRecordsEmpty(ValidatedLedger seed)
    {
        // Empty records array.
        var text = Utf8(seed);
        text = Regex.Replace(text, "\"records\":\\[.*\\]", "\"records\":[]");
        return Encoding.UTF8.GetBytes(text);
    }

    // -----------------------------------------------------------------
    // Schema-stage shape violations

    internal static byte[] MakeBootstrapShapeViolation(ValidatedLedger seed)
    {
        // A bootstrap header MUST NOT carry predecessorStateGeneration.
        var text = Utf8(seed);
        text = text.Replace("\"kind\":\"bootstrap\"",
            "\"kind\":\"bootstrap\",\"predecessorStateGeneration\":0");
        return Encoding.UTF8.GetBytes(text);
    }

    internal static byte[] MakeChangedFileLimitExceeded()
    {
        var seed = BuildBootstrap();
        var text = Utf8(seed);
        var sb = new StringBuilder();
        sb.Append("[");
        var count = LedgerLimits.MaxChangedFilesPerContext + 1;
        for (var i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"additions\":0,\"changes\":0,\"deletions\":0,\"path\":\"src/f");
            sb.Append(i);
            sb.Append(".cs\",\"status\":\"modified\"}");
        }
        sb.Append("]");
        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"""changedFiles"":\[[^\]]*\]",
            "\"changedFiles\":" + sb.ToString().Replace("$", "$$"));
        return Encoding.UTF8.GetBytes(text);
    }

    internal static byte[] MakeFindingLimitExceeded()
    {
        var seed = BuildBootstrap();
        var text = Utf8(seed);
        var count = LedgerLimits.MaxFindingsPerOutcome + 1;
        var sb = new StringBuilder();
        sb.Append("[");
        for (var i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"body\":\"b\",\"category\":\"maintainability\",\"confidence\":\"medium\",\"endLine\":null,\"path\":null,\"severity\":\"low\",\"startLine\":null,\"title\":\"t\"}");
        }
        sb.Append("]");
        text = text.Replace("\"findings\":[]", "\"findings\":" + sb.ToString());
        return Encoding.UTF8.GetBytes(text);
    }

    internal static byte[] MakeLimitationsLimitExceeded()
    {
        var seed = BuildBootstrap();
        var text = Utf8(seed);
        var count = LedgerLimits.MaxLimitationsPerOutcome + 1;
        var sb = new StringBuilder();
        sb.Append("[");
        for (var i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("\"limitation-");
            sb.Append(i);
            sb.Append("\"");
        }
        sb.Append("]");
        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"""limitations"":\[[^\]]*\]",
            "\"limitations\":" + sb.ToString().Replace("$", "$$"));
        return Encoding.UTF8.GetBytes(text);
    }

    internal static byte[] MakeRecordRoleMismatch(ValidatedLedger seed)
    {
        var text = Utf8(seed);
        text = text.Replace("\"role\":\"review_context\"", "\"role\":\"tool\"");
        return Encoding.UTF8.GetBytes(text);
    }

    // -----------------------------------------------------------------
    // Semantic invariant violations (post-schema)

    internal static byte[] MakePairInteractionIdMismatch(ValidatedLedger seed)
    {
        var text = Utf8(seed);
        var alt = "1" + new string('0', 63);
        var idx = text.IndexOf("\"role\":\"review_outcome\"", StringComparison.Ordinal);
        if (idx < 0) throw new InvalidOperationException("outcome record not found");
        var recStart = text.LastIndexOf('{', idx);
        var interactionIdKey = "\"interactionId\":\"";
        var interactionIdAt = text.IndexOf(interactionIdKey, recStart);
        if (interactionIdAt < 0) throw new InvalidOperationException("interactionId not in outcome record");
        var valueStart = interactionIdAt + interactionIdKey.Length;
        var valueEnd = text.IndexOf('"', valueStart);
        var newText = text.Substring(0, valueStart) + alt + text.Substring(valueEnd);
        return Encoding.UTF8.GetBytes(newText);
    }

    internal static byte[] MakeFindingLineRangeInvalid()
    {
        var seed = BuildBootstrap();
        var text = Utf8(seed);
        var finding = "{\"body\":\"b\",\"category\":\"maintainability\",\"confidence\":\"medium\",\"endLine\":1,\"path\":\"src/main.cs\",\"severity\":\"low\",\"startLine\":10,\"title\":\"t\"}";
        text = text.Replace("\"findings\":[]", "\"findings\":[" + finding + "]");
        return Encoding.UTF8.GetBytes(text);
    }

    internal static byte[] MakeFindingLocationMismatch()
    {
        var seed = BuildBootstrap();
        var text = Utf8(seed);
        var finding = "{\"body\":\"b\",\"category\":\"maintainability\",\"confidence\":\"medium\",\"endLine\":null,\"path\":\"src/main.cs\",\"severity\":\"low\",\"startLine\":10,\"title\":\"t\"}";
        text = text.Replace("\"findings\":[]", "\"findings\":[" + finding + "]");
        return Encoding.UTF8.GetBytes(text);
    }

    internal static byte[] MakeFindingLocationMissingPath()
    {
        var seed = BuildBootstrap();
        var text = Utf8(seed);
        var finding = "{\"body\":\"b\",\"category\":\"maintainability\",\"confidence\":\"medium\",\"endLine\":10,\"path\":null,\"severity\":\"low\",\"startLine\":5,\"title\":\"t\"}";
        text = text.Replace("\"findings\":[]", "\"findings\":[" + finding + "]");
        return Encoding.UTF8.GetBytes(text);
    }

    internal static byte[] MakeDigestMismatch(ValidatedLedger seed)
    {
        var text = Utf8(seed);
        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            "\"cacheContractDigest\":\"[a-f0-9]{64}\"",
            "\"cacheContractDigest\":\"" + new string('9', 64) + "\"");
        return Encoding.UTF8.GetBytes(text);
    }

    // -----------------------------------------------------------------
    // Helpers

    internal static byte[] ReplaceUtf8(ValidatedLedger seed, string needle, string replacement)
    {
        var text = Utf8(seed);
        var idx = text.IndexOf(needle, StringComparison.Ordinal);
        if (idx < 0) throw new InvalidOperationException($"Needle not found: {needle}");
        var newText = text.Substring(0, idx) + replacement + text.Substring(idx + needle.Length);
        return Encoding.UTF8.GetBytes(newText);
    }

    internal static string Utf8(ValidatedLedger seed) => Encoding.UTF8.GetString(seed.ToCanonicalByteArray());
}

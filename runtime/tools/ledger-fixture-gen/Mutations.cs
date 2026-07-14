using System.Text;
using AgenticPrReview.Runtime.Ledger;

namespace AgenticPrReview.Runtime.LedgerFixtureGen;

internal static partial class Program
{
    private static byte[] MakeRawOversize()
    {
        var padding = new string('x', 600_000);
        return Encoding.UTF8.GetBytes("{\"pad\":\"" + padding + "\"}");
    }

    private static byte[] MakeDuplicateJsonProperty()
    {
        return Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"schemaVersion\":1,\"prefixContractVersion\":1,\"header\":{},\"records\":[]}");
    }

    private static byte[] MakeInvalidUtf8()
    {
        return new byte[] { (byte)'{', (byte)'"', 0xC0, 0x80, (byte)'"', (byte)':', (byte)'1', (byte)'}' };
    }

    private static byte[] MakeNulInSummary(ValidatedLedger bootstrap)
    {
        var text = Encoding.UTF8.GetString(bootstrap.ToCanonicalByteArray());
        return Encoding.UTF8.GetBytes(text.Replace("Bootstrap review complete.", "Bootstrap\\u0000complete"));
    }

    private static byte[] MakeLoneSurrogate(ValidatedLedger bootstrap)
    {
        var text = Encoding.UTF8.GetString(bootstrap.ToCanonicalByteArray());
        return Encoding.UTF8.GetBytes(text.Replace("Bootstrap review complete.", "Bootstrap\\uD800complete"));
    }

    private static byte[] MakeDepthExceeded()
    {
        var sb = new StringBuilder();
        var depth = LedgerLimits.MaxJsonDepth + 5;
        for (var i = 0; i < depth; i++) sb.Append("{\"a\":");
        sb.Append("1");
        for (var i = 0; i < depth; i++) sb.Append('}');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static byte[] MakeArrayLengthExceeded()
    {
        var sb = new StringBuilder();
        sb.Append('[');
        for (var i = 0; i < LedgerLimits.MaxArrayLength + 5; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('1');
        }
        sb.Append(']');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static byte[] MakePropertyCountExceeded()
    {
        // Produce more than 65_536 top-level properties while staying below the
        // 512 KiB raw byte cap. Uses short single- and multi-letter keys so the
        // total byte count remains ~500 KiB.
        var sb = new StringBuilder();
        sb.Append('{');
        var count = 0;
        var alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
        var target = LedgerLimits.MaxTotalProperties + 5;
        for (var i = 0; i < alphabet.Length && count < target; i++, count++)
        {
            if (count > 0) sb.Append(',');
            sb.Append('"').Append(alphabet[i]).Append("\":1");
        }
        for (var i = 0; i < alphabet.Length && count < target; i++)
        {
            for (var j = 0; j < alphabet.Length && count < target; j++, count++)
            {
                if (count > 0) sb.Append(',');
                sb.Append('"').Append(alphabet[i]).Append(alphabet[j]).Append("\":1");
            }
        }
        for (var i = 0; i < alphabet.Length && count < target; i++)
        {
            for (var j = 0; j < alphabet.Length && count < target; j++)
            {
                for (var k = 0; k < alphabet.Length && count < target; k++, count++)
                {
                    if (count > 0) sb.Append(',');
                    sb.Append('"').Append(alphabet[i]).Append(alphabet[j]).Append(alphabet[k]).Append("\":1");
                }
            }
        }
        sb.Append('}');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static byte[] MakeUnknownTopLevel(ValidatedLedger bootstrap)
    {
        return InjectTopLevel(bootstrap, "\"extraField\":\"unknown\"");
    }

    private static byte[] MakeUnknownHeaderField(ValidatedLedger bootstrap)
    {
        var text = Encoding.UTF8.GetString(bootstrap.ToCanonicalByteArray());
        var idx = text.IndexOf("\"header\":{", StringComparison.Ordinal) + "\"header\":{".Length;
        return Encoding.UTF8.GetBytes(text.Substring(0, idx) + "\"extraField\":\"x\"," + text.Substring(idx));
    }

    private static byte[] MakeOverlongSummary(ValidatedLedger bootstrap)
    {
        var text = Encoding.UTF8.GetString(bootstrap.ToCanonicalByteArray());
        var oversize = new string('y', LedgerLimits.MaxSummaryChars + 100);
        return Encoding.UTF8.GetBytes(text.Replace("Bootstrap review complete.", oversize));
    }

    private static byte[] MakeUnsupportedSchemaVersion(ValidatedLedger bootstrap)
    {
        var text = Encoding.UTF8.GetString(bootstrap.ToCanonicalByteArray());
        return Encoding.UTF8.GetBytes(text.Replace("\"schemaVersion\":1", "\"schemaVersion\":99"));
    }

    private static byte[] MakeUnsupportedPrefixContractVersion(ValidatedLedger bootstrap)
    {
        var text = Encoding.UTF8.GetString(bootstrap.ToCanonicalByteArray());
        return Encoding.UTF8.GetBytes(text.Replace("\"prefixContractVersion\":1", "\"prefixContractVersion\":99"));
    }

    private static byte[] MakeUnsafeReferenceShape(ValidatedLedger bootstrap)
    {
        return InjectTopLevel(bootstrap, "\"$ref\":\"http://example.com/blob\"");
    }

    private static byte[] MakeAbsolutePathInFinding(ValidatedLedger bootstrap)
    {
        var text = Encoding.UTF8.GetString(bootstrap.ToCanonicalByteArray());
        var mutated = text.Replace(
            "\"findings\":[]",
            "\"findings\":[{\"body\":\"body text\",\"category\":\"correctness\",\"confidence\":\"medium\",\"endLine\":null,\"path\":\"/etc/passwd\",\"severity\":\"low\",\"startLine\":null,\"title\":\"Absolute path\"}]");
        return Encoding.UTF8.GetBytes(mutated);
    }

    private static byte[] MakeIdentityByteLengthExceeded(ValidatedLedger bootstrap)
    {
        var text = Encoding.UTF8.GetString(bootstrap.ToCanonicalByteArray());
        var big = new string('\u00E9', 200);
        return Encoding.UTF8.GetBytes(text.Replace("\"workflowIdentity\":\"acme/example/.github/workflows/ci.yml\"", "\"workflowIdentity\":\"" + big + "\""));
    }

    private static byte[] MakeControlCharInIdentity(ValidatedLedger bootstrap)
    {
        var text = Encoding.UTF8.GetString(bootstrap.ToCanonicalByteArray());
        return Encoding.UTF8.GetBytes(text.Replace("\"workflowIdentity\":\"acme/example/.github/workflows/ci.yml\"", "\"workflowIdentity\":\"acme\\u0001example\""));
    }

    private static byte[] MakeUnsupportedChangeStatus(ValidatedLedger bootstrap)
    {
        var text = Encoding.UTF8.GetString(bootstrap.ToCanonicalByteArray());
        return Encoding.UTF8.GetBytes(text.Replace("\"status\":\"modified\"", "\"status\":\"weird\""));
    }

    private static byte[] MakeNonCanonicalKeyOrder()
    {
        // Take the valid bootstrap fixture and move schemaVersion from the last property
        // (canonical position) to the first (non-canonical position). The ledger is still
        // schema-valid, so the pipeline advances past schema evaluation and rejects it
        // with ledger_non_canonical.
        var bootstrap = BuildBootstrap();
        var text = Encoding.UTF8.GetString(bootstrap.ToCanonicalByteArray());
        // The canonical form ends with ,"schemaVersion":1}
        var mutated = "{\"schemaVersion\":1," + text.Substring(1);
        // Now remove the trailing ,"schemaVersion":1 that appears just before the final }
        var trailing = ",\"schemaVersion\":1}";
        var replacement = "}";
        var idx = mutated.LastIndexOf(trailing, StringComparison.Ordinal);
        if (idx < 0) throw new InvalidOperationException("schemaVersion tail not found");
        mutated = mutated.Substring(0, idx) + replacement;
        return Encoding.UTF8.GetBytes(mutated);
    }

    private static byte[] MakeNonCanonicalStringEscape(ValidatedLedger bootstrap)
    {
        var text = Encoding.UTF8.GetString(bootstrap.ToCanonicalByteArray());
        return Encoding.UTF8.GetBytes(text.Replace("Bootstrap review complete.", "Bootstr\\u0041p complete."));
    }

    private static byte[] MakeRecordsEmpty(ValidatedLedger bootstrap)
    {
        var text = Encoding.UTF8.GetString(bootstrap.ToCanonicalByteArray());
        var startIdx = text.IndexOf("\"records\":[");
        var end = FindMatchingBracket(text, startIdx + "\"records\":".Length);
        return Encoding.UTF8.GetBytes(text.Substring(0, startIdx + "\"records\":".Length) + "[]" + text.Substring(end + 1));
    }

    private static byte[] MakeRecordsOddLength(ValidatedLedger bootstrap)
    {
        // Start from a valid 2-record ledger and duplicate the first record so we have
        // 3 records total. schema minItems=2 is satisfied, but the semantic invariant
        // "records.Length is even" fires.
        var text = Encoding.UTF8.GetString(bootstrap.ToCanonicalByteArray());
        var arrayStart = text.IndexOf("\"records\":[") + "\"records\":".Length;
        var arrayEnd = FindMatchingBracket(text, arrayStart);
        var body = text.Substring(arrayStart + 1, arrayEnd - arrayStart - 1);
        var parts = SplitTopLevel(body);
        var newBody = "[" + parts[0] + "," + parts[1] + "," + parts[0] + "]";
        return Encoding.UTF8.GetBytes(text.Substring(0, arrayStart) + newBody + text.Substring(arrayEnd + 1));
    }

    private static byte[] MakeOrdinalGap(ValidatedLedger bootstrap)
    {
        var continuation = BuildContinuation(bootstrap);
        var text = Encoding.UTF8.GetString(continuation.ToCanonicalByteArray());
        return Encoding.UTF8.GetBytes(text.Replace("\"interactionOrdinal\":1", "\"interactionOrdinal\":2"));
    }

    private static byte[] MakeDuplicateInteraction(ValidatedLedger bootstrap)
    {
        // Force the second pair's interactionId to match the first pair's so parser sees
        // two ordered pairs with distinct ordinals but a shared interactionId.
        var continuation = BuildContinuation(bootstrap);
        var text = Encoding.UTF8.GetString(continuation.ToCanonicalByteArray());
        var id0 = "00000000" + new string('0', 56);
        var id1 = "00000001" + new string('0', 56);
        return Encoding.UTF8.GetBytes(text.Replace(id1, id0));
    }

    private static byte[] MakePairOrderSwapped(ValidatedLedger bootstrap)
    {
        var text = Encoding.UTF8.GetString(bootstrap.ToCanonicalByteArray());
        var arrayStart = text.IndexOf("\"records\":[") + "\"records\":".Length;
        var arrayEnd = FindMatchingBracket(text, arrayStart);
        var body = text.Substring(arrayStart + 1, arrayEnd - arrayStart - 1);
        var parts = SplitTopLevel(body);
        var swapped = "[" + parts[1] + "," + parts[0] + "]";
        return Encoding.UTF8.GetBytes(text.Substring(0, arrayStart) + swapped + text.Substring(arrayEnd + 1));
    }

    private static byte[] MakeDigestMismatch(ValidatedLedger bootstrap)
    {
        var text = Encoding.UTF8.GetString(bootstrap.ToCanonicalByteArray());
        // Replace subjectDigest value with a distinct 64-hex string.
        var replacement = new string('0', 64);
        var idx = text.IndexOf("\"subjectDigest\":\"");
        var start = idx + "\"subjectDigest\":\"".Length;
        return Encoding.UTF8.GetBytes(text.Substring(0, start) + replacement + text.Substring(start + 64));
    }

    private static byte[] MakeBootstrapNonzeroGeneration(ValidatedLedger bootstrap)
    {
        var text = Encoding.UTF8.GetString(bootstrap.ToCanonicalByteArray());
        return Encoding.UTF8.GetBytes(text.Replace("\"stateGeneration\":0", "\"stateGeneration\":3"));
    }

    private static byte[] MakeRecoveryMissingReason(ValidatedLedger recovery)
    {
        var text = Encoding.UTF8.GetString(recovery.ToCanonicalByteArray());
        return DropField(text, "recoveryReason");
    }

    private static byte[] MakeResetMissingReason(ValidatedLedger reset)
    {
        var text = Encoding.UTF8.GetString(reset.ToCanonicalByteArray());
        return DropField(text, "resetReason");
    }

    private static byte[] MakeResetForbiddenField(ValidatedLedger reset)
    {
        var text = Encoding.UTF8.GetString(reset.ToCanonicalByteArray());
        var idx = text.IndexOf("\"resetReason\":");
        return Encoding.UTF8.GetBytes(text.Substring(0, idx) + "\"recoveryReason\":\"predecessor_unavailable\"," + text.Substring(idx));
    }

    private static byte[] MakeContinuationForbiddenField(ValidatedLedger continuation)
    {
        var text = Encoding.UTF8.GetString(continuation.ToCanonicalByteArray());
        var idx = text.IndexOf("\"repository\":");
        return Encoding.UTF8.GetBytes(text.Substring(0, idx) + "\"recoveryReason\":\"predecessor_unavailable\"," + text.Substring(idx));
    }

    private static byte[] MakeRecoveryForbiddenField(ValidatedLedger recovery)
    {
        var text = Encoding.UTF8.GetString(recovery.ToCanonicalByteArray());
        var idx = text.IndexOf("\"sessionEpoch\":");
        return Encoding.UTF8.GetBytes(text.Substring(0, idx) + "\"resetReason\":\"base_changed\"," + text.Substring(idx));
    }

    private static byte[] MakeRecordRoleTool(ValidatedLedger bootstrap)
    {
        var text = Encoding.UTF8.GetString(bootstrap.ToCanonicalByteArray());
        var idx = text.IndexOf("\"role\":\"review_context\"");
        var replaced = text.Substring(0, idx) + "\"role\":\"tool\"" + text.Substring(idx + "\"role\":\"review_context\"".Length);
        return Encoding.UTF8.GetBytes(replaced);
    }

    private static byte[] MakeFindingLineRangeInvalid()
    {
        var bootstrap = BuildBootstrap();
        var text = Encoding.UTF8.GetString(bootstrap.ToCanonicalByteArray());
        var finding = "{\"body\":\"b\",\"category\":\"correctness\",\"confidence\":\"medium\",\"endLine\":1,\"path\":\"src/a.cs\",\"severity\":\"low\",\"startLine\":5,\"title\":\"t\"}";
        return Encoding.UTF8.GetBytes(text.Replace("\"findings\":[]", "\"findings\":[" + finding + "]"));
    }

    private static byte[] MakeFindingLocationMismatch()
    {
        var bootstrap = BuildBootstrap();
        var text = Encoding.UTF8.GetString(bootstrap.ToCanonicalByteArray());
        var finding = "{\"body\":\"b\",\"category\":\"correctness\",\"confidence\":\"medium\",\"endLine\":null,\"path\":\"src/a.cs\",\"severity\":\"low\",\"startLine\":5,\"title\":\"t\"}";
        return Encoding.UTF8.GetBytes(text.Replace("\"findings\":[]", "\"findings\":[" + finding + "]"));
    }

    private static byte[] MakeFindingLocationMissingPath()
    {
        var bootstrap = BuildBootstrap();
        var text = Encoding.UTF8.GetString(bootstrap.ToCanonicalByteArray());
        var finding = "{\"body\":\"b\",\"category\":\"correctness\",\"confidence\":\"medium\",\"endLine\":10,\"path\":null,\"severity\":\"low\",\"startLine\":5,\"title\":\"t\"}";
        return Encoding.UTF8.GetBytes(text.Replace("\"findings\":[]", "\"findings\":[" + finding + "]"));
    }

    private static byte[] MakeInteractionLimitExceeded()
    {
        var max = BuildMaxInteractions();
        var text = Encoding.UTF8.GetString(max.ToCanonicalByteArray());
        var arrayStart = text.IndexOf("\"records\":[") + "\"records\":".Length;
        var arrayEnd = FindMatchingBracket(text, arrayStart);
        var body = text.Substring(arrayStart + 1, arrayEnd - arrayStart - 1);
        var parts = SplitTopLevel(body);
        var last = parts[^1];
        var beforeLast = parts[^2];
        var newCtx = beforeLast.Replace("\"interactionOrdinal\":31", "\"interactionOrdinal\":32");
        var newOc = last.Replace("\"interactionOrdinal\":31", "\"interactionOrdinal\":32");
        var newBody = "[" + body + "," + newCtx + "," + newOc + "]";
        return Encoding.UTF8.GetBytes(text.Substring(0, arrayStart) + newBody + text.Substring(arrayEnd + 1));
    }

    private static byte[] MakeChangedFileLimitExceeded()
    {
        var bootstrap = BuildBootstrap();
        var text = Encoding.UTF8.GetString(bootstrap.ToCanonicalByteArray());
        var sb = new StringBuilder("[");
        for (var i = 0; i < 201; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"additions\":1,\"changes\":1,\"deletions\":0,\"path\":\"src/file").Append(i).Append(".cs\",\"status\":\"modified\"}");
        }
        sb.Append(']');
        return Encoding.UTF8.GetBytes(ReplaceJsonArrayValue(text, "\"changedFiles\":", sb.ToString()));
    }

    private static byte[] MakeFindingLimitExceeded()
    {
        var bootstrap = BuildBootstrap();
        var text = Encoding.UTF8.GetString(bootstrap.ToCanonicalByteArray());
        var sb = new StringBuilder("[");
        for (var i = 0; i < 51; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"body\":\"b\",\"category\":\"correctness\",\"confidence\":\"medium\",\"endLine\":null,\"path\":null,\"severity\":\"low\",\"startLine\":null,\"title\":\"t").Append(i).Append("\"}");
        }
        sb.Append(']');
        return Encoding.UTF8.GetBytes(ReplaceJsonArrayValue(text, "\"findings\":", sb.ToString()));
    }

    private static byte[] MakeLimitationsLimitExceeded()
    {
        var bootstrap = BuildBootstrap();
        var text = Encoding.UTF8.GetString(bootstrap.ToCanonicalByteArray());
        var sb = new StringBuilder("[");
        for (var i = 0; i < 17; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("\"limit").Append(i).Append('"');
        }
        sb.Append(']');
        return Encoding.UTF8.GetBytes(ReplaceJsonArrayValue(text, "\"limitations\":", sb.ToString()));
    }

    private static byte[] MakeCanonicalByteLimitExceeded()
    {
        // Build 32-pair ledger, then inflate each outcome with many maximal-length findings
        // so canonical bytes cross 256 KiB. Manual JSON assembly since the builder rejects
        // over-bound candidates.
        var max = BuildMaxInteractions();
        var text = Encoding.UTF8.GetString(max.ToCanonicalByteArray());
        var body4000 = new string('x', LedgerLimits.MaxFindingBodyChars);
        var findings = new System.Text.StringBuilder("[");
        // 4 findings per outcome × 32 outcomes = 128 findings; each ~4100 bytes; total ~524 KB.
        // We need > 256 KB but < 512 KB (raw cap), so target 2 findings per outcome (~262 KB new).
        for (var i = 0; i < 2; i++)
        {
            if (i > 0) findings.Append(',');
            findings.Append("{\"body\":\"").Append(body4000).Append("\",\"category\":\"correctness\",\"confidence\":\"medium\",\"endLine\":null,\"path\":null,\"severity\":\"low\",\"startLine\":null,\"title\":\"t\"}");
        }
        findings.Append(']');
        var findingsJson = findings.ToString();
        // Replace every "findings":[] with the loaded findings array.
        text = text.Replace("\"findings\":[]", "\"findings\":" + findingsJson);
        return Encoding.UTF8.GetBytes(text);
    }

    private static ValidatedLedger MutateModifyHistory(ValidatedLedger continuation)
    {
        // Build a continuation whose first pair differs from the real bootstrap fixture's
        // first pair (different reviewedHeadSha/subjectDigest), then rewrite the header's
        // predecessorLedgerSha256 to match the real bootstrap's hash so that the predecessor
        // hash check passes and the transition validator surfaces
        // ledger_continuation_prefix_mismatch.
        var realBootstrap = BuildBootstrap();
        var altBootstrap = BuildAltBootstrap();
        var altContinuation = BuildContinuationFrom(altBootstrap);
        var text = Encoding.UTF8.GetString(altContinuation.ToCanonicalByteArray());
        var mutated = text.Replace(altBootstrap.ContentSha256, realBootstrap.ContentSha256);
        return Reparse(mutated, "continuation-modified-history");
    }

    private static ValidatedLedger BuildAltBootstrap()
    {
        var ctx = Ctx(0, "abcdefabcdefabcdefabcdefabcdefabcdefabcd", "efabcdefabcdefabcdefabcdefabcdefabcdefab", Ident);
        var oc = Outcome(0, "Bootstrap review complete.");
        var built = LedgerBuilder.CreateBootstrap(new BootstrapTransition(Ident, 0, 1), ctx, oc);
        return built.Ledger ?? throw new InvalidOperationException(built.Failure!.Code);
    }

    private static ValidatedLedger BuildContinuationFrom(ValidatedLedger predecessor)
    {
        var ctx = Ctx(1, "3333333333333333333333333333333333333333", "2222222222222222222222222222222222222222", Ident);
        var oc = Outcome(1, "Continuation review complete.");
        var expected = new ContinuationTransition(Ident, predecessor.ContentSha256, 0, 1, 1);
        var built = LedgerBuilder.AppendContinuation(predecessor, expected, ctx, oc);
        return built.Ledger ?? throw new InvalidOperationException(built.Failure!.Code);
    }

    private static ValidatedLedger MutatePredecessorHash(ValidatedLedger continuation)
    {
        var text = Encoding.UTF8.GetString(continuation.ToCanonicalByteArray());
        var idx = text.IndexOf("\"predecessorLedgerSha256\":\"");
        var start = idx + "\"predecessorLedgerSha256\":\"".Length;
        var mutated = text.Substring(0, start) + new string('f', 64) + text.Substring(start + 64);
        return Reparse(mutated, "continuation-wrong-predecessor-hash");
    }

    private static ValidatedLedger MutateCacheContract(ValidatedLedger continuation)
    {
        var text = Encoding.UTF8.GetString(continuation.ToCanonicalByteArray());
        // Change the adapterId to a different valid 64-hex string. Also update
        // cacheContractDigest values in records to match so digest checks still pass.
        var newAdapter = new string('f', 64);
        var mutated = text.Replace("\"adapterId\":\"" + new string('a', 64) + "\"", "\"adapterId\":\"" + newAdapter + "\"");
        // Recompute cacheContractDigest so semantic invariants pass; produce it via the digest helper.
        // Since the header's cache-contract fields other than adapter are unchanged, compute new digest.
        var newDigest = ComputeCacheContractDigestFromMutatedHeader(newAdapter);
        // Replace both cacheContractDigest values.
        var oldDigest = LedgerDigests.ComputeCacheContractDigest(Ident);
        mutated = mutated.Replace("\"cacheContractDigest\":\"" + oldDigest + "\"", "\"cacheContractDigest\":\"" + newDigest + "\"");
        return Reparse(mutated, "continuation-cache-contract-changed");
    }

    private static string ComputeCacheContractDigestFromMutatedHeader(string adapterId)
    {
        var identities = Ident with { AdapterId = adapterId };
        return LedgerDigests.ComputeCacheContractDigest(identities);
    }

    private static ValidatedLedger MutateLedgerEpoch(ValidatedLedger continuation)
    {
        var text = Encoding.UTF8.GetString(continuation.ToCanonicalByteArray());
        var mutated = text.Replace("\"ledgerEpoch\":1", "\"ledgerEpoch\":9");
        return Reparse(mutated, "continuation-epoch-changed");
    }

    private static ValidatedLedger MutatePredecessorStateGeneration(ValidatedLedger continuation)
    {
        var text = Encoding.UTF8.GetString(continuation.ToCanonicalByteArray());
        var mutated = text.Replace("\"predecessorStateGeneration\":0", "\"predecessorStateGeneration\":7");
        return Reparse(mutated, "continuation-predecessor-generation-mismatch");
    }

    private static ValidatedLedger MutateResetWithPredecessorRecords(ValidatedLedger reset, ValidatedLedger predecessor)
    {
        // Build a "reset" candidate whose records array carries two pairs (ord 0 and ord 1)
        // instead of the single pair required by the reset shape. Both pairs are internally
        // consistent so the parse pipeline accepts the candidate; the transition validator
        // rejects it with ledger_reset_records_shape_mismatch.
        //
        // Assemble the extra pair by building a fresh continuation candidate from the current
        // reset ledger — that yields byte-canonical (ord 1) records with matching digests.
        var extraLedger = ExtendResetWithContinuation(reset);
        var extraText = Encoding.UTF8.GetString(extraLedger.ToCanonicalByteArray());
        var extraArrStart = extraText.IndexOf("\"records\":[") + "\"records\":".Length;
        var extraArrEnd = FindMatchingBracket(extraText, extraArrStart);
        var extraBody = extraText.Substring(extraArrStart + 1, extraArrEnd - extraArrStart - 1);
        var extraParts = SplitTopLevel(extraBody);
        // extraParts has 4 items: [ctx0, oc0, ctx1, oc1] — we take the last pair.
        var extraCtx = extraParts[2];
        var extraOc = extraParts[3];

        var text = Encoding.UTF8.GetString(reset.ToCanonicalByteArray());
        var arrStart = text.IndexOf("\"records\":[") + "\"records\":".Length;
        var arrEnd = FindMatchingBracket(text, arrStart);
        var body = text.Substring(arrStart + 1, arrEnd - arrStart - 1);
        var newBody = "[" + body + "," + extraCtx + "," + extraOc + "]";
        var mutated = text.Substring(0, arrStart) + newBody + text.Substring(arrEnd + 1);
        return Reparse(mutated, "reset-with-predecessor-records");
    }

    private static ValidatedLedger ExtendResetWithContinuation(ValidatedLedger resetLedger)
    {
        // Append one continuation to the reset ledger (which already has cache-contract
        // identities from IdentAltCache).
        var ctx = Ctx(1, "4444444444444444444444444444444444444444", "2222222222222222222222222222222222222222", IdentAltCache);
        var oc = Outcome(1, "Extra pair that should not appear in a reset.");
        var expected = new ContinuationTransition(IdentAltCache, resetLedger.ContentSha256,
            resetLedger.Model.Header.StateGeneration, resetLedger.Model.Header.StateGeneration + 1, resetLedger.Model.Header.LedgerEpoch);
        var built = LedgerBuilder.AppendContinuation(resetLedger, expected, ctx, oc);
        return built.Ledger ?? throw new InvalidOperationException(built.Failure!.Code);
    }

    private static ValidatedLedger MutateResetSameEpoch(ValidatedLedger reset)
    {
        var text = Encoding.UTF8.GetString(reset.ToCanonicalByteArray());
        var mutated = text.Replace("\"ledgerEpoch\":2", "\"ledgerEpoch\":1");
        return Reparse(mutated, "reset-same-epoch");
    }

    private static ValidatedLedger MutateResetManifestHash(ValidatedLedger reset)
    {
        var text = Encoding.UTF8.GetString(reset.ToCanonicalByteArray());
        var idx = text.IndexOf("\"predecessorManifestSha256\":\"");
        var start = idx + "\"predecessorManifestSha256\":\"".Length;
        var mutated = text.Substring(0, start) + new string('9', 64) + text.Substring(start + 64);
        return Reparse(mutated, "reset-wrong-manifest-hash");
    }

    private static object BuildOverBoundByteScenario()
    {
        var body = new string('x', LedgerLimits.MaxFindingBodyChars);
        var findings = new List<object>();
        for (var i = 0; i < 30; i++)
        {
            findings.Add(new
            {
                severity = "low",
                confidence = "medium",
                category = "correctness",
                title = "t" + i,
                body,
                path = (string?)null,
                startLine = (int?)null,
                endLine = (int?)null,
            });
        }
        return new
        {
            contextSource = new
            {
                reviewedHeadSha = "3333333333333333333333333333333333333333",
                reviewedBaseSha = "2222222222222222222222222222222222222222",
                changedFiles = Array.Empty<object>(),
            },
            outcomeSource = new
            {
                summary = new string('x', LedgerLimits.MaxSummaryChars),
                findings = findings.ToArray(),
                limitations = Array.Empty<string>(),
            }
        };
    }

    private static object BuildOverBoundInteractionsScenario()
    {
        return new
        {
            contextSource = new
            {
                reviewedHeadSha = "3333333333333333333333333333333333333333",
                reviewedBaseSha = "2222222222222222222222222222222222222222",
                changedFiles = Array.Empty<object>(),
            },
            outcomeSource = new
            {
                summary = "One more append.",
                findings = Array.Empty<object>(),
                limitations = Array.Empty<string>(),
            }
        };
    }

    // Helpers

    private static byte[] InjectTopLevel(ValidatedLedger v, string extraJsonProperty)
    {
        var text = Encoding.UTF8.GetString(v.ToCanonicalByteArray());
        return Encoding.UTF8.GetBytes("{" + extraJsonProperty + "," + text.Substring(1));
    }

    private static int FindMatchingBracket(string s, int openIdx)
    {
        var open = s[openIdx];
        var close = open == '[' ? ']' : '}';
        var depth = 0;
        for (var i = openIdx; i < s.Length; i++)
        {
            if (s[i] == '"')
            {
                i++;
                while (i < s.Length && s[i] != '"') { if (s[i] == '\\') i++; i++; }
                continue;
            }
            if (s[i] == open) depth++;
            else if (s[i] == close) { depth--; if (depth == 0) return i; }
        }
        throw new InvalidOperationException("Unmatched bracket at " + openIdx);
    }

    private static List<string> SplitTopLevel(string body)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < body.Length; i++)
        {
            var ch = body[i];
            if (ch == '"')
            {
                i++;
                while (i < body.Length && body[i] != '"') { if (body[i] == '\\') i++; i++; }
                continue;
            }
            if (ch == '{' || ch == '[') depth++;
            else if (ch == '}' || ch == ']') depth--;
            else if (ch == ',' && depth == 0) { parts.Add(body.Substring(start, i - start)); start = i + 1; }
        }
        if (start < body.Length) parts.Add(body.Substring(start));
        return parts;
    }

    private static string ReplaceJsonArrayValue(string text, string keyPrefix, string newArray)
    {
        var idx = text.IndexOf(keyPrefix, StringComparison.Ordinal);
        if (idx < 0) throw new InvalidOperationException("key not found: " + keyPrefix);
        var arrStart = idx + keyPrefix.Length;
        var arrEnd = FindMatchingBracket(text, arrStart);
        return text.Substring(0, arrStart) + newArray + text.Substring(arrEnd + 1);
    }

    private static byte[] DropField(string text, string field)
    {
        var key = "\"" + field + "\":";
        var idx = text.IndexOf(key);
        if (idx < 0) throw new InvalidOperationException("field not found: " + field);
        // Skip the value (a quoted string).
        var valueStart = idx + key.Length;
        var q1 = text.IndexOf('"', valueStart);
        var q2 = text.IndexOf('"', q1 + 1);
        while (text[q2 - 1] == '\\') q2 = text.IndexOf('"', q2 + 1);
        var end = q2 + 1;
        // Remove either the leading comma OR the trailing comma, never both.
        int cutStart, cutEnd;
        if (idx > 0 && text[idx - 1] == ',')
        {
            cutStart = idx - 1;
            cutEnd = end;
        }
        else if (end < text.Length && text[end] == ',')
        {
            cutStart = idx;
            cutEnd = end + 1;
        }
        else
        {
            cutStart = idx;
            cutEnd = end;
        }
        return Encoding.UTF8.GetBytes(text.Substring(0, cutStart) + text.Substring(cutEnd));
    }

    private static ValidatedLedger Reparse(string mutatedText, string label)
    {
        var bytes = Encoding.UTF8.GetBytes(mutatedText);
        var res = LedgerParser.ParseAndValidate(bytes);
        if (res.Ledger is null)
        {
            throw new InvalidOperationException($"transition fixture '{label}' failed to parse: {res.Failure?.Code}");
        }
        return res.Ledger;
    }
}




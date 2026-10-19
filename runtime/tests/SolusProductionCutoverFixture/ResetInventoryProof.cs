using System.IO.Compression;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Host.State.Transactions;
using AgenticPrReview.Runtime.Host.Publishing.Recovery;

namespace AgenticPrReview.Runtime.SolusProductionCutoverFixture;

// Read-only oracle over original official SDK ZIPs. Production codecs authenticate
// every object; only the existing same-identity head retention refresh may vary.
internal static class ResetInventoryProof
{
    internal sealed record Entry(string Id, string Name, string Archive, long Expires);
    internal sealed record Input(Entry[] Before, Entry[] After);
    private sealed record Decoded(Entry Entry, byte[] Bytes, StateControlHeaderV1? Header, LineageHeadV1? Head,
        LocatorRootSentinel? Sentinel = null, string? PayloadSha256 = null, byte[]? Payload = null);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static string mismatch = "reset_retry_inventory";
    private static int cleanupStage;
    private static bool Mismatch(string code) { mismatch = code; return false; }

    internal static int Run(string path)
    {
        var input = JsonSerializer.Deserialize<Input>(File.ReadAllBytes(path), Json)!;
        using var access = AuthorizedLocatorAccess.IssueTrustedProofEvidenceOracle("42");
        Check(LocatorStateKeyRing.TryCreate(access, "42", Convert.ToBase64String(Enumerable.Repeat((byte)0x74, 32).ToArray()),
            null, out var keys, out _), "reset_oracle_keys");
        using (keys)
        {
            var before = Read(input.Before, access, keys!);
            var after = Read(input.After, access, keys!);
            Check(Matches(before, after), mismatch);
            var selectedHead = SelectHead(after)!;
            var headIndex = Array.FindIndex(after, item => ReferenceEquals(item, selectedHead));
            var nonHeadIndex = Array.FindIndex(after, item => item.Header?.ObjectClass == StateObjectClass.Acceptance);
            Check(headIndex >= 0 && nonHeadIndex >= 0, "reset_oracle_accepted");
            var mutated = after.ToArray();
            mutated[headIndex] = after[headIndex] with { Header = after[headIndex].Header! with { Epoch = new string('f', 64) } };
            Check(!Matches(before, mutated), "reset_oracle_epoch_mutant");
            mutated = after.ToArray();
            mutated[headIndex] = after[headIndex] with { Head = after[headIndex].Head! with { ResetPublicationTarget = null } };
            Check(!Matches(before, mutated), "reset_oracle_target_mutant");
            mutated = after.ToArray();
            mutated[nonHeadIndex] = after[nonHeadIndex] with { Entry = after[nonHeadIndex].Entry with { Id = "999999" } };
            Check(!Matches(before, mutated), "reset_oracle_acceptance_mutant");
            Check(!Matches(before, [.. after, after[nonHeadIndex]]), "reset_oracle_added_record_mutant");
            var ancestorIndex = Array.FindIndex(after, item => item.Head is not null && !ReferenceEquals(item, selectedHead));
            Check(ancestorIndex >= 0, "reset_oracle_predecessor");
            mutated = after.ToArray();
            mutated[ancestorIndex] = after[ancestorIndex] with { Entry = after[ancestorIndex].Entry with { Id = "999999" } };
            Check(!Matches(before, mutated), "reset_oracle_predecessor_mutant");
            var rootIndex = Array.FindIndex(after, item => item.Sentinel is not null);
            mutated = after.ToArray();
            mutated[rootIndex] = after[rootIndex] with { Sentinel = after[rootIndex].Sentinel! with { Root = new byte[32] } };
            Check(!Matches(before, mutated), "reset_oracle_root_mutant");
            var markerIndex = Array.FindIndex(before, item => item.Header?.ObjectClass == StateObjectClass.PublicationIntent);
            Check(markerIndex >= 0, "reset_oracle_terminal_markers");
            var badPayload = before[markerIndex].Payload!.ToArray();
            badPayload[0] ^= 1;
            var badBefore = before.ToArray();
            badBefore[markerIndex] = before[markerIndex] with { Payload = badPayload };
            Check(!Matches(badBefore, after), "reset_oracle_unaccepted_marker_mutant");
            CryptographicOperations.ZeroMemory(badPayload);
            Console.WriteLine("APR_SOLUS_RESET_RETRY logical_identity_unchanged=true accepted_records_unchanged=true unexpected_mutations_rejected=true");
            foreach (var item in before.Concat(after))
            {
                if (item.Sentinel is not null) CryptographicOperations.ZeroMemory(item.Sentinel.Root);
                if (item.Payload is not null) CryptographicOperations.ZeroMemory(item.Payload);
            }
        }
        return 0;
    }

    private static Decoded[] Read(Entry[] entries, AuthorizedLocatorAccess access, LocatorStateKeyRing keys)
    {
        Check(entries.Length is > 0 and <= 72, "reset_oracle_bound");
        var objects = entries.Select(entry =>
        {
            using var zip = new ZipArchive(new MemoryStream(Convert.FromBase64String(entry.Archive)), ZipArchiveMode.Read);
            Check(zip.Entries.Count == 1 && zip.Entries[0].FullName == "artifact-envelope.json" &&
                zip.Entries[0].Length < 64 * 1024 * 1024, "reset_oracle_zip");
            using var stream = zip.Entries[0].Open();
            using var document = JsonDocument.Parse(stream);
            var bytes = document.RootElement.GetProperty("encrypted_object_base64").GetBytesFromBase64();
            Check(Convert.ToHexStringLower(SHA256.HashData(bytes)) ==
                document.RootElement.GetProperty("encrypted_object_digest").GetString(), "reset_oracle_digest");
            return new Decoded(entry, bytes, null, null);
        }).ToArray();
        var root = objects.Single(item => item.Entry.Name == LocatorRootFormat.StoreName);
        if (!LocatorRootSentinelCodec.TryDecrypt(access, keys, root.Bytes, out var sentinel, out _) || sentinel is null)
            throw new CutoverProofFailure("reset_oracle_root");
        try
        {
            Check(LocatorContext.TryCreate(access, keys, sentinel.Root, true,
                sentinel.RequiredExpiresAtUnixSeconds, TimeProvider.System, out var locator), "reset_oracle_locator");
            using (locator)
            {
                return objects.Select(item =>
                {
                    if (ReferenceEquals(item, root)) return item with { Sentinel = sentinel with { Root = sentinel.Root.ToArray() } };
                    Check(StateControlEnvelopeV1Codec.TryDecrypt(locator!, access, new OpaqueStoreName(item.Entry.Name),
                        item.Bytes, out var header, out var payload, out _) && header is not null, "reset_oracle_decrypt");
                    try
                    {
                        LineageHeadV1? head = null;
                        if (header!.ObjectClass == StateObjectClass.LineageHead)
                            Check(LineageHeadCodec.TryDecode(payload, out head) && head is not null, "reset_oracle_head");
                        return item with { Header = header, Head = head,
                            PayloadSha256 = Convert.ToHexStringLower(SHA256.HashData(payload)), Payload = payload.ToArray() };
                    }
                    finally { CryptographicOperations.ZeroMemory(payload); }
                }).ToArray();
            }
        }
        finally { CryptographicOperations.ZeroMemory(sentinel.Root); }
    }

    private static bool Matches(Decoded[] before, Decoded[] after)
    {
        var priorRoots = before.Where(item => item.Sentinel is not null).ToArray();
        var nextRoots = after.Where(item => item.Sentinel is not null).ToArray();
        if (priorRoots.Length != 1 || nextRoots.Length != 1 || !RootMatches(priorRoots[0], nextRoots[0])) return Mismatch("reset_oracle_root_delta");
        var prior = SelectHead(before); var next = SelectHead(after);
        if (prior is null || next is null) return Mismatch("reset_oracle_head_selection");
        var a = prior.Header!; var b = next.Header!;
        if (b.LogicalExpiresAtUnixSeconds < a.LogicalExpiresAtUnixSeconds ||
            b.RequiredPlatformExpiresAtUnixSeconds < a.RequiredPlatformExpiresAtUnixSeconds ||
            next.Entry.Expires < prior.Entry.Expires || b.CreatedAtUnixSeconds < a.CreatedAtUnixSeconds ||
            a != b with { CreatedAtUnixSeconds = a.CreatedAtUnixSeconds, LogicalExpiresAtUnixSeconds = a.LogicalExpiresAtUnixSeconds,
                RequiredPlatformExpiresAtUnixSeconds = a.RequiredPlatformExpiresAtUnixSeconds }) return Mismatch("reset_oracle_header_delta");
        if (!LineageHeadCodec.TryEncode(prior.Head! with { PhysicalSuperseded = [] }, out var first) ||
            !LineageHeadCodec.TryEncode(next.Head! with { PhysicalSuperseded = [] }, out var second) ||
            !first.AsSpan().SequenceEqual(second)) return Mismatch("reset_oracle_head_delta");
        // A refresh records exactly the old physical head as superseded. An
        // unchanged physical head retains its original authenticated evidence.
        if (prior.Entry != next.Entry)
        {
            if (next.Head!.PhysicalSuperseded.Length != 1) return Mismatch("reset_oracle_superseded_count");
            var evidence = next.Head.PhysicalSuperseded[0];
            if (evidence.Name != prior.Entry.Name || evidence.ObjectId != prior.Entry.Id ||
                evidence.ProducingRunIdentity != a.ProducingRunIdentity || evidence.ProducingRunAttempt != a.ProducingRunAttempt ||
                evidence.ArchiveSha256 != Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(prior.Entry.Archive))) ||
                evidence.EncryptedObjectSha256 != Convert.ToHexStringLower(SHA256.HashData(prior.Bytes)) ||
                evidence.Size != prior.Bytes.Length || evidence.ExpiresAtUnixSeconds != prior.Entry.Expires) return Mismatch("reset_oracle_superseded_delta");
        }
        else if (!prior.Head!.PhysicalSuperseded.SequenceEqual(next.Head!.PhysicalSuperseded)) return Mismatch("reset_oracle_superseded_delta");
        // The production selector retains the immediate predecessor as an
        // authenticated chain anchor. It and all accepted records stay exact.
        var oldRecords = before.Where(item => !ReferenceEquals(item, prior) && item.Sentinel is null).ToArray();
        var newRecords = after.Where(item => !ReferenceEquals(item, next) && item.Sentinel is null).ToArray();
        if (oldRecords.Select(item => item.Entry).OrderBy(item => item.Id)
            .SequenceEqual(newRecords.Select(item => item.Entry).OrderBy(item => item.Id))) return true;
        var removedRecords = oldRecords.Where(item => !newRecords.Any(next => next.Entry == item.Entry)).ToArray();
        if (newRecords.All(item => oldRecords.Any(prior => prior.Entry == item.Entry)) &&
            TerminalCleanupIsAccepted(before, prior, removedRecords)) return true;
        // Fixed enum numbers, bounded counts and equality bits only; no private
        // identities, payloads or hash values enter diagnostics.
        var delta = $"reset_oracle_record_delta_t{cleanupStage}";
        foreach (var kind in Enum.GetValues<StateObjectClass>())
        {
            var oldClass = oldRecords.Where(item => item.Header!.ObjectClass == kind).ToArray();
            var newClass = newRecords.Where(item => item.Header!.ObjectClass == kind).ToArray();
            var removed = oldClass.Count(item => !newClass.Any(next => next.Entry == item.Entry));
            var added = newClass.Count(item => !oldClass.Any(prior => prior.Entry == item.Entry));
            if (removed == 0 && added == 0) continue;
            var sameLogical = oldClass.Select(item => item.Header!.ObjectIdentity).ToHashSet()
                .SetEquals(newClass.Select(item => item.Header!.ObjectIdentity));
            var samePayload = oldClass.Select(item => item.PayloadSha256).ToHashSet()
                .SetEquals(newClass.Select(item => item.PayloadSha256));
            delta += $"_c{(int)kind}_r{removed}_a{added}_l{(sameLogical ? 1 : 0)}_p{(samePayload ? 1 : 0)}";
        }
        return Mismatch(delta);
    }

    private static bool TerminalCleanupIsAccepted(Decoded[] inventory, Decoded head, Decoded[] removed)
    {
        cleanupStage = 1;
        // This exact fixture leaves the accepted intent/readback/recovery and
        // its completed dispatch anchor for ReturnCommitted's existing P5 GC.
        if (removed.Length != 4 || removed.Count(item => item.Header!.ObjectClass == StateObjectClass.PublicationIntent) != 3 ||
            removed.Count(item => item.Header!.ObjectClass == StateObjectClass.Cleanup) != 1 ||
            removed.Any(item => item.Header!.BaseScopeDigest != head.Header!.BaseScopeDigest ||
                item.Header.Epoch != head.Header.Epoch || item.Header.SessionId != head.Header.SessionId)) return false;
        cleanupStage = 2;
        var receipts = inventory.Where(item => item.Header?.ObjectClass == StateObjectClass.Acceptance &&
            item.Header.Epoch == head.Header!.Epoch && item.Header.SessionId == head.Header.SessionId).ToArray();
        if (receipts.Length != 1 || !AcceptedStateAcceptanceReceiptCodec.TryDecode(receipts[0].Payload!, out var receipt) || receipt is null)
            return false;
        cleanupStage = 3;
        var candidates = inventory.Where(item => item.Header?.ObjectClass == StateObjectClass.Candidate &&
            item.Header.ObjectIdentity == receipt.OriginalCandidateObjectIdentity).ToArray();
        if (candidates.Length != 1 || !AcceptedStateGenerationRecordCodec.TryDecode(candidates[0].Payload!, out var generation) || generation is null ||
            !AcceptedStateIdentity.TryComputeLogicalGeneration(candidates[0].Payload!, head.Header!.BaseScopeDigest,
                head.Header.Epoch, head.Header.SessionId, receipt.PreviousAcceptanceReceiptIdentity, out var logical) ||
            logical != receipt.LogicalGenerationIdentity) return false;
        var selected = new SelectedAcceptedGeneration(Authenticated(candidates[0]), generation, receipt,
            Authenticated(receipts[0]), logical, receipt.OriginalCandidateObjectIdentity);
        var selection = new AcceptedStateSelection(selected, null, new(Metadata(head), head.Header!, head.Head!), receipt.LogicalExpiresAtUnixSeconds);
        cleanupStage = 4;
        if (!PublicationRecoveryInventoryFactory.ResetSourceRecordsAreAccepted(removed.Select(Authenticated), selection)) return false;
        PublicationIntentV1? intent = null;
        StickyReadbackRecordV1? readback = null;
        RecoveryRecordV1? recovery = null;
        RetainedStateOpaqueWriteAnchor? anchor = null;
        foreach (var item in removed)
        {
            cleanupStage = 5;
            if (item.Header!.ProducingRunIdentity != receipt.ProducingRunIdentity || item.Header.ProducingRunAttempt != receipt.ProducingRunAttempt)
                return false;
            if (item.Header.ObjectClass == StateObjectClass.Cleanup)
            {
                cleanupStage = 6;
                if (!RetainedStateOpaqueWriteAnchorCodec.TryDecode(item.Payload!, out anchor) || anchor is null) return false;
                continue;
            }
            cleanupStage = 7;
            if (PublicationIntentV1Codec.TryDecode(item.Payload!, out var parsedIntent)) { if (intent is not null) return false; intent = parsedIntent; }
            else if (StickyReadbackRecordV1Codec.TryDecode(item.Payload!, out var parsedReadback)) { if (readback is not null) return false; readback = parsedReadback; }
            else if (RecoveryRecordV1Codec.TryDecode(item.Payload!, out var parsedRecovery, out _, out _)) { if (recovery is not null) return false; recovery = parsedRecovery; }
            else return false;
        }
        cleanupStage = 8;
        if (intent is null || readback is null || recovery is null || anchor is null ||
            readback.AttemptIntentRecordIdentity != intent.RecordIdentity || recovery.StickyReadback != readback ||
            readback.Operation != receipt.PublicationOperation || readback.RepositoryId != receipt.RepositoryId ||
            readback.PullRequestNumber != receipt.PullRequestNumber || readback.CommentId != receipt.CommentId || readback.CommentUrl != receipt.CommentUrl ||
            readback.Publication != new PublicationRecoveryPublicationV1(receipt.ReviewedHeadSha, receipt.ScopeSha256, receipt.BodySha256)) return false;
        cleanupStage = 9;
        if (!RetainedStateAcceptanceRecoveryCodec.TryDecode(recovery.AcceptanceRecoveryHandoff.AsSpan(),
                out var acceptanceName, out var acceptanceEnvelope, out var predecessorCopy)) return false;
        try
        {
            cleanupStage = 10;
            return predecessorCopy is null && acceptanceName?.Value == receipts[0].Entry.Name &&
                acceptanceEnvelope.AsSpan().SequenceEqual(receipts[0].Bytes) && anchor.CandidateObjectIdentity == receipt.OriginalCandidateObjectIdentity &&
                removed.Any(item => item.Header!.ObjectClass == anchor.ObjectClass && item.Entry.Name == anchor.TargetName.Value &&
                    item.Header.ObjectIdentity == anchor.TargetObjectIdentity && item.PayloadSha256 == anchor.TargetPayloadSha256 &&
                    item.Bytes.AsSpan().SequenceEqual(anchor.TargetEnvelope.AsSpan()));
        }
        finally { CryptographicOperations.ZeroMemory(acceptanceEnvelope); }
    }

    private static OpaqueStoreObjectMetadata Metadata(Decoded item) => new(
        new(new(item.Entry.Name), new(item.Entry.Id)), new(item.Header!.ProducingRunIdentity, item.Header.ProducingRunAttempt),
        new(Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(item.Entry.Archive)))),
        new(Convert.ToHexStringLower(SHA256.HashData(item.Bytes))), item.Entry.Expires, item.Bytes.Length);
    private static AuthenticatedStateObject Authenticated(Decoded item) => new(Metadata(item), item.Header!, item.Payload!);

    private static Decoded? SelectHead(Decoded[] inventory)
    {
        var heads = inventory.Where(item => item.Head is not null).ToArray();
        if (heads.Length == 0) return null;
        var candidates = heads.Select(item => new LineageHeadCandidate(Metadata(item),
            item.Header!, item.Head!)).ToImmutableArray();
        var selected = LineageHeadSelector.Select(candidates, [], heads.Length, heads[0].Header!.KeyId);
        return selected.Succeeded && selected.Selection is { } selection
            ? heads.Single(item => item.Entry.Id == selection.Head.Metadata.Reference.ObjectId.Value) : null;
    }

    private static bool RootMatches(Decoded prior, Decoded next)
    {
        var a = prior.Sentinel!; var b = next.Sentinel!;
        if (!a.Root.AsSpan().SequenceEqual(b.Root) || a.WriterKeyId != b.WriterKeyId) return false;
        if (prior.Entry == next.Entry) return LocatorRootSelection.Equivalent(a, b) &&
            a.CreatedAtUnixSeconds == b.CreatedAtUnixSeconds && a.RequiredExpiresAtUnixSeconds == b.RequiredExpiresAtUnixSeconds;
        // LocatorRootService creates one retention successor from the singleton
        // root, then prunes its exact predecessor. This is not a new state epoch.
        return b.Generation == a.Generation + 1 && b.CreatedAtUnixSeconds >= a.CreatedAtUnixSeconds &&
            b.RequiredExpiresAtUnixSeconds >= a.RequiredExpiresAtUnixSeconds &&
            b.RequiredExpiresAtUnixSeconds == b.CreatedAtUnixSeconds + StateRetentionRequirements.SentinelRequestSeconds &&
            next.Entry.Expires >= b.RequiredExpiresAtUnixSeconds && b.Superseded.IsEmpty && b.Predecessors.Length == 1 &&
            b.Predecessors[0] == new LocatorArtifactIdentity(prior.Entry.Id,
                Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(prior.Entry.Archive))),
                Convert.ToHexStringLower(SHA256.HashData(prior.Bytes)));
    }

    private static void Check(bool value, string code)
    { if (!value) throw new CutoverProofFailure(code); }
}

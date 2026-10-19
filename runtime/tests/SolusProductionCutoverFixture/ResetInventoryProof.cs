using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;

namespace AgenticPrReview.Runtime.SolusProductionCutoverFixture;

// Read-only oracle over original official SDK ZIPs. Production codecs authenticate
// every object; only the existing same-identity head retention refresh may vary.
internal static class ResetInventoryProof
{
    internal sealed record Entry(string Id, string Name, string Archive, long Expires);
    internal sealed record Input(Entry[] Before, Entry[] After);
    private sealed record Decoded(Entry Entry, byte[] Bytes, StateControlHeaderV1? Header, LineageHeadV1? Head,
        LocatorRootSentinel? Sentinel = null);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

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
            Check(Matches(before, after), "reset_retry_inventory");
            var headIndex = Array.FindIndex(after, item => item.Head is not null);
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
            var rootIndex = Array.FindIndex(after, item => item.Sentinel is not null);
            mutated = after.ToArray();
            mutated[rootIndex] = after[rootIndex] with { Sentinel = after[rootIndex].Sentinel! with { Root = new byte[32] } };
            Check(!Matches(before, mutated), "reset_oracle_root_mutant");
            Console.WriteLine("APR_SOLUS_RESET_RETRY logical_identity_unchanged=true accepted_records_unchanged=true unexpected_mutations_rejected=true");
            foreach (var item in before.Concat(after))
                if (item.Sentinel is not null) CryptographicOperations.ZeroMemory(item.Sentinel.Root);
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
        Check(LocatorRootSentinelCodec.TryDecrypt(access, keys, root.Bytes, out var sentinel, out _) && sentinel is not null,
            "reset_oracle_root");
        try
        {
            Check(LocatorContext.TryCreate(access, keys, sentinel!.Root, true,
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
                        return item with { Header = header, Head = head };
                    }
                    finally { CryptographicOperations.ZeroMemory(payload); }
                }).ToArray();
            }
        }
        finally { CryptographicOperations.ZeroMemory(sentinel!.Root); }
    }

    private static bool Matches(Decoded[] before, Decoded[] after)
    {
        var priorRoots = before.Where(item => item.Sentinel is not null).ToArray();
        var nextRoots = after.Where(item => item.Sentinel is not null).ToArray();
        if (priorRoots.Length != 1 || nextRoots.Length != 1 || !RootMatches(priorRoots[0], nextRoots[0])) return false;
        var priorHeads = before.Where(item => item.Head is not null).ToArray();
        var nextHeads = after.Where(item => item.Head is not null).ToArray();
        if (priorHeads.Length != 1 || nextHeads.Length != 1) return false;
        var prior = priorHeads[0]; var next = nextHeads[0];
        var a = prior.Header!; var b = next.Header!;
        if (b.LogicalExpiresAtUnixSeconds < a.LogicalExpiresAtUnixSeconds ||
            b.RequiredPlatformExpiresAtUnixSeconds < a.RequiredPlatformExpiresAtUnixSeconds ||
            next.Entry.Expires < prior.Entry.Expires || b.CreatedAtUnixSeconds < a.CreatedAtUnixSeconds ||
            a != b with { CreatedAtUnixSeconds = a.CreatedAtUnixSeconds, LogicalExpiresAtUnixSeconds = a.LogicalExpiresAtUnixSeconds,
                RequiredPlatformExpiresAtUnixSeconds = a.RequiredPlatformExpiresAtUnixSeconds }) return false;
        if (!LineageHeadCodec.TryEncode(prior.Head! with { PhysicalSuperseded = [] }, out var first) ||
            !LineageHeadCodec.TryEncode(next.Head! with { PhysicalSuperseded = [] }, out var second) ||
            !first.AsSpan().SequenceEqual(second)) return false;
        // A refresh records exactly the old physical head as superseded. An
        // unchanged physical head retains its original authenticated evidence.
        if (prior.Entry != next.Entry)
        {
            if (next.Head!.PhysicalSuperseded.Length != 1) return false;
            var evidence = next.Head.PhysicalSuperseded[0];
            if (evidence.Name != prior.Entry.Name || evidence.ObjectId != prior.Entry.Id ||
                evidence.ProducingRunIdentity != a.ProducingRunIdentity || evidence.ProducingRunAttempt != a.ProducingRunAttempt ||
                evidence.ArchiveSha256 != Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(prior.Entry.Archive))) ||
                evidence.EncryptedObjectSha256 != Convert.ToHexStringLower(SHA256.HashData(prior.Bytes)) ||
                evidence.Size != prior.Bytes.Length || evidence.ExpiresAtUnixSeconds != prior.Entry.Expires) return false;
        }
        else if (!prior.Head!.PhysicalSuperseded.SequenceEqual(next.Head!.PhysicalSuperseded)) return false;
        return before.Where(item => item.Head is null && item.Sentinel is null).Select(item => item.Entry).OrderBy(item => item.Id)
            .SequenceEqual(after.Where(item => item.Head is null && item.Sentinel is null).Select(item => item.Entry).OrderBy(item => item.Id));
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

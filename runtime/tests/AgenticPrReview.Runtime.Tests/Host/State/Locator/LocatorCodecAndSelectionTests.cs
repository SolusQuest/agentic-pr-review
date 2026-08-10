using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.Host.State.Locator;

namespace AgenticPrReview.Runtime.Tests.Host.State.Locator;

public sealed class LocatorCodecAndSelectionTests
{
    [Fact]
    public void CodecRoundTripsAndUsesDistinctAuthenticatedEnvelopes()
    {
        using var access = LocatorTestData.Access();
        using var keys = LocatorTestData.KeyRing(access);
        var sentinel = LocatorTestData.Sentinel(keys);

        Assert.True(LocatorRootSentinelCodec.TryEncrypt(
            access,
            keys,
            sentinel,
            out var first,
            out var firstCode),
            firstCode);
        Assert.True(LocatorRootSentinelCodec.TryEncrypt(
            access,
            keys,
            sentinel,
            out var second,
            out var secondCode),
            secondCode);
        Assert.NotEqual(first, second);
        Assert.True(LocatorRootSentinelCodec.TryDecrypt(
            access,
            keys,
            first,
            out var restored,
            out var restoreCode),
            restoreCode);
        Assert.Equal(sentinel.Generation, restored!.Generation);
        Assert.Equal(sentinel.WriterKeyId, restored.WriterKeyId);
        Assert.Equal(sentinel.Root, restored.Root);
        Assert.Equal(sentinel.Predecessors, restored.Predecessors);
        Assert.Equal(sentinel.Superseded, restored.Superseded);
    }

    [Fact]
    public void EveryEnvelopeByteMutationFailsClosed()
    {
        using var access = LocatorTestData.Access();
        using var keys = LocatorTestData.KeyRing(access);
        Assert.True(LocatorRootSentinelCodec.TryEncrypt(
            access,
            keys,
            LocatorTestData.Sentinel(keys),
            out var envelope,
            out _));

        for (var index = 0; index < envelope!.Length; index++)
        {
            var mutated = envelope.ToArray();
            mutated[index] ^= 1;
            Assert.False(LocatorRootSentinelCodec.TryDecrypt(
                access,
                keys,
                mutated,
                out _,
                out _));
        }

        Assert.False(LocatorRootSentinelCodec.TryDecrypt(
            access,
            keys,
            envelope[..^1],
            out _,
            out _));
        Assert.False(LocatorRootSentinelCodec.TryDecrypt(
            access,
            keys,
            [.. envelope, 0],
            out _,
            out _));
        Assert.False(LocatorRootSentinelCodec.TryDecrypt(
            access,
            keys,
            new byte[LocatorRootFormat.MaximumEnvelopeBytes + 1],
            out _,
            out _));
    }

    [Fact]
    public void CodecRejectsNonCanonicalReferencesAndHidesCanaries()
    {
        var keyCanary = Encoding.ASCII.GetBytes(
            "LOCATOR-KEY-CANARY".PadRight(32, '!'));
        Assert.Equal(32, keyCanary.Length);
        using var access = LocatorTestData.Access();
        using var keys = LocatorTestData.KeyRing(
            access,
            currentBase64: Convert.ToBase64String(keyCanary));
        var rootCanary = Encoding.ASCII.GetBytes(
            "LOCATOR-ROOT-CANARY".PadRight(32, '!'));
        Assert.Equal(32, rootCanary.Length);
        var first = new LocatorArtifactIdentity(
            "z",
            new string('a', 64),
            new string('b', 64));
        var second = first with { ObjectId = "a" };
        var invalid = LocatorTestData.Sentinel(
            keys,
            rootCanary,
            predecessors: [first, second]);
        Assert.False(LocatorRootSentinelCodec.TryEncrypt(
            access,
            keys,
            invalid,
            out _,
            out _));

        var valid = LocatorTestData.Sentinel(keys, rootCanary);
        Assert.True(LocatorRootSentinelCodec.TryEncrypt(
            access,
            keys,
            valid,
            out var envelope,
            out _));
        var text = Encoding.ASCII.GetString(envelope!);
        Assert.DoesNotContain("LOCATOR-KEY-CANARY", text);
        Assert.DoesNotContain("LOCATOR-ROOT-CANARY", text);
        Assert.DoesNotContain(
            Convert.ToHexString(keyCanary),
            Convert.ToHexString(envelope!));
        Assert.DoesNotContain(
            Convert.ToHexString(rootCanary),
            Convert.ToHexString(envelope!));

        Assert.False(LocatorRootSentinelCodec.TryEncrypt(
            access,
            keys,
            valid with
            {
                Generation = 1,
                Predecessors = [],
            },
            out _,
            out _));
        Assert.False(LocatorRootSentinelCodec.TryEncrypt(
            access,
            keys,
            valid with
            {
                Predecessors =
                [
                    new LocatorArtifactIdentity(
                        "prior",
                        new string('a', 64),
                        new string('b', 64)),
                ],
            },
            out _,
            out _));
    }

    [Fact]
    public void SelectionIsOrderInvariantAndChoosesOneAdequateDuplicate()
    {
        using var access = LocatorTestData.Access();
        using var keys = LocatorTestData.KeyRing(access);
        var sentinel = LocatorTestData.Sentinel(keys);
        var lower = new LocatorPhysicalCandidate(
            LocatorTestData.Metadata("b", LocatorTestData.Now + 100),
            sentinel);
        var higher = new LocatorPhysicalCandidate(
            LocatorTestData.Metadata("a", LocatorTestData.Now + 200),
            sentinel with
            {
                CreatedAtUnixSeconds = LocatorTestData.Now + 1,
                RequiredExpiresAtUnixSeconds =
                    sentinel.RequiredExpiresAtUnixSeconds + 1,
            });

        foreach (var candidates in new[]
        {
            ImmutableArray.Create(lower, higher),
            ImmutableArray.Create(higher, lower),
        })
        {
            var result = LocatorRootSelection.Select(candidates, [], 2);
            Assert.True(result.Succeeded, result.Code);
            Assert.Equal("a", result.Selection!.Head.Metadata
                .Reference.ObjectId.Value);
            Assert.Single(result.Selection.SafeToDelete);
        }
    }

    [Fact]
    public void SelectionRejectsDistinctRootsSiblingsAndInvalidPresentEdges()
    {
        using var access = LocatorTestData.Access();
        using var keys = LocatorTestData.KeyRing(access);
        var first = new LocatorPhysicalCandidate(
            LocatorTestData.Metadata("first", LocatorTestData.Now + 100),
            LocatorTestData.Sentinel(keys));
        var otherRoot = new LocatorPhysicalCandidate(
            LocatorTestData.Metadata("other", LocatorTestData.Now + 100),
            LocatorTestData.Sentinel(
                keys,
                Enumerable.Repeat((byte)0x42, 32).ToArray()));
        Assert.Equal(
            LocatorCodes.Conflict,
            LocatorRootSelection.Select(
                [first, otherRoot],
                [],
                2).Code);

        var parentIdentity = LocatorRootSentinelCodec.Identity(first.Metadata);
        var childMetadata = LocatorTestData.Metadata(
            "child",
            LocatorTestData.Now + 200);
        var child = new LocatorPhysicalCandidate(
            childMetadata,
            LocatorTestData.Sentinel(
                keys,
                first.Sentinel.Root,
                generation: 2,
                predecessors: [parentIdentity]));
        Assert.Equal(
            LocatorCodes.Conflict,
            LocatorRootSelection.Select([first, child], [], 2).Code);

        var siblingA = child with
        {
            Sentinel = child.Sentinel with
            {
                Generation = 1,
                Superseded = [],
            },
        };
        var siblingB = siblingA with
        {
            Metadata = LocatorTestData.Metadata(
                "sibling",
                LocatorTestData.Now + 200),
            Sentinel = siblingA.Sentinel with
            {
                Predecessors = [],
            },
        };
        Assert.Equal(
            LocatorCodes.Conflict,
            LocatorRootSelection.Select(
                [first, siblingA, siblingB],
                [],
                3).Code);
    }

    [Fact]
    public void PrunedHistoryIsAllowedButUnknownMustBeExactlySuperseded()
    {
        using var access = LocatorTestData.Access();
        using var keys = LocatorTestData.KeyRing(access);
        var missing = LocatorTestData.Metadata(
            "missing",
            LocatorTestData.Now + 100);
        var missingIdentity = LocatorRootSentinelCodec.Identity(missing);
        var lone = new LocatorPhysicalCandidate(
            LocatorTestData.Metadata("head", LocatorTestData.Now + 200),
            LocatorTestData.Sentinel(
                keys,
                generation: 1,
                predecessors: [missingIdentity]));
        Assert.True(LocatorRootSelection.Select([lone], [], 1).Succeeded);

        var unknown = new LocatorUnknownArtifact(
            missing,
            LocatorCodes.AuthenticationFailed);
        Assert.Equal(
            LocatorCodes.AuthenticationFailed,
            LocatorRootSelection.Select([lone], [unknown], 2).Code);

        var accountingHead = lone with
        {
            Sentinel = lone.Sentinel with
            {
                Predecessors =
                [
                    missingIdentity with
                    {
                        ObjectId = "pruned-predecessor",
                    },
                ],
                Superseded = [missingIdentity],
            },
        };
        var selected = LocatorRootSelection.Select(
            [accountingHead],
            [unknown],
            2);
        Assert.True(selected.Succeeded, selected.Code);
        Assert.Single(selected.Selection!.SafeToDelete);
    }
}

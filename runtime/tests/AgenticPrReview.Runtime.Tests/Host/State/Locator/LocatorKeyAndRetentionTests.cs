using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Locator;

namespace AgenticPrReview.Runtime.Tests.Host.State.Locator;

public sealed class LocatorKeyAndRetentionTests
{
    [Fact]
    public void RetentionRequirementsHaveOneExactSourceAndCheckedFloor()
    {
        Assert.Equal(604_800, StateRetentionRequirements.LogicalWindowSeconds);
        Assert.Equal(900, StateRetentionRequirements.PreStickyBudgetSeconds);
        Assert.Equal(
            691_200,
            StateRetentionRequirements.ScopedPlatformRequestSeconds);
        Assert.Equal(
            864_000,
            StateRetentionRequirements.SentinelRequestSeconds);
        Assert.Equal(
            86_400,
            StateRetentionRequirements.SentinelDependentMarginSeconds);
        Assert.Equal(
            StateRetentionRequirements.LogicalWindowSeconds,
            RestrictedStateFormat.MaximumRetentionSeconds);

        Assert.True(StateRetentionRequirements.TryGetRequiredSentinelExpiry(
            LocatorTestData.Now,
            LocatorTestData.Now + 900_000,
            out var dependentFloor));
        Assert.Equal(
            LocatorTestData.Now + 900_000 + 86_400,
            dependentFloor);
        Assert.True(StateRetentionRequirements.TryGetRequiredSentinelExpiry(
            LocatorTestData.Now,
            0,
            out var requestFloor));
        Assert.Equal(
            LocatorTestData.Now + 864_000,
            requestFloor);
        Assert.False(StateRetentionRequirements.TryGetRequiredSentinelExpiry(
            RestrictedStateFormat.MaximumUnixSeconds,
            0,
            out _));
    }

    [Fact]
    public void CapabilityConstructorIsPrivateAndAuthorityIsExact()
    {
        Assert.DoesNotContain(
            typeof(AuthorizedLocatorAccess).GetConstructors(
                BindingFlags.Instance | BindingFlags.Public |
                    BindingFlags.NonPublic),
            constructor => !constructor.IsPrivate);

        using var access = LocatorTestData.Access();
        using var wrongRepository = LocatorTestData.Access("other/repo");
        Assert.False(LocatorStateKeyRing.TryCreate(
            wrongRepository,
            "owner/repository",
            "not-base64",
            null,
            out _,
            out var wrongCode));
        Assert.Equal(LocatorCodes.AccessDenied, wrongCode);

        access.Dispose();
        Assert.False(LocatorStateKeyRing.TryCreate(
            access,
            "owner/repository",
            LocatorTestData.CurrentBase64,
            null,
            out _,
            out var disposedCode));
        Assert.Equal(LocatorCodes.AccessDenied, disposedCode);
    }

    [Fact]
    public void StateKeysRequireCanonicalPaddedBase64AndExactly32Bytes()
    {
        using var access = LocatorTestData.Access();
        var invalid = new[]
        {
            Convert.ToBase64String(new byte[31]),
            Convert.ToBase64String(new byte[33]),
            LocatorTestData.CurrentBase64.TrimEnd('='),
            string.Concat(" ", LocatorTestData.CurrentBase64),
            string.Concat(LocatorTestData.CurrentBase64, "\n"),
            string.Concat("-", LocatorTestData.CurrentBase64[1..]),
        };

        foreach (var encoded in invalid.Distinct(StringComparer.Ordinal))
        {
            Assert.False(LocatorStateKeyRing.TryCreate(
                access,
                "owner/repository",
                encoded,
                null,
                out _,
                out var code));
            Assert.Equal(LocatorCodes.KeyUnavailable, code);
        }

        Assert.True(LocatorStateKeyRing.TryCreate(
            access,
            "owner/repository",
            LocatorTestData.CurrentBase64,
            LocatorTestData.CurrentBase64,
            out var deduplicated,
            out var successCode),
            successCode);
        using var ring = Assert.IsType<LocatorStateKeyRing>(deduplicated);
        Assert.False(ring.HasPrevious);
        Assert.Null(ring.PreviousKeyId);
    }

    [Fact]
    public void DomainSeparatedDerivationsMatchIndependentGoldenVectors()
    {
        using var access = LocatorTestData.Access();
        using var keys = LocatorTestData.KeyRing(access);
        Assert.Equal(
            "867fc0704091942d44fa4ca5c333f00d7bd00360591bec99aff4882a1896191b",
            keys.CurrentKeyId);
        Assert.True(keys.TryDeriveInitialRoot(access, out var root));
        try
        {
            Assert.Equal(
                "6ed5509781ed7c790ea8adba57a7b7373a260065a28163f6d1026038f2597d0c",
                Convert.ToHexStringLower(root));
            using var context = new LocatorContext(
                access,
                keys,
                root,
                currentSingletonProven: true);
            Assert.True(context.TryDeriveOpaqueName(
                access,
                "restricted-state",
                [1, 2, 3],
                out var name));
            Assert.Equal(
                "apr-state-3125de78ee7005bde12b754a3fe7eda8c77f46fbf8768cb95dc396aa64d3e986",
                name!.Value);
            Assert.DoesNotContain("owner", name.Value, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "restricted-state",
                name.Value,
                StringComparison.Ordinal);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(root);
        }
    }

    [Fact]
    public void CurrentWritesAndPreviousOnlyReads()
    {
        using var access = LocatorTestData.Access();
        using var oldKeys = LocatorTestData.KeyRing(
            access,
            currentBase64: LocatorTestData.PreviousBase64);
        var oldSentinel = LocatorTestData.Sentinel(oldKeys);
        Assert.True(LocatorRootSentinelCodec.TryEncrypt(
            access,
            oldKeys,
            oldSentinel,
            out var oldEnvelope,
            out var oldCode),
            oldCode);

        using var rotated = LocatorTestData.KeyRing(
            access,
            LocatorTestData.PreviousBase64);
        Assert.True(LocatorRootSentinelCodec.TryDecrypt(
            access,
            rotated,
            oldEnvelope,
            out var restored,
            out var restoreCode),
            restoreCode);
        Assert.Equal(rotated.PreviousKeyId, restored!.WriterKeyId);

        Assert.False(LocatorRootSentinelCodec.TryEncrypt(
            access,
            rotated,
            restored,
            out _,
            out var writeCode));
        Assert.Equal(LocatorCodes.Invalid, writeCode);
    }

    [Fact]
    public void ContextDoesNotExposeRootAndRetirementRequiresCompleteEvidence()
    {
        using var access = LocatorTestData.Access();
        using var keys = LocatorTestData.KeyRing(
            access,
            LocatorTestData.PreviousBase64);
        Assert.True(keys.TryDeriveInitialRoot(access, out var root));
        using var context = new LocatorContext(
            access,
            keys,
            root,
            currentSingletonProven: true);
        CryptographicOperations.ZeroMemory(root);

        Assert.DoesNotContain(
            typeof(LocatorContext).GetProperties(
                BindingFlags.Instance | BindingFlags.Public |
                    BindingFlags.NonPublic),
            property => property.PropertyType == typeof(byte[]));
        Assert.False(context.CanRetirePreviousKey(
            access,
            new LocatorPreviousKeyRetirementEvidence(
                EnumerationComplete: false,
                NoLiveRestrictedStateDependencies: true,
                NoLiveTransactionDependencies: true)));
        Assert.True(context.CanRetirePreviousKey(
            access,
            new LocatorPreviousKeyRetirementEvidence(
                EnumerationComplete: true,
                NoLiveRestrictedStateDependencies: true,
                NoLiveTransactionDependencies: true)));

        context.Dispose();
        Span<byte> destination = stackalloc byte[32];
        Assert.False(context.TryCopyCurrentStateKey(
            access,
            destination,
            out _));
        Assert.Equal(new byte[32], destination.ToArray());
    }
}

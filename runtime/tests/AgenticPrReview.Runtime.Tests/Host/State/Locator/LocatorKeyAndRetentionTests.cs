using System.Collections.Immutable;
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
            Assert.True(LocatorContext.TryCreate(
                access,
                keys,
                root,
                currentSingletonProven: true,
                new FrozenLocatorTimeProvider(LocatorTestData.Now),
                out var created));
            using var context = Assert.IsType<LocatorContext>(created);
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
    public void ContextOwnsKeysAndEvaluatesBoundRetirementInventory()
    {
        using var access = LocatorTestData.Access();
        using var keys = LocatorTestData.KeyRing(
            access,
            LocatorTestData.PreviousBase64);
        Assert.True(keys.TryDeriveInitialRoot(access, out var root));
        var time = new FrozenLocatorTimeProvider(LocatorTestData.Now);
        Assert.True(LocatorContext.TryCreate(
            access,
            keys,
            root,
            currentSingletonProven: true,
            time,
            out var created));
        using var context = Assert.IsType<LocatorContext>(created);
        CryptographicOperations.ZeroMemory(root);

        Assert.DoesNotContain(
            typeof(LocatorContext).GetProperties(
                BindingFlags.Instance | BindingFlags.Public |
                    BindingFlags.NonPublic),
            property => property.PropertyType == typeof(byte[]));

        var forged = new LocatorContext.LocatorPreviousKeyRetirementEvidence(
            new object(),
            access,
            "owner/repository",
            keys.PreviousKeyId!,
            LocatorTestData.Now,
            enumerationComplete: true,
            requiredDependencies: []);
        Assert.False(context.CanRetirePreviousKey(access, forged));

        Assert.False(context.CanRetirePreviousKey(
            access,
            Evidence(
                context,
                access,
                enumerationComplete: false,
                dependencies: [])));

        var stale = Evidence(
            context,
            access,
            enumerationComplete: true,
            dependencies: []);
        time.UnixSeconds = LocatorTestData.Now +
            StateRetentionRequirements.PreStickyBudgetSeconds + 1;
        Assert.False(context.CanRetirePreviousKey(
            access,
            stale));
        time.UnixSeconds = LocatorTestData.Now;

        var otherPrevious = Enumerable.Repeat((byte)0x9a, 32).ToArray();
        using var otherKeys = LocatorTestData.KeyRing(
            access,
            Convert.ToBase64String(otherPrevious));
        Assert.True(otherKeys.TryDeriveInitialRoot(
            access,
            out var otherRoot));
        Assert.True(LocatorContext.TryCreate(
            access,
            otherKeys,
            otherRoot,
            currentSingletonProven: true,
            time,
            out var otherCreated));
        using var otherContext = Assert.IsType<LocatorContext>(otherCreated);
        CryptographicOperations.ZeroMemory(otherRoot);
        CryptographicOperations.ZeroMemory(otherPrevious);
        Assert.False(context.CanRetirePreviousKey(
            access,
            Evidence(
                otherContext,
                access,
                enumerationComplete: true,
                dependencies: [])));

        using var otherAccess = LocatorTestData.Access("other/repository");
        using var otherRepositoryKeys = LocatorTestData.KeyRing(
            otherAccess,
            LocatorTestData.PreviousBase64,
            repositoryId: "other/repository");
        Assert.True(otherRepositoryKeys.TryDeriveInitialRoot(
            otherAccess,
            out var otherRepositoryRoot));
        Assert.True(LocatorContext.TryCreate(
            otherAccess,
            otherRepositoryKeys,
            otherRepositoryRoot,
            currentSingletonProven: true,
            time,
            out var otherRepositoryCreated));
        using var otherRepositoryContext = Assert.IsType<LocatorContext>(
            otherRepositoryCreated);
        CryptographicOperations.ZeroMemory(otherRepositoryRoot);
        Assert.False(context.CanRetirePreviousKey(
            access,
            Evidence(
                otherRepositoryContext,
                otherAccess,
                enumerationComplete: true,
                dependencies: [])));

        Assert.False(context.CanRetirePreviousKey(
            access,
            Evidence(
                context,
                access,
                enumerationComplete: true,
                dependencies:
                [
                    new LocatorRequiredDependency(
                        LocatorDependencyKind.RestrictedState,
                        keys.PreviousKeyId!,
                        LocatorTestData.Now + 1),
                    new LocatorRequiredDependency(
                        LocatorDependencyKind.Transaction,
                        keys.CurrentKeyId,
                        LocatorTestData.Now + 10),
                ])));
        Assert.True(context.CanRetirePreviousKey(
            access,
            Evidence(
                context,
                access,
                enumerationComplete: true,
                dependencies:
                [
                    new LocatorRequiredDependency(
                        LocatorDependencyKind.Transaction,
                        keys.PreviousKeyId!,
                        LocatorTestData.Now),
                ])));
        Assert.True(context.CanRetirePreviousKey(
            access,
            Evidence(
                context,
                access,
                enumerationComplete: true,
                dependencies: [])));

        var contextRoot = Assert.IsType<byte[]>(typeof(LocatorContext)
            .GetField("root", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(context));
        var ownedKeys = Assert.IsType<LocatorStateKeyRing>(
            typeof(LocatorContext)
                .GetField(
                    "keys",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(context));
        var ownedCurrent = Assert.IsType<byte[]>(
            typeof(LocatorStateKeyRing)
                .GetField(
                    "current",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(ownedKeys));
        var ownedPrevious = Assert.IsType<byte[]>(
            typeof(LocatorStateKeyRing)
                .GetField(
                    "previous",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(ownedKeys));

        context.Dispose();
        Assert.All(contextRoot, value => Assert.Equal(0, value));
        Assert.All(ownedCurrent, value => Assert.Equal(0, value));
        Assert.All(ownedPrevious, value => Assert.Equal(0, value));
        Span<byte> destination = stackalloc byte[32];
        Assert.False(context.TryCopyCurrentStateKey(
            access,
            destination,
            out _));
        Assert.Equal(new byte[32], destination.ToArray());

        Assert.True(keys.TryGetCurrent(access, out var original));
        original!.Dispose();
    }

    private static LocatorContext.LocatorPreviousKeyRetirementEvidence
        Evidence(
        LocatorContext context,
        AuthorizedLocatorAccess access,
        bool enumerationComplete,
        ImmutableArray<LocatorRequiredDependency> dependencies)
    {
        Assert.True(context.TryCapturePreviousKeyRetirementEvidence(
            access,
            enumerationComplete,
            dependencies,
            out var evidence));
        return evidence!;
    }
}

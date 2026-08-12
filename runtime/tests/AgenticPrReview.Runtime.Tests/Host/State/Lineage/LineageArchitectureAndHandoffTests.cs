using System.Reflection;
using System.Security.Cryptography;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.Restore;

namespace AgenticPrReview.Runtime.Tests.Host.State.Lineage;

public sealed class LineageArchitectureAndHandoffTests
{
    [Fact]
    public void LocatorContextDerivesWithoutExposingRootAndProvesExpiry()
    {
        using var lease = LineageTestData.Context();
        var input = new byte[] { 1, 2, 3, 4 };
        Span<byte> first = stackalloc byte[LineageFormat.DigestBytes];
        Span<byte> retry = stackalloc byte[LineageFormat.DigestBytes];
        Span<byte> changed = stackalloc byte[LineageFormat.DigestBytes];
        Assert.True(lease.Context.TryDeriveRootKeyed(
            lease.Access,
            "apr.test.lineage",
            input,
            first));
        Assert.True(lease.Context.TryDeriveRootKeyed(
            lease.Access,
            "apr.test.lineage",
            input,
            retry));
        Assert.True(CryptographicOperations.FixedTimeEquals(first, retry));
        Assert.True(lease.Context.TryDeriveRootKeyed(
            lease.Access,
            "apr.test.lineage.changed",
            input,
            changed));
        Assert.False(CryptographicOperations.FixedTimeEquals(first, changed));

        Assert.True(lease.Context.CoversDependentExpiry(
            lease.Access,
            LineageTestData.SentinelExpiry -
                StateRetentionRequirements.SentinelDependentMarginSeconds));
        Assert.False(lease.Context.CoversDependentExpiry(
            lease.Access,
            LineageTestData.SentinelExpiry -
                StateRetentionRequirements.SentinelDependentMarginSeconds + 1));

        var visibleMembers = typeof(LocatorContext).GetMembers(
            BindingFlags.Instance | BindingFlags.Public)
            .Select(member => member.Name)
            .ToArray();
        Assert.DoesNotContain("Root", visibleMembers);
        Assert.DoesNotContain("Key", visibleMembers);
    }

    [Fact]
    public void ResetCapabilityHasNoPublicIssuerOrSecretBearingDependency()
    {
        Assert.Empty(typeof(AuthorizedLineageReset).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public));
        Assert.DoesNotContain(
            typeof(AuthorizedLineageReset).GetMethods(
                BindingFlags.Static | BindingFlags.Public),
            method => method.ReturnType == typeof(AuthorizedLineageReset));
        var issuer = Assert.Single(
            typeof(AuthorizedLineageReset).GetMethods(
                BindingFlags.Static | BindingFlags.NonPublic),
            method => method.ReturnType == typeof(AuthorizedLineageReset));
        Assert.Equal("Issue", issuer.Name);
        Assert.Equal(
            typeof(AcceptedStateProductionAuthorization),
            issuer.GetParameters()[0].ParameterType);
        Assert.DoesNotContain(
            issuer.GetParameters(),
            parameter => parameter.ParameterType == typeof(LocatorContext));

        var constructor = Assert.Single(
            typeof(AuthorizedLineageReset).GetConstructors(
                BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.True(constructor.IsPrivate);
        Assert.DoesNotContain(
            constructor.GetParameters(),
            parameter => parameter.ParameterType.FullName!.Contains(
                "ActionHostLaunchContract",
                StringComparison.Ordinal));
    }

    [Fact]
    public void ResetCapabilityBindsExactBaseScopeAndPriorHead()
    {
        using var lease = LineageTestData.Context();
        var priorHead = new string('a', 64);
        var reset = LineageTestData.Reset(
            lease.Access,
            priorHead,
            new string('b', 64));
        var scope = LineageTestData.Scope();
        Assert.True(LineageBaseScopeCodec.TryDigest(
            scope,
            out var baseScopeDigest));

        Assert.True(reset.Allows(
            lease.Access,
            scope,
            baseScopeDigest,
            "workflow-run-42",
            producingRunAttempt: 1,
            priorHead));
        Assert.False(reset.Allows(
            lease.Access,
            scope,
            baseScopeDigest,
            "workflow-run-42",
            producingRunAttempt: 1,
            new string('c', 64)));

        var changedScope = scope with
        {
            ConfigSha256 = new string('e', 64),
        };
        Assert.True(LineageBaseScopeCodec.TryDigest(
            changedScope,
            out var changedScopeDigest));
        Assert.False(reset.Allows(
            lease.Access,
            changedScope,
            changedScopeDigest,
            "workflow-run-42",
            producingRunAttempt: 1,
            priorHead));

        var missingPrior = LineageTestData.Reset(
            lease.Access,
            priorHeadIdentity: null,
            new string('d', 64));
        Assert.False(missingPrior.Allows(
            lease.Access,
            scope,
            baseScopeDigest,
            "workflow-run-42",
            producingRunAttempt: 1,
            priorHead));
    }

    [Fact]
    public void LineageProductionTreeStaysInsideFrozenDependencies()
    {
        var sourceRoot = FindRepositoryPath(
            "runtime",
            "src",
            "AgenticPrReview.Runtime",
            "Host",
            "State",
            "Lineage");
        var source = string.Join(
            "\n",
            Directory.GetFiles(sourceRoot, "*.cs")
                .Order(StringComparer.Ordinal)
                .Select(File.ReadAllText));
        foreach (var forbidden in new[]
        {
            "ActionHostLaunchContract",
            "Host.Action",
            "GitHub",
            "HttpClient",
            "Environment.GetEnvironmentVariable",
            "JsonSerializer",
            "Activator.CreateInstance",
            "Assembly.Load",
            "SESSION",
        })
        {
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
        }

        var productionTypes = typeof(LineageService).Assembly.GetTypes()
            .Where(type => type.Namespace ==
                "AgenticPrReview.Runtime.Host.State.Lineage")
            .ToArray();
        Assert.NotEmpty(productionTypes);
        Assert.All(productionTypes, type => Assert.False(type.IsPublic));
    }

    [Fact]
    public void SelectedContextBecomesUnusableWithDisposedAuthority()
    {
        var lease = LineageTestData.Context();
        var selected = new SelectedLineageContext(
            lease.Access,
            LineageTestData.Scope().RepositoryId,
            new SelectedLineageSnapshot(
                new string('1', 64),
                new string('2', 64),
                new string('3', 64),
                new string('4', 64),
                LineageTransitionKind.Initial));
        Assert.True(selected.TryGetSnapshot(lease.Access, out _));
        lease.Access.Dispose();
        Assert.False(selected.TryGetSnapshot(lease.Access, out _));
        Assert.Equal(nameof(SelectedLineageContext), selected.ToString());
        selected.Dispose();
        lease.Dispose();
    }

    private static string FindRepositoryPath(params string[] segments)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null &&
            !File.Exists(Path.Join(current.FullName, "package.json")))
        {
            current = current.Parent;
        }

        Assert.NotNull(current);
        return Path.Join([current!.FullName, .. segments]);
    }
}

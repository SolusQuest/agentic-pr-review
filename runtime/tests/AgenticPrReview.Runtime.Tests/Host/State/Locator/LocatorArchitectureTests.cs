using System.Reflection;
using AgenticPrReview.Runtime;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;

namespace AgenticPrReview.Runtime.Tests.Host.State.Locator;

public sealed class LocatorArchitectureTests
{
    private const string LocatorNamespace =
        "AgenticPrReview.Runtime.Host.State.Locator";

    [Fact]
    public void LocatorSurfaceIsInternalClosedAndCapabilityBound()
    {
        var types = typeof(RuntimeApplication).Assembly.GetTypes()
            .Where(type => type.Namespace == LocatorNamespace)
            .ToArray();
        Assert.NotEmpty(types);
        Assert.All(types, type => Assert.False(type.IsPublic));
        Assert.All(
            types.Where(type => type.IsClass),
            type => Assert.True(type.IsSealed));

        var constructor = Assert.Single(
            typeof(AuthorizedLocatorAccess).GetConstructors(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic));
        Assert.True(constructor.IsPrivate);
        Assert.All(
            typeof(LocatorRootService).GetMethods(
                BindingFlags.Instance |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly)
                .Where(method => method.Name == "ResolveAsync"),
            method => Assert.Equal(
                typeof(AuthorizedLocatorAccess),
                method.GetParameters()[0].ParameterType));
    }

    [Fact]
    public void LocatorDoesNotExpandTheFrozenOpaqueStoreBoundary()
    {
        Assert.Equal(
            new[]
            {
                "DeleteExactAsync",
                "DownloadAsync",
                "ListExactAsync",
                "ReadBackExactAsync",
                "ReadMetadataAsync",
                "UploadImmutableAsync",
            },
            typeof(IRestrictedStateStore).GetMethods()
                .Select(method => method.Name)
                .Order(StringComparer.Ordinal));

        var referencedNames = typeof(RuntimeApplication).Assembly.GetTypes()
            .Where(type => type.Namespace == LocatorNamespace)
            .SelectMany(type => type.GetFields(
                BindingFlags.Instance |
                BindingFlags.Static |
                BindingFlags.Public |
                BindingFlags.NonPublic))
            .Select(field => field.FieldType.FullName ?? field.FieldType.Name)
            .ToArray();
        Assert.DoesNotContain(referencedNames, name =>
            name.Contains("Octokit", StringComparison.Ordinal) ||
            name.Contains("System.Net.Http", StringComparison.Ordinal) ||
            name.Contains("System.Diagnostics.Process", StringComparison.Ordinal) ||
            name.Contains("AgentSession", StringComparison.Ordinal) ||
            name.Contains("Publication", StringComparison.Ordinal));
    }

    [Fact]
    public void ResultAndContextPropertiesContainNoSecretOrLineageValues()
    {
        var prohibited = new[]
        {
            "Root",
            "Key",
            "Plaintext",
            "Ciphertext",
            "Nonce",
            "Repository",
            "Workflow",
            "Session",
            "Provider",
            "Policy",
            "Lineage",
        };
        var properties = new[]
        {
            typeof(LocatorRootResult),
            typeof(LocatorContext),
        }.SelectMany(type => type.GetProperties(
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic));
        Assert.DoesNotContain(properties, property => prohibited.Any(value =>
            property.Name.Contains(
                value,
                StringComparison.OrdinalIgnoreCase)));
    }
}

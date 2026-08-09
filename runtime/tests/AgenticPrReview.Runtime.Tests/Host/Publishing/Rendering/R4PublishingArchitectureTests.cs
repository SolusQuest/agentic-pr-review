using System.Reflection;
using AgenticPrReview.Runtime;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Publishing.Rendering;

public sealed class R4PublishingArchitectureTests
{
    private const string RenderingNamespace =
        "AgenticPrReview.Runtime.Host.Publishing.Rendering";

    [Fact]
    public void RenderingSurfaceIsInternalHostOwnedAndSideEffectFree()
    {
        var types = typeof(RuntimeApplication).Assembly.GetTypes()
            .Where(type => type.Namespace == RenderingNamespace)
            .ToArray();

        Assert.NotEmpty(types);
        Assert.All(types, type => Assert.False(type.IsPublic));
        Assert.DoesNotContain(
            types.SelectMany(ReferencedTypes),
            type =>
            {
                var name = type.FullName ?? type.Name;
                return name.Contains("Octokit", StringComparison.Ordinal) ||
                    name.Contains("System.Net", StringComparison.Ordinal) ||
                    name.Contains("System.IO", StringComparison.Ordinal) ||
                    name.Contains("System.Diagnostics.Process", StringComparison.Ordinal) ||
                    name.Contains("Host.Action", StringComparison.Ordinal);
            });
    }

    [Fact]
    public void ScopeAndRenderResultsExposeNoRunStateProviderOrSecretFields()
    {
        var prohibited = new[]
        {
            "Secret",
            "Token",
            "Provider",
            "Session",
            "Attempt",
            "Artifact",
            "Lineage",
            "State",
            "Continuation",
        };
        var properties = new[]
        {
            typeof(R4PublicationScopeV1),
            typeof(R4PublicationIdentityV1),
            typeof(R4RenderedStickyComment),
            typeof(R4FindingIdentityV1),
        }.SelectMany(type => type.GetProperties());

        Assert.DoesNotContain(
            properties,
            property => prohibited.Any(word => property.Name.Contains(
                word,
                StringComparison.OrdinalIgnoreCase)));
    }

    private static IEnumerable<Type> ReferencedTypes(Type type)
    {
        yield return type;
        foreach (var field in type.GetFields(
            BindingFlags.Instance |
            BindingFlags.Static |
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly))
        {
            yield return field.FieldType;
        }

        foreach (var property in type.GetProperties(
            BindingFlags.Instance |
            BindingFlags.Static |
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly))
        {
            yield return property.PropertyType;
        }

        foreach (var method in type.GetMethods(
            BindingFlags.Instance |
            BindingFlags.Static |
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly))
        {
            yield return method.ReturnType;
            foreach (var parameter in method.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }
    }
}

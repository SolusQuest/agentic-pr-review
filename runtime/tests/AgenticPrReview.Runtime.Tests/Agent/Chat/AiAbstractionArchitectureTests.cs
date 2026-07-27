using System.Reflection;
using AgenticPrReview.Runtime;

namespace AgenticPrReview.Runtime.Tests.Agent.Chat;

public sealed class AiAbstractionArchitectureTests
{
    private const string ChatNamespace = "AgenticPrReview.Runtime.Agent.Chat";

    [Fact]
    public void AgentFacingSurfaceIsOneNarrowAsyncOperation()
    {
        var assembly = typeof(RuntimeApplication).Assembly;
        var client = assembly.GetType(
            $"{ChatNamespace}.IProjectChatClient",
            throwOnError: true)!;

        Assert.True(client.IsInterface);
        Assert.False(client.IsPublic);
        var method = Assert.Single(client.GetMethods());
        Assert.Equal("GetResponseAsync", method.Name);
        Assert.Equal("Task`1", method.ReturnType.Name);
        Assert.Equal(
            $"{ChatNamespace}.ProjectChatResponse",
            method.ReturnType.GetGenericArguments().Single().FullName);
        var parameters = method.GetParameters();
        Assert.Collection(
            parameters,
            request => Assert.Equal(
                $"{ChatNamespace}.ProjectChatRequest",
                request.ParameterType.FullName),
            cancellation => Assert.Equal(
                typeof(CancellationToken),
                cancellation.ParameterType));
    }

    [Fact]
    public void SelectedSeamHasNoHostNetworkServiceOrProcessCapability()
    {
        var assembly = typeof(RuntimeApplication).Assembly;
        var types = assembly.GetTypes()
            .Where(type => type.Namespace == ChatNamespace)
            .ToArray();
        Assert.NotEmpty(types);
        Assert.DoesNotContain(types, type => type.IsPublic);
        Assert.DoesNotContain(
            types,
            type => type.Name.Contains("Candidate", StringComparison.Ordinal));

        var referencedNames = types
            .SelectMany(ReferencedTypes)
            .Select(type => type.FullName ?? type.Name)
            .ToArray();
        var forbiddenFragments = new[]
        {
            "Microsoft.Extensions.AI",
            "System.Net.Http",
            "IServiceProvider",
            "Octokit",
            "GitHub",
            "Actions",
            "Environment",
            "Process",
            "Publisher",
            "Shell",
        };
        foreach (var fragment in forbiddenFragments)
        {
            Assert.DoesNotContain(
                referencedNames,
                name => name.Contains(fragment, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ProductionAssemblyContainsOnlyTheSelectedMinimalPath()
    {
        var assembly = typeof(RuntimeApplication).Assembly;
        Assert.NotNull(assembly.GetType(
            $"{ChatNamespace}.MinimalChatClient",
            throwOnError: false));
        Assert.DoesNotContain(
            assembly.GetReferencedAssemblies(),
            reference => reference.Name?.StartsWith(
                "Microsoft.Extensions.AI",
                StringComparison.Ordinal) == true);
    }

    private static IEnumerable<Type> ReferencedTypes(Type type)
    {
        yield return type;
        if (type.BaseType is not null)
        {
            yield return type.BaseType;
        }
        foreach (var implemented in type.GetInterfaces())
        {
            yield return implemented;
        }
        foreach (var constructor in type.GetConstructors(
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic))
        {
            foreach (var parameter in constructor.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }
        foreach (var property in type.GetProperties(
            BindingFlags.Instance |
            BindingFlags.Static |
            BindingFlags.Public |
            BindingFlags.NonPublic))
        {
            yield return property.PropertyType;
        }
        foreach (var field in type.GetFields(
            BindingFlags.Instance |
            BindingFlags.Static |
            BindingFlags.Public |
            BindingFlags.NonPublic))
        {
            yield return field.FieldType;
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

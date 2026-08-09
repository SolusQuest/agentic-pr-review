using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.Serialization;

namespace AgenticPrReview.Runtime.Tests.Host.Action.Contracts;

public sealed class ActionHostArchitectureTests
{
    [Fact]
    public void ActionHostSurfaceIsInternalAndAvoidsOpenPayloadTypes()
    {
        var types = typeof(ActionHostLaunchContract).Assembly.GetTypes()
            .Where(type => type.Namespace?.StartsWith(
                "AgenticPrReview.Runtime.ActionHost",
                StringComparison.Ordinal) is true)
            .ToArray();

        Assert.NotEmpty(types);
        Assert.All(types, type => Assert.False(type.IsPublic));
        var contractShapes = types.Where(type =>
            type.Namespace == "AgenticPrReview.Runtime.ActionHost.Contracts" ||
            type.Namespace == "AgenticPrReview.Runtime.ActionHost.Serialization" &&
            type.Name.EndsWith("Document", StringComparison.Ordinal));
        var exposed = contractShapes
            .SelectMany(type => type.GetProperties(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.DeclaredOnly)
                .Select(property => property.PropertyType)
                .Concat(type.GetFields(
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Static |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.DeclaredOnly)
                    .Select(field => field.FieldType)))
            .SelectMany(Flatten)
            .Distinct()
            .ToArray();
        Assert.DoesNotContain(typeof(object), exposed);
        Assert.DoesNotContain(typeof(JsonElement), exposed);
        Assert.DoesNotContain(exposed, type =>
            type.IsGenericType &&
            type.GetGenericTypeDefinition() == typeof(Dictionary<,>));

        var completionProperties = typeof(ActionHostCompletion)
            .GetProperties(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic);
        Assert.DoesNotContain(completionProperties, property =>
            property.Name.Contains("Command", StringComparison.Ordinal) ||
            property.Name.Contains("History", StringComparison.Ordinal));
    }

    [Fact]
    public void ProductionAndConsumerContextsUseStrictGeneratedMetadata()
    {
        var production = ActionHostJsonContext.Default;
        AssertStrict(production.Options);
        Assert.NotEqual(JsonTypeInfoKind.None,
            production.ActionHostLaunchDocument.Kind);
        Assert.NotEqual(JsonTypeInfoKind.None,
            production.ActionHostCompletionDocument.Kind);

        var requestType = typeof(ActionHostPrivateCommandEnvelope<
            ActionHostNoPrivateCommandDocument>);
        var resultType = typeof(ActionHostPrivateCommandResultEnvelope<
            ActionHostNoPrivateCommandResultDocument>);
        Assert.NotEqual(JsonTypeInfoKind.None,
            production.GetTypeInfo(requestType)!.Kind);
        Assert.NotEqual(JsonTypeInfoKind.None,
            production.GetTypeInfo(resultType)!.Kind);

        var consumer = ActionHostPrivateCommandTestJsonContext.Default;
        AssertStrict(consumer.Options);
        Assert.NotEqual(JsonTypeInfoKind.None,
            consumer.GetTypeInfo(typeof(ActionHostPrivateCommandEnvelope<
                TestPrivateCommand>))!.Kind);
        Assert.NotEqual(JsonTypeInfoKind.None,
            consumer.GetTypeInfo(typeof(ActionHostPrivateCommandResultEnvelope<
                TestPrivateResult>))!.Kind);
    }

    [Fact]
    public void TaxonomiesRemainClosedAtTheReviewedCounts()
    {
        Assert.Equal(9, Enum.GetValues<ActionHostInputError>().Length - 1);
        Assert.Equal(18, Enum.GetValues<ActionHostStatus>().Length);
        Assert.Equal(11, Enum.GetValues<ActionHostExitClass>().Length);
        Assert.Equal(4, Enum.GetValues<ActionHostStateDisposition>().Length);
        Assert.Equal(13, Enum.GetValues<ActionHostAnnotationCode>().Length);
        Assert.Equal(4, typeof(ActionHostOpaqueSecret).Assembly.GetTypes()
            .Count(type => type.BaseType == typeof(ActionHostOpaqueSecret)));
    }

    private static void AssertStrict(JsonSerializerOptions options)
    {
        Assert.False(options.PropertyNameCaseInsensitive);
        Assert.Equal(JsonUnmappedMemberHandling.Disallow,
            options.UnmappedMemberHandling);
        Assert.False(options.AllowDuplicateProperties);
        Assert.Equal(JsonIgnoreCondition.Never,
            options.DefaultIgnoreCondition);
        Assert.Same(JsonNamingPolicy.SnakeCaseLower,
            options.PropertyNamingPolicy);
    }

    private static IEnumerable<Type> Flatten(Type type)
    {
        yield return type;
        if (type.HasElementType)
        {
            foreach (var nested in Flatten(type.GetElementType()!))
            {
                yield return nested;
            }
        }

        if (!type.IsGenericType)
        {
            yield break;
        }

        foreach (var argument in type.GetGenericArguments())
        {
            foreach (var nested in Flatten(argument))
            {
                yield return nested;
            }
        }
    }
}

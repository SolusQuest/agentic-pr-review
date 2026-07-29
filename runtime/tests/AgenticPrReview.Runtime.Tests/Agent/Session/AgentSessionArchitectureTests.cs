using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using AgenticPrReview.Runtime;
using AgenticPrReview.Runtime.Agent.Session;

namespace AgenticPrReview.Runtime.Tests.Agent.Session;

public sealed class AgentSessionArchitectureTests
{
    private const string SessionNamespace =
        "AgenticPrReview.Runtime.Agent.Session";

    private static readonly string[] RetiredTypeNames =
    [
        "ProviderSessionLedgerV1",
        "StateManifestV2",
        "ReviewInputV1",
    ];

    [Fact]
    public void SessionSurfaceIsInternalClosedAndDoesNotExposeRetiredTypes()
    {
        var types = typeof(RuntimeApplication).Assembly.GetTypes()
            .Where(type => type.Namespace == SessionNamespace)
            .ToArray();

        Assert.NotEmpty(types);
        Assert.All(types, type => Assert.False(type.IsPublic));
        var referencedNames = types
            .SelectMany(ReferencedTypes)
            .Select(type => type.FullName ?? type.Name)
            .ToArray();
        Assert.DoesNotContain(
            referencedNames,
            name => RetiredTypeNames.Any(retired =>
                name.Contains(retired, StringComparison.Ordinal)));
        Assert.DoesNotContain(
            referencedNames,
            name =>
                name.Contains(
                    "System.Dynamic",
                    StringComparison.Ordinal) ||
                name.Contains(
                    "System.Text.Json.Nodes",
                    StringComparison.Ordinal) ||
                name.Contains(
                    "System.IServiceProvider",
                    StringComparison.Ordinal));
    }

    [Fact]
    public void SessionJsonGraphUsesGeneratedMetadataWithUnknownsDisallowed()
    {
        Assert.Equal(
            JsonUnmappedMemberHandling.Disallow,
            AgentSessionJsonContext.Default.Options.UnmappedMemberHandling);
        JsonTypeInfo[] metadata =
        [
            AgentSessionJsonContext.Default.AgentSessionEnvelopeRootDto,
            AgentSessionJsonContext.Default.AgentSessionRootDto,
            AgentSessionJsonContext.Default.AgentSessionReviewContextDto,
            AgentSessionJsonContext.Default.AgentSessionAssistantMessageDto,
            AgentSessionJsonContext.Default.AgentSessionToolResultDto,
            AgentSessionJsonContext.Default.AgentSessionReviewOutcomeDto,
            AgentSessionJsonContext.Default.AgentSessionTextContentDto,
            AgentSessionJsonContext.Default.AgentSessionContinuationSlotDto,
            AgentSessionJsonContext.Default.AgentSessionToolCallDto,
            AgentSessionJsonContext.Default.AgentSessionTerminalCallDto,
            AgentSessionJsonContext.Default.AgentSessionReadFileResultDto,
            AgentSessionJsonContext.Default.AgentSessionSearchTextResultDto,
        ];

        Assert.All(metadata, typeInfo =>
        {
            Assert.NotNull(typeInfo);
            Assert.NotEqual(JsonTypeInfoKind.None, typeInfo.Kind);
        });
    }

    [Fact]
    public void SessionOutcomeTaxonomyIsExactlyFrozen()
    {
        var codes = typeof(AgentSessionCodes)
            .GetFields(
                BindingFlags.Static |
                BindingFlags.NonPublic)
            .Select(field => Assert.IsType<string>(
                field.GetRawConstantValue()))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "session_association_invalid",
                "session_bootstrap_absent",
                "session_bootstrap_incompatible",
                "session_classification_invalid",
                "session_construction_limit",
                "session_continuation_invalid",
                "session_current_malformed",
                "session_current_oversized",
                "session_explicit_incompatible",
                "session_explicit_missing",
                "session_record_invalid",
                "session_reset_explicit",
                "session_scope_mismatch",
                "session_transition_rejected",
            },
            codes);
    }

    [Fact]
    public void SessionDomainHasNoExtensionDictionaryOrObjectPayload()
    {
        var domainTypes = typeof(AgentSessionDocument).Assembly.GetTypes()
            .Where(type =>
                type.Namespace == SessionNamespace &&
                !type.Name.EndsWith("Dto", StringComparison.Ordinal) &&
                !typeof(System.Text.Json.Serialization.JsonSerializerContext)
                    .IsAssignableFrom(type))
            .ToArray();
        var violations = domainTypes
            .SelectMany(type => type.GetProperties(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly))
            .Where(property =>
                property.PropertyType == typeof(object) ||
                property.PropertyType.IsGenericType &&
                property.PropertyType.GetGenericTypeDefinition() ==
                    typeof(Dictionary<,>) &&
                property.PropertyType.GetGenericArguments()[0] ==
                    typeof(string))
            .Select(property => string.Concat(
                property.DeclaringType?.FullName,
                ".",
                property.Name,
                ":",
                property.PropertyType))
            .ToArray();

        Assert.Empty(violations);
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

        foreach (var field in type.GetFields(
            BindingFlags.Instance |
            BindingFlags.Static |
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly))
        {
            foreach (var referenced in Expand(field.FieldType))
            {
                yield return referenced;
            }
        }

        foreach (var property in type.GetProperties(
            BindingFlags.Instance |
            BindingFlags.Static |
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly))
        {
            foreach (var referenced in Expand(property.PropertyType))
            {
                yield return referenced;
            }
        }

        foreach (var method in type.GetMethods(
            BindingFlags.Instance |
            BindingFlags.Static |
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly))
        {
            foreach (var referenced in Expand(method.ReturnType))
            {
                yield return referenced;
            }

            foreach (var parameter in method.GetParameters())
            {
                foreach (var referenced in Expand(parameter.ParameterType))
                {
                    yield return referenced;
                }
            }
        }
    }

    private static IEnumerable<Type> Expand(Type type)
    {
        yield return type;
        if (type.HasElementType && type.GetElementType() is { } element)
        {
            foreach (var nested in Expand(element))
            {
                yield return nested;
            }
        }

        foreach (var argument in type.GetGenericArguments())
        {
            foreach (var nested in Expand(argument))
            {
                yield return nested;
            }
        }
    }
}

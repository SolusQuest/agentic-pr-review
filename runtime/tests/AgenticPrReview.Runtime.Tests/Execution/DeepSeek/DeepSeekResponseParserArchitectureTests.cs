using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Execution;
using AgenticPrReview.Runtime.Execution.DeepSeek;

namespace AgenticPrReview.Runtime.Tests.Execution.DeepSeek;

public sealed partial class DeepSeekTransportArchitectureTests
{
    [Fact]
    public void ResponseParserHasOnlyReviewedCapabilities()
    {
        var parserTypes = new[]
            {
                typeof(DeepSeekResponseParser),
                typeof(DeepSeekResponseParseOutcome),
                typeof(DeepSeekResponseParseResult),
                typeof(DeepSeekParsedToolResponse),
                typeof(DeepSeekParsedToolCall),
                typeof(DeepSeekParsedUsage),
            }
            .SelectMany(type => new[] { type }.Concat(type.GetNestedTypes(
                BindingFlags.Public | BindingFlags.NonPublic)))
            .ToArray();
        var allowedAgentTypes = new HashSet<Type>
        {
            typeof(AgentLimits),
            typeof(AgentValueDomains),
        };
        var allowedDeepSeekTypes = new HashSet<Type>(parserTypes)
        {
            typeof(DeepSeekTransportResult),
            typeof(DeepSeekTransportOutcome),
            typeof(DeepSeekTransportPolicy),
            typeof(DeepSeekRequestWriter),
        };
        var violations = new List<string>();

        foreach (var type in parserTypes)
        {
            foreach (var referenced in ReferencedTypes(type)
                         .SelectMany(ExpandTypeGraph))
            {
                AddParserViolation(
                    violations,
                    $"signature:{type.FullName}->{TypeName(referenced)}",
                    referenced,
                    allowedAgentTypes,
                    allowedDeepSeekTypes);
            }

            foreach (var method in DeclaredExecutableMembers(type))
            {
                var body = method.GetMethodBody();
                if (body is not null)
                {
                    foreach (var local in body.LocalVariables)
                    {
                        foreach (var localType in
                                 ExpandTypeGraph(local.LocalType))
                        {
                            AddParserViolation(
                                violations,
                                $"local:{type.FullName}.{method.Name}->" +
                                TypeName(localType),
                                localType,
                                allowedAgentTypes,
                                allowedDeepSeekTypes);
                        }
                    }
                }

                foreach (var member in ResolveMethodBodyMembers(method))
                {
                    var referenced = member as Type ?? member.DeclaringType;
                    if (referenced is not null)
                    {
                        AddParserViolation(
                            violations,
                            $"body:{type.FullName}.{method.Name}->" +
                            FormatMember(member),
                            referenced,
                            allowedAgentTypes,
                            allowedDeepSeekTypes);
                    }
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void ParsedResponseSurfaceRetainsNoRawOrProjectValues()
    {
        var types = new[]
        {
            typeof(DeepSeekResponseParseResult),
            typeof(DeepSeekParsedToolResponse),
            typeof(DeepSeekParsedToolCall),
            typeof(DeepSeekParsedUsage),
        };
        var propertyNames = types
            .SelectMany(type => type.GetProperties(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly))
            .Select(property => property.Name)
            .ToArray();
        var propertyTypes = types
            .SelectMany(type => type.GetProperties(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly))
            .Select(property => property.PropertyType)
            .SelectMany(ExpandTypeGraph)
            .ToArray();

        Assert.DoesNotContain(
            propertyNames,
            name => new[]
            {
                "Body",
                "RequestId",
                "Fingerprint",
                "Created",
                "CacheHit",
                "CacheMiss",
                "Continuation",
            }.Any(fragment => name.Contains(
                fragment,
                StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(propertyTypes, type =>
            type == typeof(byte[]) ||
            type == typeof(JsonDocument) ||
            type == typeof(JsonElement) ||
            TypeName(type).Contains(
                "ProjectChat",
                StringComparison.Ordinal));
        Assert.Equal(
            typeof(ImmutableArray<DeepSeekParsedToolCall>),
            typeof(DeepSeekParsedToolResponse)
                .GetProperty(
                    nameof(DeepSeekParsedToolResponse.Calls),
                    BindingFlags.Instance |
                    BindingFlags.NonPublic)!
                .PropertyType);
    }

    private static void AddParserViolation(
        List<string> violations,
        string description,
        Type candidate,
        IReadOnlySet<Type> allowedAgentTypes,
        IReadOnlySet<Type> allowedDeepSeekTypes)
    {
        while (candidate.HasElementType &&
            candidate.GetElementType() is { } elementType)
        {
            candidate = elementType;
        }

        var name = TypeName(candidate);
        var forbidden =
            name.StartsWith("System.Net.", StringComparison.Ordinal) ||
            candidate == typeof(Environment) ||
            candidate == typeof(IServiceProvider) ||
            candidate == typeof(DeepSeekCredential) ||
            candidate == typeof(DeepSeekTransport) ||
            candidate == typeof(IDeepSeekTransport) ||
            candidate == typeof(DeepSeekProviderContract) ||
            candidate == typeof(DeepSeekLiveProviderExecutor) ||
            name.StartsWith(
                "AgenticPrReview.Runtime.Agent",
                StringComparison.Ordinal) &&
            !allowedAgentTypes.Contains(candidate) ||
            name.StartsWith(
                "AgenticPrReview.Runtime.Execution.DeepSeek",
                StringComparison.Ordinal) &&
            !allowedDeepSeekTypes.Contains(candidate);
        if (forbidden)
        {
            violations.Add(description);
        }
    }
}

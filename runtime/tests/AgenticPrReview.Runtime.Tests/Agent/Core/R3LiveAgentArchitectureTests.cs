using System.Reflection;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Execution;
using AgenticPrReview.Runtime.Host.State;

namespace AgenticPrReview.Runtime.Tests.Agent.Core;

public sealed partial class AgentCapabilityArchitectureTests
{
    [Fact]
    public void R3LiveApplicationCallsOnlyTheRestoreStateOperation()
    {
        var calls = R3LiveTypes()
            .SelectMany(type => DeclaredExecutableMembers(type)
                .SelectMany(method => ResolveMethodBodyMembers(method)
                    .OfType<MethodInfo>()
                    .Select(member => (Type: type, Member: member))))
            .Where(call => call.Member.DeclaringType ==
                typeof(RestrictedStateService))
            .Select(call =>
                $"{call.Type.FullName}|{call.Member.Name}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "AgenticPrReview.Runtime.R3LiveAgentStateRestorer|Restore",
            ],
            calls);
        Assert.DoesNotContain(
            R3LiveTypes()
                .SelectMany(DeclaredExecutableMembers)
                .SelectMany(ResolveMethodBodyMembers)
                .OfType<MethodInfo>(),
            member => member.DeclaringType ==
                    typeof(AgentSessionBuilder) &&
                StringComparer.Ordinal.Equals(member.Name, "Build"));
    }

    [Fact]
    public void R3LiveApplicationDoesNotReferenceRetiredOrBroadCapabilities()
    {
        var forbidden = new[]
        {
            typeof(DeepSeekLiveProviderExecutor).FullName!,
            typeof(ILiveProviderExecutor).FullName!,
            typeof(LiveRuntimeApplication).FullName!,
            typeof(HttpClient).FullName!,
            typeof(System.Diagnostics.Process).FullName!,
            typeof(IServiceProvider).FullName!,
        };
        var violations = new List<string>();

        foreach (var type in R3LiveTypes())
        {
            foreach (var referenced in ReferencedTypes(type)
                .SelectMany(ExpandTypeGraph))
            {
                var name = referenced.FullName ?? referenced.Name;
                if (forbidden.Contains(name, StringComparer.Ordinal) ||
                    name.Contains("GitHub", StringComparison.Ordinal) ||
                    name.Contains("Publisher", StringComparison.Ordinal))
                {
                    violations.Add(
                        $"signature:{type.FullName}->{name}");
                }
            }

            foreach (var member in DeclaredExecutableMembers(type)
                .SelectMany(ResolveMethodBodyMembers))
            {
                var declaring = member.DeclaringType?.FullName ?? string.Empty;
                if (forbidden.Contains(declaring, StringComparer.Ordinal) ||
                    declaring.Contains("GitHub", StringComparison.Ordinal) ||
                    declaring.Contains("Publisher", StringComparison.Ordinal))
                {
                    violations.Add(
                        $"body:{type.FullName}->{FormatMember(member)}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void RuntimeEntrypointsRetainOnlyTheExistingDispatch()
    {
        var entrypointTypes = new[]
        {
            typeof(RuntimeApplication),
            typeof(LiveRuntimeApplication),
        };
        var r3Types = R3LiveTypes().ToHashSet();
        var references = entrypointTypes
            .SelectMany(type => ReferencedTypes(type)
                .SelectMany(ExpandTypeGraph)
                .Concat(DeclaredExecutableMembers(type)
                    .SelectMany(ResolveMethodBodyMembers)
                    .Select(member => member.DeclaringType)
                    .Where(type => type is not null)
                    .Select(type => type!)))
            .Where(type => r3Types.Contains(type))
            .Select(type => type.FullName)
            .ToArray();

        Assert.Empty(references);
        Assert.NotNull(typeof(LiveRuntimeApplication));
        Assert.NotNull(typeof(RuntimeApplication).GetField(
            "liveExecutorFactory",
            BindingFlags.Instance | BindingFlags.NonPublic));
    }

    [Fact]
    public void ProductionEnvironmentCallsHaveAnExactTemporaryAllowlist()
    {
        var calls = typeof(RuntimeApplication).Assembly
            .GetTypes()
            .SelectMany(type => DeclaredExecutableMembers(type)
                .SelectMany(method => ResolveMethodBodyMembers(method)
                    .Where(member => member.DeclaringType ==
                            typeof(Environment) &&
                        member.Name is "GetEnvironmentVariable" or
                            "SetEnvironmentVariable")
                    .Select(member =>
                        $"{type.FullName}|{method.Name}|{member.Name}")))
            .Order(StringComparer.Ordinal)
            .ToArray();

        string[] expected =
            [
                "AgenticPrReview.Runtime.DeepSeekLiveProviderExecutor|FromEnvironment|GetEnvironmentVariable",
                "AgenticPrReview.Runtime.DeepSeekLiveProviderExecutor|ValidatePreflight|GetEnvironmentVariable",
                "AgenticPrReview.Runtime.R3LiveAgentEnvironmentSecretSource|TakeAndClear|GetEnvironmentVariable",
                "AgenticPrReview.Runtime.R3LiveAgentEnvironmentSecretSource|TakeAndClear|GetEnvironmentVariable",
                "AgenticPrReview.Runtime.R3LiveAgentEnvironmentSecretSource|TakeAndClear|SetEnvironmentVariable",
                "AgenticPrReview.Runtime.R3LiveAgentEnvironmentSecretSource|TakeAndClear|SetEnvironmentVariable",
            ];
        Assert.True(
            expected.SequenceEqual(calls, StringComparer.Ordinal),
            string.Join(Environment.NewLine, calls));
    }

    [Fact]
    public void LiveHandoffTypesHaveNoPublicSerializationSurface()
    {
        var types = new[]
        {
            typeof(LiveAgentCandidate),
            typeof(R3LiveAgentResult),
            typeof(R3LiveAgentExecution),
        };
        foreach (var type in types)
        {
            Assert.False(type.IsPublic);
            Assert.Empty(type.GetProperties(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.DeclaredOnly));
            Assert.Empty(type.GetFields(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.DeclaredOnly));
        }

        Assert.DoesNotContain(
            typeof(RuntimeJsonContext).GetProperties(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic),
            property => property.PropertyType == typeof(LiveAgentCandidate) ||
                property.PropertyType == typeof(R3LiveAgentResult) ||
                property.PropertyType == typeof(R3LiveAgentExecution));
    }

    private static IEnumerable<Type> R3LiveTypes() =>
        typeof(RuntimeApplication).Assembly.GetTypes().Where(type =>
            type.Namespace == "AgenticPrReview.Runtime" &&
            (type.Name.StartsWith("R3LiveAgent", StringComparison.Ordinal) ||
                StringComparer.Ordinal.Equals(
                    type.Name,
                    nameof(LiveAgentCandidate))));
}

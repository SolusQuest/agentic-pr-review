using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
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
    public void LiveAgentHostCommitHasAnExactStateMutationAllowlist()
    {
        var calls = HostCommitTypes()
            .SelectMany(type => DeclaredExecutableMembers(type)
                .SelectMany(method => ResolveMethodBodyMembers(method)
                    .OfType<MethodInfo>()
                    .Where(member => member.DeclaringType ==
                            typeof(RestrictedStateService) ||
                        member.DeclaringType == typeof(AgentSessionBuilder))
                    .Select(member =>
                        $"{type.FullName}|{member.DeclaringType!.Name}." +
                            member.Name)))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "AgenticPrReview.Runtime.LiveAgentStateCommitCoordinator|AgentSessionBuilder.Build",
                "AgenticPrReview.Runtime.LiveAgentStateTransaction|RestrictedStateService.Accept",
                "AgenticPrReview.Runtime.LiveAgentStateTransaction|RestrictedStateService.Prepare",
                "AgenticPrReview.Runtime.LiveAgentStateTransaction|RestrictedStateService.Reconcile",
            ],
            calls);
        Assert.DoesNotContain(
            calls,
            call => call.Contains("PrepareHandoff", StringComparison.Ordinal) ||
                call.Contains("CleanupExpired", StringComparison.Ordinal) ||
                call.Contains("Enumerate", StringComparison.Ordinal) ||
                call.Contains("Reset", StringComparison.Ordinal));
    }

    [Fact]
    public void R3LiveApplicationDoesNotReferenceRetiredOrBroadCapabilities()
    {
        var violations = FindR3CapabilityViolations(R3LiveTypes());

        Assert.True(
            violations.Count == 0,
            string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void RuntimeEntrypointsDoNotReachDirectlyIntoR3LiveApplication()
    {
        var entrypointTypes = new[]
        {
            typeof(RuntimeApplication),
        }.SelectMany(IncludeTypeAndNestedTypesRecursively).ToArray();
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
        Assert.Null(typeof(RuntimeApplication).Assembly.GetType(
            "AgenticPrReview.Runtime.LiveRuntimeApplication",
            throwOnError: false));
        Assert.Null(typeof(RuntimeApplication).GetField(
            "liveExecutorFactory",
            BindingFlags.Instance | BindingFlags.NonPublic));
    }

    [Fact]
    public void ProductionEnvironmentCallsHaveAnExactTemporaryAllowlist()
    {
        var calls = FindEnvironmentCalls(
            typeof(RuntimeApplication).Assembly.GetTypes());

        string[] expected =
            [
                "AgenticPrReview.Runtime.Canonical.LenientJsonObjectEnumerator+<Enumerate>d__5|.ctor|get_CurrentManagedThreadId",
                "AgenticPrReview.Runtime.Canonical.LenientJsonObjectEnumerator+<Enumerate>d__5|System.Collections.Generic.IEnumerable<AgenticPrReview.Runtime.Canonical.LenientJsonObjectEnumerator.Entry>.GetEnumerator|get_CurrentManagedThreadId",
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
    public void R3CapabilityScannersCheckAsyncAndClosedCapabilityFamilies()
    {
        var asyncTypes = IncludeTypeAndNestedTypesRecursively(
                typeof(AsyncNetworkCapabilityFixture))
            .ToArray();
        var violations = FindR3CapabilityViolations(asyncTypes);
        var environmentCalls = FindEnvironmentCalls(
            [typeof(UnapprovedEnvironmentCapabilityFixture)]);

        Assert.Contains(
            R3LiveTypes(),
            type => type.DeclaringType == typeof(R3LiveAgentApplication) &&
                type.Name.StartsWith("<RunAsync>", StringComparison.Ordinal));
        Assert.Contains(
            violations,
            violation => violation.Contains(
                typeof(System.Net.Sockets.TcpClient).FullName!,
                StringComparison.Ordinal));
        Assert.Contains(
            environmentCalls,
            call => call.EndsWith(
                "|GetFolderPath",
                StringComparison.Ordinal));
    }

    [Fact]
    public void LiveHandoffTypesHaveNoPublicSerializationSurface()
    {
        var types = new[]
        {
            typeof(LiveAgentCandidate),
            typeof(LiveAgentStateCommitResult),
            typeof(LiveAgentStatePrepareObservation),
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

        var contexts = typeof(RuntimeApplication).Assembly.GetTypes()
            .Where(type => typeof(JsonSerializerContext)
                .IsAssignableFrom(type))
            .ToArray();
        var registeredTypes = contexts
            .SelectMany(context => CustomAttributeData
                .GetCustomAttributes(context)
                .Where(attribute => attribute.AttributeType ==
                    typeof(JsonSerializableAttribute))
                .Select(attribute =>
                    (Type)attribute.ConstructorArguments.Single().Value!))
            .ToHashSet();

        Assert.NotEmpty(contexts);
        Assert.Contains(typeof(ReviewResult), registeredTypes);
        Assert.DoesNotContain(
            types,
            registeredTypes.Contains);
        Assert.False(typeof(LiveAgentFreshProcessCommandResult).IsPublic);
        Assert.DoesNotContain(
            typeof(LiveAgentFreshProcessCommandResult),
            registeredTypes);
        Assert.Equal(
            [
                ("DiagnosticCode", typeof(string)),
                ("ExitCode", typeof(int)),
            ],
            typeof(LiveAgentFreshProcessCommandResult)
                .GetProperties(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.DeclaredOnly)
                .Select(property => (property.Name, property.PropertyType))
                .OrderBy(property => property.Name, StringComparer.Ordinal));

        AssertExactInternalAutoPropertyStorage(
            typeof(R3LiveAgentResult),
            [
                ("AcceptedEnvelopeSha256", typeof(string)),
                ("AcceptedGeneration", typeof(long?)),
                ("AcceptedSessionSha256", typeof(string)),
                ("Code", typeof(string)),
                ("HandoffReady", typeof(bool)),
                ("ModelCalls", typeof(int)),
                ("StablePlanSha256", typeof(string)),
                ("TerminalSha256", typeof(string)),
                ("ToolCalls", typeof(int)),
            ]);
        AssertExactInternalAutoPropertyStorage(
            typeof(R3LiveAgentExecution),
            [
                ("DiagnosticCode", typeof(string)),
                ("Result", typeof(R3LiveAgentResult)),
            ]);
        AssertExactInternalAutoPropertyStorage(
            typeof(LiveAgentStateCommitResult),
            [
                ("AcceptedEnvelopeSha256", typeof(string)),
                ("AcceptedGeneration", typeof(long?)),
                ("AcceptedSessionSha256", typeof(string)),
                ("Code", typeof(string)),
                ("HandoffReady", typeof(bool)),
            ]);
    }

    private static IEnumerable<Type> R3LiveTypes() =>
        typeof(RuntimeApplication).Assembly.GetTypes()
            .Where(type =>
                type.Namespace == "AgenticPrReview.Runtime" &&
                (type.Name.StartsWith(
                        "R3LiveAgent",
                        StringComparison.Ordinal) ||
                    StringComparer.Ordinal.Equals(
                        type.Name,
                        nameof(LiveAgentCandidate))))
            .SelectMany(IncludeTypeAndNestedTypesRecursively)
            .Distinct();

    private static IEnumerable<Type> HostCommitTypes() =>
        typeof(RuntimeApplication).Assembly.GetTypes()
            .Where(type =>
                type.Namespace == "AgenticPrReview.Runtime" &&
                type.Name.StartsWith(
                    "LiveAgentState",
                    StringComparison.Ordinal))
            .SelectMany(IncludeTypeAndNestedTypesRecursively)
            .Distinct();

    private static IEnumerable<Type> IncludeTypeAndNestedTypesRecursively(
        Type type)
    {
        yield return type;
        foreach (var nested in type.GetNestedTypes(
                     BindingFlags.Public | BindingFlags.NonPublic))
        {
            foreach (var descendant in
                     IncludeTypeAndNestedTypesRecursively(nested))
            {
                yield return descendant;
            }
        }
    }

    private static List<string> FindR3CapabilityViolations(
        IEnumerable<Type> types)
    {
        var violations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in types)
        {
            foreach (var referenced in ReferencedTypes(type)
                .SelectMany(ExpandTypeGraph))
            {
                if (IsR3ForbiddenCapabilityType(referenced))
                {
                    violations.Add(
                        $"signature:{type.FullName}->{TypeName(referenced)}");
                }
            }

            foreach (var method in DeclaredExecutableMembers(type))
            {
                var body = method.GetMethodBody();
                if (body is not null)
                {
                    foreach (var local in body.LocalVariables
                        .SelectMany(local => ExpandTypeGraph(local.LocalType)))
                    {
                        if (IsR3ForbiddenCapabilityType(local))
                        {
                            violations.Add(
                                $"local:{type.FullName}.{method.Name}->{TypeName(local)}");
                        }
                    }

                    foreach (var clause in body.ExceptionHandlingClauses)
                    {
                        if (clause.Flags ==
                                ExceptionHandlingClauseOptions.Clause &&
                            clause.CatchType is { } catchType &&
                            IsR3ForbiddenCapabilityType(catchType))
                        {
                            violations.Add(
                                $"catch:{type.FullName}.{method.Name}->{TypeName(catchType)}");
                        }
                    }
                }

                foreach (var member in ResolveMethodBodyMembers(method))
                {
                    var referenced = member as Type ?? member.DeclaringType;
                    if (referenced is not null &&
                        IsR3ForbiddenCapabilityType(referenced))
                    {
                        violations.Add(
                            $"body:{type.FullName}.{method.Name}->{FormatMember(member)}");
                    }
                }
            }
        }

        return violations.Order(StringComparer.Ordinal).ToList();
    }

    private static bool IsR3ForbiddenCapabilityType(Type type)
    {
        if (typeof(IOException).IsAssignableFrom(type))
        {
            return false;
        }

        var name = TypeName(type);
        var forbiddenFragments = new[]
        {
            "System.Net.",
            "System.Diagnostics.Process",
            "System.IServiceProvider",
            "System.Management.Automation",
            "System.IO.",
            "Microsoft.Win32.SafeHandles.",
            "IRuntimeFileSystem",
            "Octokit",
            "GitHub",
            "Actions",
            "Publisher",
            "Shell",
        };
        return forbiddenFragments.Any(fragment =>
            name.Contains(fragment, StringComparison.Ordinal));
    }

    private static string[] FindEnvironmentCalls(IEnumerable<Type> types) =>
        types.SelectMany(type => DeclaredExecutableMembers(type)
                .SelectMany(method => ResolveMethodBodyMembers(method)
                    .Where(member => member.DeclaringType ==
                        typeof(Environment))
                    .Select(member =>
                        $"{type.FullName}|{method.Name}|{member.Name}")))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static void AssertExactInternalAutoPropertyStorage(
        Type type,
        (string Name, Type Type)[] expected)
    {
        var properties = type.GetProperties(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly)
            .Select(property => (property.Name, property.PropertyType))
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .ToArray();
        var fields = type.GetFields(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly)
            .Select(field => (field.Name, field.FieldType))
            .OrderBy(field => field.Name, StringComparer.Ordinal)
            .ToArray();
        var expectedFields = expected
            .Select(property =>
                ($"<{property.Name}>k__BackingField", property.Type))
            .ToArray();

        Assert.Equal(expected, properties);
        Assert.Equal(expectedFields, fields);
        Assert.All(
            type.GetProperties(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly),
            property => Assert.False(property.GetMethod!.IsPublic));
    }

    private static class AsyncNetworkCapabilityFixture
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static async Task BodyOnlyNetworkCapability()
        {
            await Task.Yield();
            using var client = new System.Net.Sockets.TcpClient();
        }
    }

    private static class UnapprovedEnvironmentCapabilityFixture
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static string ReadFolder() => Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData);
    }
}

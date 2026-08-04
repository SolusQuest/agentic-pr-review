using System.Reflection;
using System.Reflection.Emit;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.LiveAgentVerifierFixture;

namespace AgenticPrReview.Runtime.Tests.Agent.Core;

public sealed partial class AgentCapabilityArchitectureTests
{
    private static readonly string[] RetiredSingleShotTypeNames =
    [
        "AgenticPrReview.Runtime.LiveRuntimeApplication",
        "AgenticPrReview.Runtime.LiveInvocation",
        "AgenticPrReview.Runtime.ILiveProviderExecutor",
        "AgenticPrReview.Runtime.ILiveProviderExecutorFactory",
        "AgenticPrReview.Runtime.DefaultLiveProviderExecutorFactory",
        "AgenticPrReview.Runtime.ProviderRequestMessage",
        "AgenticPrReview.Runtime.ProviderRequestPlan",
        "AgenticPrReview.Runtime.ProviderRequestPlanDecoder",
        "AgenticPrReview.Runtime.ProviderExecutionObservation",
        "AgenticPrReview.Runtime.SyntheticLiveProviderExecutor",
        "AgenticPrReview.Runtime.DeepSeekProviderContract",
        "AgenticPrReview.Runtime.DeepSeekLiveProviderExecutor",
    ];

    [Fact]
    public void LiveAgentVerifierRetiredSingleShotDefinitionsAreAbsent()
    {
        var assembly = typeof(RuntimeApplication).Assembly;

        Assert.All(
            RetiredSingleShotTypeNames,
            name => Assert.Null(assembly.GetType(name, throwOnError: false)));
        Assert.Null(typeof(RuntimeApplication).GetField(
            "liveExecutorFactory",
            BindingFlags.Instance | BindingFlags.NonPublic));
    }

    [Fact]
    public void LiveAgentVerifierProductionRouteAndProviderCompositionAreExact()
    {
        var snapshot = ReadProductionRouteSnapshot();

        Assert.True(
            ProductionRouteIsExact(snapshot),
            string.Join(
                Environment.NewLine,
                [
                    .. snapshot.ExternalCommandCallers,
                    .. snapshot.AgentCommandLiterals,
                    .. snapshot.DirectApplicationCallers,
                    .. snapshot.RuntimeTransportFactories,
                    .. snapshot.VerifierTransportFactories,
                    .. snapshot.RealTransportCalls,
                ]));
    }

    [Fact]
    public void LiveAgentVerifierProductionRouteOracleRejectsRestorationMutations()
    {
        var valid = ReadProductionRouteSnapshot();
        Assert.True(ProductionRouteIsExact(valid));

        var mutations = new[]
        {
            valid with
            {
                ExternalCommandCallers =
                [.. valid.ExternalCommandCallers, "Alias.RunAsync"],
            },
            valid with
            {
                AgentCommandLiterals =
                [.. valid.AgentCommandLiterals, "review-live-agent-alias"],
            },
            valid with
            {
                DirectApplicationCallers =
                [.. valid.DirectApplicationCallers, "SecondComposition.RunAsync"],
            },
            valid with
            {
                RuntimeTransportFactories =
                [.. valid.RuntimeTransportFactories, "SecondTransportFactory"],
            },
            valid with
            {
                VerifierTransportFactories =
                [.. valid.VerifierTransportFactories, "SecondVerifierFactory"],
            },
            valid with
            {
                RealTransportCalls =
                [.. valid.RealTransportCalls, "SecondTransportFactory.Create"],
            },
        };

        Assert.All(mutations, mutation =>
            Assert.False(ProductionRouteIsExact(mutation)));
    }

    private static ProductionRouteSnapshot ReadProductionRouteSnapshot()
    {
        var runtimeAssembly = typeof(RuntimeApplication).Assembly;
        var runtimeTypes = runtimeAssembly.GetTypes();
        var freshProcessFamily = FreshProcessTypes().ToHashSet();
        var r3ApplicationFamily = IncludeTypeAndNestedTypesRecursively(
            typeof(R3LiveAgentApplication)).ToHashSet();

        var externalCommandCallers = runtimeTypes
            .Where(type => !freshProcessFamily.Contains(type))
            .SelectMany(type => DeclaredExecutableMembers(type)
                .SelectMany(method => ResolveMethodBodyMembers(method)
                    .Where(member => member.DeclaringType ==
                        typeof(LiveAgentFreshProcessCommand))
                    .Select(_ => RootTypeName(type))))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var agentCommandLiterals = IncludeTypeAndNestedTypesRecursively(
                typeof(RuntimeApplication))
            .SelectMany(DeclaredExecutableMembers)
            .SelectMany(ResolveMethodBodyStrings)
            .Where(value => value.StartsWith(
                "review-live",
                StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        var directApplicationCallers = runtimeTypes
            .Where(type => !r3ApplicationFamily.Contains(type))
            .SelectMany(type => DeclaredExecutableMembers(type)
                .SelectMany(method => ResolveMethodBodyMembers(method)
                    .Where(member => member.DeclaringType ==
                        typeof(R3LiveAgentApplication))
                    .Select(_ => RootTypeName(type))))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var runtimeTransportFactories = runtimeTypes
            .Where(type => type is { IsInterface: false, IsAbstract: false } &&
                typeof(IR3LiveAgentTransportFactory).IsAssignableFrom(type))
            .Select(type => type.FullName!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var verifierTransportFactories = typeof(LiveAgentVerifierProfile)
            .Assembly.GetTypes()
            .Where(type => type is { IsInterface: false, IsAbstract: false } &&
                typeof(IR3LiveAgentTransportFactory).IsAssignableFrom(type))
            .Select(type => type.FullName!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var realTransportCalls = runtimeTypes
            .SelectMany(type => DeclaredExecutableMembers(type)
                .SelectMany(method => ResolveMethodBodyMembers(method)
                    .OfType<MethodInfo>()
                    .Where(called => called.DeclaringType ==
                            typeof(DeepSeekTransport) &&
                        called.Name == nameof(DeepSeekTransport.Create))
                    .Select(_ => RootTypeName(type))))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        return new ProductionRouteSnapshot(
            externalCommandCallers,
            agentCommandLiterals,
            directApplicationCallers,
            runtimeTransportFactories,
            verifierTransportFactories,
            realTransportCalls);
    }

    private static bool ProductionRouteIsExact(ProductionRouteSnapshot value) =>
        value.ExternalCommandCallers.SequenceEqual(
            ["AgenticPrReview.Runtime.RuntimeApplication"],
            StringComparer.Ordinal) &&
        value.AgentCommandLiterals.SequenceEqual(
            ["review-live-agent-r3"],
            StringComparer.Ordinal) &&
        value.DirectApplicationCallers.SequenceEqual(
            ["AgenticPrReview.Runtime.LiveAgentFreshProcessCommand"],
            StringComparer.Ordinal) &&
        value.RuntimeTransportFactories.SequenceEqual(
            [
                "AgenticPrReview.Runtime.LiveAgentFreshProcessDeterministicTransportFactory",
                "AgenticPrReview.Runtime.R3LiveAgentTransportFactory",
            ],
            StringComparer.Ordinal) &&
        value.VerifierTransportFactories.SequenceEqual(
            [
                "AgenticPrReview.Runtime.LiveAgentVerifierFixture.VerifierTransportFactory",
            ],
            StringComparer.Ordinal) &&
        value.RealTransportCalls.SequenceEqual(
            ["AgenticPrReview.Runtime.R3LiveAgentTransportFactory"],
            StringComparer.Ordinal);

    private static string RootTypeName(Type type)
    {
        while (type.DeclaringType is not null)
        {
            type = type.DeclaringType;
        }

        return type.FullName!;
    }

    private static IEnumerable<string> ResolveMethodBodyStrings(MethodBase method)
    {
        var body = method.GetMethodBody();
        if (body is null)
        {
            yield break;
        }

        var il = body.GetILAsByteArray()!;
        var offset = 0;
        while (offset < il.Length)
        {
            var opCode = ReadOpCode(il, ref offset);
            var operandOffset = offset;
            var operandSize = OperandSize(opCode.OperandType, il, offset);
            if (opCode == OpCodes.Ldstr)
            {
                yield return method.Module.ResolveString(
                    BitConverter.ToInt32(il, operandOffset));
            }

            offset += operandSize;
        }
    }

    private sealed record ProductionRouteSnapshot(
        string[] ExternalCommandCallers,
        string[] AgentCommandLiterals,
        string[] DirectApplicationCallers,
        string[] RuntimeTransportFactories,
        string[] VerifierTransportFactories,
        string[] RealTransportCalls);
}

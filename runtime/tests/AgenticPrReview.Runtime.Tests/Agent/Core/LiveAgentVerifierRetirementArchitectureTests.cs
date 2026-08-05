using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
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
            FormatSnapshot(snapshot));
    }

    [Fact]
    public void LiveAgentVerifierProductionRouteOracleRejectsRestorationMutations()
    {
        var valid = ReadProductionRouteSnapshot();
        Assert.True(ProductionRouteIsExact(valid), FormatSnapshot(valid));

        var mutationTypes = IncludeTypeAndNestedTypesRecursively(
            typeof(RouteObservationMutationFixture));
        var observedMutationCalls = ReadCallEdges(mutationTypes).ToArray();
        var duplicateTransportCalls = observedMutationCalls
            .Where(call => call.LogicalCaller.Name ==
                    nameof(RouteObservationMutationFixture.DuplicateTransportCalls) &&
                call.Target.DeclaringType == typeof(DeepSeekTransport) &&
                call.Target.Name == nameof(DeepSeekTransport.Create))
            .Select(ToCallEdge)
            .ToArray();
        var secondMethodTransportCall = observedMutationCalls
            .Where(call => call.LogicalCaller.Name ==
                    nameof(RouteObservationMutationFixture.SecondTransportCall) &&
                call.Target.DeclaringType == typeof(DeepSeekTransport) &&
                call.Target.Name == nameof(DeepSeekTransport.Create))
            .Select(ToCallEdge)
            .ToArray();
        var duplicateApplicationCalls = observedMutationCalls
            .Where(call => call.LogicalCaller.Name ==
                    nameof(RouteObservationMutationFixture.DuplicateApplicationCallsAsync) &&
                call.Target.DeclaringType == typeof(R3LiveAgentApplication))
            .Select(ToCallEdge)
            .ToArray();
        var entrypointAliasLiterals = ReadLogicalMethodStrings(GetMethod(
            typeof(RouteObservationMutationFixture),
            nameof(RouteObservationMutationFixture.NormalizeRetiredEntrypointAlias),
            typeof(string[]),
            typeof(TextWriter),
            typeof(TextWriter)));
        var applicationAliasLiterals = ReadLogicalMethodStrings(GetMethod(
            typeof(RouteObservationMutationFixture),
            nameof(RouteObservationMutationFixture.NormalizeOutOfPrefixApplicationAlias),
            typeof(string[]),
            typeof(TextWriter),
            typeof(TextWriter)));

        Assert.Equal(2, duplicateTransportCalls.Length);
        Assert.Single(secondMethodTransportCall);
        Assert.Equal(3, duplicateApplicationCalls.Length);
        Assert.Contains("review-live", entrypointAliasLiterals);
        Assert.Contains("agent-r3", applicationAliasLiterals);

        var mutations = new[]
        {
            valid with
            {
                ExternalCommandCalls =
                [.. valid.ExternalCommandCalls, valid.ExternalCommandCalls[0]],
            },
            valid with
            {
                EntrypointLiterals =
                [
                    .. valid.EntrypointLiterals,
                    .. entrypointAliasLiterals.Where(value =>
                        StringComparer.Ordinal.Equals(value, "review-live")),
                ],
            },
            valid with
            {
                ApplicationCommandLiterals =
                [
                    .. valid.ApplicationCommandLiterals,
                    .. applicationAliasLiterals.Where(value =>
                        StringComparer.Ordinal.Equals(value, "agent-r3")),
                ],
            },
            valid with
            {
                DirectApplicationCalls =
                [.. valid.DirectApplicationCalls, .. duplicateApplicationCalls],
            },
            valid with
            {
                DirectApplicationCalls =
                [
                    valid.DirectApplicationCalls[0] with
                    {
                        CallerMethod = "SecondCompositionAsync",
                    },
                    .. valid.DirectApplicationCalls[1..],
                ],
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
                [.. valid.RealTransportCalls, .. duplicateTransportCalls],
            },
            valid with
            {
                RealTransportCalls =
                [.. valid.RealTransportCalls, .. secondMethodTransportCall],
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

        var externalCommandCalls = ReadCallEdges(runtimeTypes)
            .Where(edge => !freshProcessFamily.Contains(edge.PhysicalCallerType) &&
                edge.Target.DeclaringType == typeof(LiveAgentFreshProcessCommand))
            .Select(ToCallEdge)
            .OrderBy(CallEdgeSortKey, StringComparer.Ordinal)
            .ToArray();

        var runtimeEntrypoint = GetMethod(
            typeof(RuntimeEntrypoint),
            nameof(RuntimeEntrypoint.RunAsync),
            typeof(string[]),
            typeof(TextWriter),
            typeof(TextWriter),
            typeof(Func<RuntimeApplication>));
        var applicationRun = GetMethod(
            typeof(RuntimeApplication),
            nameof(RuntimeApplication.RunAsync),
            typeof(string[]),
            typeof(TextWriter),
            typeof(TextWriter));

        var directApplicationCalls = ReadCallEdges(runtimeTypes)
            .Where(edge => !r3ApplicationFamily.Contains(edge.PhysicalCallerType) &&
                edge.Target.DeclaringType == typeof(R3LiveAgentApplication))
            .Select(ToCallEdge)
            .OrderBy(CallEdgeSortKey, StringComparer.Ordinal)
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

        var realTransportCalls = ReadCallEdges(runtimeTypes)
            .Where(edge => edge.Target.DeclaringType == typeof(DeepSeekTransport) &&
                edge.Target.Name == nameof(DeepSeekTransport.Create))
            .Select(ToCallEdge)
            .OrderBy(CallEdgeSortKey, StringComparer.Ordinal)
            .ToArray();

        return new ProductionRouteSnapshot(
            externalCommandCalls,
            ReadLogicalMethodStrings(runtimeEntrypoint),
            ReadLogicalMethodStrings(applicationRun),
            directApplicationCalls,
            runtimeTransportFactories,
            verifierTransportFactories,
            realTransportCalls);
    }

    private static bool ProductionRouteIsExact(ProductionRouteSnapshot value)
    {
        var runtimeApplicationRun = GetMethod(
            typeof(RuntimeApplication),
            nameof(RuntimeApplication.RunAsync),
            typeof(string[]),
            typeof(TextWriter),
            typeof(TextWriter));
        var freshProcessRun = GetMethod(
            typeof(LiveAgentFreshProcessCommand),
            nameof(LiveAgentFreshProcessCommand.RunAsync),
            typeof(string[]),
            typeof(CancellationToken));
        var approvedFreshProcessRun = GetMethod(
            typeof(LiveAgentFreshProcessCommand),
            nameof(LiveAgentFreshProcessCommand.RunAsync),
            typeof(string),
            typeof(ILiveAgentFreshProcessFileSystem),
            typeof(CancellationToken),
            typeof(ILiveAgentFreshProcessProfile));
        var applicationConstructor = typeof(R3LiveAgentApplication).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(R3LiveAgentDependencies)],
            modifiers: null)!;
        var r3ApplicationRun = GetMethod(
            typeof(R3LiveAgentApplication),
            nameof(R3LiveAgentApplication.RunAsync),
            typeof(R3LiveAgentRequest),
            typeof(CancellationToken));
        var transportFactoryCreate = GetMethod(
            typeof(R3LiveAgentTransportFactory),
            nameof(R3LiveAgentTransportFactory.Create),
            typeof(DeepSeekCredential));
        var transportCreate = GetMethod(
            typeof(DeepSeekTransport),
            nameof(DeepSeekTransport.Create),
            typeof(DeepSeekCredential));

        return value.ExternalCommandCalls.SequenceEqual(
                [ExpectedCall(runtimeApplicationRun, freshProcessRun, OpCodes.Call)]) &&
            value.EntrypointLiterals.SequenceEqual(
                ["APR_RUNTIME_INTERNAL: Runtime initialization failed."],
                StringComparer.Ordinal) &&
            value.ApplicationCommandLiterals.SequenceEqual(
                [
                    "APR_RUNTIME_INTERNAL",
                    "Runtime execution failed.",
                    "review-live-agent-r3",
                ],
                StringComparer.Ordinal) &&
            value.DirectApplicationCalls.SequenceEqual(
                [
                    ExpectedCall(
                        approvedFreshProcessRun,
                        r3ApplicationRun,
                        OpCodes.Call),
                    ExpectedCall(
                        approvedFreshProcessRun,
                        applicationConstructor,
                        OpCodes.Newobj),
                ]) &&
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
                [ExpectedCall(transportFactoryCreate, transportCreate, OpCodes.Call)]);
    }

    private static MethodInfo GetMethod(
        Type type,
        string name,
        params Type[] parameterTypes) =>
        type.GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.Static |
                BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            parameterTypes,
            modifiers: null)!;

    private static string[] ReadLogicalMethodStrings(MethodInfo logicalMethod)
    {
        var methods = new List<MethodBase> { logicalMethod };
        var stateMachine = logicalMethod
            .GetCustomAttribute<AsyncStateMachineAttribute>()?
            .StateMachineType;
        if (stateMachine is not null)
        {
            methods.AddRange(DeclaredExecutableMembers(stateMachine));
        }

        return methods
            .SelectMany(ResolveMethodBodyStrings)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<ObservedCall> ReadCallEdges(
        IEnumerable<Type> types) =>
        types.SelectMany(type => DeclaredExecutableMembers(type)
            .SelectMany(method => ResolveMethodBodyCalls(method)
                .Select(call => new ObservedCall(
                    type,
                    LogicalCaller(method),
                    call.Target,
                    call.OpCode))));

    private static MethodBase LogicalCaller(MethodBase physicalMethod)
    {
        var physicalType = physicalMethod.DeclaringType!;
        var owner = physicalType.DeclaringType;
        if (owner is null)
        {
            return physicalMethod;
        }

        return DeclaredExecutableMembers(owner)
            .OfType<MethodInfo>()
            .SingleOrDefault(method => method
                .GetCustomAttribute<AsyncStateMachineAttribute>()?
                .StateMachineType == physicalType)
            ?? physicalMethod;
    }

    private static IEnumerable<MethodBodyCall> ResolveMethodBodyCalls(
        MethodBase method)
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
            if (opCode.OperandType == OperandType.InlineMethod)
            {
                var token = BitConverter.ToInt32(il, operandOffset);
                MethodBase target;
                try
                {
                    target = method.Module.ResolveMethod(
                        token,
                        method.DeclaringType?.GetGenericArguments(),
                        method is MethodInfo info
                            ? info.GetGenericArguments()
                            : null)!;
                }
                catch (Exception exception)
                {
                    throw new Xunit.Sdk.XunitException(
                        $"IL method resolution failed for {method.DeclaringType?.FullName}.{method.Name}: {exception.GetType().Name}");
                }

                yield return new MethodBodyCall(target, opCode);
            }

            offset += operandSize;
        }
    }

    private static CallEdge ToCallEdge(ObservedCall call) =>
        CreateCallEdge(call.LogicalCaller, call.Target, call.OpCode);

    private static CallEdge ExpectedCall(
        MethodBase caller,
        MethodBase target,
        OpCode opCode) =>
        CreateCallEdge(caller, target, opCode);

    private static CallEdge CreateCallEdge(
        MethodBase caller,
        MethodBase target,
        OpCode opCode) =>
        new(
            caller.DeclaringType!.Assembly.GetName().Name!,
            caller.DeclaringType.FullName!,
            caller.Name,
            FormatRouteSignature(caller),
            opCode.Name!,
            target.DeclaringType!.Assembly.GetName().Name!,
            target.DeclaringType.FullName!,
            target.Name,
            FormatRouteSignature(target));

    private static string FormatRouteSignature(MethodBase method) =>
        string.Concat(
            method is MethodInfo info
                ? FormatRouteType(info.ReturnType)
                : "System.Void",
            "(",
            string.Join(
                ",",
                method.GetParameters().Select(parameter =>
                    FormatRouteType(parameter.ParameterType))),
            ")");

    private static string FormatRouteType(Type type)
    {
        if (type.IsArray)
        {
            return FormatRouteType(type.GetElementType()!) + "[]";
        }

        if (!type.IsGenericType)
        {
            return type.FullName ?? type.Name;
        }

        var genericName = type.GetGenericTypeDefinition().FullName!;
        return string.Concat(
            genericName[..genericName.IndexOf('`')],
            "<",
            string.Join(",", type.GetGenericArguments().Select(FormatRouteType)),
            ">");
    }

    private static string CallEdgeSortKey(CallEdge edge) =>
        string.Join(
            "|",
            edge.CallerAssembly,
            edge.CallerType,
            edge.CallerMethod,
            edge.CallerSignature,
            edge.OpCode,
            edge.TargetAssembly,
            edge.TargetType,
            edge.TargetMember,
            edge.TargetSignature);

    private static string FormatSnapshot(ProductionRouteSnapshot snapshot) =>
        string.Join(
            Environment.NewLine,
            [
                "External command calls:",
                .. snapshot.ExternalCommandCalls.Select(CallEdgeSortKey),
                "Entrypoint literals:",
                .. snapshot.EntrypointLiterals,
                "Application command literals:",
                .. snapshot.ApplicationCommandLiterals,
                "Direct application calls:",
                .. snapshot.DirectApplicationCalls.Select(CallEdgeSortKey),
                "Runtime transport factories:",
                .. snapshot.RuntimeTransportFactories,
                "Verifier transport factories:",
                .. snapshot.VerifierTransportFactories,
                "Real transport calls:",
                .. snapshot.RealTransportCalls.Select(CallEdgeSortKey),
            ]);

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

    private sealed record MethodBodyCall(MethodBase Target, OpCode OpCode);

    private sealed record ObservedCall(
        Type PhysicalCallerType,
        MethodBase LogicalCaller,
        MethodBase Target,
        OpCode OpCode);

    private sealed record CallEdge(
        string CallerAssembly,
        string CallerType,
        string CallerMethod,
        string CallerSignature,
        string OpCode,
        string TargetAssembly,
        string TargetType,
        string TargetMember,
        string TargetSignature);

    private sealed record ProductionRouteSnapshot(
        CallEdge[] ExternalCommandCalls,
        string[] EntrypointLiterals,
        string[] ApplicationCommandLiterals,
        CallEdge[] DirectApplicationCalls,
        string[] RuntimeTransportFactories,
        string[] VerifierTransportFactories,
        CallEdge[] RealTransportCalls);

    private static class RouteObservationMutationFixture
    {
        internal static void DuplicateTransportCalls(
            DeepSeekCredential credential)
        {
            _ = DeepSeekTransport.Create(credential);
            _ = DeepSeekTransport.Create(credential);
        }

        internal static void SecondTransportCall(DeepSeekCredential credential) =>
            _ = DeepSeekTransport.Create(credential);

        internal static async Task DuplicateApplicationCallsAsync(
            R3LiveAgentDependencies dependencies,
            R3LiveAgentRequest request,
            CancellationToken cancellationToken)
        {
            var application = new R3LiveAgentApplication(dependencies);
            _ = await application.RunAsync(request, cancellationToken);
            _ = await application.RunAsync(request, cancellationToken);
        }

        internal static Task<int> NormalizeRetiredEntrypointAlias(
            string[] args,
            TextWriter stdout,
            TextWriter stderr)
        {
            var normalized = args.ToArray();
            if (normalized.Length > 0 && StringComparer.Ordinal.Equals(
                    normalized[0],
                    "review-live"))
            {
                normalized[0] = "review-live-agent-r3";
            }

            return RuntimeEntrypoint.RunAsync(normalized, stdout, stderr);
        }

        internal static Task<int> NormalizeOutOfPrefixApplicationAlias(
            string[] args,
            TextWriter stdout,
            TextWriter stderr)
        {
            var normalized = args.ToArray();
            if (normalized.Length > 0 && StringComparer.Ordinal.Equals(
                    normalized[0],
                    "agent-r3"))
            {
                normalized[0] = "review-live-agent-r3";
            }

            return new RuntimeApplication().RunAsync(
                normalized,
                stdout,
                stderr);
        }
    }
}

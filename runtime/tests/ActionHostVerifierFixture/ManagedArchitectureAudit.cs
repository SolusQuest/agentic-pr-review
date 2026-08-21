using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace AgenticPrReview.Runtime.ActionHostVerifierFixture;

internal static class ManagedArchitectureAudit
{
    private const string Kind = "apr-r4-e2-managed-architecture-v1";
    private const long MaximumAssemblyBytes = 64 * 1024 * 1024;
    private const string FixtureAssemblyName =
        "AgenticPrReview.Runtime.ActionHostVerifierFixture";
    private const string RuntimeAssemblyName = "AgenticPrReview.Runtime";
    private static readonly MetadataTypeNameProvider TypeNames = new();

    private static readonly string[] RequiredFixtureTypes =
    [
        "AgenticPrReview.Runtime.ActionHostVerifierFixture.AotProof",
        "AgenticPrReview.Runtime.ActionHostVerifierFixture.FrameworkCanaryCapture",
        "AgenticPrReview.Runtime.ActionHostVerifierFixture.FrameworkGitHubHandler",
        "AgenticPrReview.Runtime.ActionHostVerifierFixture.FrameworkHost",
        "AgenticPrReview.Runtime.ActionHostVerifierFixture.FrameworkIndentedJsonContext",
        "AgenticPrReview.Runtime.ActionHostVerifierFixture.FrameworkJsonContext",
        "AgenticPrReview.Runtime.ActionHostVerifierFixture.FrameworkProviderHandler",
        "AgenticPrReview.Runtime.ActionHostVerifierFixture.FrameworkStateDependencies",
        "AgenticPrReview.Runtime.ActionHostVerifierFixture.FrameworkSupervisor",
        "AgenticPrReview.Runtime.ActionHostVerifierFixture.ManagedArchitectureAudit",
        "AgenticPrReview.Runtime.ActionHostVerifierFixture.SyntheticOfficialPlatform",
    ];

    private static readonly string[] RequiredRuntimeTypes =
    [
        "AgenticPrReview.Runtime.ActionHost.ActionHostComposition",
        "AgenticPrReview.Runtime.ActionHost.ActionHostCoordinator",
        "AgenticPrReview.Runtime.ActionHost.Authorization.ActionHostEventJsonContext",
        "AgenticPrReview.Runtime.ActionHost.GitHub.ActionHostGitHubAuthorizationTransportFactory",
        "AgenticPrReview.Runtime.ActionHost.GitHub.ActionHostGitHubJsonContext",
        "AgenticPrReview.Runtime.ActionHost.GitHub.ActionHostGitObjectJsonContext",
        "AgenticPrReview.Runtime.ActionHost.GitHub.ActionHostReviewedSnapshotJsonContext",
        "AgenticPrReview.Runtime.ActionHost.Policy.ActionHostTrustedPolicyJsonContext",
        "AgenticPrReview.Runtime.ActionHost.ActionHostDeepSeekProviderRunnerFactory",
        "AgenticPrReview.Runtime.ActionHost.Serialization.ActionHostJsonContext",
        "AgenticPrReview.Runtime.Agent.Session.AgentSessionJsonContext",
        "AgenticPrReview.Runtime.Agent.Tools.AgentToolJsonContext",
        "AgenticPrReview.Runtime.LiveAgentFreshProcessJsonContext",
        "AgenticPrReview.Runtime.Host.Publishing.GitHub.Common.BoundedGitHubPublisherJsonContext",
        "AgenticPrReview.Runtime.Host.Publishing.GitHub.Common.BoundedGitHubPublisherTransportFactory",
        "AgenticPrReview.Runtime.Host.Publishing.GitHub.Inline.InlineCommentJsonContext",
        "AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky.StickyCommentJsonContext",
        "AgenticPrReview.Runtime.Host.State.GitHubArtifacts.ArtifactBridgeJsonContext",
        "AgenticPrReview.Runtime.Host.State.GitHubArtifacts.GitHubArtifactRestrictedStateStore",
        "AgenticPrReview.Runtime.Host.State.Restore.AcceptedStateProductionDependencies",
        "AgenticPrReview.Runtime.Host.State.RestrictedStateTransactions.RestrictedStateTransactionIndexJsonContext",
        "AgenticPrReview.Runtime.RuntimeJsonContext",
    ];

    private static readonly string[] ForbiddenAssemblies =
    [
        "AgenticPrReview.CanonicalOracle",
        "AgenticPrReview.SharedUtilities",
        "AgenticPrReview.TypeScriptHost",
    ];

    private static readonly string[] ForbiddenTypes =
    [
        "AgenticPrReview.Runtime.CanonicalOracle",
        "AgenticPrReview.Runtime.RootReviewDto",
        "AgenticPrReview.Runtime.SharedUtilities",
        "AgenticPrReview.Runtime.TypeScriptHost",
    ];

    private static readonly string[] RequiredFixtureConstructorTargets =
    [
        "AgenticPrReview.Runtime.ActionHost.ActionHostDeepSeekProviderRunnerFactory",
        "AgenticPrReview.Runtime.ActionHost.GitHub.ActionHostGitHubAuthorizationTransportFactory",
        "AgenticPrReview.Runtime.Host.Publishing.GitHub.Common.BoundedGitHubPublisherTransportFactory",
        "AgenticPrReview.Runtime.Host.State.Restore.AcceptedStateProductionDependencies",
    ];

    private static readonly ManagedCallSite[] RequiredFixtureCalls =
    [
        new(FixtureAssemblyName,
            FixtureAssemblyName + ".FrameworkHost+<RunAsync>d__1",
            "MoveNext", "instance arity=0 System.Void()",
            RuntimeAssemblyName,
            RequiredFixtureConstructorTargets[0], ".ctor",
            "instance arity=0 System.Void([System.Runtime]System.Func`2<[AgenticPrReview.Runtime]AgenticPrReview.Runtime.Execution.DeepSeek.DeepSeekCredential,[AgenticPrReview.Runtime]AgenticPrReview.Runtime.Execution.DeepSeek.DeepSeekTransport>)",
            ILOpCode.Newobj),
        new(FixtureAssemblyName,
            FixtureAssemblyName + ".FrameworkHost+<RunAsync>d__1",
            "MoveNext", "instance arity=0 System.Void()",
            RuntimeAssemblyName,
            RequiredFixtureConstructorTargets[1], ".ctor",
            "instance arity=0 System.Void([System.Runtime]System.Func`1<[System.Net.Http]System.Net.Http.HttpMessageHandler>)",
            ILOpCode.Newobj),
        new(FixtureAssemblyName,
            FixtureAssemblyName + ".FrameworkHost+<RunAsync>d__1",
            "MoveNext", "instance arity=0 System.Void()",
            RuntimeAssemblyName,
            RequiredFixtureConstructorTargets[2], ".ctor",
            "instance arity=0 System.Void([System.Runtime]System.Func`1<[System.Net.Http]System.Net.Http.HttpMessageHandler>)",
            ILOpCode.Newobj),
        new(FixtureAssemblyName,
            FixtureAssemblyName + ".FrameworkStateDependencies",
            ".ctor",
            "instance arity=0 System.Void(System.String,[AgenticPrReview.Runtime]AgenticPrReview.Runtime.ActionHost.GitHub.IActionHostGitObjectTransportFactory)",
            RuntimeAssemblyName,
            RequiredFixtureConstructorTargets[3], ".ctor",
            "instance arity=0 System.Void([AgenticPrReview.Runtime]AgenticPrReview.Runtime.ActionHost.GitHub.IActionHostGitObjectTransportFactory)",
            ILOpCode.Newobj),
        new(FixtureAssemblyName,
            FixtureAssemblyName + ".FrameworkStateDependencies",
            "CreateAncestryTransport",
            "instance arity=0 [AgenticPrReview.Runtime]AgenticPrReview.Runtime.ActionHost.GitHub.IActionHostGitObjectTransport([AgenticPrReview.Runtime]AgenticPrReview.Runtime.ActionHost.Contracts.ActionHostGitHubToken)",
            RuntimeAssemblyName, RequiredFixtureConstructorTargets[3],
            "CreateAncestryTransport",
            "instance arity=0 [AgenticPrReview.Runtime]AgenticPrReview.Runtime.ActionHost.GitHub.IActionHostGitObjectTransport([AgenticPrReview.Runtime]AgenticPrReview.Runtime.ActionHost.Contracts.ActionHostGitHubToken)",
            ILOpCode.Callvirt),
        new(FixtureAssemblyName,
            FixtureAssemblyName + ".FrameworkStateDependencies",
            "CreateArtifactStore",
            "instance arity=0 [AgenticPrReview.Runtime]AgenticPrReview.Runtime.Host.State.OpaqueStore.IRestrictedStateStore([AgenticPrReview.Runtime]AgenticPrReview.Runtime.ActionHost.Contracts.ActionHostLaunchContract)",
            RuntimeAssemblyName, RequiredFixtureConstructorTargets[3],
            "CreateArtifactStore",
            "instance arity=0 [AgenticPrReview.Runtime]AgenticPrReview.Runtime.Host.State.OpaqueStore.IRestrictedStateStore([AgenticPrReview.Runtime]AgenticPrReview.Runtime.ActionHost.Contracts.ActionHostLaunchContract)",
            ILOpCode.Callvirt),
    ];

    internal static bool TryAudit(
        string fixtureAssembly,
        string runtimeAssembly,
        out string digest)
    {
        digest = string.Empty;
        try
        {
            var fixture = Read(fixtureAssembly);
            var runtime = Read(runtimeAssembly);
            if (fixture.AssemblyName != FixtureAssemblyName ||
                runtime.AssemblyName != RuntimeAssemblyName ||
                !fixture.AssemblyReferences.Contains(
                    RuntimeAssemblyName) ||
                !RequiredFixtureTypes.All(fixture.TypeDefinitions.Contains) ||
                !RequiredRuntimeTypes.All(runtime.TypeDefinitions.Contains) ||
                !ConstructorRoutesValid(fixture.CallSites) ||
                ForbiddenAssemblies.Any(name =>
                    fixture.AssemblyReferences.Contains(name) ||
                    runtime.AssemblyReferences.Contains(name)) ||
                ForbiddenTypes.Any(name =>
                    fixture.AllTypes.Contains(name) ||
                    runtime.AllTypes.Contains(name)))
            {
                return false;
            }

            var framing = new StringBuilder(Kind).Append('\n')
                .Append("fixture-assembly:").Append(fixture.AssemblyName)
                .Append('\n')
                .Append("runtime-assembly:").Append(runtime.AssemblyName)
                .Append('\n')
                .Append("fixture-reference:").Append(RuntimeAssemblyName)
                .Append('\n');
            foreach (var type in RequiredFixtureTypes)
            {
                framing.Append("fixture-type:").Append(type).Append('\n');
            }
            foreach (var type in RequiredRuntimeTypes)
            {
                framing.Append("runtime-type:").Append(type).Append('\n');
            }
            foreach (var route in fixture.CallSites
                         .Where(call => RequiredFixtureConstructorTargets
                             .Contains(call.TargetType,
                                 StringComparer.Ordinal))
                         .OrderBy(call => call.Canonical,
                             StringComparer.Ordinal))
            {
                framing.Append("fixture-call:").Append(route.Canonical)
                    .Append('\n');
            }
            foreach (var assembly in ForbiddenAssemblies)
            {
                framing.Append("forbidden-assembly:").Append(assembly)
                    .Append('\n');
            }
            foreach (var type in ForbiddenTypes)
            {
                framing.Append("forbidden-type:").Append(type).Append('\n');
            }

            digest = Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes(framing.ToString())))
                .ToLowerInvariant();
            return true;
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or BadImageFormatException or
            InvalidOperationException or InvalidDataException)
        {
            return false;
        }
    }

    internal static bool TryAuditForTesting(
        string assembly,
        IReadOnlyCollection<string> requiredTypes,
        IReadOnlyCollection<string> forbiddenTypes,
        string targetType,
        int expectedCallCount)
    {
        try
        {
            var metadata = Read(assembly);
            return requiredTypes.All(metadata.TypeDefinitions.Contains) &&
                !forbiddenTypes.Any(metadata.AllTypes.Contains) &&
                metadata.CallSites.Count(call =>
                    call.TargetType == targetType) == expectedCallCount;
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or BadImageFormatException or
            InvalidOperationException or InvalidDataException)
        {
            return false;
        }
    }

    internal static IReadOnlyCollection<string> CallSitesForTesting(
        string assembly,
        IReadOnlyCollection<string> targetTypes) => Read(assembly).CallSites
            .Where(call => targetTypes.Contains(call.TargetType))
            .Select(call => call.Canonical)
            .Order(StringComparer.Ordinal)
            .ToArray();

    internal static bool TryDecodeInstructionsForTesting(
        string assembly,
        byte[] il)
    {
        try
        {
            using var stream = File.OpenRead(assembly);
            using var pe = new PEReader(stream,
                PEStreamOptions.PrefetchMetadata);
            _ = ReadMethodReferences(pe.GetMetadataReader(), il).ToArray();
            return true;
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or BadImageFormatException or
            InvalidOperationException or InvalidDataException)
        {
            return false;
        }
    }

    private static bool ConstructorRoutesValid(
        IReadOnlyCollection<ManagedCallSite> calls)
    {
        var relevant = calls.Where(call => RequiredFixtureConstructorTargets
                .Contains(call.TargetType, StringComparer.Ordinal))
            .Select(call => call.Canonical)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return relevant.SequenceEqual(RequiredFixtureCalls
                .Select(call => call.Canonical)
                .Order(StringComparer.Ordinal), StringComparer.Ordinal) &&
            RequiredFixtureConstructorTargets.All(target =>
                calls.Count(call => call.TargetType == target &&
                    call.TargetName == ".ctor" &&
                    call.OpCode == ILOpCode.Newobj) == 1);
    }

    private static ManagedAssemblyMetadata Read(string path)
    {
        var length = new FileInfo(path).Length;
        if (length is < 2 or > MaximumAssemblyBytes)
        {
            throw new BadImageFormatException();
        }

        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream,
            PEStreamOptions.PrefetchEntireImage);
        if (!pe.HasMetadata)
        {
            throw new BadImageFormatException();
        }

        var metadata = pe.GetMetadataReader();
        if (!metadata.IsAssembly)
        {
            throw new BadImageFormatException();
        }

        var definitions = metadata.TypeDefinitions
            .Select(handle => DefinitionName(metadata, handle))
            .ToHashSet(StringComparer.Ordinal);
        var references = metadata.TypeReferences
            .Select(handle => ReferenceName(metadata, handle))
            .ToHashSet(StringComparer.Ordinal);
        var assemblyReferences = metadata.AssemblyReferences
            .Select(handle => metadata.GetString(
                metadata.GetAssemblyReference(handle).Name))
            .ToHashSet(StringComparer.Ordinal);
        var callSites = ReadCallSites(pe, metadata).ToArray();
        return new ManagedAssemblyMetadata(
            metadata.GetString(metadata.GetAssemblyDefinition().Name),
            definitions,
            definitions.Concat(references).ToHashSet(StringComparer.Ordinal),
            assemblyReferences,
            callSites);
    }

    private static IEnumerable<ManagedCallSite> ReadCallSites(
        PEReader pe,
        MetadataReader metadata)
    {
        var assemblyName = AssemblyName(metadata);
        foreach (var typeHandle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(typeHandle);
            foreach (var methodHandle in type.GetMethods())
            {
                var method = metadata.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress == 0)
                {
                    continue;
                }

                var callerType = DefinitionName(metadata, typeHandle);
                var callerName = metadata.GetString(method.Name);
                var callerSignature = FormatSignature(
                    method.DecodeSignature(TypeNames, null));
                var il = pe.GetMethodBody(method.RelativeVirtualAddress)
                    .GetILBytes() ?? throw new InvalidDataException(
                        "The managed audit found a method without IL bytes.");
                foreach (var target in ReadMethodReferences(metadata, il))
                {
                    yield return new ManagedCallSite(
                        assemblyName,
                        callerType,
                        callerName,
                        callerSignature,
                        target.AssemblyName,
                        target.TypeName,
                        target.MethodName,
                        target.Signature,
                        target.OpCode);
                }
            }
        }
    }

    private static IEnumerable<ManagedMethodReference> ReadMethodReferences(
        MetadataReader metadata,
        byte[] il)
    {
        var offset = 0;
        while (offset < il.Length)
        {
            var opCode = ReadOpCode(il, ref offset);
            if (opCode == ILOpCode.Calli)
            {
                throw new InvalidDataException(
                    "Indirect managed audit calls are not permitted.");
            }

            var operandOffset = offset;
            var operandSize = OperandSize(opCode, il, offset);
            if (operandOffset + operandSize > il.Length)
            {
                throw new InvalidDataException(
                    "The managed audit found a truncated IL operand.");
            }

            if (IsMethodTokenOpCode(opCode))
            {
                var token = BitConverter.ToInt32(il, operandOffset);
                yield return ResolveMethod(metadata,
                    MetadataTokens.EntityHandle(token), opCode);
            }

            offset += operandSize;
        }
    }

    private static bool IsMethodTokenOpCode(ILOpCode opCode) => opCode is
        ILOpCode.Call or
        ILOpCode.Callvirt or
        ILOpCode.Jmp or
        ILOpCode.Newobj or
        ILOpCode.Ldftn or
        ILOpCode.Ldvirtftn;

    private static ManagedMethodReference ResolveMethod(
        MetadataReader metadata,
        EntityHandle handle,
        ILOpCode opCode) => handle.Kind switch
    {
        HandleKind.MemberReference => ResolveMemberReference(metadata,
            (MemberReferenceHandle)handle, opCode),
        HandleKind.MethodDefinition => ResolveMethodDefinition(metadata,
            (MethodDefinitionHandle)handle, opCode),
        HandleKind.MethodSpecification => ResolveMethodSpecification(metadata,
            (MethodSpecificationHandle)handle, opCode),
        _ => throw new InvalidDataException(
            "The managed audit found an unsupported method target."),
    };

    private static ManagedMethodReference ResolveMemberReference(
        MetadataReader metadata,
        MemberReferenceHandle handle,
        ILOpCode opCode)
    {
        var reference = metadata.GetMemberReference(handle);
        if (reference.GetKind() != MemberReferenceKind.Method)
        {
            throw new InvalidDataException(
                "The managed audit found a field token in a method position.");
        }

        var identity = TypeIdentity(metadata, reference.Parent);
        return new ManagedMethodReference(
            identity.AssemblyName,
            identity.TypeName,
            metadata.GetString(reference.Name),
            FormatSignature(reference.DecodeMethodSignature(
                TypeNames, null)),
            opCode);
    }

    private static ManagedMethodReference ResolveMethodDefinition(
        MetadataReader metadata,
        MethodDefinitionHandle handle,
        ILOpCode opCode)
    {
        var definition = metadata.GetMethodDefinition(handle);
        return new ManagedMethodReference(
            AssemblyName(metadata),
            DefinitionName(metadata, definition.GetDeclaringType()),
            metadata.GetString(definition.Name),
            FormatSignature(definition.DecodeSignature(
                TypeNames, null)),
            opCode);
    }

    private static ManagedMethodReference ResolveMethodSpecification(
        MetadataReader metadata,
        MethodSpecificationHandle handle,
        ILOpCode opCode)
    {
        var specification = metadata.GetMethodSpecification(handle);
        var method = ResolveMethod(metadata, specification.Method, opCode);
        var arguments = specification.DecodeSignature(
            TypeNames, null);
        return method with
        {
            Signature = string.Concat(method.Signature, "<",
                string.Join(',', arguments), ">"),
        };
    }

    private static ManagedTypeIdentity TypeIdentity(
        MetadataReader metadata,
        EntityHandle handle) => handle.Kind switch
    {
        HandleKind.TypeDefinition => new ManagedTypeIdentity(
            AssemblyName(metadata),
            DefinitionName(metadata, (TypeDefinitionHandle)handle)),
        HandleKind.TypeReference => new ManagedTypeIdentity(
            ResolutionAssembly(metadata,
                metadata.GetTypeReference((TypeReferenceHandle)handle)
                    .ResolutionScope),
            ReferenceName(metadata, (TypeReferenceHandle)handle)),
        HandleKind.TypeSpecification => new ManagedTypeIdentity(
            "<type-specification>",
            metadata.GetTypeSpecification((TypeSpecificationHandle)handle)
                .DecodeSignature(TypeNames, null)),
        _ => throw new InvalidDataException(
            "The managed audit found an unresolved metadata type."),
    };

    private static string ResolutionAssembly(
        MetadataReader metadata,
        EntityHandle scope) => scope.Kind switch
    {
        HandleKind.AssemblyReference => metadata.GetString(
            metadata.GetAssemblyReference((AssemblyReferenceHandle)scope).Name),
        HandleKind.ModuleDefinition => AssemblyName(metadata),
        HandleKind.TypeReference => ResolutionAssembly(metadata,
            metadata.GetTypeReference((TypeReferenceHandle)scope)
                .ResolutionScope),
        HandleKind.ModuleReference => string.Concat("<module:",
            metadata.GetString(metadata.GetModuleReference(
                (ModuleReferenceHandle)scope).Name), ">"),
        _ => throw new InvalidDataException(
            "The managed audit found an unresolved type scope."),
    };

    private static string DefinitionName(
        MetadataReader metadata,
        TypeDefinitionHandle handle)
    {
        var definition = metadata.GetTypeDefinition(handle);
        var name = metadata.GetString(definition.Name);
        return definition.GetDeclaringType().IsNil
            ? JoinName(metadata.GetString(definition.Namespace), name)
            : string.Concat(DefinitionName(metadata,
                definition.GetDeclaringType()), "+", name);
    }

    private static string ReferenceName(
        MetadataReader metadata,
        TypeReferenceHandle handle)
    {
        var reference = metadata.GetTypeReference(handle);
        var name = metadata.GetString(reference.Name);
        return reference.ResolutionScope.Kind == HandleKind.TypeReference
            ? string.Concat(ReferenceName(metadata,
                (TypeReferenceHandle)reference.ResolutionScope), "+", name)
            : JoinName(metadata.GetString(reference.Namespace), name);
    }

    private static string AssemblyName(MetadataReader metadata) =>
        metadata.IsAssembly
            ? metadata.GetString(metadata.GetAssemblyDefinition().Name)
            : throw new InvalidDataException(
                "The managed metadata does not define an assembly.");

    private static string FormatSignature(
        MethodSignature<string> signature) => string.Concat(
            signature.Header.IsInstance ? "instance" : "static",
            " arity=", signature.GenericParameterCount,
            " ", signature.ReturnType,
            "(", string.Join(',', signature.ParameterTypes), ")");

    private static ILOpCode ReadOpCode(byte[] il, ref int offset)
    {
        if (offset >= il.Length)
        {
            throw new InvalidDataException(
                "The managed audit found truncated IL.");
        }

        var first = il[offset++];
        var value = first == 0xfe
            ? offset < il.Length
                ? (ushort)(0xfe00 | il[offset++])
                : throw new InvalidDataException(
                    "The managed audit found a truncated IL opcode.")
            : first;
        var opCode = (ILOpCode)value;
        if (!Enum.IsDefined(opCode))
        {
            throw new InvalidDataException(
                string.Concat("Unknown IL opcode ", value.ToString("x4"), "."));
        }

        return opCode;
    }

    private static int OperandSize(
        ILOpCode opCode,
        byte[] il,
        int offset) => opCode switch
    {
        ILOpCode.Ldarg_s or ILOpCode.Ldarga_s or ILOpCode.Starg_s or
            ILOpCode.Ldloc_s or ILOpCode.Ldloca_s or ILOpCode.Stloc_s or
            ILOpCode.Ldc_i4_s or ILOpCode.Br_s or ILOpCode.Brfalse_s or
            ILOpCode.Brtrue_s or ILOpCode.Beq_s or ILOpCode.Bge_s or
            ILOpCode.Bgt_s or ILOpCode.Ble_s or ILOpCode.Blt_s or
            ILOpCode.Bne_un_s or ILOpCode.Bge_un_s or ILOpCode.Bgt_un_s or
            ILOpCode.Ble_un_s or ILOpCode.Blt_un_s or ILOpCode.Leave_s or
            ILOpCode.Unaligned => 1,
        ILOpCode.Ldarg or ILOpCode.Ldarga or ILOpCode.Starg or
            ILOpCode.Ldloc or ILOpCode.Ldloca or ILOpCode.Stloc => 2,
        ILOpCode.Ldc_i4 or ILOpCode.Ldc_r4 or ILOpCode.Jmp or
            ILOpCode.Call or ILOpCode.Calli or ILOpCode.Br or
            ILOpCode.Brfalse or ILOpCode.Brtrue or ILOpCode.Beq or
            ILOpCode.Bge or ILOpCode.Bgt or ILOpCode.Ble or ILOpCode.Blt or
            ILOpCode.Bne_un or ILOpCode.Bge_un or ILOpCode.Bgt_un or
            ILOpCode.Ble_un or ILOpCode.Blt_un or ILOpCode.Callvirt or
            ILOpCode.Cpobj or ILOpCode.Ldobj or ILOpCode.Ldstr or
            ILOpCode.Newobj or ILOpCode.Castclass or ILOpCode.Isinst or
            ILOpCode.Unbox or ILOpCode.Ldfld or ILOpCode.Ldflda or
            ILOpCode.Stfld or ILOpCode.Ldsfld or ILOpCode.Ldsflda or
            ILOpCode.Stsfld or ILOpCode.Stobj or ILOpCode.Box or
            ILOpCode.Newarr or ILOpCode.Ldelema or ILOpCode.Ldelem or
            ILOpCode.Stelem or ILOpCode.Unbox_any or ILOpCode.Refanyval or
            ILOpCode.Mkrefany or ILOpCode.Ldtoken or ILOpCode.Leave or
            ILOpCode.Ldftn or ILOpCode.Ldvirtftn or ILOpCode.Initobj or
            ILOpCode.Constrained or ILOpCode.Sizeof => 4,
        ILOpCode.Ldc_i8 or ILOpCode.Ldc_r8 => 8,
        ILOpCode.Switch => SwitchOperandSize(il, offset),
        _ => 0,
    };

    private static int SwitchOperandSize(byte[] il, int offset)
    {
        if (offset + sizeof(int) > il.Length)
        {
            throw new InvalidDataException(
                "The managed audit found a truncated switch operand.");
        }

        var count = BitConverter.ToInt32(il, offset);
        return count < 0
            ? throw new InvalidDataException(
                "The managed audit found an invalid switch operand.")
            : checked(sizeof(int) + count * sizeof(int));
    }

    private static string JoinName(string @namespace, string name) =>
        string.IsNullOrEmpty(@namespace)
            ? name
            : string.Concat(@namespace, ".", name);

    private sealed class MetadataTypeNameProvider :
        ISignatureTypeProvider<string, object?>
    {
        public string GetArrayType(
            string elementType,
            ArrayShape shape) => string.Concat(
                elementType, "[", new string(',', shape.Rank - 1), "]");

        public string GetByReferenceType(string elementType) =>
            string.Concat(elementType, "&");

        public string GetFunctionPointerType(
            MethodSignature<string> signature) => string.Concat(
                "<function-pointer:", FormatSignature(signature), ">");

        public string GetGenericInstantiation(
            string genericType,
            ImmutableArray<string> typeArguments) => string.Concat(
                genericType, "<", string.Join(',', typeArguments), ">");

        public string GetGenericMethodParameter(
            object? context,
            int index) => string.Concat("!!", index);

        public string GetGenericTypeParameter(
            object? context,
            int index) => string.Concat("!", index);

        public string GetModifiedType(
            string modifier,
            string unmodifiedType,
            bool isRequired) => string.Concat(
                isRequired ? "modreq(" : "modopt(",
                modifier, ")", unmodifiedType);

        public string GetPinnedType(string elementType) =>
            string.Concat(elementType, " pinned");

        public string GetPointerType(string elementType) =>
            string.Concat(elementType, "*");

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) =>
            string.Concat("System.", typeCode);

        public string GetSZArrayType(string elementType) =>
            string.Concat(elementType, "[]");

        public string GetTypeFromDefinition(
            MetadataReader reader,
            TypeDefinitionHandle handle,
            byte rawTypeKind) => Qualify(
                AssemblyName(reader), DefinitionName(reader, handle));

        public string GetTypeFromReference(
            MetadataReader reader,
            TypeReferenceHandle handle,
            byte rawTypeKind) => Qualify(
                ResolutionAssembly(reader,
                    reader.GetTypeReference(handle).ResolutionScope),
                ReferenceName(reader, handle));

        public string GetTypeFromSpecification(
            MetadataReader reader,
            object? context,
            TypeSpecificationHandle handle,
            byte rawTypeKind) => reader.GetTypeSpecification(handle)
                .DecodeSignature(this, context);

        private static string Qualify(string assembly, string type) =>
            string.Concat("[", assembly, "]", type);
    }

    private sealed record ManagedAssemblyMetadata(
        string AssemblyName,
        HashSet<string> TypeDefinitions,
        HashSet<string> AllTypes,
        HashSet<string> AssemblyReferences,
        IReadOnlyCollection<ManagedCallSite> CallSites);

    private sealed record ManagedTypeIdentity(
        string AssemblyName,
        string TypeName);

    private sealed record ManagedMethodReference(
        string AssemblyName,
        string TypeName,
        string MethodName,
        string Signature,
        ILOpCode OpCode);

    private sealed record ManagedCallSite(
        string CallerAssembly,
        string CallerType,
        string CallerName,
        string CallerSignature,
        string TargetAssembly,
        string TargetType,
        string TargetName,
        string TargetSignature,
        ILOpCode OpCode)
    {
        internal string Canonical => string.Join('|',
            CallerAssembly,
            string.Concat(CallerType, "::", CallerName),
            CallerSignature,
            OpCode.ToString(),
            TargetAssembly,
            string.Concat(TargetType, "::", TargetName),
            TargetSignature);
    }
}

using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace AgenticPrReview.Runtime.LiveAgentVerifierFixture;

internal sealed record VerifierArchitectureReceipt(
    string Kind,
    string ExecutionKind,
    string ExecutionArtifactSha256,
    string ArchitectureAssemblySha256,
    string BuildPairSha256,
    string Status,
    string AssemblySha256,
    bool ForbiddenReferencesAbsent,
    int RealTransportCreationCalls,
    int TransportFactoryTypes,
    int ProfileTypes,
    bool Passed,
    string ProcessInstanceSha256);

internal sealed record VerifierArchitectureTypeIdentity(
    string AssemblyName,
    string FullName,
    string DisplayName)
{
    internal bool HasSameDefinition(VerifierArchitectureTypeIdentity other) =>
        StringComparer.Ordinal.Equals(AssemblyName, other.AssemblyName) &&
        StringComparer.Ordinal.Equals(FullName, other.FullName);
}

internal sealed record VerifierArchitectureMethodReference(
    VerifierArchitectureTypeIdentity DeclaringType,
    string Name,
    string Signature,
    int GenericParameterCount,
    bool IsMethodSpecification,
    ILOpCode OpCode);

internal sealed record VerifierArchitectureTransportCall(
    VerifierArchitectureMethodReference Caller,
    VerifierArchitectureMethodReference Target);

internal static class VerifierArchitectureProof
{
    private const int MaximumAssemblyBytes = 64 * 1024 * 1024;
    private const string FixtureAssembly =
        "AgenticPrReview.Runtime.LiveAgentVerifierFixture";
    private const string RuntimeAssembly = "AgenticPrReview.Runtime";
    private const string TransportFactoryInterface =
        "AgenticPrReview.Runtime.IR3LiveAgentTransportFactory";
    private const string FreshProcessProfileInterface =
        "AgenticPrReview.Runtime.ILiveAgentFreshProcessProfile";
    private const string DeepSeekTransportType =
        "AgenticPrReview.Runtime.Execution.DeepSeek.DeepSeekTransport";
    private const string VerifierTransportFactoryType =
        "AgenticPrReview.Runtime.LiveAgentVerifierFixture.VerifierTransportFactory";
    private const string VerifierTransportFactoryCreateSignature =
        "instance[AgenticPrReview.Runtime]" +
        "AgenticPrReview.Runtime.Execution.DeepSeek.IDeepSeekTransport(" +
        "[AgenticPrReview.Runtime]" +
        "AgenticPrReview.Runtime.Execution.DeepSeek.DeepSeekCredential)";
    private const string CreateHandlerSignature =
        "static[System.Net.Http]System.Net.Http.SocketsHttpHandler(" +
        "[System.Runtime]System.TimeSpan," +
        "[System.Runtime]System.Func`3<" +
        "[System.Net.Http]System.Net.Http.SocketsHttpConnectionContext," +
        "[System.Runtime]System.Threading.CancellationToken," +
        "[System.Runtime]System.Threading.Tasks.ValueTask`1<" +
        "[System.Runtime]System.IO.Stream>>)";
    private const string CreateForTestingSignature =
        "static[AgenticPrReview.Runtime]" +
        "AgenticPrReview.Runtime.Execution.DeepSeek.DeepSeekTransport(" +
        "[AgenticPrReview.Runtime]" +
        "AgenticPrReview.Runtime.Execution.DeepSeek.DeepSeekCredential," +
        "[System.Net.Http]System.Net.Http.HttpMessageHandler," +
        "[System.Runtime]System.TimeSpan)";

    private static readonly HashSet<string> ForbiddenTypes = new(
        [
            "AgenticPrReview.Runtime.LiveRuntimeApplication",
            "AgenticPrReview.Runtime.ILiveProviderExecutor",
            "AgenticPrReview.Runtime.DeepSeekLiveProviderExecutor",
            "AgenticPrReview.Runtime.R3LiveAgentApplication",
            "AgenticPrReview.Runtime.Agent.Loop.AgentLoop",
            "AgenticPrReview.Runtime.Host.State.RestrictedStateService",
            "AgenticPrReview.Runtime.LiveAgentStateCommitCoordinator",
        ],
        StringComparer.Ordinal);

    internal static VerifierArchitectureReceipt Create(
        string assemblyPath,
        VerifierBuildPair buildPair,
        string processInstanceSha256)
    {
        var info = new FileInfo(assemblyPath);
        if (!info.Exists || info.Length is <= 0 or > MaximumAssemblyBytes)
        {
            throw new InvalidDataException(
                "The verifier architecture assembly is outside its byte bound.");
        }

        var assemblyBytes = File.ReadAllBytes(assemblyPath);
        var assemblySha256 = LiveAgentFreshProcessDomain.RawSha256(assemblyBytes);
        if (!StringComparer.Ordinal.Equals(
                assemblySha256,
                buildPair.ArchitectureAssemblySha256))
        {
            throw new InvalidDataException(
                "The verifier architecture assembly does not match its build pair.");
        }

        using var stream = new MemoryStream(assemblyBytes, writable: false);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata)
        {
            throw new BadImageFormatException(
                "The verifier architecture assembly has no metadata.");
        }

        var reader = pe.GetMetadataReader();
        var assemblyName = AssemblyName(reader);
        if (!StringComparer.Ordinal.Equals(assemblyName, FixtureAssembly))
        {
            throw new InvalidDataException(
                "The verifier architecture assembly identity is invalid.");
        }

        var typeReferences = reader.TypeReferences
            .Select(handle => FullName(reader, handle))
            .ToHashSet(StringComparer.Ordinal);
        var transportCalls = ReadTransportCalls(pe, reader).ToArray();
        var factoryTypes = CountImplementations(
            reader,
            new VerifierArchitectureTypeIdentity(
                RuntimeAssembly,
                TransportFactoryInterface,
                string.Concat(
                    "[",
                    RuntimeAssembly,
                    "]",
                    TransportFactoryInterface)));
        var profileTypes = CountImplementations(
            reader,
            new VerifierArchitectureTypeIdentity(
                RuntimeAssembly,
                FreshProcessProfileInterface,
                string.Concat(
                    "[",
                    RuntimeAssembly,
                    "]",
                    FreshProcessProfileInterface)));
        var forbiddenAbsent = !ForbiddenTypes.Overlaps(typeReferences);
        var passed = forbiddenAbsent &&
            TransportCallsValid(transportCalls) &&
            factoryTypes == 1 &&
            profileTypes == 1;
        return new VerifierArchitectureReceipt(
            "apr-r3-live-agent-architecture-receipt-v1",
            buildPair.ExecutionKind,
            buildPair.ExecutionArtifactSha256,
            buildPair.ArchitectureAssemblySha256,
            buildPair.BuildPairSha256,
            passed ? "passed" : "failed",
            assemblySha256,
            forbiddenAbsent,
            transportCalls.Length,
            factoryTypes,
            profileTypes,
            passed,
            processInstanceSha256);
    }

    internal static IReadOnlyList<string> DescribeTransportCallsForTesting(
        string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        return ReadTransportCalls(pe, reader)
            .Select(call => string.Concat(
                call.Caller.DeclaringType.DisplayName,
                ".",
                call.Caller.Name,
                " ",
                call.Caller.Signature,
                " -> ",
                call.Target.OpCode,
                " ",
                call.Target.DeclaringType.DisplayName,
                ".",
                call.Target.Name,
                " ",
                call.Target.Signature,
                call.Target.IsMethodSpecification ? " <method-spec>" : ""))
            .ToArray();
    }

    internal static IReadOnlyList<VerifierArchitectureTransportCall>
        ReadTransportCallsForTesting(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        return ReadTransportCalls(pe, pe.GetMetadataReader()).ToArray();
    }

    internal static bool TransportCallsValidForTesting(
        IReadOnlyCollection<VerifierArchitectureTransportCall> calls) =>
        TransportCallsValid(calls);

    internal static VerifierArchitectureMethodReference
        ReadSyntheticTransportReferenceForTesting(
            string assemblyPath,
            string methodName,
            ILOpCode opCode)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var handle = reader.MemberReferences.Single(candidate =>
        {
            var reference = reader.GetMemberReference(candidate);
            return reference.GetKind() == MemberReferenceKind.Method &&
                reader.GetString(reference.Name) == methodName &&
                TypeIdentity(reader, reference.Parent).FullName ==
                    DeepSeekTransportType;
        });
        var value = (ushort)opCode;
        var opCodeBytes = value > byte.MaxValue
            ? new[] { (byte)(value >> 8), (byte)value }
            : [(byte)value];
        var il = opCodeBytes.Concat(BitConverter.GetBytes(
            MetadataTokens.GetToken(handle))).ToArray();
        return ReadMethodReferences(reader, il).Single();
    }

    internal static int CountImplementationsForTesting(
        string assemblyPath,
        string targetAssembly,
        string targetFullName)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        return CountImplementations(
            pe.GetMetadataReader(),
            new VerifierArchitectureTypeIdentity(
                targetAssembly,
                targetFullName,
                string.Concat("[", targetAssembly, "]", targetFullName)));
    }

    private static int CountImplementations(
        MetadataReader reader,
        VerifierArchitectureTypeIdentity target)
    {
        var assemblyName = AssemblyName(reader);
        var definitions = reader.TypeDefinitions.ToDictionary(
            handle => FullName(reader, handle),
            handle => handle,
            StringComparer.Ordinal);
        return reader.TypeDefinitions.Count(handle =>
        {
            var definition = reader.GetTypeDefinition(handle);
            if ((definition.Attributes & TypeAttributes.Interface) != 0 ||
                (definition.Attributes & TypeAttributes.Abstract) != 0)
            {
                return false;
            }

            return Implements(
                reader,
                assemblyName,
                definitions,
                handle,
                target,
                []);
        });
    }

    private static bool Implements(
        MetadataReader reader,
        string assemblyName,
        IReadOnlyDictionary<string, TypeDefinitionHandle> definitions,
        TypeDefinitionHandle handle,
        VerifierArchitectureTypeIdentity target,
        HashSet<TypeDefinitionHandle> visiting)
    {
        if (!visiting.Add(handle))
        {
            throw new InvalidDataException(
                "The verifier contains a cyclic type hierarchy.");
        }

        try
        {
            var definition = reader.GetTypeDefinition(handle);
            foreach (var item in definition.GetInterfaceImplementations())
            {
                var implementation = reader.GetInterfaceImplementation(item);
                if (HierarchyTypeMatches(
                        reader,
                        assemblyName,
                        definitions,
                        implementation.Interface,
                        target,
                        visiting))
                {
                    return true;
                }
            }

            return !definition.BaseType.IsNil && HierarchyTypeMatches(
                reader,
                assemblyName,
                definitions,
                definition.BaseType,
                target,
                visiting);
        }
        finally
        {
            visiting.Remove(handle);
        }
    }

    private static bool HierarchyTypeMatches(
        MetadataReader reader,
        string assemblyName,
        IReadOnlyDictionary<string, TypeDefinitionHandle> definitions,
        EntityHandle handle,
        VerifierArchitectureTypeIdentity target,
        HashSet<TypeDefinitionHandle> visiting)
    {
        var identity = TypeIdentity(reader, handle);
        if (identity.HasSameDefinition(target))
        {
            return true;
        }

        if (!StringComparer.Ordinal.Equals(
                identity.AssemblyName,
                assemblyName))
        {
            return false;
        }

        if (!definitions.TryGetValue(identity.FullName, out var definition))
        {
            throw new InvalidDataException(
                "The verifier contains an unresolved local hierarchy type.");
        }

        return Implements(
            reader,
            assemblyName,
            definitions,
            definition,
            target,
            visiting);
    }

    private static IEnumerable<VerifierArchitectureTransportCall> ReadTransportCalls(
        PEReader pe,
        MetadataReader reader)
    {
        var assemblyName = AssemblyName(reader);
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress == 0)
                {
                    continue;
                }

                var caller = ResolveMethodDefinition(
                    reader,
                    methodHandle,
                    assemblyName);
                var il = pe.GetMethodBody(method.RelativeVirtualAddress)
                    .GetILBytes() ?? throw new InvalidDataException(
                        "The verifier contains a method without IL bytes.");
                foreach (var target in ReadMethodReferences(reader, il))
                {
                    if (target.DeclaringType.FullName == DeepSeekTransportType)
                    {
                        yield return new VerifierArchitectureTransportCall(
                            caller,
                            target);
                    }
                }
            }
        }
    }

    private static bool TransportCallsValid(
        IReadOnlyCollection<VerifierArchitectureTransportCall> calls) =>
        calls.Count == 2 &&
        calls.All(call =>
            call.Caller.DeclaringType.AssemblyName == FixtureAssembly &&
            call.Caller.DeclaringType.FullName == VerifierTransportFactoryType &&
            call.Caller.Name == "Create" &&
            call.Caller.Signature == VerifierTransportFactoryCreateSignature &&
            !call.Caller.IsMethodSpecification) &&
        calls.Count(call => TargetMatches(
            call.Target,
            "CreateHandler",
            CreateHandlerSignature)) == 1 &&
        calls.Count(call => TargetMatches(
            call.Target,
            "CreateForTesting",
            CreateForTestingSignature)) == 1;

    private static bool TargetMatches(
        VerifierArchitectureMethodReference target,
        string name,
        string signature) =>
        target.OpCode == ILOpCode.Call &&
        target.DeclaringType.AssemblyName == RuntimeAssembly &&
        target.DeclaringType.FullName == DeepSeekTransportType &&
        target.Name == name &&
        target.Signature == signature &&
        !target.IsMethodSpecification;

    private static IEnumerable<VerifierArchitectureMethodReference>
        ReadMethodReferences(
        MetadataReader reader,
        byte[] il)
    {
        var offset = 0;
        while (offset < il.Length)
        {
            var opCode = ReadOpCode(il, ref offset);
            if (opCode == ILOpCode.Calli)
            {
                throw new InvalidDataException(
                    "Indirect verifier calls are not permitted.");
            }

            var operandOffset = offset;
            var operandSize = OperandSize(opCode, il, offset);
            if (operandOffset + operandSize > il.Length)
            {
                throw new InvalidDataException(
                    "The verifier contains a truncated IL operand.");
            }

            if (IsMethodTokenOpCode(opCode))
            {
                var token = BitConverter.ToInt32(il, operandOffset);
                var handle = MetadataTokens.EntityHandle(token);
                if (opCode != ILOpCode.Ldtoken || IsMethodHandle(reader, handle))
                {
                    yield return ResolveCalledMethod(reader, handle, opCode);
                }
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
        ILOpCode.Ldvirtftn or
        ILOpCode.Ldtoken;

    private static bool IsMethodHandle(
        MetadataReader reader,
        EntityHandle handle) => handle.Kind switch
    {
        HandleKind.MethodDefinition or HandleKind.MethodSpecification => true,
        HandleKind.MemberReference => reader.GetMemberReference(
            (MemberReferenceHandle)handle).GetKind() ==
                MemberReferenceKind.Method,
        _ => false,
    };

    private static VerifierArchitectureMethodReference ResolveCalledMethod(
        MetadataReader reader,
        EntityHandle handle,
        ILOpCode opCode) => handle.Kind switch
    {
        HandleKind.MemberReference => ResolveMemberReference(
            reader,
            (MemberReferenceHandle)handle,
            opCode),
        HandleKind.MethodDefinition => ResolveMethodDefinition(
            reader,
            (MethodDefinitionHandle)handle,
            AssemblyName(reader)) with
            {
                OpCode = opCode,
            },
        HandleKind.MethodSpecification => ResolveMethodSpecification(
            reader,
            (MethodSpecificationHandle)handle,
            opCode),
        _ => throw new InvalidDataException(
            "The verifier contains an unsupported method target."),
    };

    private static VerifierArchitectureMethodReference ResolveMemberReference(
        MetadataReader reader,
        MemberReferenceHandle handle,
        ILOpCode opCode)
    {
        var reference = reader.GetMemberReference(handle);
        if (reference.GetKind() != MemberReferenceKind.Method)
        {
            throw new InvalidDataException(
                "The verifier contains a field token in a method position.");
        }

        var declaringType = TypeIdentity(reader, reference.Parent);
        var signature = reference.DecodeMethodSignature(
            new MetadataTypeIdentityProvider(),
            null);
        return new VerifierArchitectureMethodReference(
            declaringType,
            reader.GetString(reference.Name),
            FormatSignature(signature),
            signature.GenericParameterCount,
            IsMethodSpecification: false,
            opCode);
    }

    private static VerifierArchitectureMethodReference ResolveMethodDefinition(
        MetadataReader reader,
        MethodDefinitionHandle handle,
        string assemblyName)
    {
        var definition = reader.GetMethodDefinition(handle);
        var signature = definition.DecodeSignature(
            new MetadataTypeIdentityProvider(),
            null);
        var isStatic = (definition.Attributes & MethodAttributes.Static) != 0;
        if (isStatic == signature.Header.IsInstance)
        {
            throw new InvalidDataException(
                "The verifier contains an inconsistent method signature.");
        }

        return new VerifierArchitectureMethodReference(
            new VerifierArchitectureTypeIdentity(
                assemblyName,
                FullName(reader, definition.GetDeclaringType()),
                string.Concat(
                    "[",
                    assemblyName,
                    "]",
                    FullName(reader, definition.GetDeclaringType()))),
            reader.GetString(definition.Name),
            FormatSignature(signature),
            signature.GenericParameterCount,
            IsMethodSpecification: false,
            ILOpCode.Nop);
    }

    private static VerifierArchitectureMethodReference ResolveMethodSpecification(
        MetadataReader reader,
        MethodSpecificationHandle handle,
        ILOpCode opCode)
    {
        var specification = reader.GetMethodSpecification(handle);
        var method = ResolveCalledMethod(reader, specification.Method, opCode);
        var arguments = specification.DecodeSignature(
            new MetadataTypeIdentityProvider(),
            null);
        if (method.GenericParameterCount != arguments.Length)
        {
            throw new InvalidDataException(
                "The verifier contains an invalid method specification.");
        }

        return method with { IsMethodSpecification = true };
    }

    private static string FormatSignature(
        MethodSignature<VerifierArchitectureTypeIdentity> signature) =>
        string.Concat(
            signature.Header.IsInstance ? "instance" : "static",
            signature.ReturnType.DisplayName,
            "(",
            string.Join(',', signature.ParameterTypes.Select(
                parameter => parameter.DisplayName)),
            ")");

    private static ILOpCode ReadOpCode(byte[] il, ref int offset)
    {
        if (offset >= il.Length)
        {
            throw new InvalidDataException("The verifier contains truncated IL.");
        }

        var first = il[offset++];
        var value = first == 0xfe
            ? offset < il.Length
                ? (ushort)(0xfe00 | il[offset++])
                : throw new InvalidDataException(
                    "The verifier contains a truncated IL opcode.")
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
                "The verifier contains a truncated switch operand.");
        }

        var count = BitConverter.ToInt32(il, offset);
        return count < 0
            ? throw new InvalidDataException(
                "The verifier contains an invalid switch operand.")
            : checked(sizeof(int) + count * sizeof(int));
    }

    private static string FullName(
        MetadataReader reader,
        TypeReferenceHandle handle)
    {
        var reference = reader.GetTypeReference(handle);
        var name = reader.GetString(reference.Name);
        return reference.ResolutionScope.Kind == HandleKind.TypeReference
            ? string.Concat(
                FullName(reader, (TypeReferenceHandle)reference.ResolutionScope),
                "+",
                name)
            : JoinName(reader.GetString(reference.Namespace), name);
    }

    private static string FullName(
        MetadataReader reader,
        TypeDefinitionHandle handle)
    {
        var definition = reader.GetTypeDefinition(handle);
        var name = reader.GetString(definition.Name);
        return definition.GetDeclaringType().IsNil
            ? JoinName(reader.GetString(definition.Namespace), name)
            : string.Concat(
                FullName(reader, definition.GetDeclaringType()),
                "+",
                name);
    }

    private static string AssemblyName(MetadataReader reader) =>
        reader.IsAssembly
            ? reader.GetString(reader.GetAssemblyDefinition().Name)
            : throw new InvalidDataException(
                "The verifier metadata does not define an assembly.");

    private static VerifierArchitectureTypeIdentity TypeIdentity(
        MetadataReader reader,
        EntityHandle handle) => handle.Kind switch
    {
        HandleKind.TypeReference => TypeIdentity(
            reader,
            (TypeReferenceHandle)handle),
        HandleKind.TypeDefinition => DefinitionIdentity(
            reader,
            (TypeDefinitionHandle)handle),
        HandleKind.TypeSpecification => reader
            .GetTypeSpecification((TypeSpecificationHandle)handle)
            .DecodeSignature(new MetadataTypeIdentityProvider(), null),
        HandleKind.MethodDefinition => DefinitionIdentity(
            reader,
            reader.GetMethodDefinition((MethodDefinitionHandle)handle)
                .GetDeclaringType()),
        _ => throw new InvalidDataException(
            "The verifier contains an unresolved metadata type."),
    };

    private static VerifierArchitectureTypeIdentity DefinitionIdentity(
        MetadataReader reader,
        TypeDefinitionHandle handle)
    {
        var assemblyName = AssemblyName(reader);
        var fullName = FullName(reader, handle);
        return new VerifierArchitectureTypeIdentity(
            assemblyName,
            fullName,
            string.Concat("[", assemblyName, "]", fullName));
    }

    private static VerifierArchitectureTypeIdentity TypeIdentity(
        MetadataReader reader,
        TypeReferenceHandle handle)
    {
        var fullName = FullName(reader, handle);
        var assemblyName = ResolutionAssembly(
            reader,
            reader.GetTypeReference(handle).ResolutionScope);
        return new VerifierArchitectureTypeIdentity(
            assemblyName,
            fullName,
            string.Concat("[", assemblyName, "]", fullName));
    }

    private static string ResolutionAssembly(
        MetadataReader reader,
        EntityHandle scope) => scope.Kind switch
    {
        HandleKind.AssemblyReference => reader.GetString(
            reader.GetAssemblyReference((AssemblyReferenceHandle)scope).Name),
        HandleKind.ModuleDefinition => AssemblyName(reader),
        HandleKind.TypeReference => ResolutionAssembly(
            reader,
            reader.GetTypeReference((TypeReferenceHandle)scope)
                .ResolutionScope),
        HandleKind.ModuleReference => string.Concat(
            "<module:",
            reader.GetString(
                reader.GetModuleReference((ModuleReferenceHandle)scope).Name),
            ">"),
        _ => throw new InvalidDataException(
            "The verifier contains an unresolved type scope."),
    };

    private static string JoinName(string @namespace, string name) =>
        string.IsNullOrEmpty(@namespace)
            ? name
            : string.Concat(@namespace, ".", name);

    private sealed class MetadataTypeIdentityProvider :
        ISignatureTypeProvider<VerifierArchitectureTypeIdentity, object?>
    {
        private const string IntrinsicAssembly = "<intrinsic>";

        public VerifierArchitectureTypeIdentity GetArrayType(
            VerifierArchitectureTypeIdentity elementType,
            ArrayShape shape) => Decorate(
                elementType,
                string.Concat(
                    elementType.DisplayName,
                    "[",
                    new string(',', shape.Rank - 1),
                    "]"));

        public VerifierArchitectureTypeIdentity GetByReferenceType(
            VerifierArchitectureTypeIdentity elementType) => Decorate(
                elementType,
                string.Concat(elementType.DisplayName, "&"));

        public VerifierArchitectureTypeIdentity GetFunctionPointerType(
            MethodSignature<VerifierArchitectureTypeIdentity> signature) => new(
                IntrinsicAssembly,
                "<function-pointer>",
                string.Concat("<function-pointer:", FormatSignature(signature), ">"));

        public VerifierArchitectureTypeIdentity GetGenericInstantiation(
            VerifierArchitectureTypeIdentity genericType,
            ImmutableArray<VerifierArchitectureTypeIdentity> typeArguments) =>
            new(
                genericType.AssemblyName,
                genericType.FullName,
                string.Concat(
                    genericType.DisplayName,
                    "<",
                    string.Join(',', typeArguments.Select(
                        argument => argument.DisplayName)),
                    ">"));

        public VerifierArchitectureTypeIdentity GetGenericMethodParameter(
            object? context,
            int index) => Intrinsic(string.Concat("!!", index));

        public VerifierArchitectureTypeIdentity GetGenericTypeParameter(
            object? context,
            int index) => Intrinsic(string.Concat("!", index));

        public VerifierArchitectureTypeIdentity GetModifiedType(
            VerifierArchitectureTypeIdentity modifier,
            VerifierArchitectureTypeIdentity unmodifiedType,
            bool isRequired) => Decorate(
                unmodifiedType,
                string.Concat(
                    isRequired ? "modreq(" : "modopt(",
                    modifier.DisplayName,
                    ")",
                    unmodifiedType.DisplayName));

        public VerifierArchitectureTypeIdentity GetPinnedType(
            VerifierArchitectureTypeIdentity elementType) => Decorate(
                elementType,
                string.Concat(elementType.DisplayName, " pinned"));

        public VerifierArchitectureTypeIdentity GetPointerType(
            VerifierArchitectureTypeIdentity elementType) => Decorate(
                elementType,
                string.Concat(elementType.DisplayName, "*"));

        public VerifierArchitectureTypeIdentity GetPrimitiveType(
            PrimitiveTypeCode typeCode) => Intrinsic(
                string.Concat("System.", typeCode));

        public VerifierArchitectureTypeIdentity GetSZArrayType(
            VerifierArchitectureTypeIdentity elementType) => Decorate(
                elementType,
                string.Concat(elementType.DisplayName, "[]"));

        public VerifierArchitectureTypeIdentity GetTypeFromDefinition(
            MetadataReader reader,
            TypeDefinitionHandle handle,
            byte rawTypeKind) => DefinitionIdentity(reader, handle);

        public VerifierArchitectureTypeIdentity GetTypeFromReference(
            MetadataReader reader,
            TypeReferenceHandle handle,
            byte rawTypeKind) => TypeIdentity(reader, handle);

        public VerifierArchitectureTypeIdentity GetTypeFromSpecification(
            MetadataReader reader,
            object? context,
            TypeSpecificationHandle handle,
            byte rawTypeKind) => reader.GetTypeSpecification(handle)
                .DecodeSignature(this, context);

        private static VerifierArchitectureTypeIdentity Intrinsic(
            string name) => new(
            IntrinsicAssembly,
            name,
            name);

        private static VerifierArchitectureTypeIdentity Decorate(
            VerifierArchitectureTypeIdentity type,
            string displayName) => new(
                type.AssemblyName,
                type.FullName,
                displayName);
    }
}

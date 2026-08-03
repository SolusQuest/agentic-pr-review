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

internal static class VerifierArchitectureProof
{
    private const int MaximumAssemblyBytes = 64 * 1024 * 1024;
    private const string TransportFactoryInterface =
        "AgenticPrReview.Runtime.IR3LiveAgentTransportFactory";
    private const string FreshProcessProfileInterface =
        "AgenticPrReview.Runtime.ILiveAgentFreshProcessProfile";
    private const string DeepSeekTransportType =
        "AgenticPrReview.Runtime.Execution.DeepSeek.DeepSeekTransport";
    private const string VerifierTransportFactoryType =
        "AgenticPrReview.Runtime.LiveAgentVerifierFixture.VerifierTransportFactory";

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
        var typeReferences = reader.TypeReferences
            .Select(handle => FullName(reader, handle))
            .ToHashSet(StringComparer.Ordinal);
        var transportCalls = ReadTransportCalls(pe, reader).ToArray();
        var factoryTypes = CountImplementations(
            reader,
            TransportFactoryInterface);
        var profileTypes = CountImplementations(
            reader,
            FreshProcessProfileInterface);
        var forbiddenAbsent = !ForbiddenTypes.Overlaps(typeReferences);
        var passed = forbiddenAbsent &&
            transportCalls.Length == 2 &&
            transportCalls.All(call =>
                call.CallerType == VerifierTransportFactoryType &&
                call.CallerMethod == "Create") &&
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

    private static int CountImplementations(
        MetadataReader reader,
        string interfaceName) => reader.TypeDefinitions.Count(handle =>
    {
        var definition = reader.GetTypeDefinition(handle);
        if ((definition.Attributes & TypeAttributes.Interface) != 0 ||
            (definition.Attributes & TypeAttributes.Abstract) != 0)
        {
            return false;
        }

        return definition.GetInterfaceImplementations().Any(item =>
        {
            var implementation = reader.GetInterfaceImplementation(item);
            return StringComparer.Ordinal.Equals(
                FullName(reader, implementation.Interface),
                interfaceName);
        });
    });

    private static IEnumerable<TransportCall> ReadTransportCalls(
        PEReader pe,
        MetadataReader reader)
    {
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            var callerType = FullName(reader, typeHandle);
            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress == 0)
                {
                    continue;
                }

                var callerMethod = reader.GetString(method.Name);
                var il = pe.GetMethodBody(method.RelativeVirtualAddress)
                    .GetILBytes() ?? throw new InvalidDataException(
                        "The verifier contains a method without IL bytes.");
                foreach (var target in ReadCalledMethods(reader, il))
                {
                    if (target.DeclaringType == DeepSeekTransportType &&
                        target.Name is "CreateHandler" or "CreateForTesting")
                    {
                        yield return new TransportCall(
                            callerType,
                            callerMethod,
                            target.Name);
                    }
                }
            }
        }
    }

    private static IEnumerable<CalledMethod> ReadCalledMethods(
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

            if (opCode is ILOpCode.Call or ILOpCode.Callvirt)
            {
                var token = BitConverter.ToInt32(il, operandOffset);
                yield return ResolveCalledMethod(
                    reader,
                    MetadataTokens.EntityHandle(token));
            }

            offset += operandSize;
        }
    }

    private static CalledMethod ResolveCalledMethod(
        MetadataReader reader,
        EntityHandle handle) => handle.Kind switch
    {
        HandleKind.MemberReference => ResolveMemberReference(
            reader,
            (MemberReferenceHandle)handle),
        HandleKind.MethodDefinition => ResolveMethodDefinition(
            reader,
            (MethodDefinitionHandle)handle),
        HandleKind.MethodSpecification => ResolveCalledMethod(
            reader,
            reader.GetMethodSpecification((MethodSpecificationHandle)handle)
                .Method),
        _ => throw new InvalidDataException(
            "The verifier contains an unsupported call target."),
    };

    private static CalledMethod ResolveMemberReference(
        MetadataReader reader,
        MemberReferenceHandle handle)
    {
        var reference = reader.GetMemberReference(handle);
        var declaringType = FullName(reader, reference.Parent);
        if (declaringType is null)
        {
            throw new InvalidDataException(
                "The verifier contains an unresolved member parent.");
        }

        return new CalledMethod(
            declaringType,
            reader.GetString(reference.Name));
    }

    private static CalledMethod ResolveMethodDefinition(
        MetadataReader reader,
        MethodDefinitionHandle handle)
    {
        var definition = reader.GetMethodDefinition(handle);
        return new CalledMethod(
            FullName(reader, definition.GetDeclaringType()),
            reader.GetString(definition.Name));
    }

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

    private static string? FullName(
        MetadataReader reader,
        EntityHandle handle) => handle.Kind switch
    {
        HandleKind.TypeReference => FullName(
            reader,
            (TypeReferenceHandle)handle),
        HandleKind.TypeDefinition => FullName(
            reader,
            (TypeDefinitionHandle)handle),
        HandleKind.TypeSpecification => reader
            .GetTypeSpecification((TypeSpecificationHandle)handle)
            .DecodeSignature(MetadataTypeNameProvider.Instance, null),
        HandleKind.MethodDefinition => FullName(
            reader,
            reader.GetMethodDefinition((MethodDefinitionHandle)handle)
                .GetDeclaringType()),
        HandleKind.ModuleReference => string.Concat(
            "<Module:",
            reader.GetString(
                reader.GetModuleReference((ModuleReferenceHandle)handle).Name),
            ">"),
        _ => null,
    };

    private static string JoinName(string @namespace, string name) =>
        string.IsNullOrEmpty(@namespace)
            ? name
            : string.Concat(@namespace, ".", name);

    private sealed record CalledMethod(string DeclaringType, string Name);

    private sealed record TransportCall(
        string CallerType,
        string CallerMethod,
        string CalledMethod);

    private sealed class MetadataTypeNameProvider :
        ISignatureTypeProvider<string, object?>
    {
        internal static MetadataTypeNameProvider Instance { get; } = new();

        public string GetArrayType(string elementType, ArrayShape shape) =>
            string.Concat(elementType, "[", new string(',', shape.Rank - 1), "]");

        public string GetByReferenceType(string elementType) =>
            string.Concat(elementType, "&");

        public string GetFunctionPointerType(
            MethodSignature<string> signature) => "<function-pointer>";

        public string GetGenericInstantiation(
            string genericType,
            ImmutableArray<string> typeArguments) => string.Concat(
                genericType,
                "<",
                string.Join(',', typeArguments),
                ">");

        public string GetGenericMethodParameter(object? context, int index) =>
            string.Concat("!!", index);

        public string GetGenericTypeParameter(object? context, int index) =>
            string.Concat("!", index);

        public string GetModifiedType(
            string modifier,
            string unmodifiedType,
            bool isRequired) => unmodifiedType;

        public string GetPinnedType(string elementType) => elementType;

        public string GetPointerType(string elementType) =>
            string.Concat(elementType, "*");

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) =>
            string.Concat("System.", typeCode);

        public string GetSZArrayType(string elementType) =>
            string.Concat(elementType, "[]");

        public string GetTypeFromDefinition(
            MetadataReader reader,
            TypeDefinitionHandle handle,
            byte rawTypeKind) => FullName(reader, handle);

        public string GetTypeFromReference(
            MetadataReader reader,
            TypeReferenceHandle handle,
            byte rawTypeKind) => FullName(reader, handle);

        public string GetTypeFromSpecification(
            MetadataReader reader,
            object? context,
            TypeSpecificationHandle handle,
            byte rawTypeKind) => reader.GetTypeSpecification(handle)
                .DecodeSignature(this, context);
    }
}

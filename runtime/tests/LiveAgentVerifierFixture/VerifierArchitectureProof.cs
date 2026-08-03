using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using AgenticPrReview.Runtime.Execution.DeepSeek;

namespace AgenticPrReview.Runtime.LiveAgentVerifierFixture;

internal sealed record VerifierArchitectureReceipt(
    string Kind,
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
        string processInstanceSha256)
    {
        var assembly = typeof(Program).Assembly;
        var path = assembly.Location;
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var typeReferences = reader.TypeReferences
            .Select(handle => FullName(reader, handle))
            .ToHashSet(StringComparer.Ordinal);
        var types = assembly.GetTypes();
        var transportCalls = types
            .SelectMany(type => DeclaredExecutableMembers(type)
                .SelectMany(method => ResolveMethodBodyMembers(method)
                    .OfType<MethodInfo>()
                    .Where(called => called.DeclaringType ==
                            typeof(DeepSeekTransport) &&
                        called.Name is "CreateHandler" or "CreateForTesting")
                    .Select(called => (Caller: method, Called: called))))
            .ToArray();
        var factoryTypes = types.Count(type =>
            type is { IsInterface: false, IsAbstract: false } &&
            typeof(IR3LiveAgentTransportFactory).IsAssignableFrom(type));
        var profileTypes = types.Count(type =>
            type is { IsInterface: false, IsAbstract: false } &&
            typeof(ILiveAgentFreshProcessProfile).IsAssignableFrom(type));
        var forbiddenAbsent = !ForbiddenTypes.Overlaps(typeReferences);
        var passed = forbiddenAbsent &&
            transportCalls.Length == 2 &&
            transportCalls.All(call =>
                call.Caller.DeclaringType ==
                    typeof(VerifierTransportFactory) &&
                call.Caller.Name == nameof(VerifierTransportFactory.Create)) &&
            factoryTypes == 1 &&
            profileTypes == 1;
        return new VerifierArchitectureReceipt(
            "apr-r3-live-agent-architecture-receipt-v1",
            passed ? "passed" : "failed",
            LiveAgentFreshProcessDomain.RawSha256(File.ReadAllBytes(path)),
            forbiddenAbsent,
            transportCalls.Length,
            factoryTypes,
            profileTypes,
            passed,
            processInstanceSha256);
    }

    private static string FullName(
        MetadataReader reader,
        TypeReferenceHandle handle)
    {
        var reference = reader.GetTypeReference(handle);
        return string.Concat(
            reader.GetString(reference.Namespace),
            ".",
            reader.GetString(reference.Name));
    }

    private static IEnumerable<MethodBase> DeclaredExecutableMembers(Type type)
    {
        foreach (var method in type.GetMethods(
            BindingFlags.Instance |
            BindingFlags.Static |
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly))
        {
            yield return method;
        }

        foreach (var constructor in type.GetConstructors(
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly))
        {
            yield return constructor;
        }

        if (type.TypeInitializer is { } initializer)
        {
            yield return initializer;
        }
    }

    private static IEnumerable<MemberInfo> ResolveMethodBodyMembers(
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
            if (opCode == OpCodes.Calli)
            {
                throw new InvalidOperationException(
                    "Indirect verifier calls are not permitted.");
            }

            var tokenOffset = offset;
            var operandSize = OperandSize(opCode.OperandType, il, offset);
            if (opCode.OperandType is
                OperandType.InlineField or
                OperandType.InlineMethod or
                OperandType.InlineTok or
                OperandType.InlineType)
            {
                var token = BitConverter.ToInt32(il, tokenOffset);
                yield return method.Module.ResolveMember(
                    token,
                    method.DeclaringType?.GetGenericArguments(),
                    method is MethodInfo info
                        ? info.GetGenericArguments()
                        : null)!;
            }

            offset += operandSize;
        }
    }

    private static OpCode ReadOpCode(byte[] il, ref int offset)
    {
        var first = il[offset++];
        var value = first == 0xFE
            ? (ushort)(0xFE00 | il[offset++])
            : first;
        return OpCodesByValue.TryGetValue(value, out var opCode)
            ? opCode
            : throw new InvalidOperationException(
                string.Concat("Unknown IL opcode ", value.ToString("x4"), "."));
    }

    private static int OperandSize(
        OperandType operandType,
        byte[] il,
        int offset) => operandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or
            OperandType.ShortInlineI or
            OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineBrTarget or
            OperandType.InlineField or
            OperandType.InlineI or
            OperandType.InlineMethod or
            OperandType.InlineSig or
            OperandType.InlineString or
            OperandType.InlineTok or
            OperandType.InlineType or
            OperandType.ShortInlineR => 4,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch =>
            4 + checked(BitConverter.ToInt32(il, offset) * 4),
        _ => throw new InvalidOperationException(
            string.Concat("Unsupported IL operand ", operandType, ".")),
    };

    private static readonly IReadOnlyDictionary<ushort, OpCode> OpCodesByValue =
        typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode))
            .Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(
                opCode => unchecked((ushort)opCode.Value),
                opCode => opCode);
}

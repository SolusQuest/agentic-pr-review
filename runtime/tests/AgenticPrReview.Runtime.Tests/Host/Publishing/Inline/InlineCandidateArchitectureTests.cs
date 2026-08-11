using System.Reflection;
using System.Reflection.Emit;
using AgenticPrReview.Runtime;
using AgenticPrReview.Runtime.ActionHost.Policy;
using AgenticPrReview.Runtime.Agent.Tools;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Publishing.Inline;

public sealed class InlineCandidateArchitectureTests
{
    private const string InlineNamespace =
        "AgenticPrReview.Runtime.Host.Publishing.Inline";

    private static readonly IReadOnlyDictionary<ushort, OpCode> OpCodesByValue =
        typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(static field => field.FieldType == typeof(OpCode))
            .Select(static field => (OpCode)field.GetValue(null)!)
            .ToDictionary(
                static opCode => unchecked((ushort)opCode.Value),
                static opCode => opCode);

    [Fact]
    public void InlineMappingSurfaceIsInternalSynchronousAndCapabilityFree()
    {
        var types = typeof(RuntimeApplication).Assembly.GetTypes()
            .Where(static type => type.Namespace == InlineNamespace)
            .ToArray();

        Assert.NotEmpty(types);
        Assert.All(types, static type => Assert.False(type.IsPublic));
        var referenced = types.SelectMany(ReferencedTypes)
            .Where(static type => type is not null)
            .Select(static type => type!.FullName ?? type.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var forbidden = referenced.Where(IsForbiddenCapability).ToArray();
        Assert.True(
            forbidden.Length == 0,
            "Forbidden inline capability references:" + Environment.NewLine +
            string.Join(Environment.NewLine, forbidden));
        Assert.DoesNotContain(
            types.SelectMany(static type => type.GetMethods(
                BindingFlags.Instance |
                BindingFlags.Static |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly)),
            static method => typeof(Task).IsAssignableFrom(method.ReturnType));
    }

    [Fact]
    public void ProjectionRetainsNoSnapshotPolicyOrWriteAuthority()
    {
        var types = typeof(RuntimeApplication).Assembly.GetTypes()
            .Where(static type => type.Namespace == InlineNamespace)
            .ToArray();
        var fields = types.SelectMany(static type => type.GetFields(
            BindingFlags.Instance |
            BindingFlags.Static |
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly)).ToArray();

        Assert.DoesNotContain(fields, static field =>
            typeof(ReviewedSnapshot).IsAssignableFrom(field.FieldType) ||
            typeof(ActionHostTrustedPolicy).IsAssignableFrom(field.FieldType));
        Assert.DoesNotContain(
            fields,
            static field => IsAuthorityName(field.Name));
    }

    private static IEnumerable<Type?> ReferencedTypes(Type type)
    {
        yield return type.BaseType;
        foreach (var contract in type.GetInterfaces())
        {
            yield return contract;
        }

        foreach (var field in type.GetFields(
            BindingFlags.Instance |
            BindingFlags.Static |
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly))
        {
            yield return field.FieldType;
        }

        foreach (var method in type.GetMethods(
                BindingFlags.Instance |
                BindingFlags.Static |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly)
            .Cast<MethodBase>()
            .Concat(type.GetConstructors(
                BindingFlags.Instance |
                BindingFlags.Static |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly)))
        {
            Assert.True(
                (method.Attributes & MethodAttributes.PinvokeImpl) == 0,
                $"Native import is forbidden: {type.FullName}.{method.Name}");
            if (method is MethodInfo info)
            {
                yield return info.ReturnType;
            }

            foreach (var parameter in method.GetParameters())
            {
                yield return parameter.ParameterType;
            }

            var body = method.GetMethodBody();
            if (body is not null)
            {
                foreach (var local in body.LocalVariables)
                {
                    yield return local.LocalType;
                }

                foreach (var exceptionClause in body.ExceptionHandlingClauses)
                {
                    if (exceptionClause.Flags ==
                        ExceptionHandlingClauseOptions.Clause)
                    {
                        yield return exceptionClause.CatchType;
                    }
                }
            }

            foreach (var member in ResolveMethodBodyMembers(method))
            {
                yield return member.DeclaringType;
                if (member is Type referencedType)
                {
                    yield return referencedType;
                }
            }
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
            Assert.NotEqual(OpCodes.Calli, opCode);
            var tokenOffset = offset;
            var operandSize = OperandSize(opCode.OperandType, il, offset);
            if (opCode.OperandType is
                OperandType.InlineField or
                OperandType.InlineMethod or
                OperandType.InlineTok or
                OperandType.InlineType)
            {
                var token = BitConverter.ToInt32(il, tokenOffset);
                MemberInfo? resolved;
                try
                {
                    resolved = method.Module.ResolveMember(
                        token,
                        method.DeclaringType?.GetGenericArguments(),
                        method is MethodInfo info
                            ? info.GetGenericArguments()
                            : null);
                }
                catch (ArgumentException exception)
                {
                    throw new Xunit.Sdk.XunitException(
                        $"Could not resolve IL token {token} in " +
                        $"{method.DeclaringType?.FullName}.{method.Name}: " +
                        exception.Message);
                }

                if (resolved is not null)
                {
                    yield return resolved;
                }
            }

            offset += operandSize;
        }
    }

    private static OpCode ReadOpCode(byte[] il, ref int offset)
    {
        var first = il[offset++];
        var value = first == 0xfe
            ? (ushort)(0xfe00 | il[offset++])
            : first;
        Assert.True(OpCodesByValue.TryGetValue(value, out var opCode));
        return opCode;
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
            _ => throw new Xunit.Sdk.XunitException(
                $"Unsupported IL operand type {operandType}."),
        };

    private static bool IsForbiddenCapability(string name) =>
        name.Contains("System.Net", StringComparison.Ordinal) ||
        name.Contains("System.IO", StringComparison.Ordinal) ||
        name.Contains("System.Diagnostics.Process", StringComparison.Ordinal) ||
        name.Contains("GitHub", StringComparison.Ordinal) ||
        name.Contains("Transport", StringComparison.Ordinal) ||
        name.Contains("Credential", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Secret", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("GitHubToken", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("ApiKey", StringComparison.OrdinalIgnoreCase) ||
        name.Contains(
            "AgenticPrReview.Runtime.Host.State",
            StringComparison.Ordinal) ||
        name.Contains(
            "AgenticPrReview.Runtime.ActionHost.Authorization",
            StringComparison.Ordinal);

    private static bool IsAuthorityName(string name) =>
        name.Contains("Token", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Secret", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Credential", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Transport", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Client", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("State", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Root", StringComparison.OrdinalIgnoreCase);
}

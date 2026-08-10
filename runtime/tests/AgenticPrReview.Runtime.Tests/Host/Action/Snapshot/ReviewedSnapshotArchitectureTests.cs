using System.Reflection;
using System.Reflection.Emit;
using AgenticPrReview.Runtime;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.Snapshot;
using AgenticPrReview.Runtime.ActionHost.Snapshot.GitObjects;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action.Snapshot;

public sealed class ReviewedSnapshotArchitectureTests
{
    private const string SnapshotNamespace =
        "AgenticPrReview.Runtime.ActionHost.Snapshot";
    private const string NativeAccessType = SnapshotNamespace +
        ".ReviewedStagedFileAccess";

    private static readonly IReadOnlyDictionary<ushort, OpCode> OpCodesByValue =
        typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(static field => field.FieldType == typeof(OpCode))
            .Select(static field => (OpCode)field.GetValue(null)!)
            .ToDictionary(
                static opCode => unchecked((ushort)opCode.Value),
                static opCode => opCode);

    [Fact]
    public void SnapshotCapabilityIsInternalAndCannotExecuteReviewedCode()
    {
        var types = typeof(RuntimeApplication).Assembly.GetTypes()
            .Where(static type => type.Namespace is not null &&
                type.Namespace.StartsWith(
                    SnapshotNamespace,
                    StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(types);
        Assert.All(types, static type => Assert.False(type.IsPublic));
        var referenced = types
            .SelectMany(ReferencedTypes)
            .Where(static type => type is not null)
            .Select(static type => type!.FullName ?? type.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.DoesNotContain(referenced, IsForbiddenCapability);
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
                (method.Attributes & MethodAttributes.PinvokeImpl) == 0 ||
                StringComparer.Ordinal.Equals(type.FullName, NativeAccessType),
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
                MemberInfo? resolved = null;
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

    [Fact]
    public void AuthoritativeSnapshotObjectsHavePrivateConstructors()
    {
        AssertPrivateConstructors(typeof(ReviewedContentBudget));
        AssertPrivateConstructors(typeof(ReviewedTreePathRecord));
        AssertPrivateConstructors(typeof(ReviewedTreeSnapshot));
        AssertPrivateConstructors(typeof(ReviewedStagedBlob));
        AssertPrivateConstructors(typeof(ReviewedBlobStagingLease));
        AssertPrivateConstructors(typeof(ReviewedGitObjectTransport));
    }

    [Fact]
    public void AuthorityMintsHaveOnlyTheirExpectedProductionCallers()
    {
        AssertOnlyCalledBy(
            Method(typeof(ReviewedContentBudget), "Mint"),
            typeof(ReviewedTreeReader));
        AssertOnlyCalledBy(
            Method(typeof(ReviewedTreePathRecord), "Mint"),
            typeof(ReviewedTreeReader));
        AssertOnlyCalledBy(
            Method(typeof(ReviewedTreeSnapshot), "Mint"),
            typeof(ReviewedTreeReader));
        AssertOnlyCalledBy(
            Method(typeof(ReviewedTreeIdentityWriter), "Mint"),
            typeof(ReviewedTreeSnapshot));
        AssertOnlyCalledBy(
            Method(typeof(ReviewedStagedBlob), "Mint"),
            typeof(ReviewedBlobStagingLease));
        AssertOnlyCalledBy(
            Method(typeof(ReviewedGitObjectTransport), "Mint"),
            typeof(ReviewedGitObjectTransportFactory));

        var export = typeof(ActionHostGitHubToken).GetMethod(
            "ExportForPrivateLaunch",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new Xunit.Sdk.XunitException(
                "The private-launch token export was not found.");
        AssertOnlyCalledBy(export, typeof(ReviewedGitObjectTransport));
    }

    private static void AssertPrivateConstructors(Type type)
    {
        var constructors = type.GetConstructors(
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotEmpty(constructors);
        Assert.All(constructors, static constructor =>
            Assert.True(constructor.IsPrivate));
    }

    private static MethodInfo Method(Type type, string name) =>
        type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic) ??
        throw new Xunit.Sdk.XunitException(
            $"Expected method {type.FullName}.{name} was not found.");

    private static void AssertOnlyCalledBy(MethodBase target, Type owner)
    {
        var callers = SnapshotMethods()
            .Where(method => ResolveMethodBodyMembers(method).Any(member =>
                member.Module == target.Module &&
                member.MetadataToken == target.MetadataToken))
            .ToArray();
        Assert.NotEmpty(callers);
        Assert.All(callers, caller => Assert.True(
            IsOwnedBy(caller.DeclaringType, owner),
            $"Unexpected caller of {target.DeclaringType?.FullName}." +
            $"{target.Name}: {caller.DeclaringType?.FullName}.{caller.Name}"));
    }

    private static IEnumerable<MethodBase> SnapshotMethods() =>
        typeof(RuntimeApplication).Assembly.GetTypes()
            .Where(static type => type.Namespace is not null &&
                type.Namespace.StartsWith(
                    SnapshotNamespace,
                    StringComparison.Ordinal))
            .SelectMany(static type => type.GetMethods(
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
                    BindingFlags.DeclaredOnly)));

    private static bool IsOwnedBy(Type? candidate, Type owner)
    {
        while (candidate is not null)
        {
            if (candidate == owner)
            {
                return true;
            }

            candidate = candidate.DeclaringType;
        }

        return false;
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
        name.Contains("System.Diagnostics.Process", StringComparison.Ordinal) ||
        name.Contains("System.IO.Compression", StringComparison.Ordinal) ||
        name.Contains("System.Runtime.InteropServices.NativeLibrary",
            StringComparison.Ordinal) ||
        name.Contains("LibGit", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("AgenticPrReview.Runtime.Host.State",
            StringComparison.Ordinal) ||
        name.Contains("AgenticPrReview.Runtime.Host.Publishing",
            StringComparison.Ordinal) ||
        name.Contains("AgenticPrReview.Runtime.Execution",
            StringComparison.Ordinal) ||
        name.StartsWith("AgenticPrReview.Runtime", StringComparison.Ordinal) &&
        name.Contains("Provider", StringComparison.Ordinal);
}

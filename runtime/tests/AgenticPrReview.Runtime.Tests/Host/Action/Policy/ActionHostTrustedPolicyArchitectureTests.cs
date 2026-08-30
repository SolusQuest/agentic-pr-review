using System.Reflection;
using System.Reflection.Emit;
using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.ActionHost.Policy;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action.Policy;

public sealed class ActionHostTrustedPolicyArchitectureTests
{
    private static readonly IReadOnlyDictionary<ushort, OpCode> OpCodesByValue =
        typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(static field => field.FieldType == typeof(OpCode))
            .Select(static field => (OpCode)field.GetValue(null)!)
            .ToDictionary(
                static opCode => unchecked((ushort)opCode.Value),
                static opCode => opCode);

    [Fact]
    public void PolicySurfaceIsInternalImmutableAndPrivatelyMinted()
    {
        var assembly = typeof(ActionHostTrustedPolicy).Assembly;
        var policyTypes = assembly.GetTypes()
            .Where(type => type.Namespace is
                "AgenticPrReview.Runtime.ActionHost.Policy")
            .ToArray();

        Assert.NotEmpty(policyTypes);
        Assert.All(policyTypes, type => Assert.False(
            type.IsPublic,
            type.FullName));
        var constructor = Assert.Single(typeof(ActionHostTrustedPolicy)
            .GetConstructors(BindingFlags.Instance |
                BindingFlags.NonPublic));
        Assert.True(constructor.IsPrivate);
        Assert.All(typeof(ActionHostTrustedPolicy).GetProperties(
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic),
            property => Assert.Null(property.SetMethod));
        Assert.All(typeof(ActionHostTrustedPolicyRequest).GetConstructors(
            BindingFlags.Instance |
            BindingFlags.NonPublic),
            value => Assert.True(value.IsPrivate));
    }

    [Fact]
    public void OnlyApprovedProductionFactoriesExportActionHostCredentials()
    {
        var export = typeof(ActionHostOpaqueSecret).GetMethod(
            "ExportForPrivateLaunch",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var callers = typeof(ActionHostTrustedPolicy).Assembly.GetTypes()
            .Where(type => type.Namespace?.StartsWith(
                "AgenticPrReview.Runtime.ActionHost",
                StringComparison.Ordinal) == true &&
                type.Namespace is not
                    "AgenticPrReview.Runtime.ActionHost.Serialization")
            .SelectMany(static type => type.GetMethods(
                BindingFlags.Instance |
                BindingFlags.Static |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly))
            .Where(method => Calls(method, export.MetadataToken))
            .Select(method => method.DeclaringType)
            .Distinct()
            .OrderBy(type => type!.FullName, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                typeof(ActionHostDeepSeekProviderRunnerFactory),
                typeof(ActionHostGitHubAuthorizationTransportFactory),
            ],
            callers);
        Assert.IsAssignableFrom<IActionHostGitHubAuthorizationTransportFactory>(
            new ActionHostGitHubAuthorizationTransportFactory());
        Assert.IsAssignableFrom<IActionHostGitObjectTransportFactory>(
            new ActionHostGitHubAuthorizationTransportFactory());
        Assert.IsAssignableFrom<IActionHostReviewedSnapshotTransportFactory>(
            new ActionHostGitHubAuthorizationTransportFactory());
    }

    [Fact]
    public void H2InterfaceIsUnchangedAndH3UsesNarrowExactObjects()
    {
        Assert.Equal(new[]
        {
            "GetCollaboratorPermissionAsync",
            "GetCommitPullRequestsAsync",
            "GetPullRequestAsync",
            "GetRepositoryAsync",
            "GetWorkflowRunAttemptAsync",
            "GetWorkflowSourceAsync",
        }, typeof(IActionHostGitHubAuthorizationTransport)
            .GetMethods()
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal));
        Assert.Equal(new[]
        {
            "GetBlobObjectAsync",
            "GetCommitObjectAsync",
            "GetHeadArchiveAsync",
            "GetTreeObjectAsync",
        }, typeof(IActionHostGitObjectTransport)
            .GetMethods()
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal));
        var archive = Assert.Single(typeof(IActionHostGitObjectTransport)
            .GetMethods(), method => method.Name == "GetHeadArchiveAsync");
        Assert.Equal(
            typeof(Task<ActionHostGitObjectResult<ActionHostGitArchiveReader>>),
            archive.ReturnType);
        Assert.Equal(new[]
        {
            "CopyBlobObjectAsync",
            "GetCommitObjectAsync",
            "GetCurrentPullRequestAsync",
            "GetPullRequestFilesAsync",
            "GetTreeObjectAsync",
        }, typeof(IActionHostReviewedSnapshotTransport)
            .GetMethods()
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal));
        Assert.All(
            typeof(IActionHostReviewedSnapshotTransport).GetMethods(),
            static method => Assert.DoesNotContain(
                new[] { "Create", "Delete", "Patch", "Post", "Put", "Update" },
                prefix => method.Name.Contains(
                    prefix,
                    StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void MaterializerAcceptsOnlyFrozenAuthorityAndExactObjectTransport()
    {
        var materialize = Assert.Single(
            typeof(ActionHostTrustedPolicy).GetMethods(
                BindingFlags.Static | BindingFlags.NonPublic),
            method => method.Name == "MaterializeAsync");
        Assert.Equal(new[]
        {
            typeof(ActionHostTrustedPolicyRequest),
            typeof(IActionHostGitObjectTransport),
            typeof(CancellationToken),
        }, materialize.GetParameters()
            .Select(parameter => parameter.ParameterType));

        var readerConstructor = Assert.Single(
            typeof(ActionHostTrustedPolicySourceReader).GetConstructors(
                BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.Equal(new[]
        {
            typeof(ActionHostTrustedPolicyRequest),
            typeof(IActionHostGitObjectTransport),
        }, readerConstructor.GetParameters()
            .Select(parameter => parameter.ParameterType));
    }

    [Fact]
    public void EveryH3AndGitObjectJsonRootIsSourceGenerated()
    {
        Assert.NotNull(ActionHostTrustedPolicyJsonContext.Default
            .ActionHostTrustedPolicyDocument);
        Assert.NotNull(ActionHostTrustedPolicyJsonContext.Default
            .ActionHostPublicationDocument);
        Assert.NotNull(ActionHostGitObjectJsonContext.Default
            .ActionHostGitCommitDocument);
        Assert.NotNull(ActionHostGitObjectJsonContext.Default
            .ActionHostGitTreeDocument);
        Assert.NotNull(ActionHostGitObjectJsonContext.Default
            .ActionHostGitTreeEntryDocumentArray);
        Assert.NotNull(ActionHostGitObjectJsonContext.Default
            .ActionHostGitBlobDocument);
        Assert.NotNull(ActionHostReviewedSnapshotJsonContext.Default
            .ActionHostPullRequestFileDocument);
        Assert.NotNull(ActionHostReviewedSnapshotJsonContext.Default
            .ActionHostPullRequestFileDocumentArray);
        Assert.Null(ActionHostTrustedPolicyJsonContext.Default.GetTypeInfo(
            typeof(ActionHostTrustedPolicy)));
    }

    private static bool Calls(MethodInfo method, int metadataToken)
    {
        var body = method.GetMethodBody()?.GetILAsByteArray();
        if (body is null)
        {
            return false;
        }

        var offset = 0;
        while (offset < body.Length)
        {
            var opCode = ReadOpCode(body, ref offset);
            var operandOffset = offset;
            var operandSize = OperandSize(opCode.OperandType, body, offset);
            if ((opCode == OpCodes.Call || opCode == OpCodes.Callvirt) &&
                BitConverter.ToInt32(body, operandOffset) == metadataToken)
            {
                return true;
            }

            offset += operandSize;
        }

        return false;
    }

    private static OpCode ReadOpCode(byte[] il, ref int offset)
    {
        var first = il[offset++];
        var value = first == 0xfe
            ? (ushort)(0xfe00 | il[offset++])
            : first;
        return OpCodesByValue.TryGetValue(value, out var opCode)
            ? opCode
            : throw new Xunit.Sdk.XunitException("Unknown IL opcode.");
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
}

using System.Reflection;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.ActionHost.Policy;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action.Policy;

public sealed class ActionHostTrustedPolicyArchitectureTests
{
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
    public void ExistingGitHubFactoryRemainsSoleCredentialExporter()
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
                BindingFlags.NonPublic))
            .Where(method => Calls(method, export.MetadataToken))
            .Select(method => method.DeclaringType)
            .Distinct()
            .ToArray();

        Assert.Equal(
            [typeof(ActionHostGitHubAuthorizationTransportFactory)],
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
            "GetTreeObjectAsync",
        }, typeof(IActionHostGitObjectTransport)
            .GetMethods()
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal));
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

        var token = BitConverter.GetBytes(metadataToken);
        for (var index = 0; index <= body.Length - token.Length; index++)
        {
            if (body.AsSpan(index, token.Length).SequenceEqual(token))
            {
                return true;
            }
        }

        return false;
    }
}

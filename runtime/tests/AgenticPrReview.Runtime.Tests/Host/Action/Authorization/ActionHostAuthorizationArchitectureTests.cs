using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action.Authorization;

public sealed class ActionHostAuthorizationArchitectureTests
{
    [Fact]
    public void H2ProductionSurfaceRemainsInternalAndClosed()
    {
        var types = typeof(ActionHostAuthorizer).Assembly.GetTypes()
            .Where(type => type.Namespace is
                "AgenticPrReview.Runtime.ActionHost.Authorization" or
                "AgenticPrReview.Runtime.ActionHost.GitHub")
            .ToArray();

        Assert.NotEmpty(types);
        Assert.All(types, type => Assert.False(type.IsPublic, type.FullName));
        Assert.DoesNotContain(types, type => type.Name.Contains(
            "Provider",
            StringComparison.Ordinal));
        Assert.DoesNotContain(types, type => type.Name.Contains(
            "Artifact",
            StringComparison.Ordinal));
        Assert.DoesNotContain(types, type => type.Name.Contains(
            "Publication",
            StringComparison.Ordinal));
    }

    [Fact]
    public void CapabilityAndFrozenIdentityHavePrivateConstructorsAndNoSetters()
    {
        foreach (var type in new[]
        {
            typeof(ActionHostAuthorizer.AuthorizedInvocation),
            typeof(ActionHostAuthorizer.FrozenPullRequest),
        })
        {
            var constructor = Assert.Single(type.GetConstructors(
                BindingFlags.Instance | BindingFlags.NonPublic));
            Assert.True(constructor.IsPrivate);
            Assert.All(
                type.GetProperties(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic),
                property => Assert.Null(property.SetMethod));
        }

        Assert.Null(typeof(ActionHostAuthorizer.AuthorizedInvocation)
            .GetProperty("IsAuthorized",
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic));
        Assert.Throws<InvalidOperationException>(() =>
            ActionHostAuthorizer.FrozenPullRequest.Mint(
                new object(),
                1,
                1,
                new string('a', 40),
                1,
                "owner/repo",
                new string('b', 40),
                1,
                "owner/repo",
                "open",
                false,
                false));
    }

    [Fact]
    public void ExternalJsonContextsAreGeneratedStrictAndSkipUnknownFields()
    {
        foreach (var options in new[]
        {
            ActionHostEventJsonContext.Default.Options,
            ActionHostGitHubJsonContext.Default.Options,
        })
        {
            Assert.False(options.PropertyNameCaseInsensitive);
            Assert.False(options.AllowDuplicateProperties);
            Assert.Equal(JsonUnmappedMemberHandling.Skip,
                options.UnmappedMemberHandling);
            Assert.False(options.TypeInfoResolver is null);
        }

        Assert.NotNull(ActionHostEventJsonContext.Default
            .ActionHostEventDocument);
        Assert.NotNull(ActionHostGitHubJsonContext.Default
            .ActionHostGitHubWorkflowRunDocument);
        Assert.NotNull(ActionHostGitHubJsonContext.Default
            .ActionHostGitHubPullRequestDocumentArray);
        Assert.Null(ActionHostEventJsonContext.Default.GetTypeInfo(
            typeof(ActionHostAuthorizer.AuthorizedInvocation)));
    }

    [Fact]
    public void OnlyFocusedGitHubTransportFactoriesExportTheH1Token()
    {
        var export = typeof(ActionHostOpaqueSecret).GetMethod(
            "ExportForPrivateLaunch",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var callers = typeof(ActionHostAuthorizer).Assembly.GetTypes()
            .Where(type => type.Namespace is
                "AgenticPrReview.Runtime.ActionHost.Authorization" or
                "AgenticPrReview.Runtime.ActionHost.GitHub" or
                "AgenticPrReview.Runtime.Host.Publishing.GitHub.Common")
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
            [
                typeof(ActionHostGitHubAuthorizationTransportFactory),
                typeof(BoundedGitHubPublisherTransportFactory),
            ],
            callers.OrderBy(type => type!.FullName, StringComparer.Ordinal));
    }

    [Fact]
    public void PublisherMutationSurfaceRequiresTheBoundP2Capability()
    {
        var factoryMethod = Assert.Single(
            typeof(IStickyGitHubPublisherTransportFactory).GetMethods());
        Assert.Equal(
            [typeof(ActionHostGitHubToken),
                typeof(AuthorizedStickyPublicationRequest)],
            factoryMethod.GetParameters()
                .Select(static parameter => parameter.ParameterType));

        var commonMethods = typeof(IBoundedGitHubPublisherTransport)
            .GetMethods();
        Assert.DoesNotContain(commonMethods, method => method.Name.Contains(
            "Create", StringComparison.Ordinal) || method.Name.Contains(
            "Update", StringComparison.Ordinal) ||
            method.GetParameters().Any(parameter => parameter.ParameterType ==
                typeof(ReadOnlyMemory<byte>)));

        var mutation = Assert.Single(
            typeof(IStickyGitHubPublisherTransport).GetMethods(),
            method => method.Name == "MutateStickyCommentAsync");
        Assert.Equal([typeof(CancellationToken)], mutation.GetParameters()
            .Select(static parameter => parameter.ParameterType));
        Assert.All(typeof(BoundedGitHubPublisherTransport).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public |
            BindingFlags.NonPublic), constructor =>
            Assert.True(constructor.IsPrivate));
    }

    [Fact]
    public void AuthorizerHasNoLaterOwnerDependencies()
    {
        var constructor = Assert.Single(typeof(ActionHostAuthorizer)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic));
        var names = constructor.GetParameters()
            .Select(parameter => parameter.ParameterType.FullName!)
            .ToArray();
        Assert.DoesNotContain(names, name => name.Contains(
            "Provider",
            StringComparison.Ordinal));
        Assert.DoesNotContain(names, name => name.Contains(
            "State",
            StringComparison.Ordinal));
        Assert.DoesNotContain(names, name => name.Contains(
            "Artifact",
            StringComparison.Ordinal));
        Assert.DoesNotContain(names, name => name.Contains(
            "Snapshot",
            StringComparison.Ordinal));
    }

    private static bool Calls(MethodInfo method, int metadataToken)
    {
        var bytes = method.GetMethodBody()?.GetILAsByteArray();
        if (bytes is null)
        {
            return false;
        }

        for (var index = 0; index <= bytes.Length - 5; index++)
        {
            if (bytes[index] is 0x28 or 0x6f &&
                BitConverter.ToInt32(bytes, index + 1) == metadataToken)
            {
                return true;
            }
        }

        return false;
    }
}

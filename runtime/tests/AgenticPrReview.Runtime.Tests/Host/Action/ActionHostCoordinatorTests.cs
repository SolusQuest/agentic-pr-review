using System.Reflection;
using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.Host.Publishing.Recovery;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action;

public sealed class ActionHostCoordinatorTests
{
    [Fact]
    public void DispatcherNamesEveryP5RecoveryAction()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "runtime",
            "src",
            "AgenticPrReview.Runtime",
            "Host",
            "Action",
            "ActionHostCoordinator.cs"));
        var compactSource = string.Concat(
            source.Where(character => !char.IsWhiteSpace(character)));

        Assert.Equal(15, Enum.GetValues<PublicationRecoveryAction>().Length);
        foreach (var action in Enum.GetNames<PublicationRecoveryAction>())
        {
            Assert.Contains(
                "PublicationRecoveryAction." + action,
                compactSource,
                StringComparison.Ordinal);
        }

        Assert.DoesNotContain(
            "IActionHostProviderRunnerFactory providerFactory,\n        " +
            "PublicationRecoveryDecision",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ClassifyBeforeProviderAsync",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderProjectionCannotCarryGitHubStateOrPublicationAuthority()
    {
        var properties = typeof(ActionHostProviderPolicy)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["AdapterId", "ModelId", "ProviderId"],
            properties);
        var create = Assert.Single(
            typeof(IActionHostProviderRunnerFactory).GetMethods());
        var parameterTypes = create.GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();
        Assert.DoesNotContain(
            parameterTypes,
            type => type.Name.Contains("GitHub", StringComparison.Ordinal) ||
                type.Name.Contains("State", StringComparison.Ordinal) ||
                type.Name.Contains("Publication", StringComparison.Ordinal));
    }

    [Fact]
    public void PostAcceptanceAuthorizationIsPrivateAndCoordinatorOwned()
    {
        var capability = typeof(ActionHostCoordinator)
            .GetNestedType(
                "PostAcceptanceInlineAuthorization",
                BindingFlags.NonPublic);
        Assert.NotNull(capability);
        Assert.NotEmpty(capability.GetConstructors(
            BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.Empty(capability.GetConstructors(
            BindingFlags.Instance | BindingFlags.Public));

        var mint = capability.GetMethod(
            "Mint",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(mint);
        Assert.Equal(capability, mint.ReturnType);
        Assert.Null(capability.GetMethod(
            "Mint",
            BindingFlags.Static | BindingFlags.Public));
        Assert.Null(capability.GetMethod(
            "TryConsume",
            BindingFlags.Instance | BindingFlags.Public));
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "package.json")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("repository root not found");
    }
}

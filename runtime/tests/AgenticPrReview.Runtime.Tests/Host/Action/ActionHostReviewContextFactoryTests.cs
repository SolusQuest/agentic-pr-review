using System.Text;
using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Snapshot;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action;

public sealed class ActionHostReviewContextFactoryTests
{
    [Fact]
    public async Task CreatesOneDeterministicBoundedUserTextPart()
    {
        var invocation = await AuthorizeAsync();
        var identities = Identities();

        Assert.True(ActionHostReviewContextFactory.TryCreate(
            invocation,
            identities,
            out var first));
        Assert.True(ActionHostReviewContextFactory.TryCreate(
            invocation,
            identities,
            out var second));

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal("user", first.Role);
        var content = Assert.Single(first.Contents);
        var text = Assert.IsType<ProjectTextContent>(content).Text;
        Assert.Equal(
            text,
            Assert.IsType<ProjectTextContent>(
                Assert.Single(second.Contents)).Text);
        Assert.StartsWith(
            "agentic-pr-review-context-v1\nrepository-id=42\n" +
            "pull-request=147\n",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "instruction=Review only the exact bounded snapshot identified " +
            "above.",
            text,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            ActionHostAuthorizationScenario.RepositoryName,
            text,
            StringComparison.Ordinal);
        Assert.InRange(Encoding.UTF8.GetByteCount(text), 1, 4_096);
    }

    [Fact]
    public async Task RejectsAnyAuthorityOrSnapshotIdentityMismatch()
    {
        var invocation = await AuthorizeAsync();
        var valid = Identities();
        var invalid = new[]
        {
            valid with { RepositoryId = valid.RepositoryId + 1 },
            valid with { PullRequestNumber = valid.PullRequestNumber + 1 },
            valid with { BaseSha = new string('1', 40) },
            valid with { HeadSha = new string('2', 40) },
            valid with { ReviewedTreeSha256 = new string('A', 64) },
            valid with { ChangedFilesSha256 = "short" },
            valid with { DiffSha256 = new string('g', 64) },
            valid with { MaterializationSha256 = string.Empty },
        };

        foreach (var identities in invalid)
        {
            Assert.False(ActionHostReviewContextFactory.TryCreate(
                invocation,
                identities,
                out var message));
            Assert.Null(message);
        }
    }

    [Fact]
    public void ProductionCompositionHasNoPrebuiltAuthorityInjectionPoint()
    {
        var productionConstructor = Assert.Single(
            typeof(ActionHostComposition)
                .GetConstructors(System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic),
            constructor => constructor.GetParameters().Length == 0);
        Assert.Empty(productionConstructor.GetParameters());

        var dependencyParameters = typeof(ActionHostCompositionDependencies)
            .GetConstructors(System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToArray();
        Assert.DoesNotContain(typeof(ProjectChatMessage), dependencyParameters);
        Assert.DoesNotContain(
            typeof(Func<ProjectChatMessage>),
            dependencyParameters);
        Assert.DoesNotContain(
            typeof(ActionHostAuthorizer.AuthorizedInvocation),
            dependencyParameters);
        Assert.DoesNotContain(
            typeof(BoundedReviewedSnapshotLease),
            dependencyParameters);
    }

    private static async Task<ActionHostAuthorizer.AuthorizedInvocation>
        AuthorizeAsync()
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowRun);
        var result = await scenario.CreateAuthorizer().AuthorizeAsync(
            scenario.Launch,
            CancellationToken.None);
        return Assert.IsType<ActionHostAuthorizer.AuthorizedInvocation>(
            result.Invocation);
    }

    private static ReviewedSnapshotIdentities Identities() => new(
        ActionHostAuthorizationScenario.RepositoryId,
        ActionHostAuthorizationScenario.PullRequestNumber,
        ActionHostAuthorizationScenario.BaseSha,
        ActionHostAuthorizationScenario.HeadSha,
        new string('1', 64),
        new string('2', 64),
        new string('3', 64),
        new string('4', 64));
}

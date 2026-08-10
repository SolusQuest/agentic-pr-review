using System.Text;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action.Authorization;

public sealed class ActionHostAuthorizationFactTests
{
    [Fact]
    public async Task RepositoryMismatchFailsBeforeRunRead()
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch);
        scenario.Transport.Repository = scenario.Transport.Repository with
        {
            Id = 43,
        };

        var result = await Authorize(scenario);

        AssertRejected(
            result,
            ActionHostStatus.AuthorizationFailed,
            ActionHostAuthorizationFailure.RepositoryMismatch);
        Assert.Equal(["repository"], scenario.Transport.Calls);
    }

    [Fact]
    public async Task CurrentRunRouteMismatchFailsBeforeSourceRead()
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch);
        scenario.Transport.CurrentRun = scenario.Transport.CurrentRun with
        {
            Event = "push",
        };

        var result = await Authorize(scenario);

        AssertRejected(
            result,
            ActionHostStatus.AuthorizationFailed,
            ActionHostAuthorizationFailure.CurrentRunMismatch);
        Assert.DoesNotContain("source", scenario.Transport.Calls);
    }

    [Fact]
    public async Task InvalidTrustedWorkflowStopsBeforeActorOrPullRequestRead()
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch);
        scenario.Transport.Source = scenario.Transport.Source with
        {
            Bytes = Encoding.UTF8.GetBytes(
                ActionHostAuthorizationScenario.ValidWorkflow(
                    ActionHostAuthorizationScenario.ActionSha)
                    .Replace(
                        "cancel-in-progress: false",
                        "cancel-in-progress: true",
                        StringComparison.Ordinal)),
        };

        var result = await Authorize(scenario);

        AssertRejected(
            result,
            ActionHostStatus.AuthorizationFailed,
            ActionHostAuthorizationFailure.WorkflowSourceInvalid);
        Assert.DoesNotContain(
            scenario.Transport.Calls,
            call => call.StartsWith("permission:", StringComparison.Ordinal));
        Assert.DoesNotContain("pull-request", scenario.Transport.Calls);
    }

    [Fact]
    public async Task WorkflowRunRequiresCompleteUniqueAssociation()
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowRun);
        scenario.Transport.AssociatedPages =
        [
            new ActionHostGitHubPullRequestPageFact([], true),
        ];

        var result = await Authorize(scenario);

        AssertRejected(
            result,
            ActionHostStatus.AuthorizationFailed,
            ActionHostAuthorizationFailure.PullRequestAssociationInvalid);
        Assert.DoesNotContain("pull-request", scenario.Transport.Calls);
    }

    [Fact]
    public async Task EmptyRunApiInlineArrayUsesCompleteCommitAssociation()
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowRun);
        scenario.Transport.TriggerRun = scenario.Transport.TriggerRun with
        {
            PullRequests = [],
        };

        var result = await Authorize(scenario);

        Assert.NotNull(result.Invocation);
        Assert.Contains("associated:1", scenario.Transport.Calls);
        Assert.Equal(ActionHostAuthorizationScenario.HeadSha,
            result.Invocation!.PullRequest.HeadSha);
    }

    [Fact]
    public async Task EmptyEventInlineArrayCannotProveScheduledConcurrencyGroup()
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowRun);
        scenario.Transport.TriggerRun = scenario.Transport.TriggerRun with
        {
            PullRequests = [],
        };
        var trigger = scenario.Transport.TriggerRun;
        var bytes = Encoding.UTF8.GetBytes($$"""
        {
          "action": "completed",
          "workflow_run": {
            "id": {{trigger.Id}},
            "run_attempt": {{trigger.Attempt}},
            "workflow_id": {{trigger.WorkflowId}},
            "name": "{{trigger.Name}}",
            "path": "{{trigger.Path}}",
            "head_branch": "{{trigger.HeadBranch}}",
            "head_sha": "{{trigger.HeadSha}}",
            "event": "{{trigger.Event}}",
            "conclusion": "success",
            "repository": {
              "id": 42,
              "full_name": "SolusQuest/agentic-pr-review"
            },
            "head_repository": {
              "id": 42,
              "full_name": "SolusQuest/agentic-pr-review"
            },
            "actor": { "id": 7, "login": "maintainer" },
            "triggering_actor": { "id": 7, "login": "maintainer" },
            "pull_requests": []
          },
          "repository": {
            "id": 42,
            "full_name": "SolusQuest/agentic-pr-review"
          },
          "sender": { "id": 7, "login": "maintainer" }
        }
        """);
        var launch = ActionHostAuthorizationScenario.CreateLaunch(
            bytes,
            ActionHostAuthorizationRoute.WorkflowRun);
        var authorizer = new ActionHostAuthorizer(
            new FakeEventReader(bytes),
            scenario.Factory,
            ActionHostAuthorizationPolicy.TrustedProof);

        var result = await authorizer.AuthorizeAsync(
            launch,
            CancellationToken.None);

        Assert.Null(result.Invocation);
        Assert.Equal(ActionHostStatus.AuthorizationFailed,
            result.RejectionStatus);
        Assert.Equal(ActionHostAuthorizationFailure.RouteInputInvalid,
            result.Failure);
        Assert.Equal(0, scenario.Factory.Calls);
        Assert.Empty(scenario.Transport.Calls);
    }

    [Fact]
    public async Task DuplicateAssociationRowsFailClosed()
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowRun);
        scenario.Transport.AssociatedPages =
        [
            new ActionHostGitHubPullRequestPageFact(
                [
                    scenario.Transport.PullRequest,
                    scenario.Transport.PullRequest,
                ],
                true),
        ];

        var result = await Authorize(scenario);

        AssertRejected(
            result,
            ActionHostStatus.AuthorizationFailed,
            ActionHostAuthorizationFailure.PullRequestAssociationInvalid);
    }

    [Fact]
    public async Task FullFinalAssociationPageIsNotTreatedAsComplete()
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowRun);
        var full = Enumerable.Repeat(
            scenario.Transport.PullRequest,
            ActionHostGitHubAuthorizationPolicy.AssociatedPullRequestsPerPage)
            .ToArray();
        scenario.Transport.AssociatedPages =
        [
            new ActionHostGitHubPullRequestPageFact(full, false),
        ];

        var result = await Authorize(scenario);

        AssertRejected(
            result,
            ActionHostStatus.AuthorizationFailed,
            ActionHostAuthorizationFailure.PullRequestAssociationInvalid);
    }

    [Fact]
    public async Task DispatchReadPermissionIsInsufficient()
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch);
        scenario.Transport.Permission = new("read");

        var result = await Authorize(scenario);

        AssertRejected(
            result,
            ActionHostStatus.AuthorizationFailed,
            ActionHostAuthorizationFailure.ActorPermissionInsufficient);
        Assert.DoesNotContain("pull-request", scenario.Transport.Calls);
    }

    [Theory]
    [InlineData("write")]
    [InlineData("admin")]
    public async Task DispatchAcceptsDocumentedWriteLevelValues(
        string permission)
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch);
        scenario.Transport.Permission = new(permission);

        var result = await Authorize(scenario);

        Assert.NotNull(result.Invocation);
        Assert.Equal(ActionHostAuthorizationFailure.None, result.Failure);
    }

    [Fact]
    public async Task DispatchRerunChecksOriginalAndDistinctTriggeringActor()
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch);
        scenario.Transport.CurrentRun = scenario.Transport.CurrentRun with
        {
            TriggeringActor = new(8, "rerun-maintainer"),
        };

        var result = await Authorize(scenario);

        Assert.NotNull(result.Invocation);
        Assert.Contains("permission:maintainer", scenario.Transport.Calls);
        Assert.Contains("permission:rerun-maintainer", scenario.Transport.Calls);
    }

    [Fact]
    public async Task DispatchRerunRejectsUnderPermissionedTriggeringActor()
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch);
        scenario.Transport.CurrentRun = scenario.Transport.CurrentRun with
        {
            TriggeringActor = new(8, "rerun-reader"),
        };
        scenario.Transport.PermissionsByLogin.Add(
            "rerun-reader",
            new("read"));

        var result = await Authorize(scenario);

        AssertRejected(
            result,
            ActionHostStatus.AuthorizationFailed,
            ActionHostAuthorizationFailure.ActorPermissionInsufficient);
        Assert.DoesNotContain("pull-request", scenario.Transport.Calls);
    }

    [Theory]
    [InlineData("fork")]
    [InlineData("draft")]
    [InlineData("closed")]
    public async Task IneligiblePullRequestsUseBoundedSkipStatus(string kind)
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch);
        var pullRequest = scenario.Transport.PullRequest;
        if (kind == "fork")
        {
            pullRequest = pullRequest with
            {
                HeadRepository = new(
                    99,
                    "contributor/agentic-pr-review"),
            };
        }
        else if (kind == "draft")
        {
            pullRequest = pullRequest with { Draft = true };
        }
        else
        {
            pullRequest = pullRequest with { State = "closed" };
        }

        scenario.Transport.PullRequest = pullRequest;

        var result = await Authorize(scenario);

        var expected = kind switch
        {
            "fork" => ActionHostStatus.SkippedFork,
            "draft" => ActionHostStatus.SkippedDraft,
            _ => ActionHostStatus.SkippedClosed,
        };
        Assert.Equal(expected, result.RejectionStatus);
        Assert.Null(result.Invocation);
    }

    [Fact]
    public async Task UpstreamDenialDoesNotExposeCapability()
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch);
        scenario.Transport.Failure = ActionHostGitHubFailure.Forbidden;

        var result = await Authorize(scenario);

        AssertRejected(
            result,
            ActionHostStatus.AuthorizationFailed,
            ActionHostAuthorizationFailure.GitHubReadFailed);
        Assert.Null(result.Invocation);
    }

    [Fact]
    public async Task EventReaderFailureStopsBeforeFactory()
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch);
        scenario.EventReader.Bytes = null;

        var result = await Authorize(scenario);

        AssertRejected(
            result,
            ActionHostStatus.AuthorizationFailed,
            ActionHostAuthorizationFailure.EventReadFailed);
        Assert.Equal(0, scenario.Factory.Calls);
    }

    [Fact]
    public async Task InternalAuthorizationDeadlineIsNotHostCancellation()
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch);
        scenario.Transport.Delay = TimeSpan.FromMilliseconds(100);
        var authorizer = new ActionHostAuthorizer(
            scenario.EventReader,
            scenario.Factory,
            ActionHostAuthorizationPolicy.TrustedProof,
            TimeSpan.FromMilliseconds(1));

        var result = await authorizer.AuthorizeAsync(
            scenario.Launch,
            CancellationToken.None);

        AssertRejected(
            result,
            ActionHostStatus.AuthorizationFailed,
            ActionHostAuthorizationFailure.AuthorizationDeadline);
    }

    [Fact]
    public async Task CallerCancellationRemainsHostCancellation()
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch);
        scenario.Transport.Delay = TimeSpan.FromMilliseconds(100);
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(1));

        var result = await scenario.CreateAuthorizer().AuthorizeAsync(
            scenario.Launch,
            cancellation.Token);

        AssertRejected(
            result,
            ActionHostStatus.Cancelled,
            ActionHostAuthorizationFailure.Cancelled);
    }

    [Fact]
    public async Task RejectionCannotCrossProtectedCompositionSeams()
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch);
        scenario.Transport.Permission = new("read");
        var result = await Authorize(scenario);
        var seams = new FailIfCalledProtectedSeams();

        TestComposition.Continue(result, seams);

        Assert.Equal(0, seams.Calls);
    }

    private static Task<ActionHostAuthorizationResult> Authorize(
        ActionHostAuthorizationScenario scenario) =>
        scenario.CreateAuthorizer().AuthorizeAsync(
            scenario.Launch,
            CancellationToken.None);

    private static void AssertRejected(
        ActionHostAuthorizationResult result,
        ActionHostStatus status,
        ActionHostAuthorizationFailure failure)
    {
        Assert.Equal(status, result.RejectionStatus);
        Assert.Equal(failure, result.Failure);
        Assert.Null(result.Invocation);
    }

    private static class TestComposition
    {
        internal static void Continue(
            ActionHostAuthorizationResult result,
            FailIfCalledProtectedSeams seams)
        {
            if (result.Invocation is not { } invocation)
            {
                return;
            }

            seams.ResolveStateKey(invocation);
            seams.ConstructProvider(invocation);
            seams.ReadReviewedBytes(invocation);
            seams.AccessArtifacts(invocation);
            seams.WritePublicly(invocation);
        }
    }

    private sealed class FailIfCalledProtectedSeams
    {
        internal int Calls { get; private set; }

        internal void ResolveStateKey(
            ActionHostAuthorizer.AuthorizedInvocation invocation) => Called();

        internal void ConstructProvider(
            ActionHostAuthorizer.AuthorizedInvocation invocation) => Called();

        internal void ReadReviewedBytes(
            ActionHostAuthorizer.AuthorizedInvocation invocation) => Called();

        internal void AccessArtifacts(
            ActionHostAuthorizer.AuthorizedInvocation invocation) => Called();

        internal void WritePublicly(
            ActionHostAuthorizer.AuthorizedInvocation invocation) => Called();

        private void Called()
        {
            Calls++;
            throw new InvalidOperationException(
                "A rejected authorization reached a protected seam.");
        }
    }
}

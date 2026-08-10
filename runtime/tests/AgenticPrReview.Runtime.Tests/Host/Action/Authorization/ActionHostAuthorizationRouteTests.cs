using System.Text;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action.Authorization;

public sealed class ActionHostAuthorizationRouteTests
{
    [Theory]
    [InlineData((int)ActionHostAuthorizationRoute.WorkflowRun)]
    [InlineData((int)ActionHostAuthorizationRoute.WorkflowDispatch)]
    public async Task ApprovedRoutesMintOneFrozenInvocation(int routeValue)
    {
        var route = (ActionHostAuthorizationRoute)routeValue;
        var scenario = ActionHostAuthorizationScenario.Valid(route);
        Assert.True(ActionHostTrustedWorkflowPolicy.TryValidate(
            scenario.Transport.Source.Bytes,
            ActionHostAuthorizationPolicy.TrustedProof,
            ActionHostAuthorizationScenario.ActionSha,
            out _,
            out var workflowFailure), workflowFailure.ToString());

        var result = await scenario.CreateAuthorizer().AuthorizeAsync(
            scenario.Launch,
            CancellationToken.None);

        Assert.True(
            result.Invocation is not null,
            $"{result.RejectionStatus}/{result.Failure}/" +
            string.Join(',', scenario.Transport.Calls));
        var invocation = Assert.IsType<
            ActionHostAuthorizer.AuthorizedInvocation>(result.Invocation);
        Assert.Null(result.RejectionStatus);
        Assert.Equal(ActionHostAuthorizationFailure.None, result.Failure);
        Assert.Equal(route, invocation.Route);
        Assert.Equal(ActionHostAuthorizationScenario.PullRequestNumber,
            invocation.PullRequest.Number);
        Assert.Equal(ActionHostAuthorizationScenario.HeadSha,
            invocation.PullRequest.HeadSha);
        Assert.Equal(
            $"agentic-pr-review-r4-42-pr-147",
            invocation.ConcurrencyIdentity);
        Assert.Equal(1, scenario.Factory.Calls);
    }

    [Fact]
    public async Task HostCancellationStopsBeforeEventRead()
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch,
            cancellation: ActionHostCancellationState.Requested);

        var result = await scenario.CreateAuthorizer().AuthorizeAsync(
            scenario.Launch,
            CancellationToken.None);

        Assert.Equal(ActionHostStatus.Cancelled, result.RejectionStatus);
        Assert.Equal(ActionHostAuthorizationFailure.Cancelled, result.Failure);
        Assert.Null(result.Invocation);
        Assert.Equal(0, scenario.EventReader.Calls);
        Assert.Equal(0, scenario.Factory.Calls);
    }

    [Fact]
    public async Task MissingGitHubTokenOnCandidateRouteIsCredentialsMissing()
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch,
            includeToken: false);

        var result = await scenario.CreateAuthorizer().AuthorizeAsync(
            scenario.Launch,
            CancellationToken.None);

        Assert.Equal(ActionHostStatus.CredentialsMissing,
            result.RejectionStatus);
        Assert.Equal(ActionHostAuthorizationFailure.CredentialsMissing,
            result.Failure);
        Assert.Equal(0, scenario.Factory.Calls);
    }

    public static TheoryData<string> UnsupportedEvents => new()
    {
        "pull_request",
        "pull_request_review",
        "pull_request_target",
        "push",
        "schedule",
        "issue_comment",
        "unknown",
    };

    [Theory]
    [MemberData(nameof(UnsupportedEvents))]
    public async Task UnsupportedEventsSkipBeforeTransport(string eventName)
    {
        var bytes = Encoding.UTF8.GetBytes($$"""
        {
          "event_name": "{{eventName}}",
          "repository": { "id": 42, "full_name": "SolusQuest/agentic-pr-review" },
          "sender": { "id": 7, "login": "maintainer" }
        }
        """);
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch);
        var launch = ActionHostAuthorizationScenario.CreateLaunch(
            bytes,
            ActionHostAuthorizationRoute.WorkflowDispatch);
        var authorizer = new ActionHostAuthorizer(
            new FakeEventReader(bytes),
            scenario.Factory,
            ActionHostAuthorizationPolicy.TrustedProof);

        var result = await authorizer.AuthorizeAsync(
            launch,
            CancellationToken.None);

        Assert.Equal(ActionHostStatus.SkippedUntrustedEvent,
            result.RejectionStatus);
        Assert.Equal(ActionHostAuthorizationFailure.UnsupportedEvent,
            result.Failure);
        Assert.Equal(0, scenario.Factory.Calls);
    }

    [Fact]
    public async Task MalformedEventFailsBeforeTransport()
    {
        var bytes = Encoding.UTF8.GetBytes("{not-json");
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch);
        var launch = ActionHostAuthorizationScenario.CreateLaunch(
            bytes,
            ActionHostAuthorizationRoute.WorkflowDispatch);
        var authorizer = new ActionHostAuthorizer(
            new FakeEventReader(bytes),
            scenario.Factory,
            ActionHostAuthorizationPolicy.TrustedProof);

        var result = await authorizer.AuthorizeAsync(
            launch,
            CancellationToken.None);

        Assert.Equal(ActionHostStatus.AuthorizationFailed,
            result.RejectionStatus);
        Assert.Equal(ActionHostAuthorizationFailure.EventMalformed,
            result.Failure);
        Assert.Equal(0, scenario.Factory.Calls);
    }

    [Fact]
    public async Task EventDigestMismatchFailsBeforeParsingOrTransport()
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch);
        scenario.EventReader.Bytes = Encoding.UTF8.GetBytes("{}");

        var result = await scenario.CreateAuthorizer().AuthorizeAsync(
            scenario.Launch,
            CancellationToken.None);

        Assert.Equal(ActionHostAuthorizationFailure.EventDigestMismatch,
            result.Failure);
        Assert.Equal(0, scenario.Factory.Calls);
    }

    [Fact]
    public async Task NonSuccessfulWorkflowRunFailsBeforeTransport()
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowRun);
        var bytes = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(scenario.EventBytes)
                .Replace(
                    "\"conclusion\": \"success\"",
                    "\"conclusion\": \"failure\"",
                    StringComparison.Ordinal));
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

        Assert.Equal(ActionHostAuthorizationFailure.RouteInputInvalid,
            result.Failure);
        Assert.Equal(0, scenario.Factory.Calls);
    }
}

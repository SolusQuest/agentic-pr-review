using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;

namespace AgenticPrReview.Runtime.ActionHost.Authorization;

internal enum ActionHostAuthorizationRoute
{
    WorkflowRun = 1,
    WorkflowDispatch,
}

internal enum ActionHostAuthorizationFailure
{
    None = 0,
    UnsupportedEvent,
    EventReadFailed,
    EventDigestMismatch,
    EventMalformed,
    RouteInputInvalid,
    CredentialsMissing,
    RepositoryMismatch,
    CurrentRunMismatch,
    WorkflowSourceInvalid,
    TriggerRunMismatch,
    PullRequestAssociationInvalid,
    ActorMismatch,
    ActorPermissionInsufficient,
    PullRequestMissing,
    PullRequestFork,
    PullRequestDraft,
    PullRequestClosed,
    PullRequestInvalid,
    GitHubReadFailed,
    AuthorizationDeadline,
    Cancelled,
    InternalFailure,
}

internal sealed class ActionHostAuthorizationResult
{
    private ActionHostAuthorizationResult(
        ActionHostAuthorizer.AuthorizedInvocation? invocation,
        ActionHostStatus? rejectionStatus,
        ActionHostAuthorizationFailure failure)
    {
        Invocation = invocation;
        RejectionStatus = rejectionStatus;
        Failure = failure;
    }

    internal ActionHostAuthorizer.AuthorizedInvocation? Invocation { get; }

    internal ActionHostStatus? RejectionStatus { get; }

    internal ActionHostAuthorizationFailure Failure { get; }

    internal static ActionHostAuthorizationResult Granted(
        ActionHostAuthorizer.AuthorizedInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        return new(invocation, null, ActionHostAuthorizationFailure.None);
    }

    internal static ActionHostAuthorizationResult Rejected(
        ActionHostStatus status,
        ActionHostAuthorizationFailure failure)
    {
        if (failure == ActionHostAuthorizationFailure.None)
        {
            throw new ArgumentOutOfRangeException(nameof(failure));
        }

        return new(null, status, failure);
    }
}

internal sealed class ActionHostAuthorizationPolicy
{
    private ActionHostAuthorizationPolicy() { }

    internal const string PrivilegedWorkflowPath =
        ".github/workflows/r4-trusted-proof.yml";
    internal const string TriggerWorkflowPath = ".github/workflows/ci.yml";
    internal const string TriggerWorkflowName = "CI";
    internal const string TriggerEvent = "pull_request";
    internal const string PreflightJobId = "authorization-preflight";
    internal const string WorkflowRunJobId = "workflow-run-review";
    internal const string WorkflowDispatchJobId = "workflow-dispatch-review";
    internal const string PreflightOutput =
        "${{ steps.authorization.outputs.authorized }}";
    internal const string WorkflowRunGuard =
        "${{ github.event_name == 'workflow_run' && " +
        "needs.authorization-preflight.outputs.authorized == 'true' }}";
    internal const string WorkflowDispatchGuard =
        "${{ github.event_name == 'workflow_dispatch' && " +
        "needs.authorization-preflight.outputs.authorized == 'true' }}";
    internal const string ConcurrencyGroup =
        "agentic-pr-review-r4-${{ github.repository_id }}-pr-" +
        "${{ github.event.workflow_run.pull_requests[0].number || " +
        "inputs.pr-number }}";
    internal const string ActionPath =
        "SolusQuest/agentic-pr-review/.github/actions/agentic-pr-review@";

    internal static ActionHostAuthorizationPolicy TrustedProof { get; } = new();

    internal string ActionReference(string actionSourceSha) =>
        ActionPath + actionSourceSha;
}

internal sealed record ActionHostEventFact(
    ActionHostAuthorizationRoute Route,
    ActionHostGitHubRepositoryIdentity Repository,
    ActionHostGitHubActorFact Sender,
    string? Action,
    ActionHostGitHubWorkflowRunFact? WorkflowRun,
    long? DispatchPullRequestNumber);

internal interface IActionHostEventReader
{
    Task<ActionHostEventReadResult> ReadAsync(
        string exactPath,
        CancellationToken cancellationToken);
}

internal sealed class ActionHostEventReadResult
{
    private ActionHostEventReadResult(byte[]? bytes)
    {
        Bytes = bytes;
    }

    internal byte[]? Bytes { get; }

    internal static ActionHostEventReadResult Captured(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return new(bytes);
    }

    internal static ActionHostEventReadResult Failed() => new(null);
}

internal static class ActionHostAuthorizationBounds
{
    internal const int MaximumEventBytes = 1024 * 1024;
    internal const int MaximumEventPathBytes = 4096;
    internal const int MaximumYamlLineCharacters = 4096;
    internal const int MaximumYamlNodes = 4096;
}

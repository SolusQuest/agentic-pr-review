using System.Security.Cryptography;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;

namespace AgenticPrReview.Runtime.ActionHost.Authorization;

internal sealed class ActionHostAuthorizer
{
    private static readonly object MintAuthority = new();

    private readonly IActionHostEventReader _eventReader;
    private readonly IActionHostGitHubAuthorizationTransportFactory
        _transportFactory;
    private readonly ActionHostAuthorizationPolicy _policy;
    private readonly TimeSpan _authorizationTimeout;

    internal ActionHostAuthorizer(
        IActionHostEventReader eventReader,
        IActionHostGitHubAuthorizationTransportFactory transportFactory,
        ActionHostAuthorizationPolicy policy,
        TimeSpan? authorizationTimeout = null)
    {
        _eventReader = eventReader ??
            throw new ArgumentNullException(nameof(eventReader));
        _transportFactory = transportFactory ??
            throw new ArgumentNullException(nameof(transportFactory));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _authorizationTimeout = authorizationTimeout ??
            ActionHostGitHubAuthorizationPolicy.AuthorizationTimeout;
        if (_authorizationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(authorizationTimeout));
        }
    }

    internal async Task<ActionHostAuthorizationResult> AuthorizeAsync(
        ActionHostLaunchContract launch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        if (launch.Cancellation == ActionHostCancellationState.Requested ||
            cancellationToken.IsCancellationRequested)
        {
            return Reject(
                ActionHostStatus.Cancelled,
                ActionHostAuthorizationFailure.Cancelled);
        }

        try
        {
            var captured = await _eventReader.ReadAsync(
                launch.EventJsonPath,
                cancellationToken);
            if (captured.Bytes is null)
            {
                return Reject(
                    ActionHostStatus.AuthorizationFailed,
                    ActionHostAuthorizationFailure.EventReadFailed);
            }

            var eventDigest = Convert.ToHexString(
                SHA256.HashData(captured.Bytes)).ToLowerInvariant();
            if (!StringComparer.Ordinal.Equals(
                    eventDigest,
                    launch.EventJsonSha256))
            {
                return Reject(
                    ActionHostStatus.AuthorizationFailed,
                    ActionHostAuthorizationFailure.EventDigestMismatch);
            }

            if (!ActionHostEventParser.TryParse(
                    captured.Bytes,
                    out var eventFact,
                    out var unsupported))
            {
                return unsupported
                    ? Reject(
                        ActionHostStatus.SkippedUntrustedEvent,
                        ActionHostAuthorizationFailure.UnsupportedEvent)
                    : Reject(
                        ActionHostStatus.AuthorizationFailed,
                        ActionHostAuthorizationFailure.EventMalformed);
            }

            if (!ValidateRouteInputs(launch, eventFact!))
            {
                return Reject(
                    ActionHostStatus.AuthorizationFailed,
                    ActionHostAuthorizationFailure.RouteInputInvalid);
            }

            if (!SameRepository(
                    launch.RepositoryId,
                    launch.RepositoryName,
                    eventFact!.Repository))
            {
                return Reject(
                    ActionHostStatus.AuthorizationFailed,
                    ActionHostAuthorizationFailure.RepositoryMismatch);
            }

            if (launch.Inputs.GitHubToken is null)
            {
                return Reject(
                    ActionHostStatus.CredentialsMissing,
                    ActionHostAuthorizationFailure.CredentialsMissing);
            }

            using var deadline = new CancellationTokenSource(
                _authorizationTimeout);
            using var authorizationCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    deadline.Token);
            using var transport = _transportFactory.Create(
                launch.Inputs.GitHubToken);

            var repositoryResult = await transport.GetRepositoryAsync(
                launch.RepositoryName,
                authorizationCancellation.Token);
            if (repositoryResult.Value is not { } repository ||
                repository.Id != launch.RepositoryId ||
                !StringComparer.Ordinal.Equals(
                    repository.FullName,
                    launch.RepositoryName))
            {
                return RejectGitHubOr(
                    repositoryResult.Failure,
                    ActionHostAuthorizationFailure.RepositoryMismatch);
            }

            var currentRunResult = await transport.GetWorkflowRunAttemptAsync(
                repository.FullName,
                launch.RunId,
                launch.RunAttempt,
                authorizationCancellation.Token);
            if (currentRunResult.Value is not { } currentRun ||
                !ValidateCurrentRun(
                    launch,
                    eventFact,
                    repository,
                    currentRun))
            {
                return RejectGitHubOr(
                    currentRunResult.Failure,
                    ActionHostAuthorizationFailure.CurrentRunMismatch);
            }

            var sourceResult = await transport.GetWorkflowSourceAsync(
                repository.FullName,
                launch.WorkflowPath,
                launch.WorkflowSha,
                authorizationCancellation.Token);
            if (sourceResult.Value is not { } source ||
                !ActionHostTrustedWorkflowPolicy.TryValidate(
                    source.Bytes,
                    _policy,
                    launch.ActionSourceSha,
                    out var workflowEvidence))
            {
                return RejectGitHubOr(
                    sourceResult.Failure,
                    ActionHostAuthorizationFailure.WorkflowSourceInvalid);
            }

            long selectedPullRequestNumber;
            ActionHostGitHubPullRequestFact? associatedPullRequest = null;
            if (eventFact.Route == ActionHostAuthorizationRoute.WorkflowRun)
            {
                var selection = await SelectWorkflowRunPullRequestAsync(
                    transport,
                    repository,
                    currentRun,
                    eventFact,
                    authorizationCancellation.Token);
                if (selection.Failure != ActionHostAuthorizationFailure.None)
                {
                    return Reject(
                        ActionHostStatus.AuthorizationFailed,
                        selection.Failure);
                }

                associatedPullRequest = selection.PullRequest;
                selectedPullRequestNumber = associatedPullRequest!.Number;
            }
            else
            {
                if (!await ValidateDispatchActorAsync(
                        transport,
                        repository,
                        currentRun,
                        eventFact,
                        authorizationCancellation.Token))
                {
                    return Reject(
                        ActionHostStatus.AuthorizationFailed,
                        ActionHostAuthorizationFailure
                            .ActorPermissionInsufficient);
                }

                selectedPullRequestNumber =
                    eventFact.DispatchPullRequestNumber!.Value;
            }

            var pullRequestResult = await transport.GetPullRequestAsync(
                repository.FullName,
                selectedPullRequestNumber,
                authorizationCancellation.Token);
            if (pullRequestResult.Value is not { } pullRequest)
            {
                return RejectGitHubOr(
                    pullRequestResult.Failure,
                    ActionHostAuthorizationFailure.PullRequestMissing);
            }

            var eligibility = ClassifyPullRequest(
                repository,
                pullRequest,
                associatedPullRequest);
            if (eligibility != ActionHostAuthorizationFailure.None)
            {
                return eligibility switch
                {
                    ActionHostAuthorizationFailure.PullRequestFork => Reject(
                        ActionHostStatus.SkippedFork,
                        eligibility),
                    ActionHostAuthorizationFailure.PullRequestDraft => Reject(
                        ActionHostStatus.SkippedDraft,
                        eligibility),
                    ActionHostAuthorizationFailure.PullRequestClosed => Reject(
                        ActionHostStatus.SkippedClosed,
                        eligibility),
                    _ => Reject(
                        ActionHostStatus.AuthorizationFailed,
                        eligibility),
                };
            }

            var frozen = FrozenPullRequest.Mint(
                MintAuthority,
                repository.Id,
                pullRequest.Number,
                pullRequest.BaseSha,
                pullRequest.BaseRepository.Id,
                pullRequest.BaseRepository.FullName,
                pullRequest.HeadSha,
                pullRequest.HeadRepository.Id,
                pullRequest.HeadRepository.FullName,
                pullRequest.State,
                pullRequest.Draft,
                pullRequest.MergedAt is not null);
            var concurrencyIdentity =
                $"agentic-pr-review-r4-{repository.Id}-pr-{pullRequest.Number}";
            var invocation = AuthorizedInvocation.Mint(
                MintAuthority,
                eventFact.Route,
                launch.WorkflowPath,
                launch.WorkflowSha,
                source.BlobSha,
                launch.ActionSourceSha,
                workflowEvidence!.ConcurrencyGroup,
                concurrencyIdentity,
                "actions:write,contents:read,pull-requests:write",
                frozen);
            return ActionHostAuthorizationResult.Granted(invocation);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            return Reject(
                ActionHostStatus.Cancelled,
                ActionHostAuthorizationFailure.Cancelled);
        }
        catch (OperationCanceledException)
        {
            return Reject(
                ActionHostStatus.AuthorizationFailed,
                ActionHostAuthorizationFailure.AuthorizationDeadline);
        }
        catch (ActionHostGitHubCredentialException)
        {
            return Reject(
                ActionHostStatus.AuthorizationFailed,
                ActionHostAuthorizationFailure.GitHubCredentialInvalid);
        }
        catch (Exception exception) when (IsNonFatal(exception))
        {
            return Reject(
                ActionHostStatus.InternalFailure,
                ActionHostAuthorizationFailure.InternalFailure);
        }
    }

    private static bool ValidateRouteInputs(
        ActionHostLaunchContract launch,
        ActionHostEventFact eventFact)
    {
        if (eventFact.Route == ActionHostAuthorizationRoute.WorkflowRun)
        {
            return StringComparer.Ordinal.Equals(
                    eventFact.Action,
                    "completed") &&
                StringComparer.Ordinal.Equals(
                    eventFact.WorkflowRun?.Conclusion,
                    "success") &&
                eventFact.WorkflowRun?.PullRequests.Count == 1 &&
                launch.Inputs.PullRequestNumber is null &&
                launch.Inputs.StateMode == ActionHostStateMode.Auto;
        }

        return eventFact.WorkflowRun is null &&
            eventFact.Action is null &&
            eventFact.DispatchPullRequestNumber is > 0 &&
            launch.Inputs.PullRequestNumber ==
                eventFact.DispatchPullRequestNumber &&
            launch.Inputs.StateMode is
                ActionHostStateMode.Auto or ActionHostStateMode.Reset;
    }

    private static bool ValidateCurrentRun(
        ActionHostLaunchContract launch,
        ActionHostEventFact eventFact,
        ActionHostGitHubRepositoryFact repository,
        ActionHostGitHubWorkflowRunFact run)
    {
        var expectedEvent = eventFact.Route switch
        {
            ActionHostAuthorizationRoute.WorkflowRun => "workflow_run",
            ActionHostAuthorizationRoute.WorkflowDispatch =>
                "workflow_dispatch",
            _ => string.Empty,
        };
        var expectedWorkflowRef =
            $"{repository.FullName}/{launch.WorkflowPath}@" +
            $"refs/heads/{repository.DefaultBranch}";
        return run.Id == launch.RunId &&
            run.Attempt == launch.RunAttempt &&
            SameRepository(repository.Id, repository.FullName, run.Repository) &&
            SameRepository(
                repository.Id,
                repository.FullName,
                run.HeadRepository) &&
            StringComparer.Ordinal.Equals(run.Event, expectedEvent) &&
            StringComparer.Ordinal.Equals(
                run.HeadBranch,
                repository.DefaultBranch) &&
            StringComparer.Ordinal.Equals(run.HeadSha, launch.WorkflowSha) &&
            StringComparer.Ordinal.Equals(
                run.Path,
                launch.WorkflowPath) &&
            StringComparer.Ordinal.Equals(
                launch.WorkflowPath,
                ActionHostAuthorizationPolicy.PrivilegedWorkflowPath) &&
            StringComparer.Ordinal.Equals(
                launch.WorkflowRef,
                expectedWorkflowRef);
    }

    private static async Task<WorkflowRunSelection>
        SelectWorkflowRunPullRequestAsync(
            IActionHostGitHubAuthorizationTransport transport,
            ActionHostGitHubRepositoryFact repository,
            ActionHostGitHubWorkflowRunFact currentRun,
            ActionHostEventFact eventFact,
            CancellationToken cancellationToken)
    {
        var eventRun = eventFact.WorkflowRun!;
        if (eventRun.Id == currentRun.Id &&
                eventRun.Attempt == currentRun.Attempt ||
            eventRun.PullRequests.Count != 1)
        {
            return WorkflowRunSelection.Failed(
                ActionHostAuthorizationFailure.TriggerRunMismatch);
        }

        var triggerResult = await transport.GetWorkflowRunAttemptAsync(
            repository.FullName,
            eventRun.Id,
            eventRun.Attempt,
            cancellationToken);
        if (triggerResult.Value is not { } trigger ||
            !ValidateTriggerRun(repository, eventRun, trigger))
        {
            return WorkflowRunSelection.Failed(
                triggerResult.Failure == ActionHostGitHubFailure.None
                    ? ActionHostAuthorizationFailure.TriggerRunMismatch
                    : ActionHostAuthorizationFailure.GitHubReadFailed);
        }

        var expected = eventRun.PullRequests[0];
        var candidates = new List<ActionHostGitHubPullRequestFact>();
        var seen = new Dictionary<long, ActionHostGitHubPullRequestFact>();
        var total = 0;
        for (var page = 1;
            page <= ActionHostGitHubAuthorizationPolicy
                .MaximumAssociatedPullRequestPages;
            page++)
        {
            var pageResult = await transport.GetCommitPullRequestsAsync(
                repository.FullName,
                trigger.HeadSha,
                page,
                ActionHostGitHubAuthorizationPolicy
                    .AssociatedPullRequestsPerPage,
                cancellationToken);
            if (pageResult.Value is not { } pullRequestPage)
            {
                return WorkflowRunSelection.Failed(
                    ActionHostAuthorizationFailure.GitHubReadFailed);
            }

            total = checked(total + pullRequestPage.PullRequests.Count);
            if (total > ActionHostGitHubAuthorizationPolicy
                    .MaximumAssociatedPullRequests)
            {
                return WorkflowRunSelection.Failed(
                    ActionHostAuthorizationFailure
                        .PullRequestAssociationInvalid);
            }

            foreach (var pullRequest in pullRequestPage.PullRequests)
            {
                if (seen.TryGetValue(pullRequest.Id, out var duplicate))
                {
                    if (!Equals(duplicate, pullRequest))
                    {
                        return WorkflowRunSelection.Failed(
                            ActionHostAuthorizationFailure
                                .PullRequestAssociationInvalid);
                    }

                    return WorkflowRunSelection.Failed(
                        ActionHostAuthorizationFailure
                            .PullRequestAssociationInvalid);
                }

                seen.Add(pullRequest.Id, pullRequest);
                if (SameRepository(
                        repository.Id,
                        repository.FullName,
                        pullRequest.BaseRepository) &&
                    SamePullRequest(expected, pullRequest))
                {
                    candidates.Add(pullRequest);
                }
            }

            if (pullRequestPage.IsComplete)
            {
                return candidates.Count == 1
                    ? WorkflowRunSelection.Selected(candidates[0])
                    : WorkflowRunSelection.Failed(
                        ActionHostAuthorizationFailure
                            .PullRequestAssociationInvalid);
            }
        }

        return WorkflowRunSelection.Failed(
            ActionHostAuthorizationFailure.PullRequestAssociationInvalid);
    }

    private static bool ValidateTriggerRun(
        ActionHostGitHubRepositoryFact repository,
        ActionHostGitHubWorkflowRunFact eventRun,
        ActionHostGitHubWorkflowRunFact trigger)
    {
        return trigger.Id == eventRun.Id &&
            trigger.Attempt == eventRun.Attempt &&
            trigger.WorkflowId == eventRun.WorkflowId &&
            StringComparer.Ordinal.Equals(trigger.Name, eventRun.Name) &&
            StringComparer.Ordinal.Equals(
                trigger.Name,
                ActionHostAuthorizationPolicy.TriggerWorkflowName) &&
            StringComparer.Ordinal.Equals(trigger.Path, eventRun.Path) &&
            StringComparer.Ordinal.Equals(
                trigger.Path,
                ActionHostAuthorizationPolicy.TriggerWorkflowPath) &&
            StringComparer.Ordinal.Equals(trigger.HeadSha, eventRun.HeadSha) &&
            StringComparer.Ordinal.Equals(
                trigger.HeadBranch,
                eventRun.HeadBranch) &&
            StringComparer.Ordinal.Equals(
                trigger.Event,
                ActionHostAuthorizationPolicy.TriggerEvent) &&
            StringComparer.Ordinal.Equals(trigger.Event, eventRun.Event) &&
            StringComparer.Ordinal.Equals(trigger.Conclusion, "success") &&
            StringComparer.Ordinal.Equals(
                trigger.Conclusion,
                eventRun.Conclusion) &&
            SameRepository(repository.Id, repository.FullName, trigger.Repository) &&
            SameRepository(eventRun.Repository, trigger.Repository) &&
            SameRepository(eventRun.HeadRepository, trigger.HeadRepository) &&
            SameActor(eventRun.Actor, trigger.Actor) &&
            SameActor(eventRun.TriggeringActor, trigger.TriggeringActor) &&
            InlinePullRequestsAreCompatible(
                eventRun.PullRequests,
                trigger.PullRequests);
    }

    private static bool InlinePullRequestsAreCompatible(
        IReadOnlyList<ActionHostGitHubPullRequestReferenceFact> eventReferences,
        IReadOnlyList<ActionHostGitHubPullRequestReferenceFact> apiReferences)
    {
        if (eventReferences.Count > 1 || apiReferences.Count > 1)
        {
            return false;
        }

        return eventReferences.Count == 0 ||
            apiReferences.Count == 0 ||
            SamePullRequest(eventReferences[0], apiReferences[0]);
    }

    private static async Task<bool> ValidateDispatchActorAsync(
        IActionHostGitHubAuthorizationTransport transport,
        ActionHostGitHubRepositoryFact repository,
        ActionHostGitHubWorkflowRunFact currentRun,
        ActionHostEventFact eventFact,
        CancellationToken cancellationToken)
    {
        if (!SameActor(eventFact.Sender, currentRun.Actor) ||
            !await HasWritePermissionAsync(
                transport,
                repository.FullName,
                currentRun.Actor.Login,
                cancellationToken))
        {
            return false;
        }

        return SameActor(currentRun.Actor, currentRun.TriggeringActor) ||
            await HasWritePermissionAsync(
                transport,
                repository.FullName,
                currentRun.TriggeringActor.Login,
                cancellationToken);
    }

    private static async Task<bool> HasWritePermissionAsync(
        IActionHostGitHubAuthorizationTransport transport,
        string repositoryName,
        string login,
        CancellationToken cancellationToken)
    {
        var result = await transport.GetCollaboratorPermissionAsync(
            repositoryName,
            login,
            cancellationToken);
        return result.Value?.Permission is "write" or "admin";
    }

    private static ActionHostAuthorizationFailure ClassifyPullRequest(
        ActionHostGitHubRepositoryFact repository,
        ActionHostGitHubPullRequestFact pullRequest,
        ActionHostGitHubPullRequestFact? associated)
    {
        if (!SameRepository(
                repository.Id,
                repository.FullName,
                pullRequest.BaseRepository))
        {
            return ActionHostAuthorizationFailure.PullRequestInvalid;
        }

        if (!SameRepository(
                repository.Id,
                repository.FullName,
                pullRequest.HeadRepository))
        {
            return ActionHostAuthorizationFailure.PullRequestFork;
        }

        if (pullRequest.Draft)
        {
            return ActionHostAuthorizationFailure.PullRequestDraft;
        }

        if (!StringComparer.Ordinal.Equals(pullRequest.State, "open") ||
            pullRequest.MergedAt is not null)
        {
            return ActionHostAuthorizationFailure.PullRequestClosed;
        }

        return associated is null || Equals(associated, pullRequest)
            ? ActionHostAuthorizationFailure.None
            : ActionHostAuthorizationFailure.PullRequestInvalid;
    }

    private static bool SamePullRequest(
        ActionHostGitHubPullRequestReferenceFact reference,
        ActionHostGitHubPullRequestReferenceFact candidate) =>
        reference.Id == candidate.Id &&
        reference.Number == candidate.Number &&
        StringComparer.Ordinal.Equals(reference.BaseSha, candidate.BaseSha) &&
        SameRepository(reference.BaseRepository, candidate.BaseRepository) &&
        StringComparer.Ordinal.Equals(reference.HeadSha, candidate.HeadSha) &&
        SameRepository(reference.HeadRepository, candidate.HeadRepository);

    private static bool SamePullRequest(
        ActionHostGitHubPullRequestReferenceFact reference,
        ActionHostGitHubPullRequestFact candidate) =>
        reference.Id == candidate.Id &&
        reference.Number == candidate.Number &&
        StringComparer.Ordinal.Equals(reference.BaseSha, candidate.BaseSha) &&
        SameRepository(reference.BaseRepository, candidate.BaseRepository) &&
        StringComparer.Ordinal.Equals(reference.HeadSha, candidate.HeadSha) &&
        SameRepository(reference.HeadRepository, candidate.HeadRepository);

    private static bool SameRepository(
        ActionHostGitHubRepositoryReference reference,
        ActionHostGitHubRepositoryIdentity candidate)
    {
        var separator = candidate.FullName.IndexOf('/');
        return candidate.Id == reference.Id &&
            separator > 0 &&
            StringComparer.Ordinal.Equals(
                reference.Name,
                candidate.FullName[(separator + 1)..]);
    }

    private static bool SameRepository(
        ActionHostGitHubRepositoryReference left,
        ActionHostGitHubRepositoryReference right) =>
        left.Id == right.Id &&
        StringComparer.Ordinal.Equals(left.Name, right.Name);

    private static bool SameRepository(
        long id,
        string fullName,
        ActionHostGitHubRepositoryIdentity candidate) =>
        candidate.Id == id &&
        StringComparer.Ordinal.Equals(candidate.FullName, fullName);

    private static bool SameRepository(
        ActionHostGitHubRepositoryIdentity left,
        ActionHostGitHubRepositoryIdentity right) =>
        left.Id == right.Id &&
        StringComparer.Ordinal.Equals(left.FullName, right.FullName);

    private static bool SameActor(
        ActionHostGitHubActorFact left,
        ActionHostGitHubActorFact right) =>
        left.Id == right.Id &&
        StringComparer.Ordinal.Equals(left.Login, right.Login);

    private static ActionHostAuthorizationResult RejectGitHubOr(
        ActionHostGitHubFailure githubFailure,
        ActionHostAuthorizationFailure factFailure) =>
        Reject(
            ActionHostStatus.AuthorizationFailed,
            githubFailure == ActionHostGitHubFailure.None
                ? factFailure
                : ActionHostAuthorizationFailure.GitHubReadFailed);

    private static ActionHostAuthorizationResult Reject(
        ActionHostStatus status,
        ActionHostAuthorizationFailure failure) =>
        ActionHostAuthorizationResult.Rejected(status, failure);

    private static bool IsNonFatal(Exception exception) =>
        exception is not OutOfMemoryException and
        not StackOverflowException and
        not AccessViolationException;

    private sealed record WorkflowRunSelection(
        ActionHostGitHubPullRequestFact? PullRequest,
        ActionHostAuthorizationFailure Failure)
    {
        internal static WorkflowRunSelection Selected(
            ActionHostGitHubPullRequestFact pullRequest) =>
            new(pullRequest, ActionHostAuthorizationFailure.None);

        internal static WorkflowRunSelection Failed(
            ActionHostAuthorizationFailure failure) => new(null, failure);
    }

    internal sealed class FrozenPullRequest
    {
        private FrozenPullRequest(
            long repositoryId,
            long number,
            string baseSha,
            long baseRepositoryId,
            string baseRepositoryName,
            string headSha,
            long headRepositoryId,
            string headRepositoryName,
            string state,
            bool draft,
            bool merged)
        {
            RepositoryId = repositoryId;
            Number = number;
            BaseSha = baseSha;
            BaseRepositoryId = baseRepositoryId;
            BaseRepositoryName = baseRepositoryName;
            HeadSha = headSha;
            HeadRepositoryId = headRepositoryId;
            HeadRepositoryName = headRepositoryName;
            State = state;
            Draft = draft;
            Merged = merged;
        }

        internal static FrozenPullRequest Mint(
            object authority,
            long repositoryId,
            long number,
            string baseSha,
            long baseRepositoryId,
            string baseRepositoryName,
            string headSha,
            long headRepositoryId,
            string headRepositoryName,
            string state,
            bool draft,
            bool merged)
        {
            if (!ReferenceEquals(authority, MintAuthority))
            {
                throw new InvalidOperationException(
                    "Only the ActionHost authorizer may freeze a pull request.");
            }

            return new(
                repositoryId,
                number,
                baseSha,
                baseRepositoryId,
                baseRepositoryName,
                headSha,
                headRepositoryId,
                headRepositoryName,
                state,
                draft,
                merged);
        }

        internal long RepositoryId { get; }
        internal long Number { get; }
        internal string BaseSha { get; }
        internal long BaseRepositoryId { get; }
        internal string BaseRepositoryName { get; }
        internal string HeadSha { get; }
        internal long HeadRepositoryId { get; }
        internal string HeadRepositoryName { get; }
        internal string State { get; }
        internal bool Draft { get; }
        internal bool Merged { get; }
    }

    internal sealed class AuthorizedInvocation
    {
        private AuthorizedInvocation(
            ActionHostAuthorizationRoute route,
            string workflowPath,
            string workflowCommitSha,
            string workflowBlobSha,
            string actionSourceSha,
            string concurrencyDefinition,
            string concurrencyIdentity,
            string declaredPermissions,
            FrozenPullRequest pullRequest)
        {
            Route = route;
            WorkflowPath = workflowPath;
            WorkflowCommitSha = workflowCommitSha;
            WorkflowBlobSha = workflowBlobSha;
            ActionSourceSha = actionSourceSha;
            ConcurrencyDefinition = concurrencyDefinition;
            ConcurrencyIdentity = concurrencyIdentity;
            DeclaredPermissions = declaredPermissions;
            PullRequest = pullRequest;
        }

        internal static AuthorizedInvocation Mint(
            object authority,
            ActionHostAuthorizationRoute route,
            string workflowPath,
            string workflowCommitSha,
            string workflowBlobSha,
            string actionSourceSha,
            string concurrencyDefinition,
            string concurrencyIdentity,
            string declaredPermissions,
            FrozenPullRequest pullRequest)
        {
            if (!ReferenceEquals(authority, MintAuthority))
            {
                throw new InvalidOperationException(
                    "Only the ActionHost authorizer may mint authorization.");
            }

            return new(
                route,
                workflowPath,
                workflowCommitSha,
                workflowBlobSha,
                actionSourceSha,
                concurrencyDefinition,
                concurrencyIdentity,
                declaredPermissions,
                pullRequest);
        }

        internal ActionHostAuthorizationRoute Route { get; }
        internal string WorkflowPath { get; }
        internal string WorkflowCommitSha { get; }
        internal string WorkflowBlobSha { get; }
        internal string ActionSourceSha { get; }
        internal string ConcurrencyDefinition { get; }
        internal string ConcurrencyIdentity { get; }
        internal string DeclaredPermissions { get; }
        internal FrozenPullRequest PullRequest { get; }
    }
}

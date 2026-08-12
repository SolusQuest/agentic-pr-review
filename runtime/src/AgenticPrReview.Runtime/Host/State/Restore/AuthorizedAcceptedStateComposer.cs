using System.Globalization;
using System.Security.Cryptography;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.ActionHost.Policy;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Host.State.GitHubArtifacts;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;

namespace AgenticPrReview.Runtime.Host.State.Restore;

internal sealed record ArtifactStateRestoreRequest(
    ActionHostLaunchContract Launch,
    ActionHostAuthorizer.AuthorizedInvocation Invocation,
    ActionHostTrustedPolicy TrustedPolicy,
    ProjectChatMessage CurrentReviewContext,
    IAgentContinuationCodec ContinuationCodec,
    IAcceptedStateProductionDependencies? Dependencies = null,
    TimeProvider? TimeProvider = null);

internal sealed record AuthorizedAcceptedStateRestoreResult(
    string Code,
    bool IsBootstrap,
    AuthorizedAcceptedStateRestoreContext? Context)
{
    internal bool Succeeded =>
        Context is not null &&
        (StringComparer.Ordinal.Equals(Code, AcceptedStateCodes.Ready) ||
            StringComparer.Ordinal.Equals(
                Code,
                AcceptedStateCodes.Bootstrap));

    internal static AuthorizedAcceptedStateRestoreResult Ready(
        AuthorizedAcceptedStateRestoreContext context) =>
        new(AcceptedStateCodes.Ready, false, context);

    internal static AuthorizedAcceptedStateRestoreResult Bootstrap(
        AuthorizedAcceptedStateRestoreContext context) =>
        new(AcceptedStateCodes.Bootstrap, true, context);

    internal static AuthorizedAcceptedStateRestoreResult Fail(string code) =>
        new(code, false, null);
}

internal sealed class AuthorizedAcceptedStateRestoreContext : IDisposable
{
    private AuthorizedLocatorAccess? access;
    private LocatorStateKeyRing? keys;
    private LocatorContext? locator;
    private SelectedLineageContext? lineage;
    private AcceptedStateContext? accepted;

    internal AuthorizedAcceptedStateRestoreContext(
        AuthorizedLocatorAccess access,
        LocatorStateKeyRing keys,
        LocatorContext locator,
        SelectedLineageContext lineage,
        AcceptedStateContext? accepted)
    {
        this.access = access;
        this.keys = keys;
        this.locator = locator;
        this.lineage = lineage;
        this.accepted = accepted;
    }

    internal bool HasAcceptedSession => Volatile.Read(ref accepted) is not null;

    internal bool TryGetLineageSnapshot(
        out SelectedLineageSnapshot? snapshot)
    {
        snapshot = null;
        var currentAccess = Volatile.Read(ref access);
        var currentLineage = Volatile.Read(ref lineage);
        return currentAccess is not null &&
            currentLineage is not null &&
            currentLineage.TryGetSnapshot(currentAccess, out snapshot);
    }

    internal bool TryGetAdmittedValue(
        out AgentSessionStateAdmittedValue? value)
    {
        value = null;
        return Volatile.Read(ref accepted)?.TryGetAdmittedValue(out value) ==
            true;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref accepted, null)?.Dispose();
        Interlocked.Exchange(ref lineage, null)?.Dispose();
        Interlocked.Exchange(ref locator, null)?.Dispose();
        Interlocked.Exchange(ref keys, null)?.Dispose();
        Interlocked.Exchange(ref access, null)?.Dispose();
    }

    public override string ToString() =>
        nameof(AuthorizedAcceptedStateRestoreContext);
}

internal interface IAcceptedStateProductionDependencies
{
    IRestrictedStateStore CreateArtifactStore(ActionHostLaunchContract launch);

    IActionHostGitObjectTransport CreateAncestryTransport(
        ActionHostGitHubToken token);
}

internal sealed class AcceptedStateProductionDependencies :
    IAcceptedStateProductionDependencies
{
    private readonly IActionHostGitObjectTransportFactory gitFactory =
        new ActionHostGitHubAuthorizationTransportFactory();

    public IRestrictedStateStore CreateArtifactStore(
        ActionHostLaunchContract launch) =>
        new GitHubArtifactRestrictedStateStore(
            launch.ArtifactBridgeEndpoint,
            launch.BuildDiscriminator);

    public IActionHostGitObjectTransport CreateAncestryTransport(
        ActionHostGitHubToken token) =>
        gitFactory.CreateExactObjectTransport(token);
}

internal sealed class AcceptedStateProductionAuthorization
{
    private AcceptedStateProductionAuthorization(
        ActionHostLaunchContract launch,
        ActionHostAuthorizer.AuthorizedInvocation invocation,
        ActionHostTrustedPolicy policy)
    {
        Launch = launch;
        Invocation = invocation;
        Policy = policy;
    }

    internal ActionHostLaunchContract Launch { get; }
    internal ActionHostAuthorizer.AuthorizedInvocation Invocation { get; }
    internal ActionHostTrustedPolicy Policy { get; }

    internal static bool TryAuthorize(
        ArtifactStateRestoreRequest? request,
        out AcceptedStateProductionAuthorization? authorization)
    {
        authorization = null;
        if (request is null ||
            request.Launch is null ||
            request.Invocation is null ||
            request.TrustedPolicy is null ||
            request.CurrentReviewContext is null ||
            request.ContinuationCodec is null)
        {
            return false;
        }

        var launch = request.Launch;
        var invocation = request.Invocation;
        var policy = request.TrustedPolicy;
        var pullRequest = invocation.PullRequest;
        var validRoute = invocation.Route switch
        {
            ActionHostAuthorizationRoute.WorkflowRun =>
                launch.Inputs.StateMode == ActionHostStateMode.Auto,
            ActionHostAuthorizationRoute.WorkflowDispatch =>
                launch.Inputs.StateMode is
                    ActionHostStateMode.Auto or ActionHostStateMode.Reset,
            _ => false,
        };
        if (!validRoute ||
            !invocation.IsBoundTo(launch) ||
            launch.Inputs.StateKey is null ||
            launch.Inputs.GitHubToken is null ||
            (invocation.Route == ActionHostAuthorizationRoute.WorkflowRun
                ? launch.Inputs.PullRequestNumber is { } suppliedNumber &&
                    suppliedNumber != pullRequest.Number
                : launch.Inputs.PullRequestNumber != pullRequest.Number) ||
            launch.RepositoryId != pullRequest.RepositoryId ||
            launch.RepositoryId != pullRequest.BaseRepositoryId ||
            launch.RepositoryId != pullRequest.HeadRepositoryId ||
            !StringComparer.Ordinal.Equals(
                launch.RepositoryName,
                pullRequest.BaseRepositoryName) ||
            !StringComparer.Ordinal.Equals(
                launch.RepositoryName,
                pullRequest.HeadRepositoryName) ||
            !StringComparer.Ordinal.Equals(
                launch.WorkflowPath,
                invocation.WorkflowPath) ||
            !StringComparer.Ordinal.Equals(
                launch.WorkflowSha,
                invocation.WorkflowCommitSha) ||
            !StringComparer.Ordinal.Equals(
                launch.ActionSourceSha,
                invocation.ActionSourceSha) ||
            policy.RepositoryId != launch.RepositoryId ||
            !StringComparer.Ordinal.Equals(
                policy.RepositoryName,
                launch.RepositoryName) ||
            !StringComparer.Ordinal.Equals(
                policy.WorkflowPath,
                invocation.WorkflowPath) ||
            !StringComparer.Ordinal.Equals(
                policy.WorkflowCommitSha,
                invocation.WorkflowCommitSha) ||
            !StringComparer.Ordinal.Equals(
                policy.WorkflowBlobSha,
                invocation.WorkflowBlobSha) ||
            !StringComparer.Ordinal.Equals(
                policy.ActionSourceSha,
                launch.ActionSourceSha) ||
            !StringComparer.Ordinal.Equals(
                policy.PayloadSha256,
                launch.PayloadSha256) ||
            !StringComparer.Ordinal.Equals(
                policy.BuildDiscriminator,
                launch.BuildDiscriminator) ||
            !StringComparer.Ordinal.Equals(
                policy.ConfigPath,
                launch.Inputs.ConfigPath ??
                    ActionHostTrustedPolicyRequest.DefaultConfigPath) ||
            policy.StateRetentionSeconds !=
                AcceptedStateFormat.LogicalWindowSeconds)
        {
            return false;
        }

        authorization = new AcceptedStateProductionAuthorization(
            launch,
            invocation,
            policy);
        return true;
    }

    internal bool AllowsLocator(string repositoryId) =>
        StringComparer.Ordinal.Equals(
            repositoryId,
            Launch.RepositoryId.ToString(CultureInfo.InvariantCulture));

    internal bool AllowsReset(
        string repositoryId,
        long pullRequestNumber,
        string trustedWorkflowIdentity,
        string producingRunIdentity,
        long producingRunAttempt) =>
        Invocation.Route == ActionHostAuthorizationRoute.WorkflowDispatch &&
        Launch.Inputs.StateMode == ActionHostStateMode.Reset &&
        AllowsLocator(repositoryId) &&
        pullRequestNumber == Invocation.PullRequest.Number &&
        StringComparer.Ordinal.Equals(
            trustedWorkflowIdentity,
            AuthorizedAcceptedStateComposer.TrustedWorkflowIdentity(
                Invocation,
                Policy)) &&
        StringComparer.Ordinal.Equals(
            producingRunIdentity,
            Launch.RunId.ToString(CultureInfo.InvariantCulture)) &&
        producingRunAttempt == Launch.RunAttempt;

    public override string ToString() => nameof(AcceptedStateProductionAuthorization);
}

internal sealed class AuthorizedAcceptedStateComposer
{
    internal async Task<AuthorizedAcceptedStateRestoreResult> RestoreAsync(
        ArtifactStateRestoreRequest request,
        CancellationToken cancellationToken)
    {
        if (!AcceptedStateProductionAuthorization.TryAuthorize(
                request,
                out var authorization) ||
            authorization is null)
        {
            return AuthorizedAcceptedStateRestoreResult.Fail(
                AcceptedStateCodes.AccessDenied);
        }

        var launch = request.Launch;
        var invocation = request.Invocation;
        var policy = request.TrustedPolicy;
        var repositoryId = launch.RepositoryId.ToString(
            CultureInfo.InvariantCulture);
        var access = AuthorizedLocatorAccess.Issue(
            authorization,
            repositoryId);
        if (access is null)
        {
            return AuthorizedAcceptedStateRestoreResult.Fail(
                AcceptedStateCodes.AccessDenied);
        }

        LocatorStateKeyRing? keys = null;
        LocatorContext? locator = null;
        SelectedLineageContext? selectedLineage = null;
        AcceptedStateContext? accepted = null;
        var ownershipTransferred = false;
        try
        {
            var currentStateKey = launch.Inputs.StateKey!
                .ExportForPrivateLaunch();
            var previousStateKey = launch.Inputs.PreviousStateKey?
                .ExportForPrivateLaunch();
            if (!LocatorStateKeyRing.TryCreate(
                    access,
                    repositoryId,
                    currentStateKey,
                    previousStateKey,
                    out keys,
                    out _) ||
                keys is null)
            {
                return AuthorizedAcceptedStateRestoreResult.Fail(
                    AcceptedStateCodes.KeyUnavailable);
            }

            var dependencies = request.Dependencies ??
                new AcceptedStateProductionDependencies();
            var store = dependencies.CreateArtifactStore(launch);
            var timeProvider = request.TimeProvider ?? TimeProvider.System;
            var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
            if (!LineageValidation.IsTime(now) ||
                now > RestrictedStateFormat.MaximumUnixSeconds -
                    AcceptedStateFormat.LogicalWindowSeconds)
            {
                return AuthorizedAcceptedStateRestoreResult.Fail(
                    AcceptedStateCodes.OutcomeUnknown);
            }

            var logicalExpiry = checked(
                now + AcceptedStateFormat.LogicalWindowSeconds);
            var requiredPlatformExpiry = Math.Max(
                checked(now +
                    StateRetentionRequirements.ScopedPlatformRequestSeconds),
                checked(logicalExpiry +
                    StateRetentionRequirements.SentinelDependentMarginSeconds));
            var locatorResult = await new LocatorRootService(
                    store,
                    keys,
                    timeProvider)
                .ResolveAsync(
                    access,
                    requiredPlatformExpiry,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!locatorResult.Succeeded || locatorResult.Context is null)
            {
                return AuthorizedAcceptedStateRestoreResult.Fail(
                    MapLocatorCode(locatorResult.Code));
            }

            locator = locatorResult.Context;
            var baseScope = BaseScope(authorization);
            var publicationScope = new R4PublicationScopeV1(
                (ulong)launch.RepositoryId,
                (ulong)launch.RepositoryId,
                invocation.WorkflowPath,
                launch.WorkflowRef,
                (ulong)invocation.PullRequest.Number,
                policy.PolicySha256,
                baseScope.PayloadBuildIdentity);
            var publicationBinding = new AcceptedStatePublicationBinding(
                publicationScope,
                R4PublicationIdentityV1.ComputeScopeSha256(publicationScope),
                launch.RepositoryName,
                policy.PayloadSha256,
                policy.BuildDiscriminator);
            var reviewed = new ReviewedTransitionFacts(
                invocation.PullRequest.BaseSha,
                invocation.PullRequest.HeadSha);
            var runIdentity = launch.RunId.ToString(
                CultureInfo.InvariantCulture);
            var lineageRequest = new LineageResolveRequest(
                access,
                baseScope,
                reviewed,
                runIdentity,
                launch.RunAttempt,
                logicalExpiry,
                Reset: null);
            var lineageService = new LineageService(store, timeProvider);
            var observedResult = await lineageService.ObserveReadOnlyAsync(
                    locator,
                    lineageRequest,
                    cancellationToken)
                .ConfigureAwait(false);
            using var observation = observedResult.Context;
            if (!observedResult.Succeeded || observation is null)
            {
                return AuthorizedAcceptedStateRestoreResult.Fail(
                    MapLineageCode(observedResult.Code));
            }

            if (launch.Inputs.StateMode == ActionHostStateMode.Reset)
            {
                var reset = await ResolveResetAsync(
                        authorization,
                        lineageService,
                        locator,
                        lineageRequest,
                        observation,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!reset.Succeeded || reset.Context is null)
                {
                    return AuthorizedAcceptedStateRestoreResult.Fail(
                        MapLineageCode(reset.Code));
                }

                selectedLineage = reset.Context;
                var context = Transfer(
                    access,
                    keys,
                    locator,
                    selectedLineage,
                    accepted: null);
                ownershipTransferred = true;
                return AuthorizedAcceptedStateRestoreResult.Bootstrap(context);
            }

            var selectorResult = new AcceptedStateSelector(timeProvider)
                .Select(observation, lineageRequest);
            if (selectorResult.IsBootstrap)
            {
                if (selectorResult.InitialAbsence is not null)
                {
                    var initialized = await lineageService
                        .InitializeAuthorizedAsync(
                            locator,
                            lineageRequest,
                            selectorResult.InitialAbsence,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (!initialized.Succeeded || initialized.Context is null)
                    {
                        return AuthorizedAcceptedStateRestoreResult.Fail(
                            MapLineageCode(initialized.Code));
                    }

                    selectedLineage = initialized.Context;
                }
                else
                {
                    selectedLineage = SelectedContext(
                        access,
                        repositoryId,
                        observation.Selection.Selection!.Head);
                }

                var context = Transfer(
                    access,
                    keys,
                    locator,
                    selectedLineage,
                    accepted: null);
                ownershipTransferred = true;
                return AuthorizedAcceptedStateRestoreResult.Bootstrap(context);
            }

            if (selectorResult.Expiry is not null)
            {
                var expired = await lineageService.ExpireAuthorizedAsync(
                        locator,
                        lineageRequest,
                        selectorResult.Expiry,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!expired.Succeeded || expired.Context is null)
                {
                    return AuthorizedAcceptedStateRestoreResult.Fail(
                        MapLineageCode(expired.Code));
                }

                selectedLineage = expired.Context;
                var context = Transfer(
                    access,
                    keys,
                    locator,
                    selectedLineage,
                    accepted: null);
                ownershipTransferred = true;
                return AuthorizedAcceptedStateRestoreResult.Bootstrap(context);
            }

            if (!selectorResult.Succeeded || selectorResult.Selection is null)
            {
                return AuthorizedAcceptedStateRestoreResult.Fail(
                    selectorResult.Code);
            }

            selectedLineage = SelectedContext(
                access,
                repositoryId,
                selectorResult.Selection.LineageHead);
            var stateScope = new RestrictedStateScope(
                repositoryId,
                baseScope.TrustedWorkflowIdentity,
                invocation.PullRequest.Number,
                selectorResult.Selection.LineageHead.Header.SessionId,
                policy.ProviderId,
                policy.ModelId,
                policy.AdapterId,
                policy.InstructionsSha256,
                policy.LimitsSha256,
                policy.ToolsetSha256,
                policy.BuildDiscriminator);
            var accessResult = AuthorizedStateAccess.Authorize(
                new RestrictedStateAccessRequest(
                    stateScope,
                    stateScope,
                    IsTrustedWorkflow: true,
                    IsSameRepository: true,
                    IsForkOrigin: false),
                out var stateAccess);
            if (!StringComparer.Ordinal.Equals(
                    accessResult.Code,
                    RestrictedStateCodes.Authorized) ||
                stateAccess is null)
            {
                return AuthorizedAcceptedStateRestoreResult.Fail(
                    AcceptedStateCodes.AccessDenied);
            }

            var trustedPolicyBytes = policy.InstructionBytes.ToArray();
            try
            {
                var trustedRequest = new AgentSessionTrustedRequest(
                    repositoryId,
                    invocation.PullRequest.Number,
                    baseScope.TrustedWorkflowIdentity,
                    trustedPolicyBytes,
                    policy.BuildDiscriminator,
                    policy.ProviderId,
                    policy.ModelId,
                    policy.AdapterId);
                var currentReviewedIdentity = new ReviewedIdentity(
                    repositoryId,
                    invocation.PullRequest.Number,
                    invocation.PullRequest.BaseSha,
                    invocation.PullRequest.HeadSha);
                using var gitTransport =
                    dependencies.CreateAncestryTransport(
                        launch.Inputs.GitHubToken!);
                var restored = await new AcceptedStateRestoreService()
                    .RestoreAsync(
                        stateAccess,
                        access,
                        locator,
                        selectorResult.Selection,
                        new AcceptedStatePolicyBinding(
                            policy.PolicySha256,
                            policy.ConfigSha256,
                            policy.InstructionsSha256,
                            policy.PayloadSha256,
                            policy.BuildDiscriminator),
                        publicationBinding,
                        trustedRequest,
                        currentReviewedIdentity,
                        request.CurrentReviewContext,
                        request.ContinuationCodec,
                        new TrustedHeadAncestryClassifier(
                            gitTransport,
                            timeProvider),
                        launch.RepositoryName,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!restored.Succeeded || restored.Context is null)
                {
                    return AuthorizedAcceptedStateRestoreResult.Fail(
                        restored.Code);
                }

                accepted = restored.Context;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(trustedPolicyBytes);
            }

            var readyContext = Transfer(
                access,
                keys,
                locator,
                selectedLineage,
                accepted);
            ownershipTransferred = true;
            return AuthorizedAcceptedStateRestoreResult.Ready(readyContext);
        }
        catch (OperationCanceledException)
        {
            return AuthorizedAcceptedStateRestoreResult.Fail(
                AcceptedStateCodes.OutcomeUnknown);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                InvalidOperationException or
                OverflowException or
                CryptographicException or
                IOException or
                UnauthorizedAccessException)
        {
            return AuthorizedAcceptedStateRestoreResult.Fail(
                AcceptedStateCodes.OutcomeUnknown);
        }
        finally
        {
            if (!ownershipTransferred)
            {
                accepted?.Dispose();
                selectedLineage?.Dispose();
                locator?.Dispose();
                keys?.Dispose();
                access.Dispose();
            }
        }
    }

    internal static string TrustedWorkflowIdentity(
        ActionHostAuthorizer.AuthorizedInvocation invocation,
        ActionHostTrustedPolicy policy) =>
        Hash(
            "apr.trusted-workflow.s5",
            invocation.WorkflowPath,
            invocation.WorkflowBlobSha,
            policy.WorkflowCommitSha);

    internal static LineageBaseScope BaseScope(
        AcceptedStateProductionAuthorization authorization)
    {
        var launch = authorization.Launch;
        var invocation = authorization.Invocation;
        var policy = authorization.Policy;
        return new LineageBaseScope(
            launch.RepositoryId.ToString(CultureInfo.InvariantCulture),
            TrustedWorkflowIdentity(invocation, policy),
            Hash(
                "apr.trusted-source.s5",
                invocation.WorkflowCommitSha,
                invocation.ActionSourceSha),
            invocation.PullRequest.Number,
            policy.ProviderId,
            policy.ModelId,
            policy.AdapterId,
            policy.ConfigSha256,
            policy.InstructionsSha256,
            policy.ToolsetSha256,
            policy.LimitsSha256,
            PayloadBuildIdentity(policy));
    }

    internal static string PayloadBuildIdentity(
        ActionHostTrustedPolicy policy) =>
        Hash(
            "apr.payload-build.s5",
            policy.PolicySha256,
            policy.PayloadSha256,
            policy.BuildDiscriminator);

    private static async Task<LineageResolveResult> ResolveResetAsync(
        AcceptedStateProductionAuthorization authorization,
        LineageService service,
        LocatorContext locator,
        LineageResolveRequest request,
        LineageReadOnlyObservationContext observation,
        CancellationToken cancellationToken)
    {
        if (observation.Selection.IsAbsent)
        {
            var initial = new AcceptedStateSelector(TimeProvider.System)
                .Select(observation, request);
            return initial.InitialAbsence is null
                ? LineageResolveResult.Fail(LineageCodes.Conflict)
                : await service.InitializeAuthorizedAsync(
                        locator,
                        request,
                        initial.InitialAbsence,
                        cancellationToken)
                    .ConfigureAwait(false);
        }

        var selected = observation.Selection.Selection?.Head;
        if (selected is null ||
            !LineageBaseScopeCodec.TryDigest(
                request.BaseScope,
                out var baseScopeDigest))
        {
            return LineageResolveResult.Fail(LineageCodes.Conflict);
        }

        var requestIdentity = Hash(
            "apr.reset-authority.s5",
            request.ProducingRunIdentity,
            request.ProducingRunAttempt.ToString(CultureInfo.InvariantCulture),
            selected.Header.ObjectIdentity);
        var reset = AuthorizedLineageReset.Issue(
            authorization,
            request.Access,
            request.BaseScope,
            baseScopeDigest,
            request.ProducingRunIdentity,
            request.ProducingRunAttempt,
            requestIdentity,
            selected.Header.ObjectIdentity);
        if (reset is null)
        {
            return LineageResolveResult.Fail(LineageCodes.AccessDenied);
        }

        return await service.ResolveAsync(
                locator,
                request with { Reset = reset },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static SelectedLineageContext SelectedContext(
        AuthorizedLocatorAccess access,
        string repositoryId,
        LineageHeadCandidate head) =>
        new(
            access,
            repositoryId,
            new SelectedLineageSnapshot(
                head.Header.BaseScopeDigest,
                head.Header.Epoch,
                head.Header.SessionId,
                head.Header.ObjectIdentity,
                head.Head.Transition));

    private static AuthorizedAcceptedStateRestoreContext Transfer(
        AuthorizedLocatorAccess access,
        LocatorStateKeyRing keys,
        LocatorContext locator,
        SelectedLineageContext lineage,
        AcceptedStateContext? accepted) =>
        new(access, keys, locator, lineage, accepted);

    private static string Hash(string domain, params string[] values)
    {
        var writer = new LineageBinaryWriter();
        writer.WriteString(domain);
        foreach (var value in values)
        {
            writer.WriteString(value);
        }

        var bytes = writer.ToArray();
        try
        {
            return AcceptedStateRecordValidation.Sha256(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string MapLocatorCode(string code) =>
        StringComparer.Ordinal.Equals(code, LocatorCodes.AccessDenied)
            ? AcceptedStateCodes.AccessDenied
            : StringComparer.Ordinal.Equals(code, LocatorCodes.KeyUnavailable)
                ? AcceptedStateCodes.KeyUnavailable
                : StringComparer.Ordinal.Equals(code, LocatorCodes.Conflict)
                    ? AcceptedStateCodes.Conflict
                    : AcceptedStateCodes.OutcomeUnknown;

    private static string MapLineageCode(string code) =>
        StringComparer.Ordinal.Equals(code, LineageCodes.AccessDenied)
            ? AcceptedStateCodes.AccessDenied
            : StringComparer.Ordinal.Equals(code, LineageCodes.KeyUnavailable)
                ? AcceptedStateCodes.KeyUnavailable
                : StringComparer.Ordinal.Equals(code, LineageCodes.Conflict)
                    ? AcceptedStateCodes.Conflict
                    : AcceptedStateCodes.OutcomeUnknown;
}

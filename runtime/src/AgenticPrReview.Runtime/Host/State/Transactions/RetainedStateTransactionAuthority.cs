using System.Security.Cryptography;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.Restore;

namespace AgenticPrReview.Runtime.Host.State.Transactions;

internal sealed class RetainedStateTransactionAuthority : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly AcceptedStateProductionAuthorization production;
    private readonly IRestrictedStateStore store;
    private readonly TimeProvider timeProvider;
    private readonly AuthorizedStateAccess stateAccess;
    private readonly LineageBaseScope baseScope;
    private readonly ReviewedTransitionFacts reviewed;
    private readonly string producingRunIdentity;
    private readonly long producingRunAttempt;
    private readonly AcceptedStatePolicyBinding policy;
    private readonly AcceptedStatePublicationBinding publication;
    private readonly AgentSessionTrustedRequest trustedRequest;
    private readonly ReviewedIdentity currentReviewedIdentity;
    private readonly ProjectChatMessage currentReviewContext;
    private readonly IAgentContinuationCodec continuationCodec;
    private readonly string initialInventoryDigest;
    private AuthorizedLocatorAccess? locatorAccess;
    private LocatorStateKeyRing? keys;
    private LocatorContext? locator;
    private SelectedLineageContext? lineage;
    private AcceptedStateContext? accepted;
    private AcceptedStateSelection? acceptedSelection;
    private VerifiedRetainedStateAcceptance? terminalAcceptance;
    private int disposed;

    private RetainedStateTransactionAuthority(
        AcceptedStateProductionAuthorization production,
        AuthorizedLocatorAccess locatorAccess,
        LocatorStateKeyRing keys,
        LocatorContext locator,
        SelectedLineageContext lineage,
        IRestrictedStateStore store,
        TimeProvider timeProvider,
        AuthorizedStateAccess stateAccess,
        LineageBaseScope baseScope,
        ReviewedTransitionFacts reviewed,
        string producingRunIdentity,
        long producingRunAttempt,
        AcceptedStatePolicyBinding policy,
        AcceptedStatePublicationBinding publication,
        AgentSessionTrustedRequest trustedRequest,
        ReviewedIdentity currentReviewedIdentity,
        ProjectChatMessage currentReviewContext,
        IAgentContinuationCodec continuationCodec,
        AcceptedStateContext? accepted,
        AcceptedStateSelection? acceptedSelection,
        string initialInventoryDigest)
    {
        this.production = production;
        this.locatorAccess = locatorAccess;
        this.keys = keys;
        this.locator = locator;
        this.lineage = lineage;
        this.store = store;
        this.timeProvider = timeProvider;
        this.stateAccess = stateAccess;
        this.baseScope = baseScope;
        this.reviewed = reviewed;
        this.producingRunIdentity = producingRunIdentity;
        this.producingRunAttempt = producingRunAttempt;
        this.policy = policy;
        this.publication = publication;
        this.trustedRequest = trustedRequest;
        this.currentReviewedIdentity = currentReviewedIdentity;
        this.currentReviewContext = currentReviewContext;
        this.continuationCodec = continuationCodec;
        this.accepted = accepted;
        this.acceptedSelection = acceptedSelection;
        this.initialInventoryDigest = initialInventoryDigest;
    }

    internal bool IsLive =>
        Volatile.Read(ref disposed) == 0 &&
        Volatile.Read(ref locatorAccess) is not null &&
        Volatile.Read(ref keys) is not null &&
        Volatile.Read(ref locator) is not null &&
        Volatile.Read(ref lineage) is not null;

    internal bool HasTerminalAcceptance =>
        Volatile.Read(ref terminalAcceptance) is not null;

    internal static bool TryCreate(
        AcceptedStateProductionAuthorization production,
        AuthorizedLocatorAccess locatorAccess,
        LocatorStateKeyRing keys,
        LocatorContext locator,
        SelectedLineageContext lineage,
        IRestrictedStateStore store,
        TimeProvider timeProvider,
        AuthorizedStateAccess stateAccess,
        LineageBaseScope baseScope,
        ReviewedTransitionFacts reviewed,
        string producingRunIdentity,
        long producingRunAttempt,
        AcceptedStatePolicyBinding policy,
        AcceptedStatePublicationBinding publication,
        AgentSessionTrustedRequest trustedRequest,
        ReviewedIdentity currentReviewedIdentity,
        ProjectChatMessage currentReviewContext,
        IAgentContinuationCodec continuationCodec,
        AcceptedStateContext? accepted,
        AcceptedStateSelection? acceptedSelection,
        string initialInventoryDigest,
        out RetainedStateTransactionAuthority? authority)
    {
        authority = null;
        if (production is null ||
            locatorAccess is null ||
            keys is null ||
            locator is null ||
            lineage is null ||
            store is null ||
            timeProvider is null ||
            stateAccess is null ||
            !LineageValidation.IsValid(baseScope) ||
            !LineageValidation.IsValid(reviewed) ||
            !LineageValidation.IsText(
                producingRunIdentity,
                LineageFormat.MaximumRunIdentityBytes) ||
            producingRunAttempt < 0 ||
            policy is null ||
            publication is null ||
            trustedRequest is null ||
            currentReviewedIdentity is null ||
            !currentReviewedIdentity.IsValid() ||
            currentReviewContext is null ||
            continuationCodec is null ||
            !LineageValidation.IsSha256(initialInventoryDigest) ||
            !production.AllowsLocator(baseScope.RepositoryId) ||
            !lineage.TryGetSnapshot(locatorAccess, out var selected) ||
            selected is null ||
            !StringComparer.Ordinal.Equals(
                selected.BaseScopeDigest,
                Digest(baseScope)) ||
            !MatchesStateScope(stateAccess.Scope, baseScope, selected) ||
            (accepted is null) != (acceptedSelection is null) ||
            (accepted is not null &&
                (!StringComparer.Ordinal.Equals(
                    accepted.LogicalGenerationIdentity,
                    acceptedSelection!.Current.LogicalGenerationIdentity) ||
                !StringComparer.Ordinal.Equals(
                    accepted.SelectedLineageHeadIdentity,
                    selected.LineageHeadIdentity))))
        {
            return false;
        }

        var trustedPolicy = trustedRequest.TrustedPolicyBytes.ToArray();
        try
        {
            var ownedTrustedRequest = trustedRequest with
            {
                TrustedPolicyBytes = trustedPolicy,
            };
            authority = new RetainedStateTransactionAuthority(
                production,
                locatorAccess,
                keys,
                locator,
                lineage,
                store,
                timeProvider,
                stateAccess,
                baseScope,
                reviewed,
                producingRunIdentity,
                producingRunAttempt,
                policy,
                publication,
                ownedTrustedRequest,
                currentReviewedIdentity,
                currentReviewContext,
                continuationCodec,
                accepted,
                CloneSelection(acceptedSelection),
                initialInventoryDigest);
            trustedPolicy = [];
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(trustedPolicy);
        }
    }

    internal async Task<RetainedStateAuthorityLease?> EnterAsync(
        CancellationToken cancellationToken)
    {
        if (!IsLive)
        {
            return null;
        }

        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        if (!IsLive)
        {
            gate.Release();
            return null;
        }

        return RetainedStateAuthorityLease.Create(this);
    }

    internal bool TryGetLineageSnapshot(
        out SelectedLineageSnapshot? snapshot)
    {
        snapshot = null;
        var access = Volatile.Read(ref locatorAccess);
        var selected = Volatile.Read(ref lineage);
        return IsLive &&
            access is not null &&
            selected is not null &&
            selected.TryGetSnapshot(access, out snapshot);
    }

    internal bool TryGetAdmittedValue(
        out AgentSessionStateAdmittedValue? value)
    {
        value = null;
        return IsLive &&
            Volatile.Read(ref accepted)?.TryGetAdmittedValue(out value) ==
                true;
    }

    internal bool TryGetBinding(
        RetainedStateAuthorityLease lease,
        out RetainedStateTransactionBinding? binding)
    {
        binding = null;
        if (!Allows(lease) ||
            !TryGetLineageSnapshot(out var selected) ||
            selected is null)
        {
            return false;
        }

        var current = Volatile.Read(ref acceptedSelection)?.Current;
        binding = new RetainedStateTransactionBinding(
            stateAccess.Scope,
            baseScope,
            selected,
            reviewed,
            producingRunIdentity,
            producingRunAttempt,
            policy,
            publication,
            current?.Generation.Generation,
            current?.LogicalGenerationIdentity,
            current?.ReceiptPhysical.Header.ObjectIdentity,
            initialInventoryDigest);
        return true;
    }

    internal bool TryReadTrustedTime(
        RetainedStateAuthorityLease lease,
        out long unixTimeSeconds)
    {
        unixTimeSeconds = 0;
        if (!Allows(lease))
        {
            return false;
        }

        unixTimeSeconds = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        return LineageValidation.IsTime(unixTimeSeconds);
    }

    internal bool TryBuildSuccessor(
        RetainedStateAuthorityLease lease,
        AgentRunRequest? run,
        R4PreparedPublication? preparedPublication,
        out AgentSessionArtifact? artifact,
        out ValidatedPublicationPayloadV1? validatedPublication,
        out string code)
    {
        artifact = null;
        validatedPublication = null;
        code = RetainedStateTransactionCodes.Invalid;
        if (!Allows(lease) ||
            HasTerminalAcceptance ||
            run is null ||
            preparedPublication is null ||
            !preparedPublication.TryProject(
                out var outcome,
                out var rendered,
                out var preparedScope) ||
            outcome is null ||
            rendered is null ||
            preparedScope is null ||
            preparedScope != publication.Scope ||
            !SameIdentity(run.ReviewedIdentity, currentReviewedIdentity) ||
            !SameIdentity(outcome.ReviewedIdentity, currentReviewedIdentity) ||
            run.InitialMessages.Length == 0 ||
            !SameMessage(run.InitialMessages[^1], currentReviewContext) ||
            !TryGetLineageSnapshot(out var selected) ||
            selected is null ||
            !StringComparer.Ordinal.Equals(
                run.SessionId,
                selected.SessionId))
        {
            return false;
        }

        AgentSessionPredecessor? predecessor = null;
        var transition = AgentSessionHeadTransition.SameHead;
        var currentAccepted = Volatile.Read(ref accepted);
        if (currentAccepted is not null &&
            !currentAccepted.TryCreateSuccessorPredecessor(
                out predecessor,
                out transition))
        {
            return false;
        }

        try
        {
            var built = AgentSessionBuilder.Build(
                new AgentSessionBuildInput(
                    run,
                    outcome,
                    trustedRequest,
                    run.InitialMessages.Length - 1,
                    continuationCodec,
                    predecessor,
                    transition));
            if (!built.Succeeded || built.Artifact is null)
            {
                code = built.FailureCode ??
                    RetainedStateTransactionCodes.Invalid;
                return false;
            }

            if (!ValidatedPublicationPayloadV1.TryCreate(
                    rendered.Comment,
                    checked((long)publication.Scope.RepositoryId),
                    publication.RepositoryName,
                    checked((long)publication.Scope.PullRequestNumber),
                    policy.PolicyIdentitySha256,
                    publication.PayloadSha256,
                    publication.BuildDiscriminator,
                    AcceptedStateFormat.RenderingVersion,
                    out validatedPublication) ||
                validatedPublication is null ||
                !StringComparer.Ordinal.Equals(
                    validatedPublication.ScopeSha256,
                    publication.ScopeSha256) ||
                !StringComparer.Ordinal.Equals(
                    validatedPublication.ReviewedHeadSha,
                    currentReviewedIdentity.HeadSha) ||
                built.Artifact.Document.Generation !=
                    (currentAccepted is null
                        ? 0
                        : currentAccepted.TryGetAdmittedValue(out var value)
                            && value is not null
                            ? value.Artifact.Document.Generation + 1
                            : -1))
            {
                CryptographicOperations.ZeroMemory(
                    built.Artifact.Plaintext);
                validatedPublication = null;
                return false;
            }

            artifact = built.Artifact;
            code = RetainedStateTransactionCodes.Ready;
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                InvalidOperationException or
                OverflowException or
                CryptographicException)
        {
            artifact = null;
            validatedPublication = null;
            code = RetainedStateTransactionCodes.Invalid;
            return false;
        }
        finally
        {
            if (predecessor?.Plaintext is { } plaintext)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    internal bool TryEncryptSession(
        RetainedStateAuthorityLease lease,
        AgentSessionArtifact artifact,
        long preparedAtUnixSeconds,
        long logicalExpiresAtUnixSeconds,
        out byte[] envelope,
        out string code)
    {
        envelope = [];
        code = RetainedStateTransactionCodes.Invalid;
        if (!Allows(lease) || artifact is null)
        {
            return false;
        }

        var document = artifact.Document;
        var binding = new RestrictedStateBinding(
            stateAccess.Scope,
            document.ProducerBaseSha,
            document.ProducerHeadSha,
            document.Generation,
            document.PredecessorStateSha256,
            preparedAtUnixSeconds,
            logicalExpiresAtUnixSeconds);
        var access = Volatile.Read(ref locatorAccess);
        var context = Volatile.Read(ref locator);
        if (access is null || context is null)
        {
            return false;
        }

        var resolver = new TransactionRestrictedStateKeyResolver(
            stateAccess,
            access,
            context);
        if (!RestrictedStateEnvelope.TryEncrypt(
                stateAccess,
                binding,
                artifact.Plaintext,
                resolver,
                out var result,
                out var failure) ||
            result is null)
        {
            code = StringComparer.Ordinal.Equals(
                failure,
                RestrictedStateCodes.KeyUnavailable)
                ? RetainedStateTransactionCodes.KeyUnavailable
                : RetainedStateTransactionCodes.Invalid;
            return false;
        }

        envelope = result;
        code = RetainedStateTransactionCodes.Ready;
        return true;
    }

    internal async Task<string> EnsureSentinelCoverageAsync(
        RetainedStateAuthorityLease lease,
        long dependentExpiresAtUnixSeconds,
        long trustedNowUnixSeconds,
        CancellationToken cancellationToken)
    {
        if (!Allows(lease) ||
            !LineageValidation.IsTime(dependentExpiresAtUnixSeconds) ||
            !LineageValidation.IsTime(trustedNowUnixSeconds))
        {
            return RetainedStateTransactionCodes.AccessDenied;
        }

        var access = Volatile.Read(ref locatorAccess);
        var currentKeys = Volatile.Read(ref keys);
        if (access is null || currentKeys is null)
        {
            return RetainedStateTransactionCodes.AccessDenied;
        }

        var resolved = await new LocatorRootService(
                store,
                currentKeys,
                new FrozenTimeProvider(trustedNowUnixSeconds))
            .ResolveAsync(
                access,
                dependentExpiresAtUnixSeconds,
                cancellationToken)
            .ConfigureAwait(false);
        if (!resolved.Succeeded || resolved.Context is null ||
            !resolved.Context.CoversDependentExpiry(
                access,
                dependentExpiresAtUnixSeconds))
        {
            resolved.Context?.Dispose();
            return MapLocatorCode(resolved.Code);
        }

        Interlocked.Exchange(ref locator, resolved.Context)?.Dispose();
        return RetainedStateTransactionCodes.Ready;
    }

    internal async Task<RetainedStateTransactionResult<
        RetainedStateObservation>> ObserveAsync(
        RetainedStateAuthorityLease lease,
        long requiredLogicalExpiresAtUnixSeconds,
        long trustedNowUnixSeconds,
        CancellationToken cancellationToken)
    {
        if (!Allows(lease) ||
            !LineageValidation.IsTime(requiredLogicalExpiresAtUnixSeconds) ||
            !LineageValidation.IsTime(trustedNowUnixSeconds) ||
            requiredLogicalExpiresAtUnixSeconds < trustedNowUnixSeconds ||
            !TryGetLineageSnapshot(out var expected) ||
            expected is null)
        {
            return RetainedStateTransactionResult<
                RetainedStateObservation>.Fail(
                    RetainedStateTransactionCodes.AccessDenied);
        }

        var access = Volatile.Read(ref locatorAccess);
        var context = Volatile.Read(ref locator);
        if (access is null || context is null)
        {
            return RetainedStateTransactionResult<
                RetainedStateObservation>.Fail(
                    RetainedStateTransactionCodes.AccessDenied);
        }

        var request = ResolveRequest(
            access,
            requiredLogicalExpiresAtUnixSeconds);
        var observed = await new LineageService(
                store,
                new FrozenTimeProvider(trustedNowUnixSeconds))
            .ObserveReadOnlyAsync(context, request, cancellationToken)
            .ConfigureAwait(false);
        if (!observed.Succeeded || observed.Context is null)
        {
            return RetainedStateTransactionResult<
                RetainedStateObservation>.Fail(
                    MapLineageCode(observed.Code));
        }

        var observation = observed.Context;
        var selected = observation.Selection.Selection?.Head;
        if (selected is null ||
            !Matches(expected, selected))
        {
            observation.Dispose();
            return RetainedStateTransactionResult<
                RetainedStateObservation>.Fail(
                    RetainedStateTransactionCodes.Stale);
        }

        var acceptedState = new AcceptedStateSelector(
            new FrozenTimeProvider(trustedNowUnixSeconds))
            .Select(observation, request);
        return RetainedStateTransactionResult<RetainedStateObservation>
            .Success(
                RetainedStateTransactionCodes.Ready,
                RetainedStateObservation.Create(
                    this,
                    observation,
                    acceptedState));
    }

    internal async Task<string> RefreshSelectedHeadAsync(
        RetainedStateAuthorityLease lease,
        long requiredLogicalExpiresAtUnixSeconds,
        long trustedNowUnixSeconds,
        CancellationToken cancellationToken)
    {
        if (!Allows(lease) ||
            !TryHeadPlatformExpiry(
                trustedNowUnixSeconds,
                requiredLogicalExpiresAtUnixSeconds,
                out var requiredPlatformExpiry))
        {
            return RetainedStateTransactionCodes.AccessDenied;
        }

        var sentinel = await EnsureSentinelCoverageAsync(
                lease,
                requiredPlatformExpiry,
                trustedNowUnixSeconds,
                cancellationToken)
            .ConfigureAwait(false);
        if (!StringComparer.Ordinal.Equals(
                sentinel,
                RetainedStateTransactionCodes.Ready))
        {
            return sentinel;
        }

        var access = Volatile.Read(ref locatorAccess);
        var context = Volatile.Read(ref locator);
        var expected = Volatile.Read(ref lineage);
        if (access is null || context is null || expected is null)
        {
            return RetainedStateTransactionCodes.AccessDenied;
        }

        var refreshed = await new LineageService(
                store,
                new FrozenTimeProvider(trustedNowUnixSeconds))
            .RefreshSelectedHeadAsync(
                context,
                ResolveRequest(access, requiredLogicalExpiresAtUnixSeconds),
                expected,
                cancellationToken)
            .ConfigureAwait(false);
        if (!refreshed.Succeeded || refreshed.Context is null)
        {
            return MapLineageCode(refreshed.Code);
        }

        Interlocked.Exchange(ref lineage, refreshed.Context)?.Dispose();
        return RetainedStateTransactionCodes.Ready;
    }

    internal bool TryCreatePersistence(
        RetainedStateAuthorityLease lease,
        out RetainedStatePersistence? persistence)
    {
        persistence = Allows(lease)
            ? new RetainedStatePersistence(store)
            : null;
        return persistence is not null;
    }

    internal bool TryEvaluatePreviousKeyRetirement(
        RetainedStateAuthorityLease lease,
        System.Collections.Immutable.ImmutableArray<
            LocatorRequiredDependency> requiredDependencies,
        out bool mayRetire)
    {
        mayRetire = false;
        var access = Volatile.Read(ref locatorAccess);
        var context = Volatile.Read(ref locator);
        if (!Allows(lease) ||
            access is null ||
            context is null ||
            !context.TryCapturePreviousKeyRetirementEvidence(
                access,
                enumerationComplete: true,
                requiredDependencies,
                out var evidence) ||
            evidence is null)
        {
            return false;
        }

        mayRetire = context.CanRetirePreviousKey(access, evidence);
        return true;
    }

    internal bool TryGetPersistenceContext(
        RetainedStateAuthorityLease lease,
        out LocatorContext? context,
        out AuthorizedLocatorAccess? access,
        out LineageBaseScope? scope)
    {
        context = null;
        access = null;
        scope = null;
        if (!Allows(lease))
        {
            return false;
        }

        context = Volatile.Read(ref locator);
        access = Volatile.Read(ref locatorAccess);
        scope = baseScope;
        return context is not null && access is not null;
    }

    internal AcceptedStateSelection? GetAcceptedSelection(
        RetainedStateAuthorityLease lease) =>
        Allows(lease) ? Volatile.Read(ref acceptedSelection) : null;

    internal bool TryGetTerminalAcceptance(
        out VerifiedRetainedStateAcceptance? acceptance)
    {
        acceptance = IsLive
            ? Volatile.Read(ref terminalAcceptance)
            : null;
        return acceptance is not null;
    }

    internal bool TryGetTerminalAcceptance(
        RetainedStateAuthorityLease lease,
        out VerifiedRetainedStateAcceptance? acceptance)
    {
        acceptance = Allows(lease)
            ? Volatile.Read(ref terminalAcceptance)
            : null;
        return acceptance is not null;
    }

    internal bool TryMarkTerminalAcceptance(
        RetainedStateAuthorityLease lease,
        VerifiedRetainedStateAcceptance acceptance) =>
        Allows(lease) &&
        acceptance is not null &&
        ReferenceEquals(acceptance.Authority, this) &&
        Interlocked.CompareExchange(
            ref terminalAcceptance,
            acceptance,
            comparand: null) is null;

    internal bool Allows(RetainedStateAuthorityLease? lease) =>
        IsLive && lease?.Allows(this) == true;

    internal void Release() => gate.Release();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(
            trustedRequest.TrustedPolicyBytes);
        ClearSelection(Interlocked.Exchange(ref acceptedSelection, null));
        Interlocked.Exchange(ref accepted, null)?.Dispose();
        Interlocked.Exchange(ref lineage, null)?.Dispose();
        Interlocked.Exchange(ref locator, null)?.Dispose();
        Interlocked.Exchange(ref keys, null)?.Dispose();
        Interlocked.Exchange(ref locatorAccess, null)?.Dispose();
    }

    public override string ToString() => "[PRIVATE]";

    private LineageResolveRequest ResolveRequest(
        AuthorizedLocatorAccess access,
        long requiredLogicalExpiresAtUnixSeconds) =>
        new(
            access,
            baseScope,
            reviewed,
            producingRunIdentity,
            producingRunAttempt,
            requiredLogicalExpiresAtUnixSeconds,
            Reset: null);

    private static bool MatchesStateScope(
        RestrictedStateScope state,
        LineageBaseScope scope,
        SelectedLineageSnapshot selected) =>
        StringComparer.Ordinal.Equals(state.RepositoryId, scope.RepositoryId) &&
        StringComparer.Ordinal.Equals(
            state.WorkflowIdentity,
            scope.TrustedWorkflowIdentity) &&
        state.ReviewTarget == scope.PullRequestNumber &&
        StringComparer.Ordinal.Equals(state.SessionId, selected.SessionId) &&
        StringComparer.Ordinal.Equals(state.ProviderId, scope.Provider) &&
        StringComparer.Ordinal.Equals(state.ModelId, scope.Model) &&
        StringComparer.Ordinal.Equals(state.AdapterId, scope.Adapter) &&
        StringComparer.Ordinal.Equals(
            state.PolicySha256,
            scope.InstructionSha256) &&
        StringComparer.Ordinal.Equals(state.LimitsSha256, scope.LimitsSha256) &&
        StringComparer.Ordinal.Equals(state.ToolsetSha256, scope.ToolsetSha256);

    private static AcceptedStateSelection? CloneSelection(
        AcceptedStateSelection? selection) =>
        selection is null
            ? null
            : new AcceptedStateSelection(
                CloneGeneration(selection.Current),
                selection.ImmediatePredecessor is null
                    ? null
                    : CloneGeneration(selection.ImmediatePredecessor),
                selection.LineageHead,
                selection.RequiredCurrentWindowUnixSeconds);

    private static SelectedAcceptedGeneration CloneGeneration(
        SelectedAcceptedGeneration value) =>
        new(
            value.Physical with
            {
                Payload = value.Physical.Payload.ToArray(),
            },
            value.Generation,
            value.Receipt,
            value.ReceiptPhysical with
            {
                Payload = value.ReceiptPhysical.Payload.ToArray(),
            },
            value.LogicalGenerationIdentity,
            value.OriginalCandidateObjectIdentity);

    private static void ClearSelection(AcceptedStateSelection? selection)
    {
        if (selection is null)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(selection.Current.Physical.Payload);
        CryptographicOperations.ZeroMemory(
            selection.Current.ReceiptPhysical.Payload);
        if (selection.ImmediatePredecessor is { } predecessor)
        {
            CryptographicOperations.ZeroMemory(predecessor.Physical.Payload);
            CryptographicOperations.ZeroMemory(
                predecessor.ReceiptPhysical.Payload);
        }
    }

    private static string Digest(LineageBaseScope scope) =>
        LineageBaseScopeCodec.TryDigest(scope, out var value)
            ? value
            : string.Empty;

    private static bool SameIdentity(
        ReviewedIdentity? actual,
        ReviewedIdentity expected) =>
        actual is not null &&
        StringComparer.Ordinal.Equals(
            actual.RepositoryId,
            expected.RepositoryId) &&
        actual.ReviewTarget == expected.ReviewTarget &&
        StringComparer.Ordinal.Equals(actual.BaseSha, expected.BaseSha) &&
        StringComparer.Ordinal.Equals(actual.HeadSha, expected.HeadSha);

    private static bool SameMessage(
        ProjectChatMessage actual,
        ProjectChatMessage expected)
    {
        try
        {
            var tools = AgentToolRegistry.Definitions.ToArray();
            return AgentRequestWriter.Write(
                    new ProjectChatRequest(
                        [actual],
                        tools,
                        null,
                        ThinkingRequired: true))
                .AsSpan()
                .SequenceEqual(AgentRequestWriter.Write(
                    new ProjectChatRequest(
                        [expected],
                        tools,
                        null,
                        ThinkingRequired: true)));
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                InvalidOperationException or
                NotSupportedException or
                OverflowException)
        {
            return false;
        }
    }

    private static bool Matches(
        SelectedLineageSnapshot expected,
        LineageHeadCandidate actual) =>
        StringComparer.Ordinal.Equals(
            expected.BaseScopeDigest,
            actual.Header.BaseScopeDigest) &&
        StringComparer.Ordinal.Equals(expected.Epoch, actual.Header.Epoch) &&
        StringComparer.Ordinal.Equals(
            expected.SessionId,
            actual.Header.SessionId) &&
        StringComparer.Ordinal.Equals(
            expected.LineageHeadIdentity,
            actual.Header.ObjectIdentity) &&
        expected.Transition == actual.Head.Transition;

    private static bool TryHeadPlatformExpiry(
        long now,
        long logicalExpiry,
        out long required)
    {
        required = 0;
        try
        {
            required = Math.Max(
                checked(now +
                    StateRetentionRequirements.ScopedPlatformRequestSeconds),
                checked(logicalExpiry +
                    StateRetentionRequirements.SentinelDependentMarginSeconds));
            return LineageValidation.IsTime(required);
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static string MapLocatorCode(string code) =>
        StringComparer.Ordinal.Equals(code, LocatorCodes.AccessDenied)
            ? RetainedStateTransactionCodes.AccessDenied
            : StringComparer.Ordinal.Equals(code, LocatorCodes.KeyUnavailable)
                ? RetainedStateTransactionCodes.KeyUnavailable
                : StringComparer.Ordinal.Equals(code, LocatorCodes.Conflict)
                    ? RetainedStateTransactionCodes.Conflict
                    : RetainedStateTransactionCodes.OutcomeUnknown;

    private static string MapLineageCode(string code) =>
        StringComparer.Ordinal.Equals(code, LineageCodes.AccessDenied)
            ? RetainedStateTransactionCodes.AccessDenied
            : StringComparer.Ordinal.Equals(code, LineageCodes.KeyUnavailable)
                ? RetainedStateTransactionCodes.KeyUnavailable
                : StringComparer.Ordinal.Equals(code, LineageCodes.Conflict)
                    ? RetainedStateTransactionCodes.Conflict
                    : StringComparer.Ordinal.Equals(
                        code,
                        LineageCodes.RetentionFailed)
                        ? RetainedStateTransactionCodes.RetentionFailed
                        : RetainedStateTransactionCodes.OutcomeUnknown;

    private sealed class TransactionRestrictedStateKeyResolver(
        AuthorizedStateAccess stateAuthority,
        AuthorizedLocatorAccess locatorAuthority,
        LocatorContext context) : IRestrictedStateKeyResolver
    {
        public bool TryGetCurrentWriteKey(
            AuthorizedStateAccess access,
            out RestrictedStateKey? key)
        {
            key = null;
            if (!ReferenceEquals(access, stateAuthority))
            {
                return false;
            }

            Span<byte> material = stackalloc byte[RestrictedStateFormat.KeyBytes];
            try
            {
                if (!context.TryCopyCurrentStateKey(
                        locatorAuthority,
                        material,
                        out var keyId))
                {
                    return false;
                }

                key = new RestrictedStateKey(keyId, material);
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(material);
            }
        }

        public bool TryGetApprovedReadKey(
            AuthorizedStateAccess access,
            string keyId,
            long expiresAtUnixSeconds,
            out RestrictedStateKey? key)
        {
            key = null;
            if (!ReferenceEquals(access, stateAuthority) ||
                !LineageValidation.IsSha256(keyId) ||
                !LineageValidation.IsTime(expiresAtUnixSeconds))
            {
                return false;
            }

            Span<byte> material = stackalloc byte[RestrictedStateFormat.KeyBytes];
            try
            {
                if (!context.TryCopyApprovedReadKey(
                        locatorAuthority,
                        keyId,
                        material))
                {
                    return false;
                }

                key = new RestrictedStateKey(keyId, material);
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(material);
            }
        }
    }
}

internal sealed class RetainedStateAuthorityLease : IDisposable
{
    private RetainedStateTransactionAuthority? authority;

    private RetainedStateAuthorityLease(
        RetainedStateTransactionAuthority authority) =>
        this.authority = authority;

    internal static RetainedStateAuthorityLease Create(
        RetainedStateTransactionAuthority authority) => new(authority);

    internal bool Allows(RetainedStateTransactionAuthority expected) =>
        ReferenceEquals(Volatile.Read(ref authority), expected);

    public void Dispose()
    {
        var current = Interlocked.Exchange(ref authority, null);
        current?.Release();
    }

    public override string ToString() => "[PRIVATE]";
}

internal sealed class RetainedStateObservation : IDisposable
{
    private LineageReadOnlyObservationContext? observation;

    private RetainedStateObservation(
        RetainedStateTransactionAuthority authority,
        LineageReadOnlyObservationContext observation,
        AcceptedStateSelectionResult acceptedState)
    {
        Authority = authority;
        this.observation = observation;
        AcceptedState = acceptedState;
    }

    internal RetainedStateTransactionAuthority Authority { get; }
    internal AcceptedStateSelectionResult AcceptedState { get; }
    internal ScopedStateInventorySnapshot? Snapshot =>
        Volatile.Read(ref observation)?.Snapshot;
    internal string? InventoryDigest =>
        Volatile.Read(ref observation)?.InventoryDigest;
    internal LineageHeadCandidate? SelectedHead =>
        Volatile.Read(ref observation)?.Selection.Selection?.Head;

    internal static RetainedStateObservation Create(
        RetainedStateTransactionAuthority authority,
        LineageReadOnlyObservationContext observation,
        AcceptedStateSelectionResult acceptedState) =>
        new(authority, observation, acceptedState);

    public void Dispose() =>
        Interlocked.Exchange(ref observation, null)?.Dispose();

    public override string ToString() => "[PRIVATE]";
}

internal sealed class FrozenTimeProvider : TimeProvider
{
    private readonly DateTimeOffset value;

    internal FrozenTimeProvider(long unixTimeSeconds) =>
        value = DateTimeOffset.FromUnixTimeSeconds(unixTimeSeconds);

    public override DateTimeOffset GetUtcNow() => value;
}

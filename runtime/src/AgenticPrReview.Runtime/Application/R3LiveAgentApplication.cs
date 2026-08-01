using System.Security.Cryptography;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Canonical;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Host.State;

namespace AgenticPrReview.Runtime;

internal sealed class R3LiveAgentRequest(
    RestrictedStateScope authorizedScope,
    bool isTrustedWorkflow,
    bool isSameRepository,
    bool isForkOrigin,
    RestrictedStateLocatorFamily stateLocatorFamily,
    RestrictedStateRestoreIntent stateRestoreIntent,
    AcceptedLineage? acceptedLineage,
    RestrictedStateSessionAdmissionContext stateAdmissionContext,
    string stateRoot,
    string snapshotRoot,
    string[] trackedFiles,
    ReviewedChangedFile[] changedFiles,
    ReviewedDiffSource[] diffSources)
{
    internal RestrictedStateScope AuthorizedScope { get; } = authorizedScope;

    internal bool IsTrustedWorkflow { get; } = isTrustedWorkflow;

    internal bool IsSameRepository { get; } = isSameRepository;

    internal bool IsForkOrigin { get; } = isForkOrigin;

    internal RestrictedStateLocatorFamily StateLocatorFamily { get; } =
        stateLocatorFamily;

    internal RestrictedStateRestoreIntent StateRestoreIntent { get; } =
        stateRestoreIntent;

    internal AcceptedLineage? AcceptedLineage { get; } = acceptedLineage;

    internal RestrictedStateSessionAdmissionContext StateAdmissionContext {
        get;
    } = stateAdmissionContext;

    internal string StateRoot { get; } = stateRoot;

    internal string SnapshotRoot { get; } = snapshotRoot;

    internal string[] TrackedFiles { get; } = trackedFiles;

    internal ReviewedChangedFile[] ChangedFiles { get; } = changedFiles;

    internal ReviewedDiffSource[] DiffSources { get; } = diffSources;

    public override string ToString() => "r3_live_agent_request";
}

internal sealed class LiveAgentCandidate(
    AgentRunRequest run,
    AgentRunOutcome outcome,
    AgentSessionTrustedRequest trustedRequest,
    int currentReviewContextIndex,
    IAgentContinuationCodec continuationCodec,
    AgentSessionHeadTransition transition,
    AgentSessionPredecessor? predecessor,
    RestrictedStateSessionAdmissionContext stateAdmissionContext)
{
    internal AgentRunRequest Run { get; } = run;

    internal AgentRunOutcome Outcome { get; } = outcome;

    internal AgentSessionTrustedRequest TrustedRequest { get; } =
        trustedRequest;

    internal int CurrentReviewContextIndex { get; } =
        currentReviewContextIndex;

    internal IAgentContinuationCodec ContinuationCodec { get; } =
        continuationCodec;

    internal AgentSessionHeadTransition Transition { get; } = transition;

    internal AgentSessionPredecessor? Predecessor { get; } = predecessor;

    internal RestrictedStateSessionAdmissionContext StateAdmissionContext {
        get;
    } = stateAdmissionContext;

    public override string ToString() => "live_agent_candidate";
}

internal sealed class R3LiveAgentResult(
    string code,
    int modelCalls,
    int toolCalls,
    string? stablePlanSha256,
    string? terminalSha256,
    long? acceptedGeneration,
    string? acceptedSessionSha256,
    string? acceptedEnvelopeSha256,
    bool handoffReady)
{
    internal string Code { get; } = code;

    internal int ModelCalls { get; } = modelCalls;

    internal int ToolCalls { get; } = toolCalls;

    internal string? StablePlanSha256 { get; } = stablePlanSha256;

    internal string? TerminalSha256 { get; } = terminalSha256;

    internal long? AcceptedGeneration { get; } = acceptedGeneration;

    internal string? AcceptedSessionSha256 { get; } = acceptedSessionSha256;

    internal string? AcceptedEnvelopeSha256 { get; } = acceptedEnvelopeSha256;

    internal bool HandoffReady { get; } = handoffReady;

    public override string ToString() => "r3_live_agent_result";
}

internal sealed class R3LiveAgentExecution(R3LiveAgentResult result)
{
    internal R3LiveAgentResult Result { get; } = result;

    public override string ToString() => "r3_live_agent_execution";
}

internal sealed class R3LiveAgentApplication
{
    private readonly R3LiveAgentDependencies dependencies;

    internal R3LiveAgentApplication(R3LiveAgentDependencies dependencies)
    {
        this.dependencies = dependencies;
    }

    internal async Task<R3LiveAgentExecution> RunAsync(
        R3LiveAgentRequest request,
        CancellationToken cancellationToken)
    {
        AgentSessionMaterializedStableRequest? materialized = null;
        AgentSessionTrustedRequest? trusted = null;
        RestrictedStateAdmittedSession? admittedSession = null;

        try
        {
            if (!TryFreezeTrustedInput(
                    request,
                    out trusted,
                    out var currentIdentity) ||
                !AgentStableRequestMaterializer.TryMaterialize(
                    trusted!,
                    priorSessionSha256: null,
                    out materialized))
            {
                return Failure(R3LiveAgentCodes.InputInvalid);
            }

            var stable = materialized!.StablePlan;
            var requestedScope = ToStateScope(stable, request);
            var authorization = AuthorizedStateAccess.Authorize(
                new RestrictedStateAccessRequest(
                    requestedScope,
                    request.AuthorizedScope,
                    request.IsTrustedWorkflow,
                    request.IsSameRepository,
                    request.IsForkOrigin),
                out var access);
            if (authorization.Action != StateAction.Authorized ||
                !StringComparer.Ordinal.Equals(
                    authorization.Code,
                    RestrictedStateCodes.Authorized) ||
                access is null)
            {
                return Failure(RestrictedStateCodes.AccessDenied);
            }

            var secrets = dependencies.SecretSource.TakeAndClear();
            if (!TryBindSecrets(
                    secrets,
                    out var credential,
                    out var keyResolver))
            {
                return Failure(R3LiveAgentCodes.SecretInvalid);
            }

            using (keyResolver)
            {
                if (cancellationToken.IsCancellationRequested ||
                    !TryFreezeAuthorizedInput(
                        request,
                        trusted!,
                        currentIdentity!,
                        out var currentContext,
                        out var stateContext,
                        out var trackedFiles,
                        out var changedFiles,
                        out var diffSources))
                {
                    return Failure(
                        cancellationToken.IsCancellationRequested
                            ? AgentFailureCodes.Cancelled
                            : R3LiveAgentCodes.InputInvalid);
                }

                var adapter = new DeepSeekAdapterContext(
                    trusted!.ProviderId,
                    trusted.ModelId,
                    trusted.AdapterId,
                    stateContext!.SessionContext.SessionId);
                if (!adapter.IsValid)
                {
                    return Failure(R3LiveAgentCodes.InputInvalid);
                }

                var restore = dependencies.StateRestorer.Restore(
                    request.StateRoot,
                    keyResolver!,
                    access,
                    new RestrictedStateRestoreRequest(
                        request.StateLocatorFamily,
                        request.StateRestoreIntent,
                        request.AcceptedLineage,
                        stateContext),
                    dependencies.TimeProvider,
                    cancellationToken);
                admittedSession = restore.Session;

                AgentRunRequest run;
                AgentSessionPredecessor? predecessor;
                if (restore.Result.Action == StateAction.Bootstrap &&
                    restore.Session is null)
                {
                    if (!TryValidateBootstrapStateContext(
                            stateContext,
                            currentIdentity!))
                    {
                        return Failure(R3LiveAgentCodes.InputInvalid);
                    }

                    var messages = materialized.ControlMessages
                        .Append(currentContext!)
                        .ToArray();
                    run = new AgentRunRequest(
                        currentIdentity!,
                        materialized.StablePlan,
                        stateContext.SessionContext.SessionId,
                        messages);
                    predecessor = null;
                }
                else if (restore.Result.Action == StateAction.Restored &&
                    StringComparer.Ordinal.Equals(
                        restore.Result.Code,
                        RestrictedStateCodes.Restored) &&
                    restore.Session is { } restored)
                {
                    if (!TryValidateRestoredStateContext(
                            stateContext,
                            currentIdentity!,
                            restore.Result,
                            restored))
                    {
                        return Failure(R3LiveAgentCodes.InputInvalid);
                    }

                    run = restored.Value.RunRequest;
                    predecessor = new AgentSessionPredecessor(
                        restored.Value.Artifact.Plaintext,
                        restored.Value.Artifact.SessionSha256,
                        restore.Result.EnvelopeSha256!,
                        restored.Generation,
                        restored.ProducerBaseSha,
                        restored.ProducerHeadSha,
                        restored.PredecessorEnvelopeSha256);
                }
                else
                {
                    return Failure(restore.Result.Code);
                }

                ReviewedSnapshot snapshot;
                try
                {
                    snapshot = new ReviewedSnapshot(
                        currentIdentity!,
                        request.SnapshotRoot,
                        trackedFiles!,
                        changedFiles!,
                        diffSources!);
                }
                catch (Exception exception) when (IsInputException(exception))
                {
                    return Failure(R3LiveAgentCodes.InputInvalid);
                }

                using var transport =
                    dependencies.TransportFactory.Create(credential!);
                var client = DeepSeekChatBackend.CreateClient(
                    adapter,
                    transport);
                var tools = new SnapshotToolExecutor(
                    snapshot,
                    dependencies.ReviewedFileAccessFactory.Create());
                var loop = new AgentLoop(
                    client,
                    tools,
                    dependencies.TimeProvider);
                var outcome = await loop.RunAsync(run, cancellationToken);
                if (!outcome.CompletedSessionEligible)
                {
                    return FromOutcome(run, outcome);
                }

                var authorizedTransition =
                    stateContext.SessionContext.Transition;
                var candidate = new LiveAgentCandidate(
                    run,
                    outcome,
                    trusted,
                    run.InitialMessages.Length - 1,
                    DeepSeekReasoningContinuationCodec.Instance,
                    stateContext.SessionContext.Transition,
                    predecessor,
                    stateContext);
                var commit = dependencies.StateCommitCoordinator.Commit(
                    candidate,
                    access,
                    request.AcceptedLineage,
                    authorizedTransition,
                    request.StateRoot,
                    keyResolver!,
                    cancellationToken);
                return new R3LiveAgentExecution(
                    ResultFromOutcome(
                        commit.Code,
                        run,
                        outcome,
                        commit));
            }
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return Failure(R3LiveAgentCodes.CompositionFailed);
        }
        finally
        {
            if (admittedSession is not null)
            {
                Zero(admittedSession.Plaintext);
                Zero(admittedSession.Value?.Artifact?.Plaintext);
            }
        }
    }

    private static bool TryFreezeTrustedInput(
        R3LiveAgentRequest request,
        out AgentSessionTrustedRequest? trusted,
        out ReviewedIdentity? currentIdentity)
    {
        trusted = null;
        currentIdentity = null;
        if (request is null ||
            request.StateAdmissionContext is null ||
            request.StateAdmissionContext.SessionContext is null ||
            request.StateAdmissionContext.SessionContext.TrustedRequest is null ||
            request.StateAdmissionContext.SessionContext
                .CurrentReviewedIdentity is null)
        {
            return false;
        }

        var source = request.StateAdmissionContext.SessionContext;
        var sourceTrusted = source.TrustedRequest;
        if (sourceTrusted.TrustedPolicyBytes is null)
        {
            return false;
        }

        trusted = sourceTrusted with
        {
            TrustedPolicyBytes = sourceTrusted.TrustedPolicyBytes.ToArray(),
        };
        currentIdentity = source.CurrentReviewedIdentity with { };
        return true;
    }

    private static bool TryFreezeAuthorizedInput(
        R3LiveAgentRequest request,
        AgentSessionTrustedRequest trusted,
        ReviewedIdentity currentIdentity,
        out ProjectChatMessage? currentContext,
        out RestrictedStateSessionAdmissionContext? stateContext,
        out string[]? trackedFiles,
        out ReviewedChangedFile[]? changedFiles,
        out ReviewedDiffSource[]? diffSources)
    {
        currentContext = null;
        stateContext = null;
        trackedFiles = null;
        changedFiles = null;
        diffSources = null;

        var source = request.StateAdmissionContext;
        var session = source.SessionContext;
        if (request.AuthorizedScope is null ||
            request.StateRoot is null ||
            request.SnapshotRoot is null ||
            request.TrackedFiles is null ||
            request.ChangedFiles is null ||
            request.DiffSources is null ||
            source.ProducerBaseSha is null ||
            source.ProducerHeadSha is null ||
            session.CurrentReviewContext is not
            {
                Role: "user",
                Contents: [ProjectTextContent text],
            } ||
            !AgentValueDomains.IsUtf8(
                text.Text,
                1,
                AgentLimits.ContentBytes) ||
            session.EnvelopeSha256 is not null ||
            session.TrustedRequest != request.StateAdmissionContext
                .SessionContext.TrustedRequest ||
            session.CurrentReviewedIdentity != currentIdentity ||
            !StringComparer.Ordinal.Equals(
                session.SessionId,
                request.AuthorizedScope.SessionId) ||
            session.ContinuationCodec is null ||
            session.ContinuationCodec !=
                DeepSeekReasoningContinuationCodec.Instance ||
            session.Transition is not (
                AgentSessionHeadTransition.SameHead or
                AgentSessionHeadTransition.VerifiedAhead) ||
            !Enum.IsDefined(request.StateLocatorFamily) ||
            !Enum.IsDefined(request.StateRestoreIntent))
        {
            return false;
        }

        currentContext = new ProjectChatMessage(
            "user",
            [new ProjectTextContent(text.Text)]);
        var frozenSession = new AgentSessionStateAdmissionContext(
            trusted,
            session.SessionId,
                        currentIdentity!,
            currentContext,
            session.Transition,
            DeepSeekReasoningContinuationCodec.Instance,
            EnvelopeSha256: null);
        stateContext = new RestrictedStateSessionAdmissionContext(
            source.ProducerBaseSha,
            source.ProducerHeadSha,
            source.Generation,
            source.PredecessorEnvelopeSha256,
            frozenSession);
        trackedFiles = request.TrackedFiles.ToArray();
        changedFiles = request.ChangedFiles.ToArray();
        diffSources = request.DiffSources.ToArray();
        return true;
    }

    private static RestrictedStateScope ToStateScope(
        StableAgentPlan stable,
        R3LiveAgentRequest request)
    {
        var sessionId = request.StateAdmissionContext?.SessionContext?.SessionId;
        return new RestrictedStateScope(
            stable.RepositoryId,
            stable.WorkflowIdentity,
            stable.ReviewTarget,
            sessionId!,
            stable.ProviderId,
            stable.ModelId,
            stable.AdapterId,
            stable.PolicySha256,
            stable.LimitsSha256,
            stable.ToolsetSha256,
            stable.BuildId);
    }

    private static bool TryBindSecrets(
        R3LiveAgentSecrets secrets,
        out DeepSeekCredential? credential,
        out R3LiveAgentStateKeyResolver? keyResolver)
    {
        credential = null;
        keyResolver = null;
        if (secrets is null ||
            !R3LiveAgentStateKeyResolver.TryCreate(
                secrets.StateKeyBase64,
                out keyResolver))
        {
            return false;
        }

        try
        {
            credential = DeepSeekCredential.Create(
                secrets.ProviderCredential!);
            return true;
        }
        catch (ArgumentException)
        {
            keyResolver!.Dispose();
            keyResolver = null;
            return false;
        }
    }

    private static bool TryValidateBootstrapStateContext(
        RestrictedStateSessionAdmissionContext context,
        ReviewedIdentity identity) =>
        context.Generation == 0 &&
        context.PredecessorEnvelopeSha256 is null &&
        StringComparer.Ordinal.Equals(
            context.ProducerBaseSha,
            identity.BaseSha) &&
        StringComparer.Ordinal.Equals(
            context.ProducerHeadSha,
            identity.HeadSha);

    private static bool TryValidateRestoredStateContext(
        RestrictedStateSessionAdmissionContext context,
        ReviewedIdentity identity,
        StateResult state,
        RestrictedStateAdmittedSession restored) =>
        restored.Generation < long.MaxValue &&
        context.Generation == restored.Generation + 1 &&
        StringComparer.Ordinal.Equals(
            context.PredecessorEnvelopeSha256,
            state.EnvelopeSha256) &&
        StringComparer.Ordinal.Equals(
            context.ProducerBaseSha,
            identity.BaseSha) &&
        StringComparer.Ordinal.Equals(
            context.ProducerHeadSha,
            identity.HeadSha) &&
        StringComparer.Ordinal.Equals(
            restored.SessionSha256,
            restored.Value.Artifact.SessionSha256);

    private static R3LiveAgentExecution FromOutcome(
        AgentRunRequest run,
        AgentRunOutcome outcome) =>
        new(
            ResultFromOutcome(
                outcome.Diagnostic?.Code ?? R3LiveAgentCodes.CompositionFailed,
                run,
                outcome));

    private static R3LiveAgentResult ResultFromOutcome(
        string code,
        AgentRunRequest run,
        AgentRunOutcome outcome,
        LiveAgentStateCommitResult? commit = null)
    {
        var modelCalls = outcome.Diagnostic?.ModelCalls ??
            outcome.Events.OfType<AgentMessageEvent>().Count(
                current =>
                    StringComparer.Ordinal.Equals(current.Role, "assistant") &&
                    current.MessageIndex >= run.InitialMessages.Length);
        var toolCalls = outcome.Diagnostic?.ToolCalls ??
            outcome.Events.OfType<AgentToolCallEvent>().Count();
        return new R3LiveAgentResult(
            code,
            modelCalls,
            toolCalls,
            AgentCanonical.StablePlanSha256(run.StablePlan),
            outcome.Review?.TerminalSha256,
            commit?.AcceptedGeneration,
            commit?.AcceptedSessionSha256,
            commit?.AcceptedEnvelopeSha256,
            commit?.HandoffReady ?? false);
    }

    private static R3LiveAgentExecution Failure(string code) =>
        new(
            new R3LiveAgentResult(
                code,
                0,
                0,
                null,
                null,
                null,
                null,
                null,
                handoffReady: false));

    private static void Zero(byte[]? bytes)
    {
        if (bytes is not null)
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool IsInputException(Exception exception) =>
        exception is ArgumentException or
            IOException or
            UnauthorizedAccessException or
            NotSupportedException or
            System.Security.SecurityException or
            OverflowException;

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or
            StackOverflowException or
            AccessViolationException or
            AppDomainUnloadedException or
            BadImageFormatException or
            CannotUnloadAppDomainException or
            InvalidProgramException or
            ThreadAbortException;
}

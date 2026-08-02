using System.Text;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Host.State;

namespace AgenticPrReview.Runtime;

internal sealed record LiveAgentFreshProcessAuthorizedInput(
    LiveAgentFreshProcessAuthorizationDocument Document,
    AgentSessionTrustedRequest TrustedRequest,
    RestrictedStateScope Scope,
    RestrictedStateLocatorFamily LocatorFamily,
    RestrictedStateRestoreIntent RestoreIntent,
    AgentSessionHeadTransition Transition,
    string InvocationIdentitySha256);

internal static class LiveAgentFreshProcessCommand
{
    internal static async Task<LiveAgentFreshProcessCommandResult> RunAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        if (!TryParse(args, out var phase, out var root) ||
            !LiveAgentFreshProcessFileSystem.TryCreate(
                root,
                out var fileSystem))
        {
            return Failure(
                exitCode: 2,
                LiveAgentFreshProcessCodes.UsageInvalid);
        }

        return await RunAsync(
            phase!,
            fileSystem!,
            cancellationToken,
            LiveAgentFreshProcessDeterministicProfile.Instance);
    }

    internal static async Task<LiveAgentFreshProcessCommandResult> RunAsync(
        string phase,
        ILiveAgentFreshProcessFileSystem fileSystem,
        CancellationToken cancellationToken)
        => await RunAsync(
            phase,
            fileSystem,
            cancellationToken,
            LiveAgentFreshProcessDeterministicProfile.Instance);

    internal static async Task<LiveAgentFreshProcessCommandResult> RunAsync(
        string phase,
        ILiveAgentFreshProcessFileSystem fileSystem,
        CancellationToken cancellationToken,
        ILiveAgentFreshProcessProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var authorizationRead = fileSystem.ReadAuthorization();
        var authorization = authorizationRead is null
            ? null
            : LiveAgentFreshProcessCodec.ReadAuthorization(
                authorizationRead.Bytes);
        if (authorization is null ||
            !TryAdmitAuthorization(
                authorization,
                out var input,
                out var accessResult,
                out var access))
        {
            return Failure(
                exitCode: 10,
                LiveAgentFreshProcessCodes.AuthorizationInvalid);
        }

        if (accessResult!.Action != StateAction.Authorized ||
            !StringComparer.Ordinal.Equals(
                accessResult.Code,
                RestrictedStateCodes.Authorized) ||
            access is null)
        {
            return Failure(
                exitCode: 13,
                RestrictedStateCodes.AccessDenied);
        }

        var lineageExpected = input!.LocatorFamily ==
            RestrictedStateLocatorFamily.Current;
        if (!fileSystem.TryAuthorizeLayout(
                authorizationRead!,
                access,
                lineageExpected,
                out var authorizedRoot) ||
            authorizedRoot is null)
        {
            return Failure(
                exitCode: 10,
                LiveAgentFreshProcessCodes.RootInvalid);
        }
        using var authorizedRootScope = authorizedRoot;

        var reviewedRead = fileSystem.ReadReviewedInput(authorizedRoot);
        var manifestRead = fileSystem.ReadSnapshotManifest(authorizedRoot);
        var reviewed = reviewedRead is null
            ? null
            : LiveAgentFreshProcessCodec.ReadReviewedInput(
                reviewedRead.Bytes);
        var manifest = manifestRead is null
            ? null
            : LiveAgentFreshProcessCodec.ReadSnapshotManifest(
                manifestRead.Bytes);
        if (reviewed is null ||
            manifest is null ||
            !TryAdmitReviewedInput(
                input,
                reviewed,
                out var identity,
                out var currentContext) ||
            !LiveAgentFreshProcessManifest.TryAdmit(
                manifest,
                identity!,
                authorizedRoot.RootPath,
                out var snapshot))
        {
            return PublishFailure(
                fileSystem,
                authorizedRoot,
                input,
                LiveAgentFreshProcessCodes.InputInvalid);
        }

        LiveAgentFreshProcessAdmittedLineage? prior = null;
        if (lineageExpected)
        {
            var lineageRead = fileSystem.ReadLineage(authorizedRoot);
            if (lineageRead is null ||
                !LiveAgentFreshProcessLineageAdmission.TryAdmit(
                    lineageRead,
                    input.Document.ExpectedLineageSha256!,
                    input.Scope,
                    out prior))
            {
                DisposeSnapshot(snapshot!);
                return PublishFailure(
                    fileSystem,
                    authorizedRoot,
                    input,
                    LiveAgentFreshProcessCodes.LineageInvalid);
            }
        }

        if (!TryAdmitPhaseAndTransition(
                phase,
                input,
                identity!,
                prior,
                out var generation,
                out var predecessor,
                out var transportPhase))
        {
            DisposeSnapshot(snapshot!);
            return PublishFailure(
                fileSystem,
                authorizedRoot,
                input,
                input.Transition == AgentSessionHeadTransition.VerifiedAhead &&
                    prior is not null &&
                    StringComparer.Ordinal.Equals(
                        prior.InvocationIdentitySha256,
                        input.InvocationIdentitySha256)
                    ? LiveAgentFreshProcessCodes.ProcessIdentityReused
                    : LiveAgentFreshProcessCodes.TransitionRejected);
        }

        var sessionContext = new AgentSessionStateAdmissionContext(
            input.TrustedRequest,
            input.Scope.SessionId,
            identity!,
            currentContext!,
            input.Transition,
            DeepSeekReasoningContinuationCodec.Instance,
            EnvelopeSha256: null);
        var stateContext = new RestrictedStateSessionAdmissionContext(
            identity!.BaseSha,
            identity.HeadSha,
            generation,
            predecessor,
            sessionContext);
        var request = new R3LiveAgentRequest(
            input.Scope,
            input.Document.IsTrustedWorkflow,
            input.Document.IsSameRepository,
            input.Document.IsForkOrigin,
            input.LocatorFamily,
            input.RestoreIntent,
            prior?.Lineage,
            stateContext,
            authorizedRoot.StateRootPath,
            authorizedRoot.RootPath,
            snapshot!.TrackedFiles,
            snapshot.ChangedFiles,
            snapshot.DiffSources);

        using var manifestFactory =
            (IDisposable)snapshot.FileAccessFactory;
        using var profileExecution = profile.Activate(
            new LiveAgentFreshProcessProfileActivation(
                transportPhase!,
                [
                    authorizationRead!.Bytes,
                    reviewedRead!.Bytes,
                    manifestRead!.Bytes,
                ]));
        var lineageSink = new LiveAgentFreshProcessLineageSink(
            fileSystem,
            authorizedRoot,
            identity,
            input.InvocationIdentitySha256,
            prior);
        var timeProvider = TimeProvider.System;
        var stateCommitCoordinator = profileExecution.Observe(
            new LiveAgentStateCommitCoordinator(
                new LiveAgentStateTransactionFactory(timeProvider),
                new AgentSessionRestrictedStateAdmission(),
                lineageSink));
        var dependencies = new R3LiveAgentDependencies(
            new R3LiveAgentEnvironmentSecretSource(),
            new R3LiveAgentStateRestorer(),
            profileExecution.TransportFactory,
            snapshot.FileAccessFactory,
            stateCommitCoordinator,
            timeProvider);
        var execution = await new R3LiveAgentApplication(dependencies)
            .RunAsync(request, cancellationToken);
        var proof = profileExecution.Proof;
        var receipt = lineageSink.PublicationReceipt;
        var applicationResult = execution.Result;
        var proofSucceeded = proof.IsSatisfiedBy(
            applicationResult.TerminalSha256);
        var handoffReady = StringComparer.Ordinal.Equals(
                applicationResult.Code,
                R3LiveAgentCodes.Completed) &&
            applicationResult.HandoffReady &&
            proofSucceeded &&
            receipt is not null;
        var code = applicationResult.Code;
        if (StringComparer.Ordinal.Equals(code, R3LiveAgentCodes.Completed) &&
            !proofSucceeded)
        {
            code = LiveAgentFreshProcessCodes.TransportProofFailed;
            handoffReady = false;
        }

        var result = new LiveAgentFreshProcessResultDocument(
            LiveAgentFreshProcessDomain.ResultKind,
            code,
            applicationResult.AcceptedGeneration,
            input.Document.Transition.Classification,
            applicationResult.ModelCalls,
            applicationResult.ToolCalls,
            applicationResult.StablePlanSha256,
            applicationResult.TerminalSha256,
            applicationResult.AcceptedSessionSha256,
            applicationResult.AcceptedEnvelopeSha256,
            receipt?.LineageSha256,
            handoffReady ? proof.SecondProcessFirstRequestSha256 : null,
            input.InvocationIdentitySha256,
            handoffReady);
        var written = fileSystem.PublishResult(
            authorizedRoot,
            LiveAgentFreshProcessCodec.Write(result));
        if (written is not { Durable: true })
        {
            return Failure(
                exitCode: 40,
                LiveAgentFreshProcessCodes.OutputFailed);
        }

        return handoffReady
            ? new LiveAgentFreshProcessCommandResult(0, null)
            : Failure(exitCode: 1, code);
    }

    private static bool TryParse(
        IReadOnlyList<string> args,
        out string? phase,
        out string? root)
    {
        phase = null;
        root = null;
        if (args.Count != 5 ||
            !StringComparer.Ordinal.Equals(
                args[0],
                "review-live-agent-r3") ||
            !StringComparer.Ordinal.Equals(args[1], "--mode") ||
            args[2] is not ("bootstrap" or "continue") ||
            !StringComparer.Ordinal.Equals(args[3], "--root") ||
            string.IsNullOrEmpty(args[4]))
        {
            return false;
        }

        phase = args[2];
        root = args[4];
        return true;
    }

    private static bool TryAdmitAuthorization(
        LiveAgentFreshProcessAuthorizationDocument document,
        out LiveAgentFreshProcessAuthorizedInput? input,
        out StateResult? accessResult,
        out AuthorizedStateAccess? access)
    {
        input = null;
        accessResult = null;
        access = null;
        if (document is null ||
            !StringComparer.Ordinal.Equals(
                document.Kind,
                LiveAgentFreshProcessDomain.AuthorizationKind) ||
            document.Stable is null ||
            document.AuthorizedScope is null ||
            document.Transition is null ||
            !HasRequiredAuthorizationValues(document) ||
            !StringComparer.Ordinal.Equals(
                document.ExecutionProfile,
                LiveAgentFreshProcessDomain.DeterministicProfile) ||
            !LiveAgentFreshProcessDomain.IsIdentifier(
                document.InvocationIdentity) ||
            !TryMapLocator(
                document.StateLocatorFamily,
                out var locator) ||
            !TryMapRestoreIntent(
                document.RestoreIntent,
                out var intent) ||
            !TryMapTransition(
                document.Transition.Classification,
                out var transition) ||
            !LiveAgentFreshProcessDomain.IsCommitSha(
                document.Transition.FromHeadSha) ||
            !LiveAgentFreshProcessDomain.IsCommitSha(
                document.Transition.ToBaseSha) ||
            !LiveAgentFreshProcessDomain.IsCommitSha(
                document.Transition.ToHeadSha) ||
            !LiveAgentFreshProcessDomain.IsSha256(
                document.Transition.ReceiptSha256) ||
            locator == RestrictedStateLocatorFamily.Current &&
                !LiveAgentFreshProcessDomain.IsSha256(
                    document.ExpectedLineageSha256) ||
            locator != RestrictedStateLocatorFamily.Current &&
                document.ExpectedLineageSha256 is not null ||
            !StringComparer.Ordinal.Equals(
                document.Transition.ReceiptSha256,
                LiveAgentFreshProcessDomain.TransitionReceiptSha256(
                    document.ExpectedLineageSha256,
                    document.Transition.Classification,
                    document.Transition.FromHeadSha,
                    document.Transition.ToBaseSha,
                    document.Transition.ToHeadSha,
                    document.InvocationIdentity)))
        {
            return false;
        }

        byte[] policy;
        try
        {
            policy = new UTF8Encoding(false, true).GetBytes(
                document.Stable.TrustedPolicy);
        }
        catch (EncoderFallbackException)
        {
            return false;
        }

        var trusted = new AgentSessionTrustedRequest(
            document.Stable.RepositoryId,
            document.Stable.ReviewTarget,
            document.Stable.WorkflowIdentity,
            policy,
            document.Stable.BuildId,
            document.Stable.ProviderId,
            document.Stable.ModelId,
            document.Stable.AdapterId);
        if (!LiveAgentFreshProcessDomain.IsIdentifier(
                document.Stable.SessionId) ||
            !StringComparer.Ordinal.Equals(
                trusted.ProviderId,
                DeepSeekAdapterContext.Provider) ||
            !StringComparer.Ordinal.Equals(
                trusted.ModelId,
                DeepSeekAdapterContext.Model) ||
            !StringComparer.Ordinal.Equals(
                trusted.AdapterId,
                DeepSeekAdapterContext.Adapter) ||
            !AgentStableRequestMaterializer.TryMaterialize(
                trusted,
                priorSessionSha256: null,
                out var materialized) ||
            !LiveAgentFreshProcessDomain.TryMapScope(
                document.AuthorizedScope,
                out var authorizedScope))
        {
            return false;
        }

        var stable = materialized!.StablePlan;
        var requestedScope = new RestrictedStateScope(
            stable.RepositoryId,
            stable.WorkflowIdentity,
            stable.ReviewTarget,
            document.Stable.SessionId,
            stable.ProviderId,
            stable.ModelId,
            stable.AdapterId,
            stable.PolicySha256,
            stable.LimitsSha256,
            stable.ToolsetSha256,
            stable.BuildId);
        accessResult = AuthorizedStateAccess.Authorize(
            new RestrictedStateAccessRequest(
                requestedScope,
                authorizedScope!,
                document.IsTrustedWorkflow,
                document.IsSameRepository,
                document.IsForkOrigin),
            out access);
        input = new LiveAgentFreshProcessAuthorizedInput(
            document,
            trusted,
            requestedScope,
            locator,
            intent,
            transition,
            LiveAgentFreshProcessDomain.InvocationIdentitySha256(
                document.InvocationIdentity));
        return true;
    }

    private static bool HasRequiredAuthorizationValues(
        LiveAgentFreshProcessAuthorizationDocument document) =>
        document.Kind is not null &&
        document.ExecutionProfile is not null &&
        document.StateLocatorFamily is not null &&
        document.RestoreIntent is not null &&
        document.InvocationIdentity is not null &&
        document.Stable.RepositoryId is not null &&
        document.Stable.WorkflowIdentity is not null &&
        document.Stable.TrustedPolicy is not null &&
        document.Stable.BuildId is not null &&
        document.Stable.ProviderId is not null &&
        document.Stable.ModelId is not null &&
        document.Stable.AdapterId is not null &&
        document.Stable.SessionId is not null &&
        document.AuthorizedScope.RepositoryId is not null &&
        document.AuthorizedScope.WorkflowIdentity is not null &&
        document.AuthorizedScope.SessionId is not null &&
        document.AuthorizedScope.ProviderId is not null &&
        document.AuthorizedScope.ModelId is not null &&
        document.AuthorizedScope.AdapterId is not null &&
        document.AuthorizedScope.PolicySha256 is not null &&
        document.AuthorizedScope.LimitsSha256 is not null &&
        document.AuthorizedScope.ToolsetSha256 is not null &&
        document.AuthorizedScope.BuildId is not null &&
        document.Transition.Classification is not null &&
        document.Transition.FromHeadSha is not null &&
        document.Transition.ToBaseSha is not null &&
        document.Transition.ToHeadSha is not null &&
        document.Transition.ReceiptSha256 is not null;

    private static bool TryAdmitReviewedInput(
        LiveAgentFreshProcessAuthorizedInput input,
        LiveAgentFreshProcessReviewedInputDocument document,
        out ReviewedIdentity? identity,
        out ProjectChatMessage? currentContext)
    {
        identity = null;
        currentContext = null;
        if (document is null ||
            !StringComparer.Ordinal.Equals(
                document.Kind,
                LiveAgentFreshProcessDomain.ReviewedInputKind) ||
            !LiveAgentFreshProcessDomain.TryMapReviewedIdentity(
                document.ReviewedIdentity,
                out identity) ||
            !StringComparer.Ordinal.Equals(
                identity!.RepositoryId,
                input.Scope.RepositoryId) ||
            identity.ReviewTarget != input.Scope.ReviewTarget ||
            !AgentValueDomains.IsUtf8(
                document.ReviewContext,
                1,
                AgentLimits.ContentBytes))
        {
            return false;
        }

        currentContext = new ProjectChatMessage(
            "user",
            [new ProjectTextContent(document.ReviewContext)]);
        return true;
    }

    private static bool TryAdmitPhaseAndTransition(
        string phase,
        LiveAgentFreshProcessAuthorizedInput input,
        ReviewedIdentity identity,
        LiveAgentFreshProcessAdmittedLineage? prior,
        out long generation,
        out string? predecessor,
        out string? transportPhase)
    {
        generation = 0;
        predecessor = null;
        transportPhase = null;
        var transition = input.Document.Transition;
        if (!StringComparer.Ordinal.Equals(
                transition.ToBaseSha,
                identity.BaseSha) ||
            !StringComparer.Ordinal.Equals(
                transition.ToHeadSha,
                identity.HeadSha))
        {
            return false;
        }

        if (input.LocatorFamily == RestrictedStateLocatorFamily.Current)
        {
            if (!StringComparer.Ordinal.Equals(phase, "continue") ||
                input.RestoreIntent != RestrictedStateRestoreIntent.Explicit ||
                input.Transition != AgentSessionHeadTransition.VerifiedAhead ||
                prior is null ||
                !prior.Lineage.TransitionAuthorized ||
                prior.Lineage.Generation == long.MaxValue ||
                StringComparer.Ordinal.Equals(
                    prior.InvocationIdentitySha256,
                    input.InvocationIdentitySha256) ||
                !StringComparer.Ordinal.Equals(
                    transition.FromHeadSha,
                    prior.ProducerIdentity.HeadSha) ||
                !StringComparer.Ordinal.Equals(
                    input.Document.ExpectedLineageSha256,
                    prior.RawSha256))
            {
                return false;
            }

            generation = prior.Lineage.Generation + 1;
            predecessor = prior.Lineage.EnvelopeSha256;
            transportPhase = "continue";
            return true;
        }

        if (phase is not ("bootstrap" or "continue") ||
            input.LocatorFamily is not (
                RestrictedStateLocatorFamily.Absent or
                RestrictedStateLocatorFamily.NonCurrent) ||
            input.Transition != AgentSessionHeadTransition.SameHead ||
            prior is not null ||
            !StringComparer.Ordinal.Equals(
                transition.FromHeadSha,
                identity.HeadSha))
        {
            return false;
        }

        transportPhase = "bootstrap";
        return true;
    }

    private static LiveAgentFreshProcessCommandResult PublishFailure(
        ILiveAgentFreshProcessFileSystem fileSystem,
        LiveAgentFreshProcessAuthorizedRoot root,
        LiveAgentFreshProcessAuthorizedInput input,
        string code)
    {
        var result = new LiveAgentFreshProcessResultDocument(
            LiveAgentFreshProcessDomain.ResultKind,
            code,
            null,
            input.Document.Transition.Classification,
            0,
            0,
            null,
            null,
            null,
            null,
            null,
            null,
            input.InvocationIdentitySha256,
            false);
        return fileSystem.PublishResult(
                root,
                LiveAgentFreshProcessCodec.Write(result)) is not
                { Durable: true }
            ? Failure(
                exitCode: 40,
                LiveAgentFreshProcessCodes.OutputFailed)
            : Failure(exitCode: 1, code);
    }

    private static void DisposeSnapshot(
        LiveAgentFreshProcessSnapshotInput input)
    {
        if (input.FileAccessFactory is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private static bool TryMapLocator(
        string value,
        out RestrictedStateLocatorFamily locator)
    {
        locator = value switch
        {
            "absent" => RestrictedStateLocatorFamily.Absent,
            "non_current" => RestrictedStateLocatorFamily.NonCurrent,
            "current" => RestrictedStateLocatorFamily.Current,
            _ => default,
        };
        return value is "absent" or "non_current" or "current";
    }

    private static bool TryMapRestoreIntent(
        string value,
        out RestrictedStateRestoreIntent intent)
    {
        intent = value switch
        {
            "automatic" => RestrictedStateRestoreIntent.Automatic,
            "explicit" => RestrictedStateRestoreIntent.Explicit,
            _ => default,
        };
        return value is "automatic" or "explicit";
    }

    private static bool TryMapTransition(
        string value,
        out AgentSessionHeadTransition transition)
    {
        transition = value switch
        {
            "same_head" => AgentSessionHeadTransition.SameHead,
            "verified_ahead" => AgentSessionHeadTransition.VerifiedAhead,
            "unknown" => AgentSessionHeadTransition.Unknown,
            "diverged" => AgentSessionHeadTransition.Diverged,
            "unrelated" => AgentSessionHeadTransition.Unrelated,
            _ => default,
        };
        return value is "same_head" or
            "verified_ahead" or
            "unknown" or
            "diverged" or
            "unrelated";
    }

    private static LiveAgentFreshProcessCommandResult Failure(
        int exitCode,
        string code) => new(exitCode, code);
}

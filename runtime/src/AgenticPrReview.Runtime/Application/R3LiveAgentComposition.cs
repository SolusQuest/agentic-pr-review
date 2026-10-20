using AgenticPrReview.Runtime.Agent.Chat;
using System.Security.Cryptography;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Host.State;

namespace AgenticPrReview.Runtime;

internal interface IR3LiveAgentStateRestorer
{
    Task<RestrictedStateRestoreResult> RestoreAsync(
        string stateRoot,
        IRestrictedStateKeyResolver keyResolver,
        AuthorizedStateAccess access,
        RestrictedStateRestoreRequest request,
        TimeProvider timeProvider,
        CancellationToken cancellationToken);
}

internal sealed class R3LiveAgentStateRestorer(
    Func<string, LocalRestrictedStateStore>? storeFactory = null) : IR3LiveAgentStateRestorer
{
    public async Task<RestrictedStateRestoreResult> RestoreAsync(
        string stateRoot,
        IRestrictedStateKeyResolver keyResolver,
        AuthorizedStateAccess access,
        RestrictedStateRestoreRequest request,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var store = storeFactory is null ? new LocalRestrictedStateStore(stateRoot) : storeFactory(stateRoot);
        var service = new RestrictedStateService(
            store,
            keyResolver,
            new AgentSessionRestrictedStateAdmission(),
            () => timeProvider.GetUtcNow().ToUnixTimeSeconds());
        return await service.RestoreAsync(
                access,
                request,
                cancellationToken)
            .ConfigureAwait(false);
    }
}

internal interface IR3LiveAgentChatFactory
{
    IProjectChatClient Create(DeepSeekAdapterContext context);
}

internal sealed class R3LiveAgentUnavailableChatFactory
    : IR3LiveAgentChatFactory
{
    public IProjectChatClient Create(DeepSeekAdapterContext context) =>
        throw new InvalidOperationException("Historical provider execution is unavailable.");
}

internal interface IR3LiveAgentReviewedFileAccessFactory
{
    IReviewedFileAccess Create();
}

internal interface IR3LiveAgentDiagnosticStageObserver
{
    void Enter(string diagnosticCode);
}

internal sealed class R3LiveAgentReviewedFileAccessFactory
    : IR3LiveAgentReviewedFileAccessFactory
{
    public IReviewedFileAccess Create() => new VerifiedReviewedFileAccess();
}

internal sealed class R3LiveAgentDependencies(
    IR3LiveAgentSecretSource secretSource,
    IR3LiveAgentStateRestorer stateRestorer,
    IR3LiveAgentChatFactory transportFactory,
    IR3LiveAgentReviewedFileAccessFactory reviewedFileAccessFactory,
    ILiveAgentStateCommitCoordinator stateCommitCoordinator,
    TimeProvider timeProvider,
    IR3LiveAgentDiagnosticStageObserver? diagnosticStageObserver = null)
{
    internal IR3LiveAgentSecretSource SecretSource { get; } = secretSource;

    internal IR3LiveAgentStateRestorer StateRestorer { get; } = stateRestorer;

    internal IR3LiveAgentChatFactory ChatFactory { get; } =
        transportFactory;

    internal IR3LiveAgentReviewedFileAccessFactory ReviewedFileAccessFactory {
        get;
    } = reviewedFileAccessFactory;

    internal ILiveAgentStateCommitCoordinator StateCommitCoordinator { get; } =
        stateCommitCoordinator;

    internal TimeProvider TimeProvider { get; } = timeProvider;

    internal IR3LiveAgentDiagnosticStageObserver? DiagnosticStageObserver {
        get;
    } = diagnosticStageObserver;

    internal static R3LiveAgentDependencies CreateDefault(
        ILiveAgentAcceptedLineageSink lineageSink)
    {
        var timeProvider = TimeProvider.System;
        return new(
            new R3LiveAgentEnvironmentSecretSource(),
            new R3LiveAgentStateRestorer(),
            new R3LiveAgentUnavailableChatFactory(),
            new R3LiveAgentReviewedFileAccessFactory(),
            new LiveAgentStateCommitCoordinator(
                new LiveAgentStateTransactionFactory(timeProvider),
                new AgentSessionRestrictedStateAdmission(),
                lineageSink),
            timeProvider);
    }

    public override string ToString() => "r3_live_agent_dependencies";
}

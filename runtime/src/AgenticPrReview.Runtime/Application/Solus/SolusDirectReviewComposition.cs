using System.Security.Cryptography;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Execution.Solus;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Host.State.Transactions;
using AgenticPrReview.Runtime.Solus;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Runtime.Api.Exposure;

namespace AgenticPrReview.Runtime.Application.Solus;

internal sealed record SolusDirectExecution(R3LiveAgentExecution Execution, LiveAgentFreshProcessTransportProof Proof);

// Real direct filesystem authority and the same pinned runtime/provider/tool/
// candidate boundary as Action. There is no Action launch, publisher or native
// SESSION construction on this path.
internal static class SolusDirectReviewComposition
{
    internal static async Task<SolusDirectExecution> RunAsync(LiveAgentFreshProcessFileSystem files,
        SolusDirectReviewAuthorization authorization, LiveAgentFreshProcessAdmittedLineage? prior,
        long generation, string? predecessor, ILiveAgentAcceptedLineageSink lineageSink,
        CancellationToken cancellationToken,
        Func<SolusDirectTransport, HttpMessageHandler>? trustedTransportDecorator = null,
        Func<IRestrictedContextSink, IRestrictedContextSink>? trustedCaptureDecorator = null)
    {
        var modelCalls = 0;
        CountedExecutor? executor = null;
        string? stableSha = null;
        string? terminalSha = null;
        var stage = R3LiveAgentDiagnosticCodes.PreparationFailed;
        using var transport = new SolusDirectTransport(prior is null ? "bootstrap" : "continue", authorization.PublicInputs);
        SolusDirectExecution Failure(string code) => new(new(new(code, Math.Max(modelCalls, transport.Requests),
            executor?.Invocations ?? 0, stableSha, terminalSha, null, null, null, false), stage), transport.Proof);
        try
        {
            using var targets = SolusFreshHostContext.TryCreateDirect(authorization);
            if (targets is null || !AgentStableRequestMaterializer.TryMaterialize(
                    authorization.Input.TrustedRequest, null, out var stable) ||
                !SolusReviewCapacity.TrySelect(targets.RuntimePolicy.ConfigBytes.AsSpan(), out var capacity) ||
                authorization.CurrentContext.Contents is not [ProjectTextContent currentText])
                return Failure(R3LiveAgentCodes.InputInvalid);
            stableSha = AgentCanonical.HashDomain(AgentCanonical.StablePlanDomain,
                AgentCanonical.StablePlanBytes(stable!.StablePlan));
            var instructions = SolusFreshReviewComposition.Instructions(targets.RuntimePolicy.InstructionBytes.AsSpan());
            AgentInput[] data = [new(AgentInputSource.Repository, currentText.Text)];
            try { SolusReviewBridge.ValidateText(instructions, data); }
            catch (ArgumentException) { return Failure(R3LiveAgentCodes.InputInvalid); }
            var executionId = Guid.NewGuid();
            executor = new CountedExecutor(new SnapshotToolExecutor(authorization.Snapshot, authorization.CreateFileAccess()));
            var tools = new SolusSnapshotTools(executionId, authorization.Snapshot, executor);
            var hooks = new DirectHooks(targets, cancellationToken);
            var compatibility = SolusRuntimeConstruction.DescribeFreshConfiguration(targets, tools.Registrations, hooks);

            var secrets = new R3LiveAgentEnvironmentSecretSource().TakeAndClear();
            if (string.IsNullOrWhiteSpace(secrets.ProviderCredential) ||
                !R3LiveAgentStateKeyResolver.TryCreate(secrets.StateKeyBase64, out var keys))
                return Failure(R3LiveAgentCodes.SecretInvalid);
            using var ownedKeys = keys!;
            // Admission binds only the encrypted record metadata. The
            // selected typed admission arm uses the current outer binding only.
            var stateContext = new RestrictedStateSessionAdmissionContext(authorization.Snapshot.Identity.BaseSha,
                authorization.Snapshot.Identity.HeadSha, generation, predecessor);
            var state = new SolusDirectStateComposer(files, authorization, targets, compatibility, keys!, TimeProvider.System);
            stage = R3LiveAgentDiagnosticCodes.StateRestoreFailed;
            var selected = await state.SelectAsync(prior, stateContext, cancellationToken).ConfigureAwait(false);
            using var source = selected.Selection;
            if (source is null) return Failure(selected.Code);
            SolusConsumedContext? consumed = null;
            try
            {
                if (!source.IsFresh)
                {
                    using var prepared = await state.PrepareAsync(source, executionId, Guid.NewGuid(), cancellationToken)
                        .ConfigureAwait(false);
                    if (prepared is null || (consumed = RestrictedStateService.IssueDirectSolusContext(prepared)) is null)
                        return Failure(RestrictedStateCodes.Conflict);
                }
                stage = R3LiveAgentDiagnosticCodes.AgentRunFailed;
                SolusRestrictedStateCapture? capture = null;
                SolusRestrictedStateRecord? record = null;
                try
                {
                    var domain = new SolusReviewCandidateHost(executionId, tools);
                    using var bridge = new SolusReviewBridge(executionId, DeepSeekCredential.Create(secrets.ProviderCredential),
                        hooks, capacity!.Tokens, trustedTransportDecorator?.Invoke(transport) ?? transport);
                    var run = await bridge.RunAuthorizedAsync(targets, instructions, data, tools, domain, null,
                        TimeSpan.FromSeconds(capacity.TimeoutSeconds), cancellationToken,
                        sinkFactory: actual => {
                            capture = new(executionId, actual);
                            return trustedCaptureDecorator?.Invoke(capture) ?? capture;
                        }, expectedCompatibility: compatibility, consumed: consumed).ConfigureAwait(false);
                    modelCalls = run.Work?.Accounting?.ModelCalls ?? transport.Requests;
                    var actual = run.Execution;
                    if (cancellationToken.IsCancellationRequested) return Failure(RestrictedStateCodes.Cancelled);
                    AgentTerminalReview? review = null;
                    if (actual is null || !SolusFreshReviewComposition.Correlates(actual, executionId, consumed) ||
                        !domain.TryComplete(actual, out review) || run.Work is not { EligibleForProductValidation: true } || review is null)
                        return Failure(actual?.Context.Admission == ContextAdmission.Rejected
                            ? RestrictedStateCodes.EnvelopeInvalid : R3LiveAgentCodes.CompositionFailed);
                    terminalSha = review.TerminalSha256;
                    if (capture is null || !capture.TryComplete(actual, out record) || record is null ||
                        record.Checkpoint.ExecutionId != executionId || !authorization.IsLive ||
                        !transport.Proof.IsSatisfiedBy(terminalSha)) return Failure(RestrictedStateCodes.Conflict);
                    if (!SolusRestrictedStateSnapshotCodec.TryWrite(record, out var encoded))
                        return Failure(RestrictedStateCodes.EnvelopeInvalid);
                    try
                    {
                        var admission = new SolusDirectStateAdmission(authorization, targets, compatibility)
                            .Admit(authorization.Root.Access, encoded, stateContext);
                        try
                        {
                            if (admission.Session is not { } admitted ||
                                admitted.SolusRecord?.Checkpoint != record.Checkpoint) return Failure(RestrictedStateCodes.EnvelopeInvalid);
                        }
                        finally
                        {
                            if (admission.Session is { } admitted) CryptographicOperations.ZeroMemory(admitted.Plaintext);
                        }
                        stage = R3LiveAgentDiagnosticCodes.StateCommitFailed;
                        var committed = await state.CreateCommitCoordinator(lineageSink).CommitPreparedAsync(
                            new(encoded, SolusRestrictedStateSnapshotCodec.RecordSha256(encoded), generation),
                            authorization.Root.Access, prior?.Lineage, authorization.Input.Transition, stateContext,
                            authorization.Root.StateRootPath, keys!, cancellationToken).ConfigureAwait(false);
                        return new(new(new(committed.Code, modelCalls, executor.Invocations, stableSha, terminalSha,
                            committed.AcceptedGeneration, committed.AcceptedSessionSha256,
                            committed.AcceptedEnvelopeSha256, committed.HandoffReady)), transport.Proof);
                    }
                    finally { CryptographicOperations.ZeroMemory(encoded); }
                }
                finally
                {
                    capture?.Dispose();
                }
            }
            finally { consumed?.Dispose(); }
        }
        catch (OperationCanceledException) { return Failure(RestrictedStateCodes.Cancelled); }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        { return Failure(R3LiveAgentCodes.CompositionFailed); }
    }

    private sealed class CountedExecutor(IAgentToolExecutor inner) : IAgentToolExecutor
    {
        internal int Invocations;
        public string? Preflight(PreparedAgentToolCall call) => inner.Preflight(call);
        public ValueTask<AgentToolExecution> ExecuteAsync(PreparedAgentToolCall call, CancellationToken token)
        {
            Interlocked.Increment(ref Invocations);
            return inner.ExecuteAsync(call, token);
        }
    }

    private sealed class DirectHooks(SolusFreshHostContext targets, CancellationToken caller) : IRuntimeExposureHooks
    {
        public ValueTask<ExposureAcknowledgement?> BeforeDispatchAsync(RuntimeExposure exposure, CancellationToken token)
        {
            var allowed = targets.IsLive && !caller.IsCancellationRequested && !token.IsCancellationRequested;
            return ValueTask.FromResult<ExposureAcknowledgement?>(new(exposure, RuntimeHookStatus.Acknowledged,
                allowed ? ExposureDecision.Permit : ExposureDecision.Deny, allowed ? exposure.RequiredAcknowledgement : null));
        }
        public ValueTask<SettlementAcknowledgement?> AfterAttemptAsync(RuntimeSettlement settlement, CancellationToken token) =>
            ValueTask.FromResult<SettlementAcknowledgement?>(new(settlement.Exposure, RuntimeHookStatus.Acknowledged,
                targets.IsLive && !caller.IsCancellationRequested && !token.IsCancellationRequested
                    ? RuntimeContinuation.Continue : RuntimeContinuation.Stop, settlement.Accounting));
    }
}

using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Accounting;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;

internal interface ILiveChatFactory
{
    IAuthoredResponseSource Create(DeepSeekAdapterContext context);
}

// Historical live evaluation has no production provider capability after R8.
internal sealed class UnavailableLiveChatFactory : ILiveChatFactory
{
    internal static readonly UnavailableLiveChatFactory Instance = new();
    public IAuthoredResponseSource Create(DeepSeekAdapterContext context) =>
        throw new IOException("historical_live_provider_unavailable");
}

// Scripted failure labels exercise the frozen accounting journal, not real
// HTTP statuses, retries, endpoints or transport behavior.
internal sealed class HistoricalScriptFailure(string outcome) : IOException("historical_script_failure")
{
    internal string Outcome { get; } = outcome;
}

internal sealed class LiveMeteredChat(IAuthoredResponseSource inner, LiveAccounting accounting,
    ILiveAttemptObserver? attempt = null) : IAuthoredResponseSource, IDisposable
{
    public async Task<ProjectChatResponse> GetResponseAsync(ProjectChatRequest request, CancellationToken token)
    {
        var call = attempt?.CurrentCall;
        var flag = new CancellationFlag();
        using var registration = token.Register(static state =>
        {
            var (metered, seen, current) = ((LiveMeteredChat, CancellationFlag, ILiveCallObserver?))state!;
            current?.Cancel(); metered.RecordCancelledOnce(seen);
        }, (this, flag, call));
        try
        {
            token.ThrowIfCancellationRequested();
            if (request.MaxOutputTokens is < 1 or > 65_536) throw new ProjectChatNormalizationException();
            if (!accounting.TryReserve())
            {
                call?.Refuse(accounting.AccountingViolation ? "violation_refused" : "budget_refused");
                throw new IOException("historical_script_refused");
            }
            if (call is not null && !call.Dispatch())
            {
                token.ThrowIfCancellationRequested(); throw new IOException("historical_script_refused");
            }
            try
            {
                // Fence the sample here: this owner must record cancellation before
                // the outer scheduler can seal accounting and publish its summary.
                var response = await inner.GetResponseAsync(request, token).WaitAsync(token);
                var outcome = response.CapturedResponseBodyBytes > Agent.AgentLimits.ResponseBytes ? "response_too_large" : "success";
                call?.ScriptFinished(outcome); accounting.RecordOutcome(outcome);
                return response;
            }
            catch (ProjectChatNormalizationException)
            {
                // An explicitly authored typed normalization failure has completed its
                // script dispatch. The frozen journal retains unknown usage, not zero.
                call?.ScriptFinished("success"); accounting.RecordOutcome("success"); throw;
            }
            catch (HistoricalScriptFailure error)
            {
                call?.ScriptFinished(error.Outcome); accounting.RecordOutcome(error.Outcome); throw;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                call?.ScriptFinished("transport_failure"); accounting.RecordOutcome("transport_failure"); throw;
            }
        }
        catch (OperationCanceledException)
        {
            call?.Cancel(); RecordCancelledOnce(flag); throw;
        }
    }
    private void RecordCancelledOnce(CancellationFlag flag)
    { if (Interlocked.Exchange(ref flag.Seen, 1) == 0) accounting.RecordCancelled(); }
    private sealed class CancellationFlag { internal int Seen; }
    public void Dispose() => (inner as IDisposable)?.Dispose();
}

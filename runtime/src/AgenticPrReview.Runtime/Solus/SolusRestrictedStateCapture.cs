using System.Security.Cryptography;
using AgenticPrReview.Runtime.Host.State;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;

namespace AgenticPrReview.Runtime.Solus;

// The trusted Host gives this sink to exactly one awaited runtime invocation.
// No keys, live clients, storage, claims or invocation capabilities enter it.
internal sealed class SolusRestrictedStateCapture : IRestrictedContextSink, IDisposable
{
    private readonly object gate = new();
    private readonly Guid executionId;
    private readonly SolusRestrictedStateCompatibility expected;
    private byte[]? payload;
    private bool failed;
    private bool finished;

    internal SolusRestrictedStateCapture(Guid executionId, SolusRestrictedStateCompatibility expected)
    {
        if (executionId == Guid.Empty || !SolusRestrictedStateAdmission.IsSupported(expected))
            throw new ArgumentException("APR_SOLUS_CAPTURE_INVALID");
        this.executionId = executionId;
        this.expected = expected;
    }

    public void Capture(AgentContextEnvelope context)
    {
        lock (gate)
        {
            if (finished || failed || payload is not null || context is null ||
                context.ImplementationId != expected.ImplementationId ||
                context.FormatVersion != expected.FormatVersion ||
                context.CompatibilityVersion != expected.CompatibilityVersion ||
                context.PayloadByteCount is < 1 or > SolusRestrictedStateFormat.MaximumPayloadBytes)
            {
                failed = true;
                Clear();
                throw new InvalidOperationException("APR_SOLUS_CAPTURE_INVALID");
            }

            payload = context.CopyRestrictedPayload();
        }
    }

    internal bool TryComplete(ContextCandidateExecutionResult? result, out SolusRestrictedStateRecord? record)
    {
        lock (gate)
        {
            record = null;
            if (finished) return false;
            finished = true;
            try
            {
                var context = result?.Context;
                var candidates = result?.Candidates;
                var source = context?.Checkpoint;
                if (failed || payload is null || context is null || candidates is null || source is null ||
                    context.ExecutionId != executionId || source.ExecutionId != executionId ||
                    context.CaptureStatus != ContextCaptureStatus.Delivered ||
                    !((context.Admission == ContextAdmission.Fresh && context.Intent == ContextExecutionIntent.Fresh) ||
                      (context.Admission == ContextAdmission.Supplied && context.Intent == ContextExecutionIntent.NewRunFromContext)) ||
                    context.Outcome?.Reason != AgentTerminationReason.Completed ||
                    candidates.StopReason != CandidateStopReason.Completed ||
                    candidates.Receipts.Count != 1 || candidates.Receipts[0].ExecutionId != executionId ||
                    !candidates.Receipts[0].IsAccepted ||
                    candidates.Receipts[0].Continuation != CandidateContinuation.End)
                {
                    return false;
                }

                return SolusRestrictedStateRecord.TryCreate(expected,
                    new(source.CheckpointId, source.LogicalWorkId, source.RoundId, source.ExecutionId), payload, out record);
            }
            finally
            {
                Clear();
            }
        }
    }

    private void Clear()
    {
        if (payload is not null) CryptographicOperations.ZeroMemory(payload);
        payload = null;
    }

    public void Dispose()
    {
        lock (gate)
        {
            finished = true;
            Clear();
        }
    }

    public override string ToString() => nameof(SolusRestrictedStateCapture);
}

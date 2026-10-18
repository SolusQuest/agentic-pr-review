using SolusAgent.Api.Context;

namespace AgenticPrReview.Runtime.SolusFreshHostProof;

internal sealed class CaptureFailure(IRestrictedContextSink inner) : IRestrictedContextSink
{
    public void Capture(AgentContextEnvelope context)
    {
        inner.Capture(context);
        throw new InvalidOperationException("V1_PRIVATE_CAPTURE_CANARY");
    }
}

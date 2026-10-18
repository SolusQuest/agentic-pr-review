using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.Host.State;
using SolusAgent.Api.Context;
using SolusAgent.Runtime.Api.Providers;

namespace AgenticPrReview.Runtime.SolusFreshHostProof;

// Deliberately corrupt the opaque source at the trusted fault seam, before S1
// encrypts/authenticates it. These are negative inputs, never genuine captures.
internal sealed class SourceFault(IRestrictedContextSink inner, string mode) : IRestrictedContextSink
{
    public void Capture(AgentContextEnvelope context)
    {
        var bytes = context.CopyRestrictedPayload();
        byte[]? changed = null;
        try
        {
            var value = JsonNode.Parse(bytes)!;
            if (mode == "seed-source-checkpoint") value["Info"]!["CheckpointId"] = Guid.NewGuid();
            else if (mode == "seed-source-unfinished") value["Candidates"]!["Cursor"] = 1;
            else if (mode == "seed-source-provider") value["Provider"]!["FormatVersion"] = 99;
            else throw new ProofFailure("unknown_source_fault");
            changed = System.Text.Encoding.UTF8.GetBytes(value.ToJsonString());
            inner.Capture(new(context.ImplementationId, context.FormatVersion, context.CompatibilityVersion, changed));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (changed is not null) CryptographicOperations.ZeroMemory(changed);
        }
    }
}

internal sealed class CaptureFault(IRestrictedContextSink inner, string mode, CancellationTokenSource cancellation) : IRestrictedContextSink
{
    public void Capture(AgentContextEnvelope context)
    {
        if (mode == "capture-missing") return;
        if (mode == "cancel-capture") { inner.Capture(context); cancellation.Cancel(); return; }
        if (mode == "capture-duplicate") { inner.Capture(context); inner.Capture(context); return; }
        if (mode == "capture-throw") { inner.Capture(context); throw new InvalidOperationException("S4_PRIVATE_CAPTURE_CANARY"); }
        if (mode == "capture-wrong-format")
        {
            var bytes = context.CopyRestrictedPayload();
            try { inner.Capture(new(context.ImplementationId, context.FormatVersion + 1, context.CompatibilityVersion, bytes)); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            return;
        }
        if (mode == "capture-oversized")
        {
            inner.Capture(new(context.ImplementationId, context.FormatVersion, context.CompatibilityVersion,
                new byte[SolusRestrictedStateFormat.MaximumPayloadBytes + 1]));
            return;
        }
        throw new ProofFailure("unknown_capture_fault");
    }
}

internal sealed class ExportFailure(IModelProvider inner) : IModelProvider, IProviderContextPersistence, IProviderRequestValidation
{
    public ProviderScope Scope => inner.Scope;
    public ProviderCapabilities Capabilities => inner.Capabilities;
    public ValueTask<ProviderExchangeResult> ExchangeAsync(ProviderRequest request, CancellationToken token = default) => inner.ExchangeAsync(request, token);
    public ProviderError ValidateRequest(ProviderRequest request) => ((IProviderRequestValidation)inner).ValidateRequest(request);
    public ProviderSavedState? ExportContext(ProviderContextBinding binding) => throw new InvalidOperationException("S4_PRIVATE_CAPTURE_CANARY");
    public bool AdmitContext(ProviderContextBinding binding, ProviderSavedState state) => ((IProviderContextPersistence)inner).AdmitContext(binding, state);
}
internal sealed class CapabilitiesMismatch(IModelProvider inner) : IModelProvider, IProviderContextPersistence, IProviderRequestValidation
{
    public ProviderScope Scope => inner.Scope;
    public ProviderCapabilities Capabilities => inner.Capabilities & ~ProviderCapabilities.UsageReporting;
    public ValueTask<ProviderExchangeResult> ExchangeAsync(ProviderRequest request, CancellationToken token = default) => inner.ExchangeAsync(request, token);
    public ProviderError ValidateRequest(ProviderRequest request) => ((IProviderRequestValidation)inner).ValidateRequest(request);
    public ProviderSavedState? ExportContext(ProviderContextBinding binding) => ((IProviderContextPersistence)inner).ExportContext(binding);
    public bool AdmitContext(ProviderContextBinding binding, ProviderSavedState state) => ((IProviderContextPersistence)inner).AdmitContext(binding, state);
}

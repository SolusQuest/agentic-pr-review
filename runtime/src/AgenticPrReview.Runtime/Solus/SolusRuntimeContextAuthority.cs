using System.Security.Cryptography;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Restore;
using SolusAgent.Api.Context;
using SolusAgent.Runtime.Api.Configuration;

namespace AgenticPrReview.Runtime.Solus;

internal sealed class SolusRuntimeContextAuthority(
    SolusFreshHostContext targets,
    SolusRestrictedStateCompatibility compatibility,
    Func<bool> actualTargetsAreCurrent) : IRuntimeContextAuthority, IDisposable
{
    private SolusConsumedContext? prepared;
    private int disposed;

    internal bool TryArm(SolusConsumedContext consumed) => Volatile.Read(ref disposed) == 0 &&
        ReferenceEquals(consumed.Targets, targets) && consumed.Compatibility == compatibility &&
        targets.IsLive && actualTargetsAreCurrent() &&
        Interlocked.CompareExchange(ref prepared, consumed, null) is null;

    public bool TryClaim(AgentContextEnvelope context, ContextRoundGrant grant)
    {
        var consumed = Volatile.Read(ref prepared);
        if (Volatile.Read(ref disposed) != 0 || consumed is null || context is null || grant is null ||
            !targets.IsLive || !actualTargetsAreCurrent() ||
            context.ImplementationId != compatibility.ImplementationId ||
            context.FormatVersion != compatibility.FormatVersion ||
            context.CompatibilityVersion != compatibility.CompatibilityVersion ||
            context.PayloadByteCount is < 1 or > SolusRestrictedStateFormat.MaximumPayloadBytes)
            return false;
        var bytes = context.CopyRestrictedPayload();
        try
        {
            return consumed.TryClaim(targets, compatibility,
                new(grant.Source.CheckpointId, grant.Source.LogicalWorkId,
                    grant.Source.RoundId, grant.Source.ExecutionId),
                grant.GrantId, grant.ExecutionId, grant.RoundId, bytes);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref disposed, 1);
        Interlocked.Exchange(ref prepared, null)?.Dispose();
    }
}

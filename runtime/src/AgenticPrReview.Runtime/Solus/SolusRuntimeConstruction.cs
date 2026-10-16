using AgenticPrReview.Runtime.Agent;
using SolusAgent.Api.Execution;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Startup;

namespace AgenticPrReview.Runtime.Solus;

// Managed-only construction boundary. No production route selects it in R2.
internal static class SolusRuntimeConstruction
{
    internal static IAgent Create(RuntimeConfiguration configuration, string profileId,
        int logicalCalls = Cr1DevelopmentProfile.LogicalCalls)
    {
        if (!StringComparer.Ordinal.Equals(profileId, Cr1DevelopmentProfile.Id) ||
            logicalCalls is < 1 or > Cr1DevelopmentProfile.LogicalCalls ||
            configuration.Bounds.MaximumInputs != Cr1DevelopmentProfile.ProviderInputs ||
            configuration.Bounds.MaximumTools != Cr1DevelopmentProfile.NonterminalDefinitions ||
            configuration.Bounds.MaximumToolCalls != Cr1DevelopmentProfile.ToolCallsPerBatch ||
            configuration.Bounds.MaximumRequestBytes != Cr1DevelopmentProfile.RequestPayloadBytes ||
            configuration.Bounds.MaximumResponseBytes != Cr1DevelopmentProfile.ResponsePayloadBytes ||
            configuration.Bounds.MaximumContinuationBytes != Cr1DevelopmentProfile.ContinuationBytes ||
            configuration.RequiredGuarantees !=
                (RuntimeGuarantee.OrderedExposure | RuntimeGuarantee.ProviderBounds))
            throw new ArgumentException("APR_SOLUS_PROFILE_UNSUPPORTED");
        return RuntimeAgentFactory.Create(configuration, Options());
    }

    internal static RuntimeOptions Options() => new(
        maximumAttempts: Cr1DevelopmentProfile.RetainedAttempts,
        maximumRecords: Cr1DevelopmentProfile.RetainedRecords,
        maximumRetainedBytes: Cr1DevelopmentProfile.RetainedBytes,
        settlementGrace: TimeSpan.FromSeconds(Cr1DevelopmentProfile.SettlementGraceSeconds),
        requireContinuation: Cr1DevelopmentProfile.RequireContinuation);
}

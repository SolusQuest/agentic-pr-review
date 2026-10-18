using AgenticPrReview.Runtime.Agent;
using System.Security.Cryptography;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.Restore;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using SolusRuntimeOptions = SolusAgent.Runtime.Startup.RuntimeOptions;

namespace AgenticPrReview.Runtime.Solus;

// Managed-only construction boundary. No production route selects it in R2.
internal static class SolusRuntimeConstruction
{
    // This callback is trusted compiled Host composition. Its input is fresh
    // authorization and actual snapshot access, never deserialized metadata.
    internal static SolusAuthorizedRuntime CreateForAuthorizedReview(SolusFreshHostContext context,
        Func<SolusFreshHostContext, SolusRuntimeTargets> construct) =>
        SolusAuthorizedRuntime.Create(context, construct);

    internal static SolusRestrictedStateCompatibility DescribeFreshConfiguration(SolusFreshHostContext context,
        IReadOnlyList<RuntimeToolRegistration> registrations, IRuntimeExposureHooks hooks)
    {
        if (!context.IsLive || registrations.Count is < 1 or > Cr1DevelopmentProfile.NonterminalDefinitions)
            throw new ArgumentException("APR_SOLUS_TARGET_AUTHORITY");
        var tools = registrations.ToArray();
        if (tools.Any(tool => tool is null || tool.Recovery is not null))
            throw new ArgumentException("APR_SOLUS_TARGET_PROFILE_UNSUPPORTED");
        var correction = SolusSnapshotTools.ArgumentCorrectionFor(tools, context.Snapshot)
            ?? throw new ArgumentException("APR_SOLUS_TARGET_PROFILE_UNSUPPORTED");
        // Exact installed internal.4 DeepSeek contract. Actual construction is
        // independently compared before exchange; this description grants no claim.
        const ProviderCapabilities capabilities = ProviderCapabilities.ToolCalls | ProviderCapabilities.Continuation |
            ProviderCapabilities.UsageReporting | ProviderCapabilities.ToolArgumentCorrection;
        var bounds = new ProviderExchangeBounds(Cr1DevelopmentProfile.ProviderInputs,
            Cr1DevelopmentProfile.NonterminalDefinitions, Cr1DevelopmentProfile.ToolCallsPerBatch,
            Cr1DevelopmentProfile.RequestPayloadBytes, Cr1DevelopmentProfile.ResponsePayloadBytes,
            Cr1DevelopmentProfile.ContinuationBytes);
        return SolusAuthorizedRuntime.Compatibility(context, capabilities, tools, hooks, bounds,
            RuntimeGuarantee.OrderedExposure | RuntimeGuarantee.ProviderBounds | RuntimeGuarantee.ToolArgumentCorrection, correction);
    }

    internal static IAgent Create(RuntimeConfiguration configuration, string profileId,
        int logicalCalls = Cr1DevelopmentProfile.LogicalCalls, SolusRuntimeOptions? options = null)
    {
        var guarantees = RuntimeGuarantee.OrderedExposure | RuntimeGuarantee.ProviderBounds;
        if (configuration.ToolArgumentCorrection is not null)
            guarantees |= RuntimeGuarantee.ToolArgumentCorrection;
        if (!StringComparer.Ordinal.Equals(profileId, Cr1DevelopmentProfile.Id) ||
            logicalCalls is < 1 or > Cr1DevelopmentProfile.LogicalCalls ||
            configuration.Bounds.MaximumInputs != Cr1DevelopmentProfile.ProviderInputs ||
            configuration.Bounds.MaximumTools != Cr1DevelopmentProfile.NonterminalDefinitions ||
            configuration.Bounds.MaximumToolCalls != Cr1DevelopmentProfile.ToolCallsPerBatch ||
            configuration.Bounds.MaximumRequestBytes != Cr1DevelopmentProfile.RequestPayloadBytes ||
            configuration.Bounds.MaximumResponseBytes != Cr1DevelopmentProfile.ResponsePayloadBytes ||
            configuration.Bounds.MaximumContinuationBytes != Cr1DevelopmentProfile.ContinuationBytes ||
            configuration.Bounds.MaximumTextBytes != Cr1DevelopmentProfile.ResponsePayloadBytes ||
            configuration.RequiredGuarantees != guarantees)
            throw new ArgumentException("APR_SOLUS_PROFILE_UNSUPPORTED");
        options ??= Options();
        if (options.MaximumAttempts != Cr1DevelopmentProfile.RetainedAttempts ||
            options.MaximumRecords != Cr1DevelopmentProfile.RetainedRecords ||
            options.MaximumRetainedBytes != Cr1DevelopmentProfile.RetainedBytes ||
            options.SettlementGrace != TimeSpan.FromSeconds(Cr1DevelopmentProfile.SettlementGraceSeconds) ||
            options.RequireContinuation != Cr1DevelopmentProfile.RequireContinuation)
            throw new ArgumentException("APR_SOLUS_PROFILE_UNSUPPORTED");
        return RuntimeAgentFactory.Create(configuration, options);
    }

    internal static SolusRuntimeOptions Options() => new(
        maximumAttempts: Cr1DevelopmentProfile.RetainedAttempts,
        maximumRecords: Cr1DevelopmentProfile.RetainedRecords,
        maximumRetainedBytes: Cr1DevelopmentProfile.RetainedBytes,
        settlementGrace: TimeSpan.FromSeconds(Cr1DevelopmentProfile.SettlementGraceSeconds),
        requireContinuation: Cr1DevelopmentProfile.RequireContinuation);
}

internal sealed record SolusRuntimeTargets(IModelProvider Provider,
    IReadOnlyList<RuntimeToolRegistration> Tools, IRuntimeExposureHooks? Hooks);

internal sealed class SolusAuthorizedRuntime : IDisposable
{
    private readonly SolusRuntimeContextAuthority authority;

    private SolusAuthorizedRuntime(IAgent agent, SolusRuntimeContextAuthority authority,
        SolusSourceSelectionRequest selection)
    {
        Agent = agent;
        this.authority = authority;
        SourceSelection = selection;
    }

    internal IAgent Agent { get; }
    internal SolusSourceSelectionRequest SourceSelection { get; }

    internal static SolusAuthorizedRuntime Create(SolusFreshHostContext context,
        Func<SolusFreshHostContext, SolusRuntimeTargets> construct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(construct);
        if (!context.TryBeginConstruction()) throw new ArgumentException("APR_SOLUS_TARGET_AUTHORITY");
        var targets = construct(context);
        if (!context.IsLive || targets is null || targets.Provider is null || targets.Tools is null ||
            targets.Tools.Count is < 1 or > Cr1DevelopmentProfile.NonterminalDefinitions ||
            targets.Provider.Scope.Provider != context.Policy.ProviderId ||
            targets.Provider.Scope.Model != context.Policy.ModelId)
            throw new ArgumentException("APR_SOLUS_TARGET_AUTHORITY");
        var tools = targets.Tools.ToArray();
        if (tools.Any(tool => tool is null || tool.Recovery is not null))
            throw new ArgumentException("APR_SOLUS_TARGET_PROFILE_UNSUPPORTED");
        var correction = SolusSnapshotTools.ArgumentCorrectionFor(tools, context.Snapshot);
        var guarantees = RuntimeGuarantee.OrderedExposure | RuntimeGuarantee.ProviderBounds;
        if (correction is not null) guarantees |= RuntimeGuarantee.ToolArgumentCorrection;
        var bounds = new ProviderExchangeBounds(Cr1DevelopmentProfile.ProviderInputs,
            Cr1DevelopmentProfile.NonterminalDefinitions, Cr1DevelopmentProfile.ToolCallsPerBatch,
            Cr1DevelopmentProfile.RequestPayloadBytes, Cr1DevelopmentProfile.ResponsePayloadBytes,
            Cr1DevelopmentProfile.ContinuationBytes);
        // Compute compatibility from current trusted construction. These hashes
        // reject mismatches; the live context and exact references grant authority.
        var compatibility = Compatibility(context, targets.Provider.Capabilities, tools, targets.Hooks, bounds, guarantees, correction);
        RuntimeConfiguration? configuration = null;
        bool CurrentTargets() => context.IsLive && configuration is not null &&
            ReferenceEquals(configuration.Provider, targets.Provider) &&
            ReferenceEquals(configuration.Hooks, targets.Hooks) &&
            ReferenceEquals(configuration.ToolArgumentCorrection, correction) &&
            configuration.RequiredGuarantees == guarantees &&
            configuration.Provider.Scope.Provider == context.Policy.ProviderId &&
            configuration.Provider.Scope.Model == context.Policy.ModelId &&
            configuration.Tools.Count == tools.Length && tools.Select((tool, index) =>
                ReferenceEquals(configuration.Tools[index].Tool, tool.Tool) &&
                ReferenceEquals(configuration.Tools[index].Capability, tool.Capability) &&
                ReferenceEquals(configuration.Tools[index].ArgumentCorrection, tool.ArgumentCorrection) &&
                ReferenceEquals(tool.Descriptor, tool.Tool.Descriptor)).All(value => value);
        var authority = new SolusRuntimeContextAuthority(context, compatibility, CurrentTargets);
        try
        {
            configuration = new RuntimeConfiguration(targets.Provider, tools, targets.Hooks, bounds,
                requiredGuarantees: guarantees, contextAuthority: authority, toolArgumentCorrection: correction);
            var agent = SolusRuntimeConstruction.Create(configuration, Cr1DevelopmentProfile.Id);
            return new(agent, authority, new(context, compatibility));
        }
        catch { authority.Dispose(); throw; }
    }

    internal bool TryArm(SolusConsumedContext consumed, out AgentContextEnvelope? envelope,
        out ContextRoundGrant? grant)
    {
        envelope = null;
        grant = null;
        if (consumed is null || !authority.TryArm(consumed)) return false;
        var bytes = consumed.CopyPayload();
        try
        {
            var expected = consumed.Compatibility;
            envelope = new(expected.ImplementationId, expected.FormatVersion, expected.CompatibilityVersion, bytes);
            var claim = consumed.Claim;
            grant = new(claim.GrantId, new(claim.Source.CheckpointId, claim.Source.LogicalWorkId,
                claim.Source.RoundId, claim.Source.ExecutionId), claim.ExecutionId, claim.RoundId);
            return true;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public void Dispose() => authority.Dispose();

    internal static SolusRestrictedStateCompatibility Compatibility(SolusFreshHostContext context,
        ProviderCapabilities capabilities, RuntimeToolRegistration[] tools, IRuntimeExposureHooks? hooks,
        ProviderExchangeBounds bounds, RuntimeGuarantee guarantees, ToolArgumentCorrectionPolicy? correction)
    {
        var runtime = new LineageBinaryWriter(1024 * 1024);
        runtime.WriteString("apr.solus-runtime-configuration.v1");
        runtime.WriteString(Cr1DevelopmentProfile.Id);
        runtime.WriteInt64((long)ExposureStrength.Volatile);
        runtime.WriteInt64((long)guarantees);
        foreach (var number in new[] { bounds.MaximumInputs, bounds.MaximumTools, bounds.MaximumToolCalls,
            bounds.MaximumRequestBytes, bounds.MaximumResponseBytes, bounds.MaximumContinuationBytes,
            Cr1DevelopmentProfile.RetainedAttempts,
            Cr1DevelopmentProfile.RetainedRecords, Cr1DevelopmentProfile.RetainedBytes,
            Cr1DevelopmentProfile.SettlementGraceSeconds }) runtime.WriteInt64(number);
        runtime.WriteByte(Cr1DevelopmentProfile.RequireContinuation ? (byte)1 : (byte)0);
        runtime.WriteByte(hooks is null ? (byte)0 : (byte)1);
        runtime.WriteInt64((long)capabilities);
        runtime.WriteString(context.Policy.ToolsetSha256);
        runtime.WriteInt64(tools.Length);
        foreach (var tool in tools)
        {
            var descriptor = tool.Descriptor;
            runtime.WriteString(descriptor.Name);
            runtime.WriteString(descriptor.Description);
            runtime.WriteString(descriptor.CapabilityId);
            runtime.WriteString(descriptor.InputSchema.NormalizedJson);
            runtime.WriteString(descriptor.ResultSchema.NormalizedJson);
            runtime.WriteInt64((long)descriptor.Effect);
            runtime.WriteInt64(descriptor.MaximumArgumentBytes);
            runtime.WriteInt64(descriptor.MaximumResultBytes);
        }
        // Preserve the existing policyless byte sequence. Correction mode binds
        // fixed semantics and canonical per-tool declarations, never delegates
        // or transient capability identity; exact references are checked above.
        if (correction is not null)
        {
            runtime.WriteString("apr.solus-argument-correction.v1");
            runtime.WriteString(correction.Identity);
            runtime.WriteString(correction.MarkerProperty);
            runtime.WriteString(correction.SemanticFingerprint);
            foreach (var tool in tools)
            {
                var binding = tool.ArgumentCorrection!;
                runtime.WriteString(binding.Identity);
                runtime.WriteInt64(binding.AllowedErrorKeys.Count);
                foreach (var key in binding.AllowedErrorKeys) runtime.WriteString(key);
            }
        }
        var options = new LineageBinaryWriter(16 * 1024);
        options.WriteString("apr.solus-provider-configuration.v1");
        options.WriteString(context.Policy.ProviderId);
        options.WriteString(context.Policy.ModelId);
        options.WriteString(context.Policy.AdapterId);
        options.WriteString(context.Policy.Endpoint);
        options.WriteString(context.Policy.ConfigSha256);
        options.WriteString(context.Policy.LimitsSha256);
        foreach (var number in new[] { Cr1DevelopmentProfile.WireRequestBytes,
            Cr1DevelopmentProfile.WireResponseBytes, Cr1DevelopmentProfile.ProviderTimeoutSeconds,
            Cr1DevelopmentProfile.ProviderOutputTokens }) options.WriteInt64(number);
        return new(SolusRestrictedStateFormat.PackageVersion, Cr1DevelopmentProfile.Id,
            SolusRestrictedStateFormat.CompletedCandidateMode, SolusRestrictedStateFormat.Implementation,
            SolusRestrictedStateFormat.ContextFormat, SolusRestrictedStateFormat.ContextCompatibility,
            SolusRestrictedStateFormat.PackageClosureSha256,
            Convert.ToHexStringLower(SHA256.HashData(runtime.ToArray())),
            Convert.ToHexStringLower(SHA256.HashData(options.ToArray())));
    }
}

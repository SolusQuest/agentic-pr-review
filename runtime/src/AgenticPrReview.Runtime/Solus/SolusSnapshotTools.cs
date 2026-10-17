using System.Collections.Immutable;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Tools.Api;

namespace AgenticPrReview.Runtime.Solus;

// One fresh current-run evidence index. Restored provider facts never populate it.
// The Host supplies the exact immutable snapshot and confined file-access seam.
internal sealed class SolusSnapshotTools
{
    private readonly object gate = new();
    private readonly List<AgentObservation> observations = [];
    private readonly ReviewedSnapshot snapshot;
    private bool hostBound;

    internal SolusSnapshotTools(Guid executionId, ReviewedSnapshot snapshot, IReviewedFileAccess fileAccess)
        : this(executionId, snapshot, new SnapshotToolExecutor(snapshot, fileAccess)) { }

    // Internal fault-injection seam; it is never a model or saved-state capability.
    internal SolusSnapshotTools(Guid executionId, ReviewedSnapshot snapshot, IAgentToolExecutor executor)
    {
        if (executionId == Guid.Empty) throw new ArgumentException("APR_SOLUS_EXECUTION_INVALID");
        ExecutionId = executionId;
        this.snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        ArgumentNullException.ThrowIfNull(executor);
        var registrations = new List<RuntimeToolRegistration>();
        foreach (var name in new[] { AgentToolRegistry.ListFilesName, AgentToolRegistry.ListChangedFilesName,
            AgentToolRegistry.ReadDiffName, AgentToolRegistry.ReadFileName, AgentToolRegistry.SearchTextName })
        {
            var descriptor = SolusToolSchemas.Descriptor(name);
            var capability = new SnapshotCapability(this, snapshot, executor, descriptor.CapabilityId);
            var tool = new SnapshotFunction(descriptor, capability);
            var correction = new ToolArgumentCorrectionBinding("apr_" + name + "_arguments_v1",
                SolusToolCorrection.AllowedKeys(name), (call, supplied) =>
                    tool.ValidateCapability(supplied) == ToolError.None && call.ToolName == name
                        ? SolusToolCorrection.Classify(call, executor)
                        : ToolArgumentDisposition.Fail(ToolError.UnsupportedCapability));
            registrations.Add(new(tool, capability, argumentCorrection: correction));
        }
        Registrations = registrations.AsReadOnly();
    }

    internal IReadOnlyList<RuntimeToolRegistration> Registrations { get; }
    internal Guid ExecutionId { get; }
    internal ReviewedIdentity Identity => snapshot.Identity;
    internal ImmutableArray<AgentObservation> CurrentObservations
    {
        get { lock (gate) return observations.ToImmutableArray(); }
    }

    internal bool TryBindCandidateHost(Guid executionId)
    {
        lock (gate)
        {
            if (hostBound || executionId != ExecutionId) return false;
            hostBound = true;
            return true;
        }
    }

    private bool Admit(AgentObservation observation)
    {
        lock (gate)
        {
            if (observations.Count >= Cr1DevelopmentProfile.ToolInvocations) return false;
            observations.Add(observation);
            return true;
        }
    }

    public override string ToString() => nameof(SolusSnapshotTools);

    private sealed class SnapshotCapability(SolusSnapshotTools owner, ReviewedSnapshot snapshot,
        IAgentToolExecutor executor, string capabilityId) : IToolCapability
    {
        internal SolusSnapshotTools Owner { get; } = owner;
        internal ReviewedSnapshot Snapshot { get; } = snapshot;
        internal IAgentToolExecutor Executor { get; } = executor;
        public string CapabilityId { get; } = capabilityId;
        public override string ToString() => nameof(SnapshotCapability);
    }

    // Composition is deliberate: the base guard's methods are nonvirtual. Every
    // interface entry checks the actual Host instance before entering that guard.
    private sealed class SnapshotFunction : IFunctionTool
    {
        private readonly SnapshotCapability capability;
        private readonly GuardedFunction guarded;
        internal SnapshotFunction(ToolDescriptor descriptor, SnapshotCapability capability)
        { this.capability = capability; guarded = new(descriptor, capability); }
        public ToolDescriptor Descriptor => guarded.Descriptor;
        public ToolPreparation Prepare(ToolCall call) => guarded.Prepare(call);
        public ToolError ValidateCapability(IToolCapability? supplied) =>
            ReferenceEquals(supplied, capability) && ReferenceEquals(capability.Owner.snapshot, capability.Snapshot)
                ? guarded.ValidateCapability(supplied) : ToolError.UnsupportedCapability;
        public ToolError ValidateInvocation(PreparedToolInvocation prepared, ToolCall expectedCall, IToolCapability? supplied) =>
            ValidateCapability(supplied) == ToolError.None
                ? guarded.ValidateInvocation(prepared, expectedCall, supplied) : ToolError.UnsupportedCapability;
        public ValueTask<ToolResult> InvokeAsync(PreparedToolInvocation prepared, ToolCall expectedCall,
            IToolCapability? supplied, CancellationToken cancellationToken = default) =>
            guarded.InvokeAsync(prepared, expectedCall,
                ValidateCapability(supplied) == ToolError.None ? supplied : null, cancellationToken);
        public override string ToString() => nameof(SnapshotFunction);
    }

    private sealed class GuardedFunction(ToolDescriptor descriptor, SnapshotCapability bound)
        : FunctionTool<SnapshotCapability>(descriptor)
    {
        protected override bool ValidateArguments(System.Text.Json.JsonElement arguments) =>
            SolusToolCorrection.TryParse(new("apr_prepare", Descriptor.Name, arguments.GetRawText()), out _, out _);

        protected override async ValueTask<ToolOutput> InvokeCoreAsync(ToolCall call,
            SnapshotCapability capability, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(bound, capability) ||
                !SolusToolCorrection.TryParse(call, out var parsed, out _) ||
                capability.Executor.Preflight(parsed!) is not null)
                return ToolOutput.Failure(call, ToolError.DomainRejected);
            var execution = await capability.Executor.ExecuteAsync(parsed!, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!execution.Succeeded)
                return ToolOutput.Failure(call, execution.FailureCode == AgentFailureCodes.ToolResultLimit
                    ? ToolError.LimitExceeded : ToolError.InvocationFailed);
            if (execution.CanonicalResult is null || execution.CanonicalResult.Length > Descriptor.MaximumResultBytes)
                return ToolOutput.Failure(call, ToolError.LimitExceeded);
            if (!AgentToolResultAdmission.TryAdmit(parsed!, capability.Snapshot.Identity, execution,
                    out var json, out var observation) ||
                Descriptor.ResultSchema.Validate(json, Descriptor.MaximumResultBytes) != ToolError.None)
                return ToolOutput.Failure(call, ToolError.InvalidResult);
            cancellationToken.ThrowIfCancellationRequested();
            if (!capability.Owner.Admit(observation)) return ToolOutput.Failure(call, ToolError.LimitExceeded);
            return ToolOutput.Success(call, json);
        }
    }
}

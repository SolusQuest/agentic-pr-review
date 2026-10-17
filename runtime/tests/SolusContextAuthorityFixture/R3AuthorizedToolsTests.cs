using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Solus;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Tools.Api;

// Exercises the genuine authorized construction and durable S1/S2 boundaries.
// No test RuntimeConfiguration or substitute context authority is used here.
static class R3AuthorizedToolsTests
{
    private const string EmptyFinal = "{\"summary\":\"No supported findings.\",\"findings\":[]}";
    private const string Rejected = "REJECTED_AUTHORIZED_ARGUMENT_399";

    internal static async Task Run(string parent)
    {
        Proof.Stage = "authorized-r3-construction";
        var root = Path.Join(parent, "authorized-r3"); Directory.CreateDirectory(root);
        SolusRestrictedStateRecord? record = null;
        string? historicalId = null;
        await using (var fixture = await Fixture.OpenAsync(root, default))
        {
            Require(fixture.Runtime.SourceSelection.Compatibility.RuntimeConfigurationSha256 ==
                CompatibilityHash(fixture.Targets, fixture.Provider,
                    [new(fixture.Tool, new SnapshotCapability(fixture.Targets))], correction: false), "policyless_identity");
            Construction(fixture);
            var variants = Variants().ToArray();
            Require(variants.Length == 18, "reachable_variant_count");
            foreach (var variant in variants)
                await Correct(fixture, variant.Call, variant.Feedback, reverse: false, capture: false);
            foreach (var reverse in new[] { false, true })
            foreach (var schemaInvalid in new[] { false, true })
            {
                var result = await Correct(fixture,
                    new("invalid", "read_file", "{\"path\":" + Json(Rejected) + (schemaInvalid ? ",\"unknown\":true}" : "}")),
                    schemaInvalid ? AgentRecoveryFeedback.ArgumentsInvalid : AgentRecoveryFeedback.TrackedPathMissing,
                    reverse, capture: !reverse && !schemaInvalid);
                if (result.Record is not null) { record = result.Record; historicalId = result.Observation; }
            }
        }
        Require(record is not null && historicalId is not null, "captured");
        // Distinct stores keep each genuine one-shot context claim independent.
        foreach (var mode in new[] { "historical_only", "fresh_read", "policy", "binding" })
            await Restore(parent, record!, historicalId!, mode);
        Proof.Stage = "startup";
    }

    private static void Construction(Fixture fixture)
    {
        Proof.Stage = "authorized-r3-bindings";
        var snapshot = fixture.Snapshot.Snapshot;
        // A copied snapshot with identical metadata/root still has no authority
        // to replace the exact snapshot installed in the fresh Host context.
        var foreign = new ReviewedSnapshot(snapshot.Identity, snapshot.AbsoluteRoot,
            snapshot.OrderedTrackedFiles, snapshot.OrderedReviewedHeadPaths,
            snapshot.OrderedChangedFiles, snapshot.DiffByChangedPath.Values);
        var executor = new AuthorizedExecutor(new SnapshotToolExecutor(fixture.Snapshot.Snapshot, new VerifiedReviewedFileAccess()));
        var tools = new SolusSnapshotTools(Guid.NewGuid(), fixture.Snapshot.Snapshot, executor);
        var other = new SolusSnapshotTools(Guid.NewGuid(), fixture.Snapshot.Snapshot, executor);
        var foreignTools = new SolusSnapshotTools(Guid.NewGuid(), foreign, executor);
        var generic = new RuntimeToolRegistration(fixture.Tool, new SnapshotCapability(fixture.Targets));
        foreach (var mode in new[] { "stripped_all", "stripped_one", "cloned", "changed_keys", "mixed", "foreign", "subset", "generic" })
        {
            var provider = Provider(fixture);
            IReadOnlyList<RuntimeToolRegistration> registrations = mode switch
            {
                "stripped_all" => tools.Registrations.Select(r => new RuntimeToolRegistration(r.Tool, r.Capability)).ToArray(),
                "stripped_one" => tools.Registrations.Select((r, i) => i == 0 ? new(r.Tool, r.Capability) : r).ToArray(),
                "cloned" or "changed_keys" => tools.Registrations.Select(r => new RuntimeToolRegistration(r.Tool, r.Capability,
                    argumentCorrection: new(r.ArgumentCorrection!.Identity,
                        mode == "cloned" ? r.ArgumentCorrection.AllowedErrorKeys : ["arguments_invalid"],
                        (call, capability) => r.ArgumentCorrection.Classify(call, capability)))).ToArray(),
                "mixed" => tools.Registrations.Select((r, i) => i == 0 ? other.Registrations[i] : r).ToArray(),
                "foreign" => foreignTools.Registrations,
                "subset" => tools.Registrations.Take(4).ToArray(),
                _ => [new(generic.Tool, generic.Capability, argumentCorrection: new("forged",
                    ["arguments_invalid"], (_, _) => ToolArgumentDisposition.Allow()))],
            };
            Reject(fixture, provider, registrations);
            Require(provider.Calls == 0 && executor.Calls == 0 && tools.CurrentObservations.Length == 0, "binding_zero_effects");
        }
        foreach (var provider in new IModelProvider[] {
            new AuthorizedProvider(fixture.Targets.Policy.ProviderId, fixture.Targets.Policy.ModelId, correction: false),
            new AuthorizedFlagOnlyProvider(fixture.Targets.Policy.ProviderId, fixture.Targets.Policy.ModelId) })
            Reject(fixture, provider, tools.Registrations);
        Require(executor.Calls == 0, "provider_zero_effects");
    }

    private static void Reject(Fixture fixture, IModelProvider provider, IReadOnlyList<RuntimeToolRegistration> registrations)
    {
        using var targets = Targets(fixture);
        try
        {
            using var runtime = SolusRuntimeConstruction.CreateForAuthorizedReview(targets, _ => new(provider, registrations, new Hooks()));
        }
        catch (ArgumentException) { return; }
        throw new ProofFailure("authorized_r3_expected_rejection");
    }

    private static async Task<(SolusRestrictedStateRecord? Record, string Observation)> Correct(Fixture fixture,
        ToolCall invalid, string feedbackJson, bool reverse, bool capture)
    {
        Proof.Stage = "authorized-r3-correction";
        var execution = Guid.NewGuid();
        using var targets = Targets(fixture);
        var executor = new AuthorizedExecutor(new SnapshotToolExecutor(targets.Snapshot, new VerifiedReviewedFileAccess()));
        var tools = new SolusSnapshotTools(execution, targets.Snapshot, executor);
        var path = targets.Snapshot.OrderedTrackedFiles.Single();
        var sibling = new ToolCall("sibling", "list_files", "{}");
        var batch = reverse ? new[] { sibling, invalid } : new[] { invalid, sibling };
        var provider = Provider(fixture);
        provider.Reply = (request, attempt) =>
        {
            Catalog(request);
            if (attempt == 1) return Response(request, batch);
            if (attempt == 2)
            {
                Require(executor.Calls == 0 && tools.CurrentObservations.Length == 0, "batch_zero_effects");
                var model = request.Inputs.Single(i => i.Model?.Finish == ProviderFinish.ToolCorrection).Model!;
                var members = model.Correction!.Members;
                Require(members.Count == 2 && members.Select(m => m.Call.CallId).SequenceEqual(batch.Select(c => c.CallId)) &&
                    members.Select(m => m.Call.ToolName).SequenceEqual(batch.Select(c => c.ToolName)), "batch_identity_order");
                Require(members.Single(m => m.Rejected).Call.ArgumentsJson == AgentRecoveryFeedback.RejectedArguments &&
                    members.Single(m => !m.Rejected).Call.ArgumentsJson == "{}", "sanitized_marker");
                var feedback = request.Inputs.Where(i => i.CorrectionFeedback is not null).Select(i => i.CorrectionFeedback!).ToArray();
                Require(feedback.Length == 2 && feedback.Single(f => f.Call.CallId == invalid.CallId).Json == feedbackJson &&
                    feedback.Single(f => f.Call.CallId == sibling.CallId).Json == AgentRecoveryFeedback.BatchNotExecuted,
                    "fixed_feedback");
                Require(model.Continuation is not null && Encoding.UTF8.GetString(model.Continuation.CopyReplayBytes()) ==
                    "authorized-replay", "correction_continuation");
                NoRejected(request);
                return Response(request, [new("fresh_read", "read_file", "{\"path\":" + Json(path) + ",\"line_count\":1}")]);
            }
            return Response(request, [], Grounded(tools.CurrentObservations.Single().ObservationId, path));
        };
        using var runtime = SolusRuntimeConstruction.CreateForAuthorizedReview(targets, _ => new(provider, tools.Registrations, new Hooks()));
        var compatibility = runtime.SourceSelection.Compatibility;
        Require(compatibility.RuntimeConfigurationSha256 == CompatibilityHash(targets, provider, tools.Registrations, correction: true),
            "correction_identity");
        Require(compatibility.Profile == fixture.Runtime.SourceSelection.Compatibility.Profile &&
            compatibility.ProviderConfigurationSha256 == fixture.Runtime.SourceSelection.Compatibility.ProviderConfigurationSha256 &&
            compatibility.FormatVersion == 2 && compatibility.CompatibilityVersion == 1, "identity_scope");
        var host = new SolusReviewCandidateHost(execution, tools);
        using var sink = new SolusRestrictedStateCapture(execution, compatibility);
        var result = await ((IContextCandidateAgent)runtime.Agent).ExecuteCandidatesWithContextAsync(
            new(Request(execution), ContextExecutionIntent.Fresh), SolusReviewCandidateHost.Bounds(), host, sink);
        Require(host.TryComplete(result, out var review) && review!.Findings.Length == 1 &&
            !host.TryComplete(result, out _) && provider.Calls == 3 && executor.Calls == 1 &&
            result.Context.Outcome!.Usage!.ToolInvocations!.Invoked == 1, "fresh_complete");
        Require(sink.TryComplete(result, out var record), "authorized_capture");
        Require(!Encoding.UTF8.GetString(record!.CopyRestrictedPayload()).Contains(Rejected, StringComparison.Ordinal), "capture_sanitized");
        return (capture ? record : null, tools.CurrentObservations.Single().ObservationId);
    }

    private static async Task Restore(string parent, SolusRestrictedStateRecord original, string historicalId, string mode)
    {
        Proof.Stage = "authorized-r3-restore";
        var root = Path.Join(parent, "authorized-restore-" + mode); Directory.CreateDirectory(root);
        await using var fixture = await Fixture.OpenAsync(root, default);
        var record = original;
        if (mode is "policy" or "binding")
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(original.CopyRestrictedPayload())!.AsObject();
            if (mode == "binding") node["Binding"] = new string('0', 64);
            else
            {
                // Alter the saved fixed correction declaration, then authenticate
                // it through S1: authentication alone must not grant SDK admission.
                var correction = node["Records"]!.AsArray().Single(r => r?["CorrectionModel"] is not null)!["CorrectionModel"]!.AsObject();
                Require(correction["DefinitionFingerprint"]!.GetValue<string>().Length == 64, "saved_policy_declaration");
                correction["DefinitionFingerprint"] = new string('0', 64);
            }
            Require(SolusRestrictedStateRecord.TryCreate(original.Compatibility, original.Checkpoint,
                JsonSerializer.SerializeToUtf8Bytes(node), out var tampered), "tampered_record");
            record = tampered!;
        }
        await fixture.SeedAsync(record);
        var execution = Guid.NewGuid();
        using var targets = Targets(fixture);
        var executor = new AuthorizedExecutor(new SnapshotToolExecutor(targets.Snapshot, new VerifiedReviewedFileAccess()));
        var tools = new SolusSnapshotTools(execution, targets.Snapshot, executor);
        var provider = Provider(fixture);
        provider.Reply = (request, attempt) =>
        {
            Catalog(request); NoRejected(request);
            if (attempt == 1)
            {
                Require(executor.Calls == 0 && tools.CurrentObservations.Length == 0, "history_zero_replay");
                Require(request.Inputs.Any(i => i.Model?.Finish == ProviderFinish.ToolCorrection) &&
                    request.Inputs.Where(i => i.ToolResult is not null).All(i => i.ToolResult!.IsHistorical), "history_is_data");
                Require(AgentToolArguments.TryFinishReviewProvider(Grounded(historicalId, targets.Snapshot.OrderedTrackedFiles.Single()), out var prior) &&
                    !TerminalReviewValidator.TryValidate(prior!, tools.Identity, tools.CurrentObservations, out _), "history_not_grounding");
                if (mode == "fresh_read") return Response(request,
                    [new("restored_fresh_read", "read_file", "{\"path\":" + Json(targets.Snapshot.OrderedTrackedFiles.Single()) + ",\"line_count\":1}")]);
            }
            return Response(request, [], Grounded(mode == "fresh_read" ? tools.CurrentObservations.Single().ObservationId : historicalId,
                targets.Snapshot.OrderedTrackedFiles.Single()));
        };
        using var runtime = SolusRuntimeConstruction.CreateForAuthorizedReview(targets, _ => new(provider, tools.Registrations, new Hooks()));
        Require(runtime.SourceSelection.Compatibility == original.Compatibility, "new_authorized_identity");
        var policyless = await RestrictedStateService.RestoreAuthorizedArtifactStateAsync(
            fixture.Request with { SolusSourceSelection = fixture.Runtime.SourceSelection }, default);
        using (var policylessSource = policyless.SolusSource)
        {
            Require(policylessSource is not null, "policyless_source");
            using var denied = await policylessSource!.PrepareAsync(Guid.NewGuid(), Guid.NewGuid(), default);
            Require(denied is null && fixture.Provider.Imports == 0 && fixture.Provider.Calls == 0 && fixture.Tool.Calls == 0,
                "policyless_history_rejected");
        }
        var selected = await RestrictedStateService.RestoreAuthorizedArtifactStateAsync(
            fixture.Request with { SolusSourceSelection = runtime.SourceSelection }, default);
        using var source = selected.SolusSource;
        Require(source is not null, "real_source_selection");
        using var consumed = await source!.PrepareAsync(execution, Guid.NewGuid(), default);
        Require(consumed is not null, "real_consumed_context");
        Require(runtime.TryArm(consumed!, out var envelope, out var grant), "real_context_arm");
        Require(runtime.TryArm(consumed!, out _, out _) == false, "single_arm");
        var host = new SolusReviewCandidateHost(execution, tools);
        var result = await ((IContextCandidateAgent)runtime.Agent).ExecuteCandidatesWithContextAsync(
            new(Request(execution), ContextExecutionIntent.NewRunFromContext, envelope, grant), SolusReviewCandidateHost.Bounds(), host);
        if (mode is "policy" or "binding")
            Require(result.Context.Admission == ContextAdmission.Rejected && provider.Calls == 0 && provider.Imports == 0 &&
                executor.Calls == 0 && tools.CurrentObservations.Length == 0 && !host.TryComplete(result, out _), "tampered_history_rejected");
        else
            Require(result.Context.Admission == ContextAdmission.Supplied && consumed!.WasClaimed && provider.Imports == 1 &&
                host.TryComplete(result, out _) == (mode == "fresh_read") && executor.Calls == (mode == "fresh_read" ? 1 : 0),
                "real_restored_completion");
    }

    private static IEnumerable<(ToolCall Call, string Feedback)> Variants()
    {
        yield return (new("invalid", "read_file", "{\"path\":\"missing.cs\"}"), AgentRecoveryFeedback.TrackedPathMissing);
        yield return (new("invalid", "read_diff", "{\"path\":\"missing.cs\"}"), AgentRecoveryFeedback.ChangedPathMissing);
        yield return (new("invalid", "list_files", "{\"after\":\"missing.cs\"}"), AgentRecoveryFeedback.CursorInvalid);
        foreach (var field in new[] { ListFilesPathField.Prefix, ListFilesPathField.After })
        foreach (var (path, rule) in new[] { ("", RepositoryPathFailure.Empty), ("/bad", RepositoryPathFailure.Absolute),
            (new string('x', 1025), RepositoryPathFailure.TooLong), ("a:b", RepositoryPathFailure.ForbiddenCharacter),
            ("a//b", RepositoryPathFailure.EmptySegment), ("../bad", RepositoryPathFailure.DotSegment), ("bad.", RepositoryPathFailure.TrailingDotOrSpace) })
            yield return (new("invalid", "list_files", "{\"" + field.ToString().ToLowerInvariant() + "\":" + Json(path) + "}"),
                AgentRecoveryFeedback.ListFilesPathInvalid(new(field, rule)));
        yield return (new("invalid", "list_files", "{\"prefix\":\"../bad\",\"after\":\"/bad\"}"),
            AgentRecoveryFeedback.ListFilesPathInvalid(new(ListFilesPathField.Both, RepositoryPathFailure.Unknown)));
    }

    private static void Catalog(ProviderRequest request)
    {
        var policy = request.ToolArgumentCorrection;
        Require(ReferenceEquals(policy, SolusToolCorrection.Policy) && policy!.Errors.Count == 23 &&
            policy.MarkerArgumentsJson == AgentRecoveryFeedback.RejectedArguments, "installed_policy");
        var expected = new HashSet<string>(StringComparer.Ordinal) { AgentRecoveryFeedback.TrackedPathMissing,
            AgentRecoveryFeedback.ChangedPathMissing, AgentRecoveryFeedback.CursorInvalid };
        foreach (var field in Enum.GetValues<ListFilesPathField>())
        foreach (var rule in Enum.GetValues<RepositoryPathFailure>())
            if (field is ListFilesPathField.Both or ListFilesPathField.Unknown ? rule == RepositoryPathFailure.Unknown
                : rule is not RepositoryPathFailure.None and not RepositoryPathFailure.Unknown)
                expected.Add(AgentRecoveryFeedback.ListFilesPathInvalid(new(field, rule)));
        Require(expected.Count == 21 && policy!.Errors.Where(e => e.Key is not "arguments_invalid" and not "batch_not_executed")
            .Select(e => e.Json).ToHashSet(StringComparer.Ordinal).SetEquals(expected), "installed_catalogue");
        Require(policy!.GetError("arguments_invalid").Json == AgentRecoveryFeedback.ArgumentsInvalid &&
            policy.GetError("batch_not_executed").Json == AgentRecoveryFeedback.BatchNotExecuted, "installed_builtins");
    }

    private static string CompatibilityHash(SolusFreshHostContext context, IModelProvider provider,
        IReadOnlyList<RuntimeToolRegistration> tools, bool correction)
    {
        // Independent frozen pre-fix serialization, with the explicit correction
        // extension. The policyless branch guards byte-for-byte S2 compatibility.
        var writer = new LineageBinaryWriter(1024 * 1024);
        writer.WriteString("apr.solus-runtime-configuration.v1"); writer.WriteString(Cr1DevelopmentProfile.Id);
        writer.WriteInt64((long)ExposureStrength.Volatile);
        writer.WriteInt64((long)(RuntimeGuarantee.OrderedExposure | RuntimeGuarantee.ProviderBounds |
            (correction ? RuntimeGuarantee.ToolArgumentCorrection : RuntimeGuarantee.None)));
        foreach (var value in new[] { Cr1DevelopmentProfile.ProviderInputs, Cr1DevelopmentProfile.NonterminalDefinitions,
            Cr1DevelopmentProfile.ToolCallsPerBatch, Cr1DevelopmentProfile.RequestPayloadBytes, Cr1DevelopmentProfile.ResponsePayloadBytes,
            Cr1DevelopmentProfile.ContinuationBytes, Cr1DevelopmentProfile.RetainedAttempts, Cr1DevelopmentProfile.RetainedRecords,
            Cr1DevelopmentProfile.RetainedBytes, Cr1DevelopmentProfile.SettlementGraceSeconds }) writer.WriteInt64(value);
        writer.WriteByte(Cr1DevelopmentProfile.RequireContinuation ? (byte)1 : (byte)0); writer.WriteByte(1);
        writer.WriteInt64((long)provider.Capabilities); writer.WriteString(context.Policy.ToolsetSha256); writer.WriteInt64(tools.Count);
        foreach (var tool in tools)
        {
            var d = tool.Descriptor;
            foreach (var text in new[] { d.Name, d.Description, d.CapabilityId, d.InputSchema.NormalizedJson, d.ResultSchema.NormalizedJson }) writer.WriteString(text);
            writer.WriteInt64((long)d.Effect); writer.WriteInt64(d.MaximumArgumentBytes); writer.WriteInt64(d.MaximumResultBytes);
        }
        if (correction)
        {
            var policy = SolusToolCorrection.Policy;
            foreach (var text in new[] { "apr.solus-argument-correction.v1", policy.Identity, policy.MarkerProperty, policy.SemanticFingerprint }) writer.WriteString(text);
            foreach (var tool in tools)
            {
                writer.WriteString(tool.ArgumentCorrection!.Identity); writer.WriteInt64(tool.ArgumentCorrection.AllowedErrorKeys.Count);
                foreach (var key in tool.ArgumentCorrection.AllowedErrorKeys) writer.WriteString(key);
            }
        }
        return Convert.ToHexStringLower(SHA256.HashData(writer.ToArray()));
    }

    private static SolusFreshHostContext Targets(Fixture fixture) =>
        SolusFreshHostContext.TryCreate(fixture.Request, fixture.Snapshot, fixture.Exclusion) ?? throw new ProofFailure("authorized_r3_targets");
    private static AuthorizedProvider Provider(Fixture fixture) => new(fixture.Targets.Policy.ProviderId, fixture.Targets.Policy.ModelId);
    private static AgentRequest Request(Guid execution) => new(execution, "Review the exact snapshot.", [],
        new(6, TimeSpan.FromSeconds(10)), AgentCapability.WorkUnitLimit | AgentCapability.DurationLimit | AgentCapability.Cancellation |
        AgentCapability.DispatchLimits | AgentCapability.ToolInvocationLimit | AgentCapability.ToolCallAdmissionLimit |
        AgentCapability.UsageReporting | AgentCapability.UsageThresholds | AgentCapability.UsageAccounting,
        new(maximumLogicalCalls: 6, maximumPhysicalDispatches: 6, maximumToolInvocations: 6, maximumToolCallAdmissions: 6,
            inputTokenThreshold: Cr1DevelopmentProfile.InputTokenThreshold, outputTokenThreshold: Cr1DevelopmentProfile.OutputTokenThreshold,
            accountingPolicy: new(new(Cr1DevelopmentProfile.InputTokenReservation, Cr1DevelopmentProfile.OutputTokenReservation),
                Cr1DevelopmentProfile.InputTokenAllowance, Cr1DevelopmentProfile.OutputTokenAllowance, UnknownUsagePolicy.Stop)));
    private static ProviderResponse Response(ProviderRequest request, ToolCall[] calls, string? text = null) =>
        new(request.Scope, request.Attempt, calls.Length == 0 ? ProviderFinish.Final : ProviderFinish.ToolCalls,
            text ?? (calls.Length == 0 ? EmptyFinal : null), calls, new(request.Scope, request.Attempt, "authorized-replay"u8.ToArray()));
    private static void NoRejected(ProviderRequest request) => Require(request.Inputs.All(i =>
        !(i.Text ?? i.ToolResult?.Json ?? i.CorrectionFeedback?.Json ?? "").Contains(Rejected, StringComparison.Ordinal) &&
        (i.Model?.Correction?.Members.All(m => !m.Call.ArgumentsJson.Contains(Rejected, StringComparison.Ordinal)) ?? true)), "no_rejected_bytes");
    private static string Grounded(string id, string path) =>
        "{\"summary\":\"A supported defect.\",\"findings\":[{\"severity\":\"high\",\"title\":\"Missing guard\",\"message\":\"Validate before use.\",\"evidence\":[{\"observation_id\":" +
        Json(id) + ",\"path\":" + Json(path) + ",\"start_line\":1,\"end_line\":1}]}]}";
    private static string Json(string value) => JsonSerializer.Serialize(value);
    private static void Require(bool value, string stage) => Proof.Require(value, "authorized_r3_" + stage);
}

sealed class AuthorizedProvider(string provider, string model, bool correction = true) : ModelProvider(new(provider, model),
    ProviderCapabilities.Continuation | ProviderCapabilities.UsageReporting | ProviderCapabilities.ToolCalls |
    (correction ? ProviderCapabilities.ToolArgumentCorrection : ProviderCapabilities.None)), IProviderContextPersistence
{
    internal int Calls, Imports;
    internal Func<ProviderRequest, int, ProviderResponse>? Reply;
    protected override ValueTask<ProviderResponse> ExchangeCoreAsync(ProviderRequest request,
        ProviderObservation observation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); observation.ObserveDispatch(DispatchExposure.Dispatched);
        observation.CaptureUsage(new(12, 3));
        return ValueTask.FromResult((Reply ?? throw new InvalidOperationException())(request, ++Calls));
    }
    public ProviderSavedState? ExportContext(ProviderContextBinding binding) => new(binding.Scope, binding.Origin, 1, [3, 9, 9]);
    public bool AdmitContext(ProviderContextBinding binding, ProviderSavedState state)
    { Imports++; return state.FormatVersion == 1 && state.CopyRestrictedPayload().AsSpan().SequenceEqual(new byte[] { 3, 9, 9 }); }
}

sealed class AuthorizedFlagOnlyProvider(string provider, string model) : IModelProvider
{
    public ProviderScope Scope { get; } = new(provider, model);
    public ProviderCapabilities Capabilities => ProviderCapabilities.ToolCalls | ProviderCapabilities.Continuation |
        ProviderCapabilities.UsageReporting | ProviderCapabilities.ToolArgumentCorrection;
    public ValueTask<ProviderExchangeResult> ExchangeAsync(ProviderRequest request, CancellationToken token = default) =>
        throw new InvalidOperationException();
}

sealed class AuthorizedExecutor(IAgentToolExecutor inner) : IAgentToolExecutor
{
    internal int Calls;
    public string? Preflight(PreparedAgentToolCall call) => inner.Preflight(call);
    public async ValueTask<AgentToolExecution> ExecuteAsync(PreparedAgentToolCall call, CancellationToken token)
    { Calls++; return await inner.ExecuteAsync(call, token); }
}

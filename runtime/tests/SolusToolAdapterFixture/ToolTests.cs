using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Solus;
using SolusAgent.Api.Candidates;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;
using SolusAgent.Tools.Api;

namespace AgenticPrReview.Runtime.SolusToolAdapterFixture;

internal static class ToolTests
{
    internal static async Task Run()
    {
        Catalog();
        using var fixture = new SnapshotFixture();
        Construction(fixture);
        Proof.Stage = "five-tools-schemas-history";
        var (tools, executor) = fixture.Tools(Guid.NewGuid());
        Proof.Check(tools.Registrations.Count == 5 && !tools.Registrations.Any(r => r.Descriptor.Name == "finish_review"));
        var inputs = new Dictionary<string, string>
        {
            ["list_files"] = "{}", ["list_changed_files"] = "{}", ["read_diff"] = "{\"path\":\"changed.cs\"}",
            ["read_file"] = "{\"path\":\"changed.cs\",\"start_line\":1.0,\"line_count\":1e0}",
            ["search_text"] = "{\"query\":\"guard\",\"path\":\"changed.cs\"}",
        };
        foreach (var registration in tools.Registrations)
        {
            var call = new ToolCall("direct_" + registration.Descriptor.Name, registration.Descriptor.Name, inputs[registration.Descriptor.Name]);
            var result = await Invoke(tools, call);
            Proof.Check(result.Outcome == ToolOutcome.Succeeded && result.Call.Matches(call));
            Proof.Check(registration.Descriptor.InputSchema.Profile == "bounded_closed_object" &&
                registration.Descriptor.ResultSchema.Profile == "bounded_closed_object");
            Proof.MaxSchema = Math.Max(Proof.MaxSchema, Math.Max(Encoding.UTF8.GetByteCount(registration.Descriptor.InputSchema.NormalizedJson),
                Encoding.UTF8.GetByteCount(registration.Descriptor.ResultSchema.NormalizedJson)));
            var historical = ToolResult.RestoreHistorical(registration.Descriptor, call, result.Call,
                result.Outcome, result.Error, result.InvocationStarted, result.Json);
            Proof.Check(historical.IsHistorical && executor.Invocations == tools.CurrentObservations.Length);
            Proof.Reject(() => ToolResult.RestoreHistorical(registration.Descriptor, call,
                new("wrong", call.ToolName, call.ArgumentsJson), result.Outcome, result.Error, true, result.Json));
            Proof.Reject(() => ToolResult.RestoreHistorical(registration.Descriptor, call, call,
                result.Outcome, result.Error, true, result.Json![..^1] + ",\"unknown\":true}"));
        }
        Proof.Check(executor.Invocations == 5 && tools.CurrentObservations.Length == 5);
        var listed = tools.CurrentObservations.Take(2).ToArray();
        Proof.Check(listed.All(x => x.ReturnedLines.Count == 0));
        foreach (var observation in listed)
            Proof.Check(!TerminalReviewValidator.TryValidate(ParseFinal(Proof.Grounded(observation.ObservationId)), tools.Identity,
                tools.CurrentObservations, out _));

        Proof.Stage = "capability-association-cancellation-once";
        await Guards(fixture);
        Proof.Stage = "arguments-result-limits";
        await Limits(fixture);
        Proof.Stage = "invalid-output-no-observation";
        await InvalidOutputs(fixture);
        Proof.Stage = "path-encoding-io";
        foreach (var path in new[] { "link.cs", "invalid.cs", "binary.cs", "lone-cr.cs", "oversize.cs" })
        {
            var (badTools, _) = fixture.Tools(Guid.NewGuid());
            var result = await Invoke(badTools, new("bad", "read_file", "{\"path\":" + Proof.Json(path) + "}"));
            Proof.Check(result.Outcome != ToolOutcome.Succeeded && badTools.CurrentObservations.Length == 0);
        }
        Proof.Stage = "terminal-strict-current-evidence";
        Terminal(tools);
    }

    internal static async Task<ToolResult> Invoke(SolusSnapshotTools tools, ToolCall call, CancellationToken token = default)
    {
        var r = tools.Registrations.Single(r => r.Descriptor.Name == call.ToolName);
        var preparation = r.Tool.Prepare(call);
        Proof.Check(preparation.Accepted);
        var result = await r.Tool.InvokeAsync(preparation.Prepared!, call, r.Capability, token);
        if (result.Json is not null) Proof.MaxResult = Math.Max(Proof.MaxResult, Encoding.UTF8.GetByteCount(result.Json));
        return result;
    }

    private static async Task Guards(SnapshotFixture fixture)
    {
        var (tools, executor) = fixture.Tools(Guid.NewGuid());
        var (other, _) = fixture.Tools(Guid.NewGuid());
        using var sameMetadataOtherRoot = new SnapshotFixture();
        var (foreign, _) = sameMetadataOtherRoot.Tools(Guid.NewGuid());
        var r = tools.Registrations.Single(r => r.Descriptor.Name == "read_file");
        var otherR = other.Registrations.Single(r => r.Descriptor.Name == "read_file");
        var call = new ToolCall("guard", "read_file", "{\"path\":\"changed.cs\"}");
        var prepared = r.Tool.Prepare(call).Prepared!;
        foreach (var capability in new IToolCapability?[] { null, new FakeCapability(r.Descriptor.CapabilityId),
            otherR.Capability, foreign.Registrations.Single(x => x.Descriptor.Name == "read_file").Capability,
            tools.Registrations[0].Capability })
        {
            Proof.Check(r.Tool.ValidateCapability(capability) == ToolError.UnsupportedCapability);
            Proof.Check(r.Tool.ValidateInvocation(prepared, call, capability) == ToolError.UnsupportedCapability);
            var denied = await r.Tool.InvokeAsync(prepared, call, capability);
            Proof.Check(!denied.InvocationStarted && denied.Outcome == ToolOutcome.Rejected && executor.Invocations == 0);
        }
        var swapped = otherR.Tool.Prepare(call).Prepared!;
        Proof.Check(r.Tool.ValidateInvocation(swapped, call, r.Capability) == ToolError.PreparedMismatch);
        Proof.Check(!(await r.Tool.InvokeAsync(swapped, call, r.Capability)).InvocationStarted);
        foreach (var changed in new[] { new ToolCall("changed", call.ToolName, call.ArgumentsJson),
            new ToolCall(call.CallId, "search_text", call.ArgumentsJson),
            new ToolCall(call.CallId, call.ToolName, "{\"path\":\"unchanged.cs\"}"),
            new ToolCall(call.CallId, call.ToolName, call.ArgumentsJson + " ") })
        {
            Proof.Check(r.Tool.ValidateInvocation(prepared, changed, r.Capability) == ToolError.CallMismatch);
            Proof.Check(!(await r.Tool.InvokeAsync(prepared, changed, r.Capability)).InvocationStarted);
        }
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var cancelled = await r.Tool.InvokeAsync(prepared, call, r.Capability, cancellation.Token);
        Proof.Check(cancelled.Outcome == ToolOutcome.Cancelled && !cancelled.InvocationStarted && executor.Invocations == 0);
        var pair = await Task.WhenAll(r.Tool.InvokeAsync(prepared, call, r.Capability).AsTask(),
            r.Tool.InvokeAsync(prepared, call, r.Capability).AsTask());
        Proof.Check(pair.Count(x => x.Outcome == ToolOutcome.Succeeded) == 1 && executor.Invocations == 1);
        Proof.Check(pair.Single(x => x.Outcome != ToolOutcome.Succeeded).Error == ToolError.AlreadyInvoked);
        Proof.Check(r.Tool.ValidateInvocation(prepared, call, r.Capability) == ToolError.AlreadyInvoked);
        using var during = new CancellationTokenSource();
        var (duringTools, duringExecutor) = fixture.Tools(Guid.NewGuid(), value => { during.Cancel(); return value; });
        var interrupted = await Invoke(duringTools, new("interrupted", "read_file", "{\"path\":\"changed.cs\"}"), during.Token);
        Proof.Check(interrupted.Outcome == ToolOutcome.Cancelled && interrupted.InvocationStarted &&
            duringExecutor.Invocations == 1 && duringTools.CurrentObservations.Length == 0);
        Proof.Reject(() => new SolusReviewCandidateHost(Guid.NewGuid(), tools));
        _ = new SolusReviewCandidateHost(tools.ExecutionId, tools);
        Proof.Reject(() => new SolusReviewCandidateHost(tools.ExecutionId, tools));
    }

    private static async Task Limits(SnapshotFixture fixture)
    {
        var (tools, _) = fixture.Tools(Guid.NewGuid());
        var r = tools.Registrations.Single(r => r.Descriptor.Name == "read_file");
        foreach (var json in new[] { "{\"path\":\"changed.cs\",\"start_line\":1.1}",
            "{\"path\":\"changed.cs\",\"start_line\":2147483648}", "{\"path\":\"changed.cs\",\"start_line\":1e999}",
            "{\"path\":\"changed.cs\",\"start_line\":null}", "{\"path\":\"changed.cs\",\"path\":\"changed.cs\"}",
            "{\"path\":\"changed.cs\",\"unknown\":1}", "{\"path\":\"../escape\"}" })
            Proof.Check(!r.Tool.Prepare(new("invalid", "read_file", json)).Accepted);
        var exact = "{\"path\":\"changed.cs\"}".PadRight(Cr1DevelopmentProfile.ToolArgumentsBytes);
        Proof.Check(r.Tool.Prepare(new("exact", "read_file", exact)).Accepted);
        Proof.Check(!r.Tool.Prepare(new("over", "read_file", exact + " ")).Accepted);
        var native = new SnapshotToolExecutor(fixture.Snapshot, new VerifiedReviewedFileAccess());
        File.WriteAllText(Path.Combine(fixture.Root, "large.cs"), new string('x', 100) + "\n");
        AgentToolArguments.TryReadFileProvider("{\"path\":\"large.cs\",\"line_count\":1}", out var args);
        var baseline = await native.ExecuteAsync(new PreparedReadFileCall("measure", args!), default);
        var length = 100 + Cr1DevelopmentProfile.ToolResultBytes - baseline.CanonicalResult!.Length;
        File.WriteAllText(Path.Combine(fixture.Root, "large.cs"), new string('x', length) + "\n");
        var (exactTools, _) = fixture.Tools(Guid.NewGuid());
        var call = new ToolCall("result_exact", "read_file", "{\"path\":\"large.cs\",\"line_count\":1}");
        var result = await Invoke(exactTools, call);
        Proof.Check(result.Outcome == ToolOutcome.Succeeded && Encoding.UTF8.GetByteCount(result.Json!) == 4096);
        File.WriteAllText(Path.Combine(fixture.Root, "large.cs"), new string('x', length + 1) + "\n");
        var (overTools, _) = fixture.Tools(Guid.NewGuid());
        var over = await Invoke(overTools, call);
        Proof.Check(over.Outcome == ToolOutcome.Failed && over.Error == ToolError.LimitExceeded && overTools.CurrentObservations.Length == 0);
    }

    private static async Task InvalidOutputs(SnapshotFixture fixture)
    {
        foreach (var corrupt in new Func<AgentToolExecution, AgentToolExecution>[] {
            x => x with { ResultJson = x.ResultJson + " " },
            x => x with { Observation = x.Observation! with { Identity = x.Observation.Identity with { HeadSha = new string('c', 40) } } },
            x => x with { Observation = x.Observation! with { ObservationId = new string('f', 64) } },
            x => x with { Observation = x.Observation! with { ReturnedLines = ImmutableDictionary<string, ImmutableHashSet<int>>.Empty } },
            x => x with { Observation = x.Observation! with { ReturnedLines = x.Observation.ReturnedLines.SetItem("changed.cs", ImmutableHashSet.Create(99)) } },
            x => x with { CanonicalResult = Encoding.UTF8.GetBytes(x.ResultJson![..^1] + ",\"unknown\":true}") },
            x => x with { Succeeded = false, FailureCode = AgentFailureCodes.ToolPathUnsafe },
        })
        {
            var (tools, _) = fixture.Tools(Guid.NewGuid(), corrupt);
            var result = await Invoke(tools, new("corrupt", "read_file", "{\"path\":\"changed.cs\",\"line_count\":1}"));
            Proof.Check(result.Outcome != ToolOutcome.Succeeded && tools.CurrentObservations.Length == 0);
        }
    }

    private static FinishReviewArguments ParseFinal(string json)
    { Proof.Check(AgentToolArguments.TryFinishReviewProvider(json, out var args)); return args!; }

    private static void Terminal(SolusSnapshotTools tools)
    {
        var read = tools.CurrentObservations[3];
        var valid = Proof.Grounded(read.ObservationId);
        Proof.Check(TerminalReviewValidator.TryValidate(ParseFinal(valid), tools.Identity, tools.CurrentObservations, out _));
        foreach (var invalid in new[] { valid.Replace("high", "invalid"), valid.Replace("\"title\":", "\"unknown\":true,\"title\":"),
            valid.Replace("\"summary\":", "\"summary\":\"duplicate\",\"summary\":"),
            valid.Replace("\"evidence\":[", "\"evidence\":null,\"unused\":["),
            Proof.Grounded(new string('f', 64)), Proof.Grounded(read.ObservationId, "unchanged.cs"),
            Proof.Grounded(read.ObservationId, last: 2) })
        {
            var parsed = AgentToolArguments.TryFinishReviewProvider(invalid, out var args);
            Proof.Check(!parsed || !TerminalReviewValidator.TryValidate(args!, tools.Identity, tools.CurrentObservations, out _));
        }
        Proof.Check(!TerminalReviewValidator.TryValidate(ParseFinal(valid), tools.Identity with { HeadSha = new string('c', 40) }, tools.CurrentObservations, out _));
        Proof.Check(!TerminalReviewValidator.TryValidate(ParseFinal(valid), tools.Identity, [], out _));
    }

    private static void Catalog()
    {
        Proof.Stage = "fixed-feedback-catalogue";
        var policy = SolusToolCorrection.Policy;
        Proof.Check(policy.Errors.Count == 23 && policy.MarkerArgumentsJson == AgentRecoveryFeedback.RejectedArguments);
        var expected = new HashSet<string>(StringComparer.Ordinal) { AgentRecoveryFeedback.TrackedPathMissing,
            AgentRecoveryFeedback.ChangedPathMissing, AgentRecoveryFeedback.CursorInvalid };
        foreach (var field in Enum.GetValues<ListFilesPathField>())
        foreach (var rule in Enum.GetValues<RepositoryPathFailure>())
            if (field is ListFilesPathField.Both or ListFilesPathField.Unknown ? rule == RepositoryPathFailure.Unknown
                : rule is not RepositoryPathFailure.None and not RepositoryPathFailure.Unknown)
                expected.Add(AgentRecoveryFeedback.ListFilesPathInvalid(new(field, rule)));
        Proof.Check(expected.Count == 21 && policy.Errors.Where(x => x.Key is not "arguments_invalid" and not "batch_not_executed")
            .Select(x => x.Json).ToHashSet(StringComparer.Ordinal).SetEquals(expected));
        foreach (var error in policy.Errors) Proof.Check(policy.GetError(error.Key).Json == error.Json);
        Proof.Check(policy.GetError("arguments_invalid").Json == AgentRecoveryFeedback.ArgumentsInvalid &&
            policy.GetError("batch_not_executed").Json == AgentRecoveryFeedback.BatchNotExecuted);
        var declarations = Enumerable.Range(0, 30).Select(i => new ToolArgumentError("key_" + i, "code_" + i)).ToArray();
        Proof.Check(new ToolArgumentCorrectionPolicy("finite", declarations).Errors.Count == 32);
        Proof.Reject(() => new ToolArgumentCorrectionPolicy("over", declarations.Append(new("extra", "extra")).ToArray()));
        Proof.Reject(() => new ToolArgumentCorrectionPolicy("duplicate_key", [new("same", "one"), new("same", "two")]));
        Proof.Reject(() => new ToolArgumentCorrectionPolicy("duplicate_wire", [new("one", "same"), new("two", "same")]));
        Proof.Reject(() => new ToolArgumentCorrectionPolicy("builtin_shadow", [new("custom", "arguments_invalid")]));
        var allowed = Enumerable.Range(0, 31).Select(i => "key_" + i).ToArray();
        Proof.Check(new ToolArgumentCorrectionBinding("finite", allowed, (_, _) => ToolArgumentDisposition.Allow()).AllowedErrorKeys.Count == 31);
        Proof.Reject(() => new ToolArgumentCorrectionBinding("over", allowed.Append("extra").ToArray(), (_, _) => ToolArgumentDisposition.Allow()));
        var binding = new ToolArgumentCorrectionBinding("wrong_selector", ["arguments_invalid"], (_, _) => ToolArgumentDisposition.Correct("uninstalled"));
        Proof.Check(binding.Classify(new("x", "read_file", "{}"), new FakeCapability("fake")).Kind == ToolArgumentDispositionKind.Fatal);
    }

    private static void Construction(SnapshotFixture fixture)
    {
        Proof.Stage = "correction-construction-profile";
        var (tools, executor) = fixture.Tools(Guid.NewGuid());
        var flagOnly = new FlagOnlyProvider();
        Proof.Reject(() => Proof.Agent(Proof.Configuration(tools, flagOnly)));
        var absentFlag = new AbsentFlagProvider();
        Proof.Reject(() => Proof.Agent(Proof.Configuration(tools, absentFlag)));
        var provider = new Provider((r, _) => Proof.Response(r, []));
        var configuration = Proof.Configuration(tools, provider);
        Proof.Check(configuration.RequiredGuarantees == (RuntimeGuarantee.OrderedExposure | RuntimeGuarantee.ProviderBounds | RuntimeGuarantee.ToolArgumentCorrection));
        Proof.Reject(() => SolusRuntimeConstruction.Create(configuration, "cr1-solus-internal1-dev"));
        foreach (var options in new[] { new RuntimeOptions(maximumAttempts: 63, requireContinuation: true),
            new RuntimeOptions(maximumRecords: 63, requireContinuation: true),
            new RuntimeOptions(maximumRetainedBytes: 196607, requireContinuation: true),
            new RuntimeOptions(requireContinuation: false), new RuntimeOptions(settlementGrace: TimeSpan.FromSeconds(2), requireContinuation: true) })
            Proof.Reject(() => SolusRuntimeConstruction.Create(configuration, Cr1DevelopmentProfile.Id, options: options));
        Proof.Check(flagOnly.Calls == 0 && absentFlag.Calls == 0 && provider.Calls == 0 && executor.Invocations == 0);
    }

    private sealed class FlagOnlyProvider : IModelProvider
    {
        internal int Calls;
        public ProviderScope Scope { get; } = new("flag-only", "synthetic");
        public ProviderCapabilities Capabilities => ProviderCapabilities.ToolCalls | ProviderCapabilities.Continuation |
            ProviderCapabilities.UsageReporting | ProviderCapabilities.ToolArgumentCorrection;
        public ValueTask<ProviderExchangeResult> ExchangeAsync(ProviderRequest request, CancellationToken token = default)
        { Calls++; throw new InvalidOperationException(); }
    }

    private sealed class AbsentFlagProvider() : ModelProvider(new("absent-flag", "synthetic"),
        ProviderCapabilities.ToolCalls | ProviderCapabilities.Continuation | ProviderCapabilities.UsageReporting)
    {
        internal int Calls;
        protected override ValueTask<ProviderResponse> ExchangeCoreAsync(ProviderRequest request, ProviderObservation observation, CancellationToken token)
        { Calls++; throw new InvalidOperationException(); }
    }

    private sealed class FakeCapability(string id) : IToolCapability { public string CapabilityId => id; }
}

using System.Text;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Solus;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Tools.Api;

namespace AgenticPrReview.Runtime.SolusToolAdapterFixture;

internal static class RuntimeTests
{
    internal static async Task Run()
    {
        using var fixture = new SnapshotFixture();
        Proof.Stage = "actual-runtime-five-tools-final";
        var execution = Guid.NewGuid(); var (tools, executor) = fixture.Tools(execution);
        var provider = new Provider((request, n) => n switch
        {
            1 => Proof.Response(request, [new("files", "list_files", "{}"), new("changed", "list_changed_files", "{}"),
                new("diff", "read_diff", "{\"path\":\"changed.cs\"}")], Proof.EmptyFinal),
            2 => Proof.Response(request, [new("read", "read_file", "{\"path\":\"changed.cs\",\"line_count\":1.0}"),
                new("search", "search_text", "{\"query\":\"guard\",\"path\":\"changed.cs\"}")]),
            _ => Proof.Response(request, [], Proof.Grounded(tools.CurrentObservations[3].ObservationId)),
        });
        var host = new SolusReviewCandidateHost(execution, tools); var sink = new Sink();
        var result = await Proof.Agent(Proof.Configuration(tools, provider)).ExecuteCandidatesWithContextAsync(
            new(Proof.Request(execution), ContextExecutionIntent.Fresh), SolusReviewCandidateHost.Bounds(), host, sink);
        Proof.Check(host.TryComplete(result, out var review) && review!.Findings.Length == 1);
        Proof.Check(!host.TryComplete(result, out _));
        Proof.Check(provider.Calls == 3 && executor.Invocations == 5 && tools.CurrentObservations.Length == 5);
        Proof.Check(result.Context.Outcome!.Usage!.Attempts.Count == 3 && result.Context.Outcome.Usage.ToolInvocations!.Invoked == 5 &&
            result.Context.Outcome.Usage.ToolCallAdmissions!.Admitted == 5);
        Proof.Check(result.Context.CaptureStatus == ContextCaptureStatus.Delivered && sink.Context!.FormatVersion == 2 &&
            sink.Context.CompatibilityVersion == 1 && result.Candidates!.RepairsAdmitted == 0 && result.Candidates.ContinuationsAdmitted == 0);
        Proof.Check(provider.Requests[1].Inputs.Where(i => i.ToolResult is not null).All(i => i.Kind == ProviderInputKind.ToolResultData));

        Proof.Stage = "actual-runtime-correction-atomic";
        foreach (var reverse in new[] { false, true })
        foreach (var schemaInvalid in new[] { false, true })
            await Correction(fixture, reverse, schemaInvalid);
        Proof.Stage = "same-wire-code-domains-and-path-rules";
        await Variants(fixture);
        Proof.Stage = "fatal-precedence-and-bounds";
        await FatalAndBounds(fixture);
        Proof.Stage = "rejected-final-no-accepted-state";
        await RejectedFinal(fixture);
        Proof.Stage = "actual-context-restore-current-evidence";
        await Restore(fixture, sink.Context!, result.Context.Checkpoint!, tools.CurrentObservations[3].ObservationId);
    }

    private static async Task Correction(SnapshotFixture fixture, bool reverse, bool schemaInvalid)
    {
        var execution = Guid.NewGuid(); var (tools, executor) = fixture.Tools(execution);
        var invalid = new ToolCall("invalid_a", "read_file", schemaInvalid
            ? "{\"path\":\"" + Proof.RejectedCanary + "\",\"unknown\":true}"
            : "{\"path\":\"" + Proof.RejectedCanary + "\"}");
        var valid = new ToolCall("valid_b", "list_files", "{}");
        var batch = reverse ? new[] { valid, invalid } : new[] { invalid, valid };
        var provider = new Provider((request, n) =>
        {
            if (n == 1) return Proof.Response(request, batch);
            if (n == 2)
            {
                Proof.Check(executor.Invocations == 0 && tools.CurrentObservations.Length == 0);
                var correction = request.Inputs.Single(i => i.Model?.Finish == ProviderFinish.ToolCorrection).Model!;
                var members = correction.Correction!.Members;
                Proof.Check(members.Count == 2 && members.Select(m => m.Call.CallId).SequenceEqual(batch.Select(c => c.CallId)));
                Proof.Check(members.Select(m => m.Call.ToolName).SequenceEqual(batch.Select(c => c.ToolName)));
                var rejected = members.Single(m => m.Rejected); var sibling = members.Single(m => !m.Rejected);
                Proof.Check(rejected.Call.ArgumentsJson == AgentRecoveryFeedback.RejectedArguments && sibling.Call.ArgumentsJson == "{}");
                var feedback = request.Inputs.Where(i => i.CorrectionFeedback is not null).Select(i => i.CorrectionFeedback!).ToArray();
                Proof.Check(feedback.Length == 2 && feedback.Single(f => f.Call.CallId == invalid.CallId).Json ==
                    (schemaInvalid ? AgentRecoveryFeedback.ArgumentsInvalid : AgentRecoveryFeedback.TrackedPathMissing));
                Proof.Check(feedback.Single(f => f.Call.CallId == valid.CallId).Json == AgentRecoveryFeedback.BatchNotExecuted);
                Proof.Check(correction.Continuation is not null && Encoding.UTF8.GetString(correction.Continuation.CopyReplayBytes()) == "synthetic-replay");
                CheckNoRejected(request);
                return Proof.Response(request, [new("corrected_c", "read_file", "{\"path\":\"changed.cs\",\"line_count\":1}")]);
            }
            return Proof.Response(request, [], Proof.Grounded(tools.CurrentObservations.Single().ObservationId));
        });
        var host = new SolusReviewCandidateHost(execution, tools); var sink = new Sink();
        var result = await Proof.Agent(Proof.Configuration(tools, provider)).ExecuteCandidatesWithContextAsync(
            new(Proof.Request(execution), ContextExecutionIntent.Fresh), SolusReviewCandidateHost.Bounds(), host, sink);
        Proof.Check(host.TryComplete(result, out _) && provider.Calls == 3 && executor.Invocations == 1);
        Proof.Check(provider.Validations > provider.Calls);
        Proof.Check(result.Context.Outcome!.Usage!.Attempts.Count == 3 && result.Context.Outcome.Usage.ToolCallAdmissions!.Admitted == 3);
        Proof.Check(result.Context.Outcome.Usage.ToolInvocations!.Invoked == 1);
        Proof.Check(sink.Context is not null && !Encoding.UTF8.GetString(sink.Context.CopyRestrictedPayload()).Contains(Proof.RejectedCanary, StringComparison.Ordinal));
    }

    private static void CheckNoRejected(ProviderRequest request)
    {
        var rawHash = AgentCanonical.HashRaw(Encoding.UTF8.GetBytes("{\"path\":\"" + Proof.RejectedCanary + "\"}"));
        foreach (var input in request.Inputs)
        {
            var text = input.Text ?? input.ToolResult?.Json ?? input.CorrectionFeedback?.Json ?? "";
            var arguments = input.Model?.Correction?.Members.Select(m => m.Call.ArgumentsJson) ?? [];
            Proof.Check(!text.Contains(Proof.RejectedCanary, StringComparison.Ordinal) && !text.Contains(rawHash, StringComparison.Ordinal));
            Proof.Check(arguments.All(x => !x.Contains(Proof.RejectedCanary, StringComparison.Ordinal) && !x.Contains(rawHash, StringComparison.Ordinal)));
        }
    }

    private static async Task Variants(SnapshotFixture fixture)
    {
        foreach (var calls in new[] {
            new[] { new ToolCall("tracked", "read_file", "{\"path\":\"missing.cs\"}"),
                new ToolCall("changed", "read_diff", "{\"path\":\"unchanged.cs\"}") },
            new[] { new ToolCall("prefix", "list_files", "{\"prefix\":\"../bad\"}"),
                new ToolCall("after", "list_files", "{\"after\":\"/bad\"}") },
            new[] { new ToolCall("cursor", "list_files", "{\"after\":\"missing.cs\"}") },
        })
        {
            var execution = Guid.NewGuid(); var (tools, executor) = fixture.Tools(execution);
            var provider = new Provider((request, n) =>
            {
                if (n == 1) return Proof.Response(request, calls);
                var feedback = request.Inputs.Where(i => i.CorrectionFeedback is not null).Select(i => i.CorrectionFeedback!).ToArray();
                Proof.Check(feedback.Length == calls.Length && executor.Invocations == 0);
                var expected = calls[0].CallId switch
                {
                    "tracked" => new[] { AgentRecoveryFeedback.TrackedPathMissing, AgentRecoveryFeedback.ChangedPathMissing },
                    "prefix" => [AgentRecoveryFeedback.ListFilesPathInvalid(new(ListFilesPathField.Prefix, RepositoryPathFailure.DotSegment)),
                        AgentRecoveryFeedback.ListFilesPathInvalid(new(ListFilesPathField.After, RepositoryPathFailure.Absolute))],
                    _ => [AgentRecoveryFeedback.CursorInvalid],
                };
                Proof.Check(feedback.Select(f => f.Json).SequenceEqual(expected));
                return Proof.Response(request, []);
            });
            var host = new SolusReviewCandidateHost(execution, tools);
            var result = await Proof.Agent(Proof.Configuration(tools, provider)).ExecuteCandidatesWithContextAsync(
                new(Proof.Request(execution), ContextExecutionIntent.Fresh), SolusReviewCandidateHost.Bounds(), host);
            Proof.Check(host.TryComplete(result, out _) && provider.Calls == 2 && executor.Invocations == 0 && tools.CurrentObservations.Length == 0);
        }
    }

    private static async Task FatalAndBounds(SnapshotFixture fixture)
    {
        foreach (var reverse in new[] { false, true })
        {
            var execution = Guid.NewGuid(); var (tools, executor) = fixture.Tools(execution);
            executor.PreflightFault = call => call.Name == "read_file" ? AgentFailureCodes.ToolPathInvalid : null;
            var batch = new[] { new ToolCall("fatal", "read_file", "{\"path\":\"changed.cs\"}"),
                new ToolCall("correctable", "list_files", "{\"prefix\":\"../bad\"}") };
            if (reverse) Array.Reverse(batch);
            var provider = new Provider((request, _) => Proof.Response(request, batch));
            var host = new SolusReviewCandidateHost(execution, tools);
            var result = await Proof.Agent(Proof.Configuration(tools, provider)).ExecuteCandidatesWithContextAsync(
                new(Proof.Request(execution), ContextExecutionIntent.Fresh), SolusReviewCandidateHost.Bounds(), host);
            Proof.Check(!host.TryComplete(result, out _) && provider.Calls == 1 && executor.Invocations == 0);
        }
        foreach (var mode in new[] { "prospective", "logical_budget", "admission_budget", "unknown_tool", "terminal_function", "duplicate_id" })
        {
            var execution = Guid.NewGuid(); var (tools, executor) = fixture.Tools(execution);
            var batch = new[] { new ToolCall("a", "read_file", "{\"path\":\"missing.cs\"}"), new ToolCall("b", "list_files", "{}") };
            if (mode is "unknown_tool" or "terminal_function") batch[0] = new("a", mode == "unknown_tool" ? "unknown" : "finish_review", Proof.EmptyFinal);
            if (mode == "duplicate_id") batch[1] = new("a", "list_files", "{}");
            var provider = new Provider((request, _) => Proof.Response(request, batch)) { RefuseCorrection = mode == "prospective" };
            var host = new SolusReviewCandidateHost(execution, tools);
            var result = await Proof.Agent(Proof.Configuration(tools, provider)).ExecuteCandidatesWithContextAsync(
                new(Proof.Request(execution, mode == "logical_budget" ? 1 : 6, mode == "admission_budget" ? 1 : 6),
                    ContextExecutionIntent.Fresh), SolusReviewCandidateHost.Bounds(), host);
            Proof.Check(!host.TryComplete(result, out _) && provider.Calls == 1 && executor.Invocations == 0 && tools.CurrentObservations.Length == 0);
        }
        var cancelledExecution = Guid.NewGuid(); var (cancelledTools, cancelledExecutor) = fixture.Tools(cancelledExecution);
        var never = new Provider((r, _) => Proof.Response(r, [])); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var cancelledHost = new SolusReviewCandidateHost(cancelledExecution, cancelledTools);
        var cancelled = await Proof.Agent(Proof.Configuration(cancelledTools, never)).ExecuteCandidatesWithContextAsync(
            new(Proof.Request(cancelledExecution), ContextExecutionIntent.Fresh), SolusReviewCandidateHost.Bounds(), cancelledHost,
            cancellationToken: cancellation.Token);
        Proof.Check(!cancelledHost.TryComplete(cancelled, out _) && never.Calls == 0 && cancelledExecutor.Invocations == 0);
        Proof.Reject(() => new ProviderResponse(never.Scope, new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1), ProviderFinish.Final,
            Proof.EmptyFinal, [new("mixed", "read_file", "{\"path\":\"changed.cs\"}")]));
    }

    internal static SolusRestrictedStateCompatibility Compatibility() => new(SolusRestrictedStateFormat.PackageVersion,
        SolusRestrictedStateFormat.Profile, 1, SolusRestrictedStateFormat.Implementation, 2, 1,
        SolusRestrictedStateFormat.PackageClosureSha256, new string('a', 64), new string('b', 64));

    private static async Task RejectedFinal(SnapshotFixture fixture)
    {
        foreach (var text in new[] { "{}", Proof.Grounded(new string('f', 64)),
            Proof.EmptyFinal.Replace("\"summary\":", "\"unknown\":true,\"summary\":"),
            Proof.EmptyFinal.Replace("\"findings\":[]", "\"findings\":[{\"severity\":\"high\"}]") })
        {
            var execution = Guid.NewGuid(); var (tools, executor) = fixture.Tools(execution);
            var provider = new Provider((r, _) => Proof.Response(r, [], text)); var host = new SolusReviewCandidateHost(execution, tools);
            using var capture = new SolusRestrictedStateCapture(execution, Compatibility());
            var result = await Proof.Agent(Proof.Configuration(tools, provider)).ExecuteCandidatesWithContextAsync(
                new(Proof.Request(execution), ContextExecutionIntent.Fresh), SolusReviewCandidateHost.Bounds(), host, capture);
            Proof.Check(!host.TryComplete(result, out _) && !capture.TryComplete(result, out _) && provider.Calls == 1 && executor.Invocations == 0);
            Proof.Check(result.Candidates!.Receipts.Count == 1 && !result.Candidates.Receipts[0].IsAccepted && result.Candidates.RepairsAdmitted == 0);
        }
    }

    private static async Task Restore(SnapshotFixture fixture, AgentContextEnvelope source, ContextCheckpointInfo checkpoint, string historicalId)
    {
        foreach (var reobserve in new[] { false, true })
        {
            var execution = Guid.NewGuid(); var (tools, executor) = fixture.Tools(execution);
            var grant = new ContextRoundGrant(Guid.NewGuid(), checkpoint, execution, Guid.NewGuid()); var authority = new Authority(source, grant);
            var provider = new Provider((request, n) =>
            {
                if (n == 1)
                {
                    Proof.Check(executor.Invocations == 0 && tools.CurrentObservations.Length == 0);
                    Proof.Check(request.Inputs.Where(x => x.ToolResult is not null).All(x => x.ToolResult!.IsHistorical));
                    if (reobserve) return Proof.Response(request, [new("fresh_read", "read_file", "{\"path\":\"changed.cs\",\"line_count\":1}")]);
                }
                return Proof.Response(request, [], Proof.Grounded(reobserve ? tools.CurrentObservations.Single().ObservationId : historicalId));
            });
            var host = new SolusReviewCandidateHost(execution, tools);
            var result = await Proof.Agent(Proof.Configuration(tools, provider, authority)).ExecuteCandidatesWithContextAsync(
                new(Proof.Request(execution), ContextExecutionIntent.NewRunFromContext, source, grant), SolusReviewCandidateHost.Bounds(), host);
            Proof.Check(result.Context.Admission == ContextAdmission.Supplied && authority.Claims == 1 && provider.Imports == 1);
            Proof.Check(host.TryComplete(result, out _) == reobserve && executor.Invocations == (reobserve ? 1 : 0));
        }
        foreach (var mode in new[] { "format", "policy", "binding_fingerprint", "scope", "tamper" })
        {
            var execution = Guid.NewGuid(); var (tools, executor) = fixture.Tools(execution);
            var grant = new ContextRoundGrant(Guid.NewGuid(), checkpoint, execution, Guid.NewGuid()); var authority = new Authority(source, grant, mode != "scope");
            var provider = new Provider((r, _) => Proof.Response(r, []));
            var policy = SolusToolCorrection.Policy;
            IReadOnlyList<RuntimeToolRegistration>? registrations = null;
            if (mode == "policy")
            {
                var originalKey = policy.Errors.First(e => e.Key is not "arguments_invalid" and not "batch_not_executed").Key;
                policy = new(policy.Identity, policy.Errors.Where(e => e.Key is not "arguments_invalid" and not "batch_not_executed")
                    .Select((e, i) => new ToolArgumentError(i == 0 ? "different_static_key" : e.Key, e.Code, e.Attributes)).ToArray(), "_apr_rejected");
                registrations = tools.Registrations.Select(r => new RuntimeToolRegistration(r.Tool, r.Capability,
                    argumentCorrection: new ToolArgumentCorrectionBinding(r.ArgumentCorrection!.Identity,
                        r.ArgumentCorrection.AllowedErrorKeys.Select(k => k == originalKey ? "different_static_key" : k).ToArray(),
                        (call, capability) =>
                        {
                            var value = r.ArgumentCorrection.Classify(call, capability);
                            return value.ErrorKey == originalKey ? ToolArgumentDisposition.Correct("different_static_key") : value;
                        }))).ToArray();
            }
            var supplied = source;
            if (mode == "format") supplied = new(source.ImplementationId, 1, 1, source.CopyRestrictedPayload());
            if (mode is "tamper" or "binding_fingerprint")
            {
                // Fixture rejection input only, never a product context converter.
                var bytes = source.CopyRestrictedPayload();
                var text = Encoding.UTF8.GetString(bytes);
                if (mode == "tamper") text = text.Replace("guard missing", "tampered line", StringComparison.Ordinal);
                else
                {
                    var node = System.Text.Json.Nodes.JsonNode.Parse(text)!.AsObject();
                    node["Binding"] = new string('0', 64);
                    text = node.ToJsonString();
                }
                Proof.Check(text != Encoding.UTF8.GetString(bytes));
                supplied = new(source.ImplementationId, 2, 1, Encoding.UTF8.GetBytes(text));
            }
            var host = new SolusReviewCandidateHost(execution, tools);
            var result = await Proof.Agent(Proof.Configuration(tools, provider, authority, policy, registrations)).ExecuteCandidatesWithContextAsync(
                new(Proof.Request(execution), ContextExecutionIntent.NewRunFromContext, supplied, grant), SolusReviewCandidateHost.Bounds(), host);
            Proof.Check(result.Context.Admission == ContextAdmission.Rejected && provider.Calls == 0 && executor.Invocations == 0 && !host.TryComplete(result, out _));
            if (mode is "format" or "policy" or "binding_fingerprint") Proof.Check(authority.Claims == 0);
        }
    }
}

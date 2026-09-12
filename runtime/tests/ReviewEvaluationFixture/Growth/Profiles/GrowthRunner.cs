using System.Collections.Immutable;
using System.Security.Cryptography;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Reporting;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Growth.Profiles;

internal sealed class GrowthOptions
{
    internal string? Profile { get; init; }
    internal int AttemptLimit { get; init; } = GrowthProfiles.Attempts;
    internal ReplayFault Fault { get; init; }
    internal int FaultPhase { get; init; } = 1;
    internal Func<ReplayChildInput, ReplayChildReply, ReplayChildReply>? TransformReply { get; init; }
    internal Action<ReplayChildInput, ReplayChildReply>? ObserveReply { get; init; }
    internal Func<string, bool> Cleanup { get; init; } = ReplayProcess.Cleanup;
    internal Func<ReplayChildInput, TimeSpan, CancellationToken, Task<ReplayProcessResult>> RunProcess { get; init; } = ReplayProcess.RunAsync;
    internal Func<string, CancellationToken, ReplayAdmissionResult> AdmitBundle { get; init; } = ReplayAdmission.Load;
}

internal static class GrowthRunner
{
    internal static async Task<GrowthReport> RunAsync(string bundle, GrowthOptions? options = null, CancellationToken token = default)
    {
        options ??= new();
        var profiles = ImmutableArray.CreateBuilder<GrowthProfileReport>();
        string? corpus = null;
        string? seedCorpus = null;
        var schedule = new GrowthSchedule(options.AttemptLimit, options.Fault, options.FaultPhase);
        var code = "input_invalid";
        var cleanup = "cleaned";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            if (!schedule.Valid ||
                options.Profile is not null && !GrowthProfiles.Names.Contains(options.Profile)) return Report();
            // Capture once before any profile runs. Every child re-admits its private copy.
            var captured = ReplayDirectory.Capture(bundle, deadline.Token);
            var admitted = LoadBundle(options, bundle, deadline.Token);
            if (admitted.Fixture is not { } original || !GrowthProfiles.Matches(original)) return Report();
            var selected = options.Profile is null ? GrowthProfiles.Names : [options.Profile];
            foreach (var profile in selected)
            {
                var root = ReplayProcess.CreatePrivateRoot();
                var key = RandomNumberGenerator.GetBytes(RestrictedStateFormat.KeyBytes);
                var reaped = true;
                try
                {
                    var privateBundle = Path.Combine(root, "bundle");
                    Directory.CreateDirectory(privateBundle);
                    foreach (var member in captured.Files)
                    {
                        var target = Path.Combine(privateBundle, member.Key);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.WriteAllBytes(target, member.Value.ToArray());
                    }
                    File.WriteAllBytes(Path.Combine(privateBundle, ReplayLimits.ManifestName), ReplayJson.Write(captured.Manifest));
                    var loaded = LoadBundle(options, privateBundle, deadline.Token);
                    if (loaded.Fixture is not { } fixture || !GrowthProfiles.Matches(fixture) || fixture.CorpusSha256 != original.CorpusSha256)
                        throw new ReplayRejected(ReplayAdmissionCode.ContentMismatch);
                    seedCorpus = fixture.CorpusSha256;
                    corpus = GrowthProfiles.Corpus(fixture, schedule);
                    var result = await ProfileAsync(fixture, profile, root, key, options, deadline.Token);
                    reaped = result.Reaped;
                    profiles.Add(result.Report);
                }
                catch (ReplayProcessUnreaped) { reaped = false; throw; }
                finally
                {
                    CryptographicOperations.ZeroMemory(key);
                    try { if (!reaped || !options.Cleanup(root)) cleanup = "cleanup_failed"; }
                    catch { cleanup = "cleanup_failed"; }
                }
                if (cleanup != "cleaned") break;
            }
            code = deadline.IsCancellationRequested ? "cancelled" : profiles.Count == selected.Length && profiles.All(p => p.LimitObserved && p.Rows.All(r => r.PredecessorPreserved))
                ? "verified" : "observation_incomplete";
        }
        catch (ReplayRejected) { code = "input_invalid"; }
        catch (OperationCanceledException) { code = "cancelled"; }
        catch { code = "infrastructure_failed"; }
        return Report();

        GrowthReport Report() => new(cleanup == "cleaned" ? code : "cleanup_failed", cleanup, corpus, seedCorpus, schedule.Valid ? schedule : GrowthSchedule.Default,
            EvaluationSource.Commit, EvaluationSource.Tree, EvaluationSource.Clean, profiles.ToImmutable(),
            profiles.Count == 0 ? null : GrowthJson.Normalize(profiles.ToImmutable()));
    }

    private static ReplayAdmissionResult LoadBundle(GrowthOptions options, string bundle, CancellationToken token)
    {
        var result = options.AdmitBundle(bundle, token);
        if (result.Code == ReplayAdmissionCode.Cancelled) throw new OperationCanceledException(token);
        if (result.Code == ReplayAdmissionCode.IoFailure) throw new IOException();
        return result;
    }

    private static async Task<(GrowthProfileReport Report, bool Reaped)> ProfileAsync(AdmittedReplayFixture fixture, string profile,
        string root, byte[] key, GrowthOptions options, CancellationToken token)
    {
        var rows = ImmutableArray.CreateBuilder<GrowthRow>();
        var evaluations = ImmutableArray.CreateBuilder<ReadOnlyMemory<byte>>();
        var session = Guid.NewGuid().ToString("N");
        var operation = Guid.NewGuid().ToString("N");
        var schedule = new GrowthSchedule(options.AttemptLimit, options.Fault, options.FaultPhase);
        var corpus = GrowthProfiles.Corpus(fixture, schedule);
        AcceptedLineage? lineage = null;
        AdmittedReplayRun? previous = null;
        var startups = new HashSet<string>();
        var reaped = true;
        var terminalStage = "schedule";
        var terminalCode = "attempt_limit";
        for (var phase = 0; phase < options.AttemptLimit; phase++)
        {
            if (token.IsCancellationRequested) { terminalStage = "schedule"; terminalCode = "cancelled"; break; }
            var fault = phase == options.FaultPhase ? options.Fault : ReplayFault.None;
            var run = GrowthProfiles.Run(fixture, profile, phase, fault, schedule);
            var descriptor = new EvaluationRunInput(run.Input.Id, "deterministic", EvaluationSource.Commit,
                EvaluationSource.Tree, EvaluationSource.Clean, run.ProviderConfigurationSha256);
            var attempt = EvaluationAttempt.Admit(run.CreateTrustedRequest(ReplayState.Build), descriptor)!;
            (GrowthState Measurement, byte[] Bytes)? before;
            try { before = lineage is null ? null : await ReadAsync(previous!, lineage, root, session, key, token); }
            catch (Exception error)
            {
                terminalStage = "schedule";
                terminalCode = error is OperationCanceledException ? "cancelled" : "infrastructure_failed";
                break;
            }
            if (lineage is not null && before is null) { terminalStage = "schedule"; terminalCode = "predecessor_unavailable"; break; }
            var input = new ReplayChildInput(operation, root, corpus, phase, session, key, lineage, fault, profile, schedule);
            try
            {
                var process = await options.RunProcess(input, TimeSpan.FromSeconds(60), token);
                var reply = process.Reply;
                if (reply is not null) reply = options.TransformReply?.Invoke(input, reply) ?? reply;
                EvaluationOutcome? evaluation = null;
                var admitted = reply is not null && ReplayRunner.AdmitReply(input, run, reply, out evaluation) && startups.Add(reply.Startup);
                if (!admitted) evaluation = null;
                var stage = admitted ? reply!.ObservedStage ?? "executor" : "executor";
                var resultCode = admitted ? reply!.ObservedCode ?? reply.Code : reply is null ? process.Code : "result_invalid";
                var accepted = false;
                GrowthState? stateAfter = null;
                using var state = new ReplayState(run, session, root, key);
                if (admitted)
                {
                    options.ObserveReply?.Invoke(input, reply!);
                    token.ThrowIfCancellationRequested();
                    if (reply!.Code == "prepared")
                    {
                        var identity = run.Input.ReviewedIdentity.Runtime;
                        var context = state.Context(identity, identity, phase, lineage?.EnvelopeSha256,
                            ReplayState.Transition(run, previous), run.InitialContext);
                        var result = await state.Service.AcceptAsync(state.Access, lineage, reply.Prepared!, context, token);
                        stage = "accept"; resultCode = result.Code;
                        if (result.Action == StateAction.Accepted && result.Generation == phase &&
                            result.SessionSha256 == reply.Prepared!.SessionSha256 && result.EnvelopeSha256 == reply.Prepared.EnvelopeSha256)
                        {
                            var next = new AcceptedLineage(state.Access.Scope, phase, result.SessionSha256!, result.EnvelopeSha256!,
                                lineage?.EnvelopeSha256, ReplayState.Now, ReplayState.Now + RestrictedStateFormat.MaximumRetentionSeconds, true);
                            var read = await ReadAsync(run, next, root, session, key, token);
                            if (read is not null && reply.Plaintext!.AsSpan().SequenceEqual(read.Value.Bytes))
                            {
                                accepted = true; lineage = next; previous = run; stateAfter = read.Value.Measurement;
                            }
                            else { stage = "readback"; resultCode = "accepted_readback_failed"; }
                        }
                    }
                }
                var preserved = accepted;
                if (!accepted && before is not null)
                {
                    var restored = await ReadAsync(previous!, lineage!, root, session, key, CancellationToken.None);
                    preserved = restored is not null && restored.Value.Measurement == before.Value.Measurement &&
                        restored.Value.Bytes.AsSpan().SequenceEqual(before.Value.Bytes);
                }
                else if (!accepted)
                {
                    var inventory = await state.Service.EnumerateAsync(state.Access, CancellationToken.None);
                    preserved = inventory.Result.Action == StateAction.Enumerated && inventory.Candidates.IsEmpty;
                }
                evaluation ??= EvaluationScorer.Failure(run.Expected, admitted ? EvaluationFailure.Unknown : EvaluationFailure.Invalid, attempt);
                evaluations.Add(EvaluationJson.Write(evaluation));
                var classification = Classify(stage, resultCode, admitted ? reply!.GrowthCounts : null);
                rows.Add(new(phase, run.Input.CaseId, attempt.AttemptSha256, run.Input.Transition, stage, resultCode, classification,
                    accepted, before?.Measurement, stateAfter, preserved, admitted ? reply!.ModelCalls : null,
                    admitted ? reply!.ToolCalls : null, admitted ? reply!.Requests.Length : null,
                    admitted ? reply!.Requests.Sum(b => (long)b.Length) : null,
                    admitted && !reply!.Requests.IsEmpty ? reply.Requests[^1].Length : null, admitted ? reply!.GrowthCounts : null));
                if (!accepted) { terminalStage = stage; terminalCode = resultCode; break; }
            }
            catch (Exception error)
            {
                // The attempted child must not disappear when a later accept/readback/cancellation fails.
                reaped = error is not ReplayProcessUnreaped;
                var preserved = false;
                try
                {
                    // Never inspect a store that an unconfirmed child might still be changing.
                    if (reaped && before is not null && previous is not null && lineage is not null)
                    {
                        var restored = await ReadAsync(previous, lineage, root, session, key, CancellationToken.None);
                        preserved = restored is not null && restored.Value.Measurement == before.Value.Measurement &&
                            restored.Value.Bytes.AsSpan().SequenceEqual(before.Value.Bytes);
                    }
                }
                catch { /* preservation remains unverified */ }
                terminalStage = "executor";
                terminalCode = !reaped ? "process_unreaped" : error is OperationCanceledException ? "cancelled" : "infrastructure_failed";
                evaluations.Add(EvaluationJson.Write(EvaluationScorer.Failure(run.Expected, EvaluationFailure.Invalid, attempt)));
                rows.Add(new(phase, run.Input.CaseId, attempt.AttemptSha256, run.Input.Transition,
                    terminalStage, terminalCode, "harness_failure", false, before?.Measurement, null, preserved,
                    null, null, null, null, null, null));
                break;
            }
        }
        if (rows.Count == 0) token.ThrowIfCancellationRequested();
        var report = EvaluationReport.Create(evaluations.ToImmutable());
        if (!report.Succeeded) throw new InvalidOperationException("growth_report_invalid");
        var final = rows[^1];
        return (new(profile, options.AttemptLimit, terminalStage, terminalCode,
            !final.Accepted && final.Classification is "append_limit" or "run_budget" or "continuation_limit" or "message_limit",
            rows.ToImmutable(), report.Value!.Document), reaped);
    }

    internal static string Classify(string stage, string code, GrowthChatCounts? counts) => (stage, code) switch
    {
        ("build", AgentSessionCodes.ConstructionLimit) => "append_limit",
        ("agent", AgentFailureCodes.ModelLimit or AgentFailureCodes.ToolLimit or AgentFailureCodes.TokenLimit or AgentFailureCodes.RequestTooLarge) => "run_budget",
        ("agent", AgentFailureCodes.ResponseInvalid) when counts?.LastContinuationAfterBytes > AgentLimits.ContinuationTotalBytes => "continuation_limit",
        ("agent", AgentFailureCodes.ResponseInvalid) when counts?.LastResponseMessages > AgentLimits.Messages => "message_limit",
        ("restore", _) => "invalid_current_state",
        ("accept", RestrictedStateCodes.Accepted) => "accepted",
        ("agent", _) => "agent_failure",
        ("build" or "prepare" or "accept", _) => "state_failure",
        _ => "harness_failure",
    };

    private static async Task<(GrowthState Measurement, byte[] Bytes)?> ReadAsync(AdmittedReplayRun run, AcceptedLineage lineage,
        string root, string session, byte[] key, CancellationToken token)
    {
        using var state = new ReplayState(run, session, root, key);
        var identity = run.Input.ReviewedIdentity.Runtime;
        var context = state.Context(identity, identity, lineage.Generation, lineage.ExpectedPredecessorEnvelopeSha256,
            AgentSessionHeadTransition.SameHead, run.InitialContext);
        var read = await state.Service.RestoreAsync(state.Access,
            new(RestrictedStateLocatorFamily.Current, RestrictedStateRestoreIntent.Explicit, lineage, context), token);
        var inventory = await state.Service.EnumerateAsync(state.Access, token);
        if (read.Result.Action != StateAction.Restored || read.Session?.Value is not { } restored ||
            inventory.Result.Action != StateAction.Enumerated) return null;
        var candidate = inventory.Candidates.FirstOrDefault(c => c.EnvelopeSha256 == lineage.EnvelopeSha256);
        var artifact = restored.Artifact;
        if (candidate is null || candidate.SessionSha256 != lineage.SessionSha256 || artifact.SessionSha256 != lineage.SessionSha256 ||
            candidate.Binding.Generation != lineage.Generation || artifact.Document.Generation != lineage.Generation ||
            artifact.Document.PredecessorStateSha256 != lineage.ExpectedPredecessorEnvelopeSha256) return null;
        var metadata = inventory.Candidates.Sum(RestrictedStateSnapshotCodec.CandidateMetadataBytes);
        var measure = new GrowthState(lineage.Generation, artifact.Document.CompletedRuns.Length,
            artifact.Document.CompletedRuns.Sum(r => r.Records.Length + r.Continuation.Items.Length),
            artifact.Document.CompletedRuns.Sum(r => r.Continuation.Items.Sum(i => (long)i.PayloadBytes.Length)),
            artifact.Plaintext.Length, candidate.Envelope.Length, inventory.Candidates.Sum(c => (long)c.Envelope.Length) + metadata,
            metadata, inventory.Candidates.Length, lineage.SessionSha256, lineage.EnvelopeSha256,
            lineage.ExpectedPredecessorEnvelopeSha256, ReplayProjection.Logical(artifact, state.Trusted));
        return (measure, artifact.Plaintext);
    }
}

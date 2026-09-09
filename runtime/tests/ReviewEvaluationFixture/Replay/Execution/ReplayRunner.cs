using System.Collections;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

internal sealed class ReplayOptions
{
    internal ReplayFault Fault { get; init; }
    internal int FaultPhase { get; init; } = 1;
    internal TimeSpan PhaseTimeout { get; init; } = TimeSpan.FromSeconds(60);
    internal Func<AdmittedReplayFixture, int, ReplayChildReply, bool> Verify { get; init; } = ReplayCoverage.Verify;
    internal Action<string>? BeforeCleanup { get; init; }
    internal Action<ReplayChildInput, AdmittedReplayRun, ReplayChildReply>? ObserveReply { get; init; }
    internal Func<string, bool> Cleanup { get; init; } = ReplayProcess.Cleanup;
}

internal static class ReplayRunner
{
    internal static async Task<ReplayReport> RunAsync(string bundle, ReplayOptions? options = null, CancellationToken token = default)
    {
        options ??= new();
        var operation = Guid.NewGuid().ToString("N");
        var session = Guid.NewGuid().ToString("N");
        var key = RandomNumberGenerator.GetBytes(32);
        var canaries = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .Where(entry => new[] { "TOKEN", "KEY", "SECRET", "CANARY", "PASSWORD", "AUTH" }
                .Any(word => ((string)entry.Key).Contains(word, StringComparison.OrdinalIgnoreCase)))
            .Select(entry => entry.Value?.ToString()).Where(value => value is { Length: >= 12 and <= 4096 }).Cast<string>().ToArray();
        string? root = null, corpus = null;
        var cleanup = "not_created";
        var code = "infrastructure_failed";
        var steps = ImmutableArray.CreateBuilder<ReplayStep>();
        var observations = ImmutableArray.CreateBuilder<ReplayObservation>();
        try
        {
            root = ReplayProcess.CreatePrivateRoot();
            token.ThrowIfCancellationRequested();
            var captured = ReplayDirectory.Capture(bundle, token);
            var privateBundle = Path.Combine(root, "bundle");
            Directory.CreateDirectory(privateBundle);
            foreach (var member in captured.Files)
            {
                var destination = Path.Combine(privateBundle, member.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.WriteAllBytes(destination, member.Value.ToArray());
            }
            File.WriteAllBytes(Path.Combine(privateBundle, ReplayLimits.ManifestName), ReplayJson.Write(captured.Manifest));
            var loaded = ReplayAdmission.Load(privateBundle, token);
            if (loaded.Code is ReplayAdmissionCode.IoFailure or ReplayAdmissionCode.Cancelled) code = "infrastructure_failed";
            else if (loaded.Fixture is not { } fixture) code = "input_invalid";
            else
            {
                corpus = fixture.CorpusSha256;
                AcceptedLineage? predecessor = null;
                code = "verified";
                for (var index = 0; index < fixture.Runs.Length; index++)
                {
                    token.ThrowIfCancellationRequested();
                    var run = fixture.Runs[index];
                    var fault = index == options.FaultPhase ? options.Fault : ReplayFault.None;
                    var input = new ReplayChildInput(operation, root, corpus, index, session, key, predecessor, fault);
                    var timeout = fault == ReplayFault.AfterPrepareHang ? TimeSpan.FromSeconds(3) : options.PhaseTimeout;
                    var process = await ReplayProcess.RunAsync(input, timeout, token);
                    var reply = process.Reply;
                    var phaseCode = process.Code;
                    EvaluationOutcome? quality = null;
                    var replyAdmitted = false;
                    using var state = new ReplayState(run, session, root, key);
                    var identity = run.Input.ReviewedIdentity.Runtime;
                    var context = state.Context(identity, identity, index, predecessor?.EnvelopeSha256,
                        ReplayState.Transition(run, index == 0 ? null : fixture.Runs[index - 1]), run.InitialContext);
                    StateResult? accepted = null;
                    if (reply is not null)
                    {
                        options.ObserveReply?.Invoke(input, run, reply);
                        replyAdmitted = AdmitReply(input, run, reply, out quality) && !observations.Any(item => item.Startup == reply.Startup);
                        if (!replyAdmitted) { quality = null; phaseCode = "result_invalid"; }
                        else if (ContainsForbidden(reply, canaries)) phaseCode = "canary_failed";
                        else if (reply.Code != "prepared") phaseCode = reply.Code;
                        else if (!options.Verify(fixture, index, reply)) phaseCode = "assertion_failed";
                        else
                        {
                            token.ThrowIfCancellationRequested();
                            accepted = await state.Service.AcceptAsync(state.Access, predecessor, reply.Prepared!, context, token);
                            phaseCode = accepted.Action == StateAction.Accepted && accepted.Generation == index &&
                                accepted.SessionSha256 == reply.Prepared!.SessionSha256 && accepted.EnvelopeSha256 == reply.Prepared.EnvelopeSha256
                                ? "completed" : "state_failed";
                        }
                    }
                    var succeeded = phaseCode == "completed" && accepted?.Action == StateAction.Accepted;
                    var preserved = succeeded || await PreservedAsync(fixture, index, predecessor, session, root, key);
                    steps.Add(new(run.Input.CaseId, run.Expected.Sha256, run.ConfigurationSha256, Snapshot(fixture, run),
                        run.Input.Transition, phaseCode, succeeded, succeeded ? accepted!.Generation : null,
                        succeeded ? reply!.LogicalSha256 : null, succeeded ? reply!.ProviderSha256 : null,
                        replyAdmitted ? reply!.ModelCalls : 0, replyAdmitted ? reply!.ToolCalls : 0,
                        succeeded ? quality?.Code.ToString() : null,
                        succeeded ? quality?.EvidenceStatus.ToString() : null, succeeded ? quality?.ScenarioStatus.ToString() : null, preserved));
                    if (reply is not null && replyAdmitted)
                        observations.Add(new(index, reply.ProcessId, reply.Startup, session,
                            succeeded ? accepted!.SessionSha256 : null, succeeded ? accepted!.EnvelopeSha256 : null,
                            succeeded ? ReplayState.Now : null, quality?.ExecutionSha256));
                    if (!succeeded) { code = preserved ? phaseCode : "predecessor_changed"; break; }
                    predecessor = new(state.Access.Scope, index, accepted!.SessionSha256!, accepted.EnvelopeSha256!,
                        predecessor?.EnvelopeSha256, ReplayState.Now, ReplayState.Now + RestrictedStateFormat.MaximumRetentionSeconds, true);
                }
                if (code == "verified" && (steps.Count != fixture.Runs.Length || observations.Select(item => item.Startup).Distinct().Count() != steps.Count))
                    code = "result_invalid";
            }
        }
        catch (ReplayRejected) { code = "input_invalid"; }
        catch (OperationCanceledException) { code = "cancelled"; }
        catch { code = "infrastructure_failed"; }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (root is not null)
            {
                try { options.BeforeCleanup?.Invoke(root); }
                catch { code = "infrastructure_failed"; }
                try { cleanup = options.Cleanup(root) ? "cleaned" : "cleanup_failed"; }
                catch { cleanup = "cleanup_failed"; }
                if (cleanup != "cleaned") code = "cleanup_failed";
            }
        }
        var rows = steps.ToImmutable();
        var report = new ReplayReport("deterministic", code, cleanup, corpus, EvaluationSource.Commit, EvaluationSource.Tree,
            EvaluationSource.Clean, operation, rows, observations.ToImmutable(), rows.IsEmpty ? null : ReplayProjection.Steps(rows));
        if (Contains(ReplayWire.Write(report), canaries))
            report = report with { Code = "canary_failed", Steps = [], Observations = [], NormalizedSha256 = null };
        return report;
    }

    internal static bool AdmitIdentity(ReplayChildInput input, ReplayChildReply reply) =>
        reply.Operation == input.Operation && reply.Corpus == input.Corpus && reply.Phase == input.Phase && reply.Session == input.Session &&
        reply.Commit == EvaluationSource.Commit && reply.Tree == EvaluationSource.Tree && reply.SourceClean == EvaluationSource.Clean &&
        reply.ProcessId > 0 && Guid.TryParseExact(reply.Startup, "N", out _);

    internal static bool AdmitReply(ReplayChildInput input, AdmittedReplayRun run, ReplayChildReply reply, out EvaluationOutcome? quality)
    {
        quality = null;
        if (!AdmitIdentity(input, reply) || reply.Requests.IsDefault || reply.Requests.Length > 64 ||
            reply.Requests.Any(bytes => bytes is null || bytes.Length > 1048576) || reply.Requests.Sum(bytes => (long)bytes.Length) > ReplayWire.EvidenceLimit ||
            reply.EnvironmentKeys.IsDefault || reply.EnvironmentBytes is null || reply.EnvironmentBytes.Length > ReplayWire.InputLimit || reply.Evaluation is null ||
            !reply.EnvironmentKeys.Order(StringComparer.Ordinal).SequenceEqual(ReplayProcess.EnvironmentNames.Order(StringComparer.Ordinal)) ||
            reply.ModelCalls is < 0 or > 64 || reply.ToolCalls is < 0 or > 256 ||
            reply.Code is not ("prepared" or "input_invalid" or "infrastructure_failed" or "state_failed" or "session_failed" or "agent_failed" or "provider_failed" or "script_exhausted" or "history_failed" or "cancelled" or "assertion_failed")) return false;
        if (reply.Evaluation.Length != 0)
        {
            quality = EvaluationJson.ReadOutcome(reply.Evaluation);
            if (quality is null || quality.CaseId != run.Input.CaseId || quality.CaseSha256 != run.Expected.Sha256 ||
                quality.CorpusSha256 != input.Corpus || quality.ConfigurationSha256 != run.ConfigurationSha256 ||
                quality.SourceCommit != reply.Commit || quality.SourceTree != reply.Tree || quality.SourceClean != reply.SourceClean || quality.Mode != "deterministic") return false;
        }
        if (reply.Code != "prepared") return reply.Prepared is null;
        if (reply.Prepared is not { } receipt || receipt.Generation != input.Phase || !EvaluationLimits.Hash(receipt.EnvelopeSha256) ||
            reply.Plaintext is not { Length: > 0 and <= 1048576 } || quality is not { ExecutionStatus: EvaluationStatus.Completed } ||
            quality.Code != run.ExpectedCode || !EvaluationLimits.Hash(reply.LogicalSha256) || !EvaluationLimits.Hash(reply.ProviderSha256)) return false;
        using var state = new ReplayState(run, input.Session, input.Root, input.Key);
        var identity = run.Input.ReviewedIdentity.Runtime;
        var context = state.Context(identity, identity, input.Phase, input.Predecessor?.EnvelopeSha256,
            AgentSessionHeadTransition.SameHead, run.InitialContext);
        var admission = AgentSessionStateBoundary.Admit(reply.Plaintext, context.SessionContext);
        return admission.Succeeded && admission.SessionSha256 == receipt.SessionSha256 && admission.Generation == input.Phase &&
            admission.PredecessorEnvelopeSha256 == input.Predecessor?.EnvelopeSha256 &&
            reply.LogicalSha256 == ReplayProjection.Logical(admission.Value!.Artifact, state.Trusted) &&
            reply.ProviderSha256 == ReplayProjection.Provider(reply.Requests);
    }

    private static async Task<bool> PreservedAsync(AdmittedReplayFixture fixture, int index, AcceptedLineage? predecessor,
        string session, string root, byte[] key)
    {
        var previous = fixture.Runs[Math.Max(0, index - 1)];
        using var state = new ReplayState(previous, session, root, key);
        if (predecessor is null)
        {
            var enumeration = await state.Service.EnumerateAsync(state.Access, CancellationToken.None);
            return enumeration.Result.Action == StateAction.Enumerated && enumeration.Candidates.IsEmpty;
        }
        var identity = previous.Input.ReviewedIdentity.Runtime;
        var context = state.Context(identity, identity, predecessor.Generation, predecessor.ExpectedPredecessorEnvelopeSha256,
            AgentSessionHeadTransition.SameHead, previous.InitialContext);
        var restored = await state.Service.RestoreAsync(state.Access,
            new(RestrictedStateLocatorFamily.Current, RestrictedStateRestoreIntent.Explicit, predecessor, context), CancellationToken.None);
        return restored.Result.Action == StateAction.Restored && restored.Session?.SessionSha256 == predecessor.SessionSha256;
    }

    private static string Snapshot(AdmittedReplayFixture fixture, AdmittedReplayRun run) => EvaluationAttempt.Hash("replay-snapshot",
        run.Input.ReviewedIdentity.RepositoryId, run.Input.ReviewedIdentity.ReviewTarget.ToString(System.Globalization.CultureInfo.InvariantCulture),
        run.Input.ReviewedIdentity.BaseSha, run.Input.ReviewedIdentity.HeadSha,
        string.Join(',', run.Input.Repository.Select(entry => entry.Path + ":" + fixture.Files.Single(file => file.Path == entry.File).Sha256)),
        fixture.Files.Single(file => file.Path == run.Input.Diff).Sha256);

    private static bool Contains(byte[] bytes, string[] values)
    {
        var text = Encoding.UTF8.GetString(bytes);
        return values.Any(value => text.Contains(value, StringComparison.Ordinal));
    }

    private static bool ContainsForbidden(ReplayChildReply reply, string[] values) =>
        Contains(reply.EnvironmentBytes, values) || reply.Plaintext is not null && Contains(reply.Plaintext, values) ||
        reply.Requests.Any(bytes => Contains(bytes, values)) || reply.Evaluation is not null && Contains(reply.Evaluation, values);
}

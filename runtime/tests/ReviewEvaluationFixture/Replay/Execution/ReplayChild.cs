using System.Collections;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

internal static class ReplayChild
{
    internal static async Task<int> MainAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var bytes = await ReplayWire.ReadBoundedAsync(Console.OpenStandardInput(), ReplayWire.InputLimit, deadline.Token);
        var input = ReplayWire.Read(bytes, ReplayExecutionJson.Default.ReplayChildInput, ReplayWire.InputLimit);
        if (input is null || !Valid(input)) return 2;
        var reply = await ExecuteAsync(input, deadline.Token);
        if (input.Fault == ReplayFault.AfterPrepareCrash && reply.Prepared is not null) return 9;
        if (input.Fault == ReplayFault.AfterPrepareHang && reply.Prepared is not null)
            await Task.Delay(Timeout.Infinite, deadline.Token);
        if (input.Fault == ReplayFault.AfterPrepareOverflow && reply.Prepared is not null)
        { await Console.OpenStandardOutput().WriteAsync(new byte[ReplayWire.ReplyLimit + 1], deadline.Token); return 0; }
        if (input.Fault == ReplayFault.PartialReply && reply.Prepared is not null)
        { Console.Write("{\"operation\":"); return 0; }
        if (input.Fault == ReplayFault.WrongReply) reply = reply with { Phase = reply.Phase + 1 };
        var output = JsonSerializer.SerializeToUtf8Bytes(reply, ReplayExecutionJson.Default.ReplayChildReply);
        if (output.Length > ReplayWire.ReplyLimit) return 2;
        await Console.OpenStandardOutput().WriteAsync(output, deadline.Token);
        return 0;
    }

    private static bool Valid(ReplayChildInput input) =>
        Guid.TryParseExact(input.Operation, "N", out _) && Guid.TryParseExact(input.Session, "N", out _) &&
        EvaluationLimits.Hash(input.Corpus) && input.Phase is >= 0 and < ReplayLimits.Runs &&
        input.Key is { Length: 32 } && Enum.IsDefined(input.Fault) &&
        input.Root is { Length: > 0 and <= 4096 } && Path.IsPathFullyQualified(input.Root) &&
        Path.GetFileName(input.Root).StartsWith("apr-r5-replay-", StringComparison.Ordinal) &&
        Path.GetFullPath(Directory.GetCurrentDirectory()) == Path.GetFullPath(input.Root) &&
        (File.GetAttributes(input.Root) & FileAttributes.ReparsePoint) == 0 &&
        (input.Phase == 0 ? input.Predecessor is null : input.Predecessor?.Generation == input.Phase - 1);

    internal static async Task<ReplayChildReply> ExecuteAsync(ReplayChildInput input, CancellationToken token)
    {
        var environment = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .OrderBy(entry => (string)entry.Key, StringComparer.Ordinal).ToArray();
        var environmentKeys = environment.Select(entry => (string)entry.Key).ToImmutableArray();
        var environmentBytes = Encoding.UTF8.GetBytes(string.Join('\n', environment.Select(entry => entry.Key + "=" + entry.Value)));
        var startup = Guid.NewGuid().ToString("N");
        AdmittedReplayRun? fixtureRun = null;
        ReplayChildReply Reply(string code, PreparedStateReceipt? receipt = null, EvaluationOutcome? evaluation = null,
            AgentSessionArtifact? artifact = null, ReplayTransport? transport = null, int tools = 0) => new(
                input.Operation, input.Corpus, input.Phase, input.Session, EvaluationSource.Commit, EvaluationSource.Tree,
                EvaluationSource.Clean, Environment.ProcessId, startup, code, receipt,
                evaluation is null ? [] : EvaluationJson.Write(evaluation),
                artifact is null ? null : ReplayProjection.Logical(artifact, fixtureRun!.CreateTrustedRequest(ReplayState.Build)),
                transport is null ? null : ReplayProjection.Provider(transport.Requests), transport?.Requests.Count ?? 0, tools,
                artifact?.Plaintext, transport?.Requests.ToImmutableArray() ?? [], environmentKeys, environmentBytes);

        // Captured bundle is re-admitted in every fresh process; only this run supplies model/tool inputs.
        var loaded = ReplayAdmission.Load(Path.Combine(input.Root, "bundle"), token);
        if (loaded.Code is ReplayAdmissionCode.IoFailure or ReplayAdmissionCode.Cancelled) return Reply("infrastructure_failed");
        if (loaded.Fixture is not { } fixture || fixture.CorpusSha256 != input.Corpus || input.Phase >= fixture.Runs.Length)
            return Reply("input_invalid");
        fixtureRun = fixture.Runs[input.Phase];
        var priorRun = input.Phase == 0 ? null : fixture.Runs[input.Phase - 1];
        using var state = new ReplayState(fixtureRun, input.Session, input.Root, input.Key);
        var identity = fixtureRun.Input.ReviewedIdentity.Runtime;
        var transition = ReplayState.Transition(fixtureRun, priorRun);
        var producer = priorRun?.Input.ReviewedIdentity.Runtime ?? identity;
        var context = state.Context(producer, identity, input.Predecessor?.Generation ?? 0,
            input.Predecessor?.ExpectedPredecessorEnvelopeSha256, transition, fixtureRun.InitialContext);
        if (input.Fault == ReplayFault.WrongScope)
            context = context with { SessionContext = context.SessionContext with { SessionId = "wrong-session" } };
        if (input.Fault == ReplayFault.WrongHead)
            context = context with { SessionContext = context.SessionContext with
            { CurrentReviewedIdentity = identity with { HeadSha = new string('f', 40) }, Transition = AgentSessionHeadTransition.SameHead } };
        var restored = await state.Service.RestoreAsync(state.Access,
            new(input.Phase == 0 ? RestrictedStateLocatorFamily.Absent : RestrictedStateLocatorFamily.Current,
                input.Phase == 0 ? RestrictedStateRestoreIntent.Automatic : RestrictedStateRestoreIntent.Explicit,
                input.Predecessor, context), token);
        AgentRunRequest request;
        AgentSessionPredecessor? predecessor = null;
        if (input.Phase == 0)
        {
            if (restored.Result.Action != StateAction.Bootstrap ||
                !AgentStableRequestMaterializer.TryMaterialize(state.Trusted, null, out var stable)) return Reply("state_failed");
            request = new(identity, stable!.StablePlan, input.Session,
                [.. stable.ControlMessages, new("user", [new ProjectTextContent(fixtureRun.InitialContext)])]);
        }
        else
        {
            if (restored.Result.Action != StateAction.Restored || restored.Session?.Value is not { } admitted) return Reply("state_failed");
            request = admitted.RunRequest;
            predecessor = new(admitted.Artifact.Plaintext, input.Predecessor!.SessionSha256, input.Predecessor.EnvelopeSha256,
                input.Predecessor.Generation, producer.BaseSha, producer.HeadSha, input.Predecessor.ExpectedPredecessorEnvelopeSha256);
            if (input.Fault == ReplayFault.MissingHistory)
            {
                AgentStableRequestMaterializer.TryMaterialize(state.Trusted, null, out var stable);
                request = new(identity, stable!.StablePlan, input.Session,
                    [.. stable.ControlMessages, new("user", [new ProjectTextContent(fixtureRun.InitialContext)])]);
            }
            if (request.Continuation is { Items.Length: > 0 } continuation)
            {
                if (input.Fault == ReplayFault.ChangedContinuation)
                    request = request with { Continuation = continuation with
                    { Items = [continuation.Items[0] with { Readable = "altered" }, .. continuation.Items.Skip(1)] } };
                if (input.Fault == ReplayFault.MissingContinuation)
                    request = request with { Continuation = continuation with { Items = [.. continuation.Items.Skip(1)] } };
                if (input.Fault == ReplayFault.WrongContinuationPosition)
                    request = request with { Continuation = continuation with
                    { Items = [continuation.Items[0] with { ContentPosition = 1 }, .. continuation.Items.Skip(1)] } };
            }
        }
        var descriptor = new EvaluationRunInput(fixtureRun.Input.Id, "deterministic", EvaluationSource.Commit,
            EvaluationSource.Tree, EvaluationSource.Clean, fixtureRun.ProviderConfigurationSha256);
        var attempt = EvaluationAttempt.Admit(state.Trusted, descriptor);
        using var transport = new ReplayTransport(fixtureRun.Script, input.Fault);
        Directory.CreateDirectory(Path.Combine(input.Root, "reviewed"));
        var snapshot = fixtureRun.CreateSnapshot(Path.Combine(input.Root, "reviewed"));
        var client = DeepSeekChatBackend.CreateClient(new(state.Trusted.ProviderId, state.Trusted.ModelId,
            state.Trusted.AdapterId, input.Session), transport);
        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (input.Fault == ReplayFault.Cancelled) await runCancellation.CancelAsync();
        var outcome = await new AgentLoop(client, new SnapshotToolExecutor(snapshot, fixtureRun.CreateFileAccess(snapshot))).RunAsync(request, runCancellation.Token);
        var tools = outcome.Events.OfType<AgentToolResultEvent>().Count();
        if (!outcome.Succeeded)
            return Reply(outcome.Diagnostic?.Code == AgentFailureCodes.Cancelled ? "cancelled" : transport.FailureCode ?? "agent_failed",
                evaluation: EvaluationScorer.Failure(fixtureRun.Expected, EvaluationFailure.FromAgentOutcome(outcome), attempt), transport: transport, tools: tools);
        var buildInput = new AgentSessionBuildInput(request, outcome, state.Trusted, request.InitialMessages.Length - 1,
            DeepSeekReasoningContinuationCodec.Instance, predecessor, transition);
        var built = AgentSessionBuilder.Build(buildInput);
        if (!built.Succeeded || built.Artifact is null)
            return Reply("session_failed", evaluation: EvaluationScorer.Failure(fixtureRun.Expected,
                EvaluationFailure.FromSessionBuild(built), attempt), transport: transport, tools: tools);
        var scored = EvaluationScorer.Evaluate(fixtureRun.Expected, EvaluationSubject.Admit(buildInput, descriptor));
        if (transport.Consumed != fixtureRun.Script.Turns.Length || transport.Failed || scored.Code != fixtureRun.ExpectedCode)
            return Reply("assertion_failed", evaluation: scored, artifact: built.Artifact, transport: transport, tools: tools);
        var candidate = built.Artifact.Plaintext;
        if (input.Fault == ReplayFault.NonCompleted)
        {
            var runs = built.Artifact.Document.CompletedRuns;
            var incomplete = runs[^1] with { Records = runs[^1].Records.RemoveAt(runs[^1].Records.Length - 1) };
            if (!AgentSessionCodec.TryWrite(built.Artifact.Document with { CompletedRuns = runs.SetItem(runs.Length - 1, incomplete) },
                out var invalid, out _) || invalid is null) return Reply("session_failed");
            candidate = invalid.Plaintext;
        }
        var prepareContext = state.Context(identity, identity, input.Phase, input.Predecessor?.EnvelopeSha256,
            transition, fixtureRun.InitialContext);
        var prepared = await state.Service.PrepareAsync(state.Access, new(input.Predecessor, candidate, prepareContext), token);
        return Reply(prepared.Result.Action == StateAction.Prepared ? "prepared" : "state_failed", prepared.Receipt,
            scored, built.Artifact, transport, tools);
    }
}

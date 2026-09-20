using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Prefix;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Growth.Profiles;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Histories;

internal sealed record HistoryStaged(int Phase, int ProcessId, string Startup, bool Admitted,
    bool RestoredMatch, bool WireMatch, PrefixComparison Comparison, HistoryCapture Capture);

internal static class HistoryBridge
{
    // Independent supervisor restore of the already accepted predecessor, before child execution.
    internal static async Task<(AdmittedReplayRun Run, PrefixObservation Expected)> ExpectedAsync(ReplayChildInput input, CancellationToken token)
    {
        var fixture = ReplayAdmission.Load(Path.Combine(input.Root, "bundle"), token).Fixture
            ?? throw new InvalidOperationException("history_input_invalid");
        AdmittedReplayRun Run(int phase) => input.GrowthProfile is { } profile
            ? GrowthProfiles.Run(fixture, profile, phase, input.GrowthSchedule!.At(phase), input.GrowthSchedule)
            : fixture.Runs[phase];
        var run = Run(input.Phase);
        using var state = new ReplayState(run, input.Session, input.Root, input.Key);
        var identity = run.Input.ReviewedIdentity.Runtime;
        if (input.Predecessor is null)
        {
            if (!AgentStableRequestMaterializer.TryMaterialize(state.Trusted, null, out var stable))
                throw new InvalidOperationException("history_input_invalid");
            var request = new Agent.Loop.AgentRunRequest(identity, stable!.StablePlan, input.Session,
                [.. stable.ControlMessages, new("user", [new ProjectTextContent(run.InitialContext)])]);
            return (run, PrefixMeasurement.Observe(PrefixBoundary.Bootstrap(state.Trusted, request), HistoryCapture.Request(request)));
        }
        var previous = Run(input.Phase - 1);
        var context = state.Context(previous.Input.ReviewedIdentity.Runtime, identity, input.Predecessor.Generation,
            input.Predecessor.ExpectedPredecessorEnvelopeSha256, ReplayState.Transition(run, previous), run.InitialContext);
        var restored = await state.Service.RestoreAsync(state.Access,
            new(RestrictedStateLocatorFamily.Current, RestrictedStateRestoreIntent.Explicit, input.Predecessor, context), token);
        if (restored.Result.Action != StateAction.Restored || restored.Session?.Value is not { } admitted ||
            restored.Session.SessionSha256 != input.Predecessor.SessionSha256)
            throw new InvalidOperationException("history_predecessor_unavailable");
        var boundary = PrefixBoundary.Restored(state.Trusted, AgentSessionRestoreResult.Success(admitted.RunRequest, admitted.Artifact));
        return (run, PrefixMeasurement.Observe(boundary, HistoryCapture.Request(admitted.RunRequest)));
    }

    internal static HistoryStaged Stage(ReplayChildInput input, AdmittedReplayRun run, PrefixObservation expected, ReplayChildReply reply)
    {
        var admitted = ReplayRunner.AdmitReply(input, run, reply, out _);
        var capture = admitted ? reply.PrefixHistory! : HistoryCapture.Unavailable;
        var compared = capture.Calls.FirstOrDefault() is { } first ? expected.Compare(first) : new PrefixComparison("unavailable", null, null);
        var wire = admitted && reply.Requests.Length > 0 && reply.Requests.Length <= capture.Calls.Length &&
            reply.Requests.Select((bytes, index) => capture.Calls[index]?.Provider.Whole == WireSegment(bytes)).All(x => x);
        return new(input.Phase, reply.ProcessId, reply.Startup, admitted, capture.Baseline == expected,
            wire, compared, capture);
    }

    // Independent byte oracle for P1's single, length-framed whole-body diagnostic segment.
    // Continuity still uses Control/History/Settings; this only binds observations to actual transport.
    private static PrefixSegment WireSegment(byte[] body)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes("apr.r6.prefix.provider.whole\0"));
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(size, body.Length);
        hash.AppendData(size); hash.AppendData(body);
        return new(Convert.ToHexStringLower(hash.GetHashAndReset()), body.Length, 1);
    }
}

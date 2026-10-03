using System.Collections.Immutable;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Session;

namespace AgenticPrReview.Runtime.Tests.Agent.Session;

public sealed partial class AgentSessionRoundTripTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task CurrentWireRecordCountIsBoundedBeforeTypedAllocation(int delta)
    {
        var built = await BuildGenerationAsync(Trusted(), null, "wire-count", "finish", reasoning: false);
        var run = built.Artifact.Document.CompletedRuns[0];
        var count = AgentLimits.SessionRecords + delta;
        var document = built.Artifact.Document with
        {
            CompletedRuns = [run with
            {
                Records = Enumerable.Repeat(run.Records[0], count).ToImmutableArray(),
                Continuation = run.Continuation with { Items = [] },
            }],
        };
        Assert.True(AgentSessionCodec.TryWrite(document, out var wire, out var writeFailure), writeFailure);
        Assert.True(AgentSessionCodec.TryParseEnvelope(wire!.Plaintext, out var parsed, out var parseFailure), parseFailure);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var converted = AgentSessionCodec.TryConvertEnvelope(parsed!, out var artifact, out var code);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(delta <= 0, converted);
        if (delta > 0)
        {
            Assert.Equal(AgentSessionCodes.RecordInvalid, code);
            Assert.Null(artifact);
            Assert.InRange(allocated, 0, 256 * 1024);
        }
        else
        {
            Assert.Equal(count, artifact!.Document.CompletedRuns[0].Records.Length);
            // Repeated IDs are deliberate wire-only fixtures, never accepted history.
            Assert.False(AgentSessionValidation.TryValidateRecords(artifact.Document, SyntheticContinuationCodec.Instance, out _));
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task CurrentWireCombinedCountIncludesContinuationItems(int delta)
    {
        var built = await BuildGenerationAsync(Trusted(), null, "combined-count", "finish", reasoning: true);
        var run = built.Artifact.Document.CompletedRuns[0];
        Assert.NotEmpty(run.Continuation.Items);
        var document = built.Artifact.Document with
        {
            CompletedRuns = [run with
            {
                Records = Enumerable.Repeat(run.Records[0], AgentLimits.SessionRecords + delta - run.Continuation.Items.Length).ToImmutableArray(),
            }],
        };
        Assert.True(AgentSessionCodec.TryWrite(document, out var wire, out _));
        Assert.Equal(delta <= 0, AgentSessionCodec.TryParse(wire!.Plaintext, out var parsed, out var code));
        if (delta > 0) Assert.Equal(AgentSessionCodes.RecordInvalid, code);
        else Assert.Equal(AgentLimits.SessionRecords + delta,
            parsed!.Document.CompletedRuns.Sum(r => r.Records.Length + r.Continuation.Items.Length));
    }
}

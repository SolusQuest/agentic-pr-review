using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Session;

namespace AgenticPrReview.Runtime.Tests.Agent.Session;

public sealed partial class AgentSessionRoundTripTests
{
    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public async Task CurrentWireTotalPartsAreBoundedBeforeTypedAllocation(int delta, bool mixedAcrossRuns)
    {
        var wire = await CreatePartBoundaryWireAsync(AgentLimits.PartsTotal + delta, mixedAcrossRuns);
        Assert.True(AgentSessionCodec.TryParseEnvelope(wire.Plaintext, out var parsed, out _));
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
            // Independently count the actual reconstruction, including the slot's
            // continuation item exactly once and all four scalar record shapes.
            Assert.True(AgentSessionRequestReconstruction.TryReconstructHistory(artifact!.Document,
                SyntheticContinuationCodec.Instance, 0, out var history, out var continuation, out var failure), failure);
            Assert.Equal(AgentLimits.PartsTotal + delta,
                history!.Sum(message => message.Contents.Length) + continuation!.Items.Length);
            // These are wire/reconstruction fixtures, not a claim that a single
            // accepted run can make this many model calls or skip its grammar.
            Assert.False(AgentSessionValidation.TryValidateRecords(artifact.Document, SyntheticContinuationCodec.Instance, out _));
        }
    }

    [Fact]
    public async Task TotalPartPreflightAcrossRunsPrecedesMalformedContinuationGrammar()
    {
        var wire = await CreatePartBoundaryWireAsync(AgentLimits.PartsTotal + 1, mixedAcrossRuns: true);
        var root = JsonNode.Parse(wire.Plaintext.AsSpan(AgentSessionFormat.FramingBytes))!;
        root["completed_runs"]![0]!["continuation"]!["items"] = "malformed";
        Assert.True(AgentSessionCodec.TryParseEnvelope(Frame(Encoding.UTF8.GetBytes(root.ToJsonString())), out var parsed, out _));
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.False(AgentSessionCodec.TryValidateRecordGrammarBeforeContinuation(parsed!, out var code));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(AgentSessionCodes.RecordInvalid, code);
        Assert.InRange(allocated, 0, 256 * 1024);
    }

    private static async Task<AgentSessionArtifact> CreatePartBoundaryWireAsync(int parts, bool mixedAcrossRuns)
    {
        var built = await BuildGenerationAsync(Trusted(), null, "part-count", "finish", reasoning: mixedAcrossRuns);
        var run = built.Artifact.Document.CompletedRuns[0];
        var assistant = run.Records.OfType<AgentSessionAssistantMessageRecord>().Single();
        var records = new List<AgentSessionRecord>();
        if (mixedAcrossRuns)
        {
            records.Add(run.Records.OfType<AgentSessionReviewContextRecord>().Single());
            records.Add(new AgentSessionToolResultRecord("result", 1, "message", "call", "read_file", "observation", "{}", "tool", "framing", "restricted"));
            records.Add(new AgentSessionToolErrorRecord("error", 2, "message", "call", "read_file", "{}", "tool", "framing", "restricted"));
            records.Add(run.Records.OfType<AgentSessionReviewOutcomeRecord>().Single());
            parts -= records.Count;
        }
        var ordinal = 0;
        while (parts > 0)
        {
            var count = Math.Min(parts, AgentLimits.PartsPerMessage);
            var contents = Enumerable.Range(0, count)
                .Select(position => (AgentSessionAssistantContent)new AgentSessionTextContent(position, "x")).ToImmutableArray();
            if (mixedAcrossRuns && ordinal == 0)
            {
                var slot = assistant.Contents.OfType<AgentSessionContinuationSlotContent>().Single();
                contents = contents.SetItem(slot.ContentPosition, slot);
            }
            records.Add(assistant with { Id = ordinal == 0 ? assistant.Id : "parts-" + ordinal, Contents = contents });
            parts -= count;
            ordinal++;
        }
        var split = records.Count / 2;
        var runs = mixedAcrossRuns
            ? ImmutableArray.Create(run with { Records = records.Take(split).ToImmutableArray() },
                run with { RunOrdinal = 1, Records = records.Skip(split).ToImmutableArray(), Continuation = run.Continuation with { Items = [] } })
            : [run with { Records = records.ToImmutableArray() }];
        Assert.True(AgentSessionCodec.TryWrite(built.Artifact.Document with { CompletedRuns = runs }, out var wire, out var failure), failure);
        return wire!;
    }

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

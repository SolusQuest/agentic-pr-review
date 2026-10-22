using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.Serialization;

namespace AgenticPrReview.Runtime.Tests.Host.Action.Contracts;

public sealed class ActionHostAccountingTests
{
    [Fact]
    public void SharedSemanticCorpusRetainsDomainRejectionsAndExactFinalProjection()
    {
        using var cases = JsonDocument.Parse(File.ReadAllBytes(FixturePath()));
        using var dispositions = JsonDocument.Parse(File.ReadAllBytes(Path.Join(Path.GetDirectoryName(FixturePath()), "final-projection-dispositions.json")));
        Assert.Equal(107, dispositions.RootElement.EnumerateObject().Count());
        var validCount = 0; var domainRejections = 0; var retiredInboundShapes = 0;
        foreach (var item in cases.RootElement.EnumerateArray())
        {
            var name = item.GetProperty("name").GetString();
            var valid = item.GetProperty("valid").GetBoolean();
            var disposition = dispositions.RootElement.GetProperty(name!).GetString();
            if (!CompletionFixtureInputs.Representable(item.GetProperty("document"), out var result))
            {
                // Old missing/lexical/closed-enum input forms cannot be supplied to
                // the typed producer. P1 transport has independent shape negatives.
                Assert.False(valid, name);
                Assert.True(disposition is "typed-producer-guarantee" or "retired-inbound-shape", name);
                retiredInboundShapes++; continue;
            }
            Assert.Equal(valid ? "canonical-projection" : "domain-rejection", disposition);
            Assert.True((result is not null) == valid, name);
            if (!valid) { domainRejections++; continue; }
            validCount++;
            Assert.True(ActionHostJsonCodec.TryWriteCompletion(result, out var wire), name);
            FinalProjectionAssertions.Matches(result!, wire);
            using var projected = JsonDocument.Parse(wire);
            foreach (var field in item.GetProperty("document").GetProperty("accounting").EnumerateObject())
                Assert.Equal(field.Value.GetRawText(), projected.RootElement.GetProperty("outputs")
                    .GetProperty(field.Name.Replace('_', '-')).GetRawText());
            Assert.DoesNotContain("CANARY", Encoding.UTF8.GetString(wire));
        }
        Assert.Equal(107, cases.RootElement.GetArrayLength());
        Assert.Equal(31, validCount);
        Assert.Equal(46, domainRejections);
        Assert.Equal(30, retiredInboundShapes);
        Assert.Equal(76, domainRejections + retiredInboundShapes);
    }

    [Fact]
    public void FinalProjectionHasRequiredKeysEvenWhenNumericalFactsAreUnavailable()
    {
        using var cases = JsonDocument.Parse(File.ReadAllBytes(FixturePath()));
        foreach (var item in cases.RootElement.EnumerateArray().Where(c => c.GetProperty("valid").GetBoolean()))
        {
            Assert.True(CompletionFixtureInputs.Representable(item.GetProperty("document"), out var completion));
            Assert.True(ActionHostJsonCodec.TryWriteCompletion(completion, out var bytes));
            FinalProjectionAssertions.Read(bytes);
        }
    }

    [Fact]
    public void CheckedNodeFixturesAreTheActualCanonicalCSharpProducerBytes()
    {
        using var source = JsonDocument.Parse(File.ReadAllBytes(FixturePath()));
        using var fixtures = JsonDocument.Parse(File.ReadAllBytes(Path.Join(Path.GetDirectoryName(FixturePath()), "final-projection.json")));
        var expected = fixtures.RootElement.EnumerateArray().ToDictionary(
            row => row.GetProperty("name").GetString()!, row => row.GetProperty("document"));
        void Check(string name, ActionHostCompletion completion)
        {
            Assert.True(ActionHostJsonCodec.TryWriteCompletion(completion, out var bytes));
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected[name].GetRawText()), JsonNode.Parse(bytes)), name);
            expected.Remove(name);
        }
        foreach (var row in source.RootElement.EnumerateArray().Where(row => row.GetProperty("valid").GetBoolean()))
        {
            Assert.True(CompletionFixtureInputs.Representable(row.GetProperty("document"), out var completion));
            Check(row.GetProperty("name").GetString()!, completion!);
        }
        foreach (var status in Enum.GetValues<ActionHostStatus>())
        {
            var reviewed = status is ActionHostStatus.Reviewed or ActionHostStatus.ReviewedWithInlineWarnings;
            var skipped = status is ActionHostStatus.SkippedUntrustedEvent or ActionHostStatus.SkippedFork or ActionHostStatus.SkippedDraft or ActionHostStatus.SkippedClosed;
            var disposition = reviewed ? ActionHostStateDisposition.Accepted : skipped ? ActionHostStateDisposition.NotAccessed :
                status == ActionHostStatus.StateConflict ? ActionHostStateDisposition.Conflict : ActionHostStateDisposition.NotCommitted;
            Assert.True(ActionHostStepSummary.TryCreate(reviewed ? new string('c', 40) : null,
                reviewed ? "https://github.com/SolusQuest/agentic-pr-review/pull/163" : null, reviewed ? 0 : null,
                disposition, out var summary));
            Assert.True(ActionHostStatusRules.TryClassify(status, out _, out _, out var code));
            var annotations = new List<ActionHostAnnotation>();
            if (code is {} value) { Assert.True(ActionHostAnnotation.TryCreate(value, out var annotation)); annotations.Add(annotation!); }
            Assert.True(ActionHostCompletion.TryCreate("r4-h1", status, summary, annotations, out var completion));
            Check("status-" + FinalProjectionAssertions.Token(status), completion!);
        }
        Assert.Empty(expected);
    }

    private static string FixturePath([CallerFilePath] string path = "") =>
        Path.Join(Path.GetDirectoryName(path), "Fixtures", "provider-accounting.json");
}

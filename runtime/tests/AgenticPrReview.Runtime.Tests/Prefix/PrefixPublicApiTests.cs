using System;
using System.Linq;
using System.Text.Json;
using AgenticPrReview.Runtime.Prefix;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Prefix;

public sealed class PrefixPublicApiTests
{
    [Fact]
    public void DefaultJsonElementEnvelopeYieldsTypedFailure()
    {
        var outcome = CacheContractDigests.ComputeTemplateId(default);
        Assert.Null(outcome.Digest);
        Assert.Equal("prefix_envelope_invalid", Assert.Single(outcome.Diagnostics).Code);
    }

    [Fact]
    public void DisposedDocumentElementYieldsTypedFailure()
    {
        JsonElement disposed;
        using (var doc = JsonDocument.Parse("""{"schemaVersion":1,"templateVersion":1,"definition":{}}"""))
        {
            disposed = doc.RootElement;
        }

        var outcome = CacheContractDigests.ComputeTemplateId(disposed);
        Assert.Null(outcome.Digest);
        Assert.Equal("prefix_envelope_invalid", Assert.Single(outcome.Diagnostics).Code);
    }

    [Fact]
    public void InteractionDeriverAcceptsOrdinalEndpoints()
    {
        Assert.NotNull(
            InteractionIdDeriver
                .Derive(PredecessorLedgerReference.Bootstrap.Instance, new string('e', 64), new string('7', 40), 0)
                .InteractionId);
        Assert.NotNull(
            InteractionIdDeriver
                .Derive(PredecessorLedgerReference.Bootstrap.Instance, new string('e', 64), new string('7', 40), 1_000_000)
                .InteractionId);
    }

    [Fact]
    public void InteractionDeriverRejectsBootstrapSpelledAsHash()
    {
        var outcome = InteractionIdDeriver.Derive(
            new PredecessorLedgerReference.LedgerHash("bootstrap"),
            new string('e', 64),
            new string('7', 40),
            0);
        Assert.Null(outcome.InteractionId);
        Assert.Equal("prefix_digest_invalid", Assert.Single(outcome.Diagnostics).Code);
    }

    [Fact]
    public void CanonicalNamespaceDoesNotDependOnLedgerOrPrefix()
    {
        var canonicalDir = Path.Combine(FindRepoRoot(), "runtime", "src", "AgenticPrReview.Runtime", "Canonical");
        foreach (var text in Directory.GetFiles(canonicalDir, "*.cs").Select(File.ReadAllText))
        {
            Assert.DoesNotContain("AgenticPrReview.Runtime.Ledger", text);
            Assert.DoesNotContain("AgenticPrReview.Runtime.Prefix", text);
        }
    }

    [Fact]
    public void LedgerCanonicalizerUsesExtractedCanonicalNamespace()
    {
        var ledgerFile = Path.Combine(
            FindRepoRoot(), "runtime", "src", "AgenticPrReview.Runtime", "Ledger", "LedgerCanonicalizer.cs");
        Assert.Contains("using AgenticPrReview.Runtime.Canonical;", File.ReadAllText(ledgerFile));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null, "repo root not found");
        return dir!.FullName;
    }
}

using System.Collections.Immutable;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.Transactions;
using AgenticPrReview.Runtime.Tests.Host.Publishing.Rendering;

namespace AgenticPrReview.Runtime.Tests.Host.State.Transactions;

public sealed class RetainedStateTransactionContractTests
{
    [Fact]
    public void CandidateRetentionUsesExactUnifiedBoundaries()
    {
        const long now = 1_700_000_000;

        Assert.True(RetainedStateRetention.TryCandidate(
            now,
            out var logical,
            out var platform));

        Assert.Equal(
            now + StateRetentionRequirements.LogicalWindowSeconds,
            logical);
        Assert.Equal(
            now + StateRetentionRequirements.ScopedPlatformRequestSeconds,
            platform);
        Assert.True(RetainedStateRetention.CoversPreSticky(
            logical + StateRetentionRequirements.PreStickyBudgetSeconds,
            now));
        Assert.False(RetainedStateRetention.CoversPreSticky(
            logical + StateRetentionRequirements.PreStickyBudgetSeconds - 1,
            now));
    }

    [Fact]
    public void AcceptanceReceiptRequiresTwoLogicalWindows()
    {
        const long acceptedAt = 1_700_000_000;

        Assert.True(RetainedStateRetention.TryAcceptance(
            acceptedAt,
            out var logical,
            out var platform));

        Assert.Equal(
            acceptedAt + StateRetentionRequirements.LogicalWindowSeconds,
            logical);
        Assert.Equal(
            acceptedAt +
                2 * StateRetentionRequirements.LogicalWindowSeconds,
            platform);
    }

    [Fact]
    public void RetentionOverflowFailsClosed()
    {
        Assert.False(RetainedStateRetention.TryCandidate(
            RestrictedStateFormat.MaximumUnixSeconds,
            out _,
            out _));
        Assert.False(RetainedStateRetention.TryAcceptance(
            RestrictedStateFormat.MaximumUnixSeconds,
            out _,
            out _));
        Assert.False(RetainedStateRetention.TryOpaque(
            RestrictedStateFormat.MaximumUnixSeconds,
            RestrictedStateFormat.MaximumUnixSeconds,
            out _));
    }

    [Fact]
    public void CleanupRecordIsCanonicalAndOperationBound()
    {
        var first = Metadata("candidate-name", "2");
        var second = Metadata("acceptance-name", "1");
        Assert.True(RetainedStateCleanupRecordCodec.TryCreate(
            new string('1', 64),
            new string('2', 64),
            new string('3', 64),
            new string('4', 64),
            new string('5', 64),
            [first, second],
            out var value));
        Assert.NotNull(value);
        Assert.Equal(second, value!.Targets[0]);
        Assert.Equal(first, value.Targets[1]);
        Assert.True(RetainedStateCleanupRecordCodec.TryEncode(
            value,
            out var bytes));
        Assert.True(RetainedStateCleanupRecordCodec.TryDecode(
            bytes,
            out var decoded));
        Assert.NotNull(decoded);
        Assert.Equal(value.TerminalAcceptanceIdentity,
            decoded!.TerminalAcceptanceIdentity);
        Assert.Equal(value.BaseScopeDigest, decoded.BaseScopeDigest);
        Assert.Equal(value.Epoch, decoded.Epoch);
        Assert.Equal(value.SessionId, decoded.SessionId);
        Assert.Equal(value.PreCleanupInventoryDigest,
            decoded.PreCleanupInventoryDigest);
        Assert.Equal(value.OperationIdentity, decoded.OperationIdentity);
        Assert.True(value.Targets.SequenceEqual(decoded.Targets));

        var tampered = bytes.ToArray();
        tampered[^1] ^= 1;
        Assert.False(RetainedStateCleanupRecordCodec.TryDecode(
            tampered,
            out _));
    }

    [Fact]
    public void PublicationCapabilityCarriesExactOutcomeAndRendering()
    {
        var outcome = R4PublicationTestData.Outcome(summary: "exact A");
        Assert.True(R4PreparedPublication.TryCreate(
            outcome,
            R4PublicationTestData.Scope,
            out var prepared));
        Assert.NotNull(prepared);
        Assert.True(prepared!.TryProject(
            out var projectedOutcome,
            out var rendered,
            out var scope));
        Assert.Same(outcome, projectedOutcome);
        Assert.Equal(R4PublicationTestData.Scope, scope);
        Assert.Contains("exact A", rendered!.Comment, StringComparison.Ordinal);
        Assert.Equal("[PRIVATE]", prepared.ToString());
    }

    private static OpaqueStoreObjectMetadata Metadata(
        string name,
        string id) =>
        new(
            new OpaqueStoreObjectReference(
                new OpaqueStoreName(name),
                new OpaqueStoreObjectId(id)),
            new OpaqueStoreProducingRun("900", 2),
            new OpaqueStoreArchiveDigest(new string('a', 64)),
            new OpaqueStoreEncryptedObjectDigest(new string('b', 64)),
            1_800_000_000,
            100);
}

using AgenticPrReview.Runtime.ActionHostVerifierFixture;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action.TrustedProof;

public sealed class SyntheticTransactionPartitionTests
{
    [Fact]
    public void CompleteProductionStoreLifecycleDerivesOneExactPartition()
    {
        var root = SyntheticPartitionProductionVectors.CreateLifecycleRoot();
        try
        {
            var partition = SyntheticTransactionPartition.Derive(
                root, SyntheticPartitionProductionVectors.Binding,
                SyntheticPartitionProductionVectors.StateKey);

            Assert.NotNull(partition);
            Assert.Equal(35, partition!["total_record_count"]!.GetValue<int>());
            Assert.Equal(7, partition["live_anchor_count"]!.GetValue<int>());
            Assert.Equal(28,
                partition["transient_record_count"]!.GetValue<int>());
            Assert.Equal(13,
                partition["internally_reconciled_count"]!.GetValue<int>());
            Assert.Equal(15,
                partition["cleanup_self_deleted_count"]!.GetValue<int>());
            Assert.Equal(7,
                partition["live_anchor_object_identities"]!.AsArray().Count);
            Assert.All(
                partition["live_anchor_object_identities"]!.AsArray(),
                identity => Assert.Equal(64, identity!.GetValue<string>().Length));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MissingLifecycleRecordFailsClosedInsteadOfAcceptingFiveCounts()
    {
        var root = SyntheticPartitionProductionVectors.CreateLifecycleRoot();
        try
        {
            var path = Path.Join(
                root, "stale-head", "state-lifecycle-evidence.tsv");
            File.WriteAllLines(path, File.ReadAllLines(path).Skip(1));

            Assert.Null(SyntheticTransactionPartition.Derive(
                root, SyntheticPartitionProductionVectors.Binding,
                SyntheticPartitionProductionVectors.StateKey));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

}

using System.Security.Cryptography;
using AgenticPrReview.Runtime.ActionHostVerifierFixture;
using AgenticPrReview.Runtime.Host.State.Lineage;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action.TrustedProof;

public sealed class SyntheticSemanticTransactionPartitionTests
{
    [Fact]
    public void ProductionCodecRolesAreIndependentOfFreshEncryptionNonces()
    {
        var firstRoot = SyntheticPartitionProductionVectors.CreateLifecycleRoot();
        var secondRoot = SyntheticPartitionProductionVectors.CreateLifecycleRoot();
        try
        {
            var first = SyntheticTransactionPartition.Derive(
                firstRoot, SyntheticPartitionProductionVectors.Binding,
                SyntheticPartitionProductionVectors.StateKey);
            var second = SyntheticTransactionPartition.Derive(
                secondRoot, SyntheticPartitionProductionVectors.Binding,
                SyntheticPartitionProductionVectors.StateKey);

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.Equal(first!.ToJsonString(), second!.ToJsonString());
            Assert.Equal(35, first["total_record_count"]!.GetValue<int>());
            Assert.Equal(7, first["live_anchor_count"]!.GetValue<int>());
            Assert.Equal(28, first["transient_record_count"]!.GetValue<int>());
            Assert.Equal(13,
                first["internally_reconciled_count"]!.GetValue<int>());
            Assert.Equal(15,
                first["cleanup_self_deleted_count"]!.GetValue<int>());
            SyntheticPartitionProductionVectors.AssertStableIdentities(
                first, "live_anchor_object_identities", 7);
            SyntheticPartitionProductionVectors.AssertStableIdentities(
                first, "internally_reconciled_object_identities", 13);
            SyntheticPartitionProductionVectors.AssertStableIdentities(
                first, "cleanup_self_deleted_object_identities", 15);
        }
        finally
        {
            Directory.Delete(firstRoot, recursive: true);
            Directory.Delete(secondRoot, recursive: true);
        }
    }

    [Fact]
    public void AuthenticatedEnvelopeMutationFailsClosed()
    {
        var root = SyntheticPartitionProductionVectors.CreateLifecycleRoot();
        try
        {
            var path = Path.Join(
                root, "dispatch-bootstrap", "state-lifecycle-evidence.tsv");
            var lines = File.ReadAllLines(path);
            var index = Array.FindIndex(lines, line => line.StartsWith(
                "upload\topaque-lineage-normal\t", StringComparison.Ordinal));
            Assert.True(index >= 0);
            var fields = lines[index].Split('\t');
            var envelope = Convert.FromBase64String(fields[13]);
            envelope[^1] ^= 1;
            var digest = Convert.ToHexString(SHA256.HashData(envelope))
                .ToLowerInvariant();
            fields[2] = LineageCryptography.CorrelationId(envelope);
            fields[3] = digest;
            fields[8] = digest;
            fields[13] = Convert.ToBase64String(envelope);
            lines[index] = string.Join('\t', fields);
            File.WriteAllLines(path, lines);

            Assert.Null(SyntheticTransactionPartition.Derive(
                root, SyntheticPartitionProductionVectors.Binding,
                SyntheticPartitionProductionVectors.StateKey));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CommittedDeleteMustMatchTheExactUploadedMetadata()
    {
        var root = SyntheticPartitionProductionVectors.CreateLifecycleRoot();
        try
        {
            var path = Path.Join(
                root, "dispatch-bootstrap", "state-lifecycle-evidence.tsv");
            var lines = File.ReadAllLines(path);
            var index = Array.FindIndex(lines, line => line.StartsWith(
                "delete\topaque-intent-normal\t", StringComparison.Ordinal));
            Assert.True(index >= 0);
            var fields = lines[index].Split('\t');
            fields[3] = new string('f', 64);
            lines[index] = string.Join('\t', fields);
            File.WriteAllLines(path, lines);

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

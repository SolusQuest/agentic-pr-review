using System.Globalization;
using AgenticPrReview.Runtime.ActionHostVerifierFixture;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action.TrustedProof;

public sealed class SyntheticTransactionPartitionTests
{
    [Fact]
    public void CompleteProductionStoreLifecycleDerivesOneExactPartition()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "apr-r4-e2p-partition-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var ordinal = 0;
            WriteScenario(root, "dispatch-bootstrap", 7, 3, 0, ref ordinal);
            WriteScenario(root, "dispatch-continuation", 6, 3, 2,
                ref ordinal);
            WriteScenario(root, "stale-head", 0, 1, 0, ref ordinal);

            var partition = SyntheticTransactionPartition.Derive(
                root,
                new(
                    new string('1', 40),
                    new string('2', 40),
                    new string('3', 64),
                    new string('4', 64)));

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
        var root = Path.Join(
            Path.GetTempPath(),
            "apr-r4-e2p-partition-invalid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var ordinal = 0;
            WriteScenario(root, "dispatch-bootstrap", 7, 3, 0, ref ordinal);
            WriteScenario(root, "dispatch-continuation", 6, 3, 2,
                ref ordinal);
            WriteScenario(root, "stale-head", 0, 0, 0, ref ordinal);

            Assert.Null(SyntheticTransactionPartition.Derive(
                root,
                new(
                    new string('1', 40),
                    new string('2', 40),
                    new string('3', 64),
                    new string('4', 64))));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void WriteScenario(
        string root,
        string name,
        int targetCleanupPairs,
        int liveRecords,
        int emptyCleanupRecords,
        ref int ordinal)
    {
        var lines = new List<string>();
        for (var index = 0; index < targetCleanupPairs; index++)
        {
            var target = Upload(lines, ref ordinal);
            var cleanup = Upload(lines, ref ordinal);
            Delete(lines, target);
            Delete(lines, cleanup);
        }

        for (var index = 0; index < emptyCleanupRecords; index++)
        {
            var cleanup = Upload(lines, ref ordinal);
            Delete(lines, cleanup);
        }

        for (var index = 0; index < liveRecords; index++)
        {
            Upload(lines, ref ordinal);
        }

        var scenario = Path.Join(root, name);
        Directory.CreateDirectory(scenario);
        File.WriteAllLines(
            Path.Join(scenario, "state-operation-identities.tsv"),
            lines);
    }

    private static (string ObjectId, string Identity, string Digest) Upload(
        List<string> lines,
        ref int ordinal)
    {
        ordinal++;
        var objectId = ordinal.ToString(CultureInfo.InvariantCulture);
        var identity = ordinal.ToString("x64", CultureInfo.InvariantCulture);
        var digest = (ordinal + 100).ToString("x64",
            CultureInfo.InvariantCulture);
        lines.Add(string.Join('\t',
            "upload",
            "opaque",
            identity,
            digest,
            "None",
            "Committed",
            string.Join('|', objectId, new string('a', 64), digest,
                "1800001000", "900", "1", "100")));
        return (objectId, identity, digest);
    }

    private static void Delete(
        List<string> lines,
        (string ObjectId, string Identity, string Digest) value) =>
        lines.Add(string.Join('\t',
            "delete",
            "opaque",
            value.ObjectId,
            value.Digest,
            "None",
            "Committed",
            string.Join('|', value.ObjectId, new string('a', 64), value.Digest,
                "1800001000", "900", "1", "100")));
}

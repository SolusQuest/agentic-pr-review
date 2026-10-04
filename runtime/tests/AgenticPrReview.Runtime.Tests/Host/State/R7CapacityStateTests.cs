using System.Security.Cryptography;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Capacity;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.State;

public sealed class R7CapacityStateTests
{
    [Fact]
    public async Task LargeAcceptedHistoryRejectsForgedRoleAssociationAndSelectedScopeWithoutWriting()
    {
        var root = ReplayProcess.CreatePrivateRoot();
        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            var seed = await CapacityCommand.RunProcessAsync(new(root, key, new("default64", "success", 64, 7, 64), null, [], false));
            Assert.Equal(442, seed.Receipt.Tools);
            Assert.True(seed.Receipt.Metrics.SessionRecords > 256);
            Assert.NotNull(seed.Lineage);
            foreach (var mode in new[] { "role", "association", "policy", "scope" })
            {
                var rejected = await CapacityCommand.RunProcessAsync(new(root, key, new(mode, mode, 0, 0, 64),
                    seed.Lineage, [new("default64", 64, 7)], false, seed.PlaintextSha256));
                Assert.Equal("admission_rejected", rejected.Receipt.Code);
                Assert.True(rejected.Receipt.PredecessorPreserved);
                Assert.True(rejected.Receipt.RestoredExact);
                Assert.Equal(seed.Lineage, rejected.Lineage);
                Assert.Equal(seed.PlaintextSha256, rejected.PlaintextSha256);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            Assert.True(ReplayProcess.Cleanup(root));
        }
    }
}

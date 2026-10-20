using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

namespace AgenticPrReview.Runtime.Tests.Agent.Quality;

public sealed class PrivateProofLifetimeTests
{
    [Fact]
    public async Task UnreapedChildBlocksReadbackAndCleanupWithoutChangingPriorBytes()
    {
        var root = ReplayProcess.CreatePrivateRoot();
        var path = Path.Join(root, "prior-evidence.bin");
        byte[] prior = [1, 7, 3, 9]; // Authored bytes; no state admission or SDK execution claim.
        File.WriteAllBytes(path, prior);
        try
        {
            var readBackCalled = false;
            var cleanupCalled = false;
            Assert.False(await PrivateProofLifetime.VerifyPreservationAsync(false, () =>
            {
                readBackCalled = true;
                return Task.FromResult(true);
            }));
            Assert.False(PrivateProofLifetime.TryCleanup(false, root, _ =>
            {
                cleanupCalled = true;
                return true;
            }));
            Assert.False(readBackCalled);
            Assert.False(cleanupCalled);
            Assert.True(Directory.Exists(root));
            Assert.Equal(prior, File.ReadAllBytes(path));
            Assert.True(await PrivateProofLifetime.VerifyPreservationAsync(true, () =>
                Task.FromResult(File.ReadAllBytes(path).AsSpan().SequenceEqual(prior))));
        }
        finally { Assert.True(ReplayProcess.Cleanup(root)); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CleanupFalseOrExceptionCannotClaimRootWasRemoved(bool throws)
    {
        var root = ReplayProcess.CreatePrivateRoot();
        try
        {
            string? calledWith = null;
            Assert.False(PrivateProofLifetime.TryCleanup(true, root, path =>
            {
                calledWith = path;
                if (throws) throw new IOException("private-cleanup-detail");
                return false;
            }));
            Assert.Equal(root, calledWith);
            Assert.True(Directory.Exists(root));
        }
        finally { Assert.True(ReplayProcess.Cleanup(root)); }
    }

    [Fact]
    public void ConfirmedReapingAllowsActualRootCleanup()
    {
        var root = ReplayProcess.CreatePrivateRoot();
        Assert.True(PrivateProofLifetime.TryCleanup(true, root, ReplayProcess.Cleanup));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task ReadbackFalseOrExceptionRemainsUnverified()
    {
        Assert.False(await PrivateProofLifetime.VerifyPreservationAsync(true, () => Task.FromResult(false)));
        Assert.False(await PrivateProofLifetime.VerifyPreservationAsync(true, () =>
            throw new IOException("private-preservation-detail")));
    }
}

using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Agent.Tools;

public sealed class ReviewedSnapshotLifecycleTests
{
    [Fact]
    public void NonRegularHeadPathCanBeChangedWithoutEnteringToolAllowlist()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "apr-agent-h5-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var snapshot = new ReviewedSnapshot(
                new ReviewedIdentity(
                    "42",
                    7,
                    new string('a', 40),
                    new string('b', 40)),
                root,
                trackedFiles: [],
                reviewedHeadPaths: ["link"],
                changedFiles:
                [
                    new ReviewedChangedFile(
                        "link",
                        null,
                        "modified",
                        0,
                        0,
                        0,
                        "unavailable",
                        null,
                        false),
                ],
                diffSources: []);

            Assert.Empty(snapshot.OrderedTrackedFiles);
            Assert.Equal<string>(
                ["link"],
                snapshot.OrderedReviewedHeadPaths);
            Assert.False(snapshot.Contains("link"));
            Assert.True(snapshot.ContainsChangedPath("link"));
            Assert.False(snapshot.TryGetDiffSource("link", out _));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

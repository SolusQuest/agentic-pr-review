using AgenticPrReview.Runtime;

namespace AgenticPrReview.Runtime.Tests;

public sealed class RetiredLiveCommandTests
{
    [Fact]
    public async Task LiveAgentVerifierRetiredReviewLiveCommandIsRejectedWithoutOutputs()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            $"apr-retired-live-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var outputPaths = new[]
            {
                Path.Join(root, "result.json"),
                Path.Join(root, "trace.json"),
                Path.Join(root, "candidate-ledger.json"),
                Path.Join(root, "provider-run-metadata.json"),
            };
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var exitCode = await RuntimeEntrypoint.RunAsync(
                [
                    "review-live",
                    "--input", Path.Join(root, "input.json"),
                    "--context", Path.Join(root, "live-context.json"),
                    "--output", outputPaths[0],
                    "--trace", outputPaths[1],
                    "--candidate-ledger", outputPaths[2],
                    "--provider-run-metadata", outputPaths[3],
                ],
                stdout,
                stderr);

            Assert.Equal(2, exitCode);
            Assert.Empty(stdout.ToString());
            Assert.StartsWith(
                "APR_USAGE_INVALID:",
                stderr.ToString(),
                StringComparison.Ordinal);
            Assert.All(outputPaths, path => Assert.False(File.Exists(path)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

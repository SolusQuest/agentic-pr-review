using System.Text.Json;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Capacity;

internal static class CapacityCommand
{
    internal static int Invoke(string[] args)
    {
        try
        {
            if (args is ["r7-capacity-verify", "--corpus", { } verifyCorpus, "--report", { } reportPath])
            {
                var bytes = File.ReadAllBytes(reportPath);
                CapacitySpec.Require(bytes.Length <= CapacitySpec.IpcBytes && CapacitySpec.IsPrivate(bytes), "report_size_privacy");
                var report = JsonSerializer.Deserialize(bytes, CapacityJson.Default.CapacityReport);
                CapacitySpec.Require(report is not null && report.SourceCommit == EvaluationSource.Commit &&
                    report.SourceTree == EvaluationSource.Tree && report.SourceClean == EvaluationSource.Clean, "verifier_source");
                var error = CapacityVerifier.Verify(report, File.ReadAllBytes(verifyCorpus));
                Console.WriteLine(error ?? "APR_R7_CAPACITY_VERIFIED");
                return error is null ? 0 : 1;
            }
            if (args is ["r7-capacity-parity", "--corpus", { } parityCorpus, { } framework, { } aot])
            {
                var first = ReadReport(framework); var second = ReadReport(aot);
                var corpus = File.ReadAllBytes(parityCorpus);
                CapacitySpec.Require(CapacityVerifier.Verify(first, corpus) is null && CapacityVerifier.Verify(second, corpus) is null,
                    "parity_admission");
                CapacitySpec.Require(CapacityVerifier.Equivalent(first, second), "parity");
                Console.WriteLine("APR_R7_CAPACITY_PARITY_VERIFIED");
                return 0;
            }
            CapacitySpec.Require(false, "arguments");
        }
        catch (Exception error)
        {
            // No arbitrary exception text, filenames, transcript or credential may become gate output.
            Console.Error.WriteLine(error is InvalidOperationException && error.Message.StartsWith("r7_capacity_", StringComparison.Ordinal)
                ? error.Message : "r7_capacity_failed");
        }
        return 1;
    }

    private static CapacityReport ReadReport(string path)
    {
        var bytes = File.ReadAllBytes(path);
        CapacitySpec.Require(bytes.Length <= CapacitySpec.IpcBytes && CapacitySpec.IsPrivate(bytes), "parity_input");
        return JsonSerializer.Deserialize(bytes, CapacityJson.Default.CapacityReport) ?? throw new InvalidOperationException("r7_capacity_parity_input");
    }

}

using System.Text;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Quality;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture;

internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "quality" && args[1] == "--corpus")
        {
            var quality = await QualityRunner.RunAsync(args[2]);
            foreach (var execution in quality.Executions)
                Console.WriteLine(Encoding.UTF8.GetString(EvaluationJson.Write(execution.Outcome)));
            Console.WriteLine(Encoding.UTF8.GetString(QualityRunner.Write(quality.Summary)));
            return quality.ExitCode;
        }
        if (!args.SequenceEqual(["evaluate", "--fixture", "self-test"]))
        {
            Console.Error.WriteLine("r5_evaluation_input_invalid");
            return 2;
        }
        try
        {
            var result = await EvaluationSelfTest.RunAsync();
            foreach (var outcome in result.Outcomes)
                Console.WriteLine(Encoding.UTF8.GetString(EvaluationJson.Write(outcome)));
            if (!result.Passed) Console.Error.WriteLine("r5_evaluation_self_test_failed");
            return result.Passed ? 0 : 1;
        }
        catch
        {
            Console.Error.WriteLine("r5_evaluation_infrastructure_failed");
            return 1;
        }
    }
}

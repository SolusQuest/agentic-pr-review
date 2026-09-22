using System.Globalization;
using System.Text;
using static AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Verification.GateContracts;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Verification;

internal static class GateCommandCapture
{
    internal static string Run(Func<int> command)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new BoundedWriter();
        using var error = new BoundedWriter();
        try
        {
            Console.SetOut(output); Console.SetError(error);
            Require(command() == 0 && error.ToString().Length == 0);
            return output.ToString();
        }
        finally { Console.SetOut(originalOut); Console.SetError(originalError); }
    }

    private sealed class BoundedWriter() : StringWriter(CultureInfo.InvariantCulture)
    {
        public override void Write(char value) { Check(1); base.Write(value); }
        public override void Write(string? value) { Check(value?.Length ?? 0); base.Write(value); }
        public override void Write(char[] buffer, int index, int count) { Check(count); base.Write(buffer, index, count); }
        public override void WriteLine(string? value) { Check((value?.Length ?? 0) + Environment.NewLine.Length); base.WriteLine(value); }
        private void Check(int count) => Require(GetStringBuilder().Length + count <= MaximumBytes);
    }
}

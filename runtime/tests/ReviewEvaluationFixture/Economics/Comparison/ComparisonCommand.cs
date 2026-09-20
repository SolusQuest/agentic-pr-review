using System.Globalization;
using System.Text;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Comparison;

internal static class ComparisonCommand
{
    internal static int Invoke(string[] args)
    {
        try
        {
            if (!Arguments(args, out var leftPath, out var rightPath, out var format))
                return Reject("r6_comparison_arguments_invalid", 2);
            var leftBytes = ReadFile(leftPath!); var rightBytes = ReadFile(rightPath!);
            var left = leftBytes is null ? null : ComparisonJson.ReadInput(leftBytes);
            var right = rightBytes is null ? null : ComparisonJson.ReadInput(rightBytes);
            if (left is null) return Reject("r6_comparison_left_invalid", 2);
            if (right is null) return Reject("r6_comparison_right_invalid", 2);
            var report = ComparisonReport.Create(left, right);
            var json = ComparisonJson.Write(report);
            if (ComparisonJson.Read(json) is not { } admitted) return Reject("r6_comparison_report_invalid", 1);
            var output = format == "markdown" ? Markdown(admitted) : Encoding.UTF8.GetString(json);
            Console.WriteLine(output);
            return 0;
        }
        catch (ComparisonInputException error) { return Reject(error.Code, 2); }
        catch (OverflowException) { return Reject("r6_comparison_arithmetic_overflow", 1); }
        catch { return Reject("r6_comparison_infrastructure_failed", 1); }
    }

    internal static string Markdown(ComparisonReportDocument report)
    {
        var result = report.Result;
        var text = new StringBuilder("# R6 economics comparison\n\n");
        static string Number(decimal? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "unavailable";
        text.AppendLine("Observed traffic under the selected fixed reference tariff; supplied artifact consistency is not origin authentication.");
        text.AppendLine().AppendLine("| Dimension | Left | Right |").AppendLine("| --- | --- | --- |");
        void Row(string label, object left, object right) => text.AppendLine($"| {label} | {left} | {right} |");
        Row("Scheduled", result.Left.Campaign.Scheduled, result.Right.Campaign.Scheduled);
        Row("Completed", result.Left.Campaign.Completed, result.Right.Campaign.Completed);
        Row("Failed / invalid / unattempted",
            $"{result.Left.Campaign.Failed} / {result.Left.Campaign.Invalid} / {result.Left.Campaign.Unattempted}",
            $"{result.Right.Campaign.Failed} / {result.Right.Campaign.Invalid} / {result.Right.Campaign.Unattempted}");
        Row("Scheduled completion rate", Number(result.Left.CompletionRate.Value), Number(result.Right.CompletionRate.Value));
        Row("Usage / observed pricing", $"{result.Left.Usage.Status} / {result.Left.Pricing.Status}",
            $"{result.Right.Usage.Status} / {result.Right.Pricing.Status}");
        Row("Reference currency", report.Left.Pricing.ObservedUsage.Currency, report.Right.Pricing.ObservedUsage.Currency);
        Row("Observed total", Number(report.Left.Pricing.ObservedUsage.TotalAmount), Number(report.Right.Pricing.ObservedUsage.TotalAmount));
        Row("Known subtotal", Number(report.Left.Pricing.ObservedUsage.KnownTotalSubtotal), Number(report.Right.Pricing.ObservedUsage.KnownTotalSubtotal));
        Row("Same-token all-miss hypothetical total", Number(report.Left.Pricing.SameTokenAllMiss.TotalAmount), Number(report.Right.Pricing.SameTokenAllMiss.TotalAmount));
        Row("Reserved micro USD (not prices)", result.Left.Reservations.SpendMicroUsd, result.Right.Reservations.SpendMicroUsd);
        Row("Quality-eligible outcomes", result.Left.QualityEligibleOutcomes, result.Right.QualityEligibleOutcomes);
        Row("AI / declared human / unknown origin", $"{result.Left.AiAnnotations} / {result.Left.HumanDeclaredAnnotations} / {result.Left.UnknownAnnotationOrigins}",
            $"{result.Right.AiAnnotations} / {result.Right.HumanDeclaredAnnotations} / {result.Right.UnknownAnnotationOrigins}");
        Row("Conditional per-effective-review cost", Number(result.Left.EffectiveReviewCost.Value), Number(result.Right.EffectiveReviewCost.Value));
        Row("Effectiveness availability", result.Left.EffectiveReviewCost.Reason, result.Right.EffectiveReviewCost.Reason);
        Row("Effective / excluded / unassessed", $"{result.Left.EffectiveReviewCost.Eligible} / {result.Left.EffectiveReviewCost.Excluded} / {result.Left.EffectiveReviewCost.Unassessed}",
            $"{result.Right.EffectiveReviewCost.Eligible} / {result.Right.EffectiveReviewCost.Excluded} / {result.Right.EffectiveReviewCost.Unassessed}");
        text.AppendLine().AppendLine($"Descriptive comparison: **{result.DescriptiveComparison.Status}**; direction: {result.DescriptiveDirection}; observed reference total difference (right minus left): {Number(result.ObservedReferenceTotalDifference)}.");
        text.AppendLine($"Reasons: {(result.DescriptiveComparison.Reasons.IsEmpty ? "none" : string.Join(", ", result.DescriptiveComparison.Reasons))}.");
        text.AppendLine($"Experimental control: {string.Join(", ", result.ExperimentalControl.Reasons)}. Rule predeclaration: {result.RulePredeclaration}. Formal regression: {result.FormalRegression}.");
        text.AppendLine($"Campaign lifecycle and history equivalence: unproven. Unbound optional expectations: {result.PrefixEvidence.UnboundExpectations}.");
        foreach (var source in result.PrefixEvidence.Sources)
            text.AppendLine($"Source {source.SourceCommit}: observed instability {source.ObservedInstability}, unexpected drift {source.UnexpectedDrift}, intentional faults {source.IntentionalFaults}, unknown expectedness {source.UnknownExpectedness}, unverified observations {source.UnverifiedObservations}; {source.PromotionDisposition}.");
        text.AppendLine($"Promotion: {result.PromotionDisposition}. Historical R5 quality: inconclusive. Independent human confirmation: not evidenced. R7 readiness: not evaluated.");
        text.AppendLine("All-miss amounts are hypothetical repricing, not measured cache-disabled runs or savings. Execution-time billing remains unknown. Effectiveness values are conditional on the explicit definition and supplied annotation origin; they do not establish model quality.");
        if (Encoding.UTF8.GetByteCount(text.ToString()) > 64 * 1024) throw new InvalidOperationException("comparison_markdown_limit");
        return text.ToString();
    }

    private static bool Arguments(string[] args, out string? left, out string? right, out string format)
    {
        left = right = null; format = "json";
        if (args.Length is not (5 or 7) || args[0] != "economics-compare") return false;
        var seenFormat = false;
        for (var i = 1; i < args.Length; i += 2)
        {
            if (string.IsNullOrWhiteSpace(args[i + 1])) return false;
            if (args[i] == "--left" && left is null) left = args[i + 1];
            else if (args[i] == "--right" && right is null) right = args[i + 1];
            else if (args[i] == "--format" && !seenFormat) { format = args[i + 1]; seenFormat = true; }
            else return false;
        }
        return left is not null && right is not null && format is "json" or "markdown";
    }

    private static byte[]? ReadFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bytes = new byte[ComparisonLimits.InputBytes + 1];
            var length = stream.ReadAtLeast(bytes, bytes.Length, false);
            return length <= ComparisonLimits.InputBytes ? bytes[..length] : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return null; }
    }

    private static int Reject(string code, int exitCode) { Console.Error.WriteLine(code); return exitCode; }
}

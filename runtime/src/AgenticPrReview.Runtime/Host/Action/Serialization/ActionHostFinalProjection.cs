using System.Globalization;
using System.Text;
using AgenticPrReview.Runtime.ActionHost.Contracts;

namespace AgenticPrReview.Runtime.ActionHost.Serialization;

internal static partial class ActionHostJsonCodec
{
    internal const string ResultProtocol = "apr.cli.result.cr2";

    private static ActionHostCompletionDocument ProjectCompletion(ActionHostCompletion completion)
    {
        var accounting = completion.Accounting;
        var outputs = new ActionHostOutputDocument(
            StatusString(completion.Status), TerminationString(completion.TerminationReason),
            Decimal(accounting.ModelCalls), Decimal(accounting.ProviderAttempts),
            Decimal(accounting.ProviderRetries), Decimal(accounting.ProviderFailedAttempts),
            Decimal(accounting.UnknownUsageAttempts), Decimal(accounting.UnknownCachePartitionAttempts),
            CompletenessString(accounting.AttemptCompleteness), CompletenessString(accounting.UsageCompleteness),
            Decimal(accounting.InputTokens), Decimal(accounting.CacheHitTokens),
            Decimal(accounting.CacheMissTokens), Decimal(accounting.OutputTokens));
        return new ActionHostCompletionDocument(ResultProtocol, completion.BuildDiscriminator,
            completion.ProcessExitCode, outputs, ProjectSummary(completion, outputs),
            completion.Annotations.Select(annotation => new ActionHostAnnotationDocument(
                AnnotationCodeString(annotation.Code), AnnotationSeverityString(annotation.Severity),
                annotation.Message)).ToArray());
    }

    private static string ProjectSummary(ActionHostCompletion completion, ActionHostOutputDocument output)
    {
        static string Known(string? value) => value ?? "Not available";
        var summary = string.Join("\n", new[] {
            "Agentic PR Review", "",
            "Status: " + output.Status,
            "Reviewed SHA: " + Known(completion.Summary.ReviewedSha),
            "Publication URL: " + Known(completion.Summary.PublicationUrl),
            "Finding count: " + Known(completion.Summary.FindingCount?.ToString(CultureInfo.InvariantCulture)),
            "State disposition: " + StateDispositionString(completion.Summary.StateDisposition),
            "Review termination: " + output.TerminationReason,
            "Attempt accounting completeness: " + output.AttemptAccountingCompleteness,
            "Usage completeness: " + output.UsageCompleteness,
            "Model calls: " + Known(output.ModelCalls),
            "Provider attempts: " + Known(output.ProviderAttempts),
            "Provider retries: " + Known(output.ProviderRetries),
            "Failed provider attempts: " + Known(output.ProviderFailedAttempts),
            "Attempts with unknown usage: " + Known(output.ProviderUnknownUsageAttempts),
            "Attempts with unknown cache partition: " + Known(output.ProviderUnknownCachePartitionAttempts),
            "Known input token sum: " + Known(output.InputTokens),
            "Known cache hit token sum: " + Known(output.InputCacheHitTokens),
            "Known cache miss token sum: " + Known(output.InputCacheMissTokens),
            "Known output token sum: " + Known(output.OutputTokens), "",
            "Incomplete counts and known token sums are observed lower bounds. Not available means no finalization or an overflowed sum; usage is not billing.", ""
        });
        if (Encoding.UTF8.GetByteCount(summary) > 8 * 1024 || summary.Contains("::", StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid completion presentation.");
        return summary;
    }
}

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.Serialization;

namespace AgenticPrReview.Runtime.Tests.Host.Action.Contracts;

internal static class FinalProjectionAssertions
{
    internal static readonly string[] OutputNames = ["status", "termination-reason", "model-calls", "provider-attempts",
        "provider-retries", "provider-failed-attempts", "provider-unknown-usage-attempts", "provider-unknown-cache-partition-attempts",
        "attempt-accounting-completeness", "usage-completeness", "input-tokens", "input-cache-hit-tokens", "input-cache-miss-tokens", "output-tokens"];

    internal static string Token(object value) => Regex.Replace(value.ToString()!, "(?<!^)([A-Z])", "_$1").ToLowerInvariant();

    internal static ActionHostCompletionDocument Read(byte[] bytes)
    {
        Assert.InRange(bytes.Length, 1, ActionHostContractBounds.MaximumCompletionDocumentBytes);
        using var json = JsonDocument.Parse(bytes);
        Assert.Equal(new[] { "annotations", "build_discriminator", "outputs", "process_exit_code", "protocol", "summary" },
            json.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(OutputNames.Order(), json.RootElement.GetProperty("outputs").EnumerateObject().Select(p => p.Name).Order());
        var result = JsonSerializer.Deserialize(bytes, ActionHostJsonContext.Default.ActionHostCompletionDocument)!;
        Assert.Equal("apr.cli.result.cr2", result.Protocol);
        Assert.NotNull(result.Outputs.Status); Assert.NotNull(result.Outputs.TerminationReason);
        Assert.NotNull(result.Outputs.AttemptAccountingCompleteness); Assert.NotNull(result.Outputs.UsageCompleteness);
        Assert.All(json.RootElement.GetProperty("outputs").EnumerateObject(), p =>
            Assert.True(p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Null));
        return result;
    }

    internal static void Matches(ActionHostCompletion expected, byte[] bytes)
    {
        var actual = Read(bytes); var output = actual.Outputs; var a = expected.Accounting;
        Assert.Equal(expected.BuildDiscriminator, actual.BuildDiscriminator);
        Assert.Equal(expected.ProcessExitCode, actual.ProcessExitCode);
        Assert.Equal(Token(expected.Status), output.Status);
        Assert.Equal(Token(expected.TerminationReason), output.TerminationReason);
        Assert.Equal(Token(a.AttemptCompleteness), output.AttemptAccountingCompleteness);
        Assert.Equal(Token(a.UsageCompleteness), output.UsageCompleteness);
        long?[] numbers = [a.ModelCalls, a.ProviderAttempts, a.ProviderRetries, a.ProviderFailedAttempts,
            a.UnknownUsageAttempts, a.UnknownCachePartitionAttempts, a.InputTokens, a.CacheHitTokens, a.CacheMissTokens, a.OutputTokens];
        string?[] projected = [output.ModelCalls, output.ProviderAttempts, output.ProviderRetries, output.ProviderFailedAttempts,
            output.ProviderUnknownUsageAttempts, output.ProviderUnknownCachePartitionAttempts, output.InputTokens,
            output.InputCacheHitTokens, output.InputCacheMissTokens, output.OutputTokens];
        Assert.Equal(numbers.Select(n => n?.ToString(CultureInfo.InvariantCulture)), projected);
        Assert.Contains("State disposition: " + Token(expected.Summary.StateDisposition), actual.Summary);
        Assert.Contains("Reviewed SHA: " + (expected.Summary.ReviewedSha ?? "Not available"), actual.Summary);
        Assert.Contains("Publication URL: " + (expected.Summary.PublicationUrl ?? "Not available"), actual.Summary);
        Assert.Equal(expected.Annotations.Length, actual.Annotations.Length);
        for (var i = 0; i < expected.Annotations.Length; i++)
        {
            Assert.Equal(Token(expected.Annotations[i].Code), actual.Annotations[i].Code);
            Assert.Equal(Token(expected.Annotations[i].Severity), actual.Annotations[i].Severity);
            Assert.Equal(expected.Annotations[i].Message, actual.Annotations[i].Message);
        }
    }

    internal static void CompleteZero(ActionHostCompletionDocument value)
    {
        Assert.Equal("complete", value.Outputs.AttemptAccountingCompleteness);
        Assert.Equal("complete", value.Outputs.UsageCompleteness);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(value, ActionHostJsonContext.Default.ActionHostCompletionDocument));
        foreach (var field in json.RootElement.GetProperty("outputs").EnumerateObject())
            if (field.Name is not ("status" or "termination-reason") && !field.Name.EndsWith("completeness", StringComparison.Ordinal))
                Assert.Equal("0", field.Value.GetString());
    }
}

using System.Globalization;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.ActionHost.Contracts;

namespace AgenticPrReview.Runtime.Tests.Host.Action.Contracts;

// Decodes the retained test corpus into typed domain inputs only. It is not a
// production wire reader and implements no accounting/status consistency rules.
internal static class CompletionFixtureInputs
{
    internal static bool Representable(JsonElement root, out ActionHostCompletion? completion)
    {
        completion = null;
        string[] rootKeys = ["build_discriminator", "status", "exit_class", "process_exit_code", "summary", "annotations", "accounting", "termination_reason"];
        if (!root.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(rootKeys.Order())) return false;
        var value = root.GetProperty("accounting");
        string[] countKeys = ["model_calls", "provider_attempts", "provider_retries", "provider_failed_attempts", "provider_unknown_usage_attempts", "provider_unknown_cache_partition_attempts", "input_tokens", "input_cache_hit_tokens", "input_cache_miss_tokens", "output_tokens"];
        var required = countKeys.Concat(["attempt_accounting_completeness", "usage_completeness"]).Order();
        if (!value.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(required)) return false;
        var numbers = new long?[10];
        for (var i = 0; i < countKeys.Length; i++)
        {
            var number = value.GetProperty(countKeys[i]);
            if (number.ValueKind == JsonValueKind.Null) continue;
            if (number.ValueKind != JsonValueKind.String ||
                !long.TryParse(number.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed) ||
                parsed.ToString(CultureInfo.InvariantCulture) != number.GetString()) return false;
            numbers[i] = parsed;
        }
        if (!EnumToken(value.GetProperty("attempt_accounting_completeness"), out AccountingCompleteness attempts) ||
            !EnumToken(value.GetProperty("usage_completeness"), out AccountingCompleteness usage) ||
            !EnumToken(root.GetProperty("status"), out ActionHostStatus status) ||
            !EnumToken(root.GetProperty("termination_reason"), out ActionHostTerminationReason reason)) return false;
        var annotations = new List<ActionHostAnnotation>();
        foreach (var annotation in root.GetProperty("annotations").EnumerateArray())
        {
            if (!EnumToken(annotation.GetProperty("code"), out ActionHostAnnotationCode code) ||
                !ActionHostAnnotation.TryCreate(code, out var canonical) ||
                annotation.GetProperty("severity").GetString() != FinalProjectionAssertions.Token(canonical!.Severity) ||
                annotation.GetProperty("message").GetString() != canonical.Message) return false;
            annotations.Add(canonical);
        }
        if (!ActionHostAccounting.TryCreate(numbers[0], numbers[1], numbers[2], numbers[3], numbers[4], numbers[5],
            numbers[6], numbers[7], numbers[8], numbers[9], attempts, usage, out var accounting)) return true;
        var summary = root.GetProperty("summary");
        if (!EnumToken(summary.GetProperty("state_disposition"), out ActionHostStateDisposition disposition)) return false;
        if (!ActionHostStepSummary.TryCreate(summary.GetProperty("reviewed_sha").GetString(),
                summary.GetProperty("publication_url").GetString(),
                summary.GetProperty("finding_count").ValueKind == JsonValueKind.Null ? null : summary.GetProperty("finding_count").GetInt32(),
                disposition, out var typedSummary)) return true;
        ActionHostCompletion.TryCreate(root.GetProperty("build_discriminator").GetString(), status,
            typedSummary, annotations, out completion, accounting, reason);
        return true;
    }

    private static bool EnumToken<T>(JsonElement value, out T result) where T : struct, Enum
    {
        result = default;
        if (value.ValueKind != JsonValueKind.String) return false;
        foreach (var item in Enum.GetValues<T>())
            if (FinalProjectionAssertions.Token(item) == value.GetString()) { result = item; return true; }
        return false;
    }
}

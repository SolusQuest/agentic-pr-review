using System.Globalization;

namespace AgenticPrReview.Runtime.ActionHostVerifierFixture;

internal static partial class FrameworkSupervisor
{
    private static readonly (string Name, string Label)[] OutputRows =
    [
        ("status", "Status"), ("termination-reason", "Review termination"),
        ("model-calls", "Model calls"), ("provider-attempts", "Provider attempts"),
        ("provider-retries", "Provider retries"), ("provider-failed-attempts", "Failed provider attempts"),
        ("provider-unknown-usage-attempts", "Attempts with unknown usage"),
        ("provider-unknown-cache-partition-attempts", "Attempts with unknown cache partition"),
        ("attempt-accounting-completeness", "Attempt accounting completeness"),
        ("usage-completeness", "Usage completeness"),
        ("input-tokens", "Known input token sum"), ("input-cache-hit-tokens", "Known cache hit token sum"),
        ("input-cache-miss-tokens", "Known cache miss token sum"), ("output-tokens", "Known output token sum"),
    ];

    internal static bool OutputsMatchSummary(string output, string summary, bool allowAbsent)
    {
        if (output.Length > 16 * 1024 || summary.Length > 8 * 1024 ||
            !output.StartsWith(FrameworkCanaries.OutputSentinel, StringComparison.Ordinal)) return false;
        var frames = output[FrameworkCanaries.OutputSentinel.Length..]
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        if (frames.Contains('\r')) return false;
        if (summary.Length == 0) return allowAbsent && frames.Length == 0;
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        const string fixedSummary = "## Agentic PR Review\n\nThe private review wrapper failed safely.\n\nStatus: failed. Review termination: host_failure. Attempt accounting completeness: unavailable. Usage completeness: unavailable. Provider counts, token sums and state disposition: Not available.\n";
        if (summary == fixedSummary)
        {
            expected.Add("status", "failed");
            expected.Add("termination-reason", "host_failure");
            expected.Add("attempt-accounting-completeness", "unavailable");
            expected.Add("usage-completeness", "unavailable");
        }
        else
        {
            var lines = summary.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            foreach (var (name, label) in OutputRows)
            {
                var prefix = $"| {label} | ";
                var rows = lines.Where(line => line.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
                if (rows.Length != 1 || !rows[0].EndsWith(" |", StringComparison.Ordinal)) return false;
                var value = rows[0][prefix.Length..^2];
                if (name is "status" or "termination-reason" || name.EndsWith("completeness", StringComparison.Ordinal))
                {
                    if (value.Length == 0 || value.Length > 64 || value.Any(c => c is not (>= 'a' and <= 'z') && c != '_')) return false;
                    expected.Add(name, value);
                }
                else if (value != "Not available")
                {
                    if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 0 ||
                        number.ToString(CultureInfo.InvariantCulture) != value) return false;
                    expected.Add(name, value);
                }
            }
        }
        var actual = new Dictionary<string, string>(StringComparer.Ordinal);
        var parts = frames.Split('\n');
        if (parts[^1] != "" || (parts.Length - 1) % 3 != 0) return false;
        for (var index = 0; index < parts.Length - 1; index += 3)
        {
            var header = parts[index];
            var split = header.IndexOf("<<", StringComparison.Ordinal);
            if (split < 1) return false;
            var name = header[..split];
            var delimiter = header[(split + 2)..];
            const string delimiterPrefix = "ghadelimiter_";
            if (!delimiter.StartsWith(delimiterPrefix, StringComparison.Ordinal) ||
                !Guid.TryParseExact(delimiter[delimiterPrefix.Length..], "D", out _) ||
                parts[index + 2] != delimiter ||
                !expected.TryGetValue(name, out var value) || parts[index + 1] != value ||
                !actual.TryAdd(name, value)) return false;
        }
        return actual.Count == expected.Count;
    }
}

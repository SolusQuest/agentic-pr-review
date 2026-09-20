using System.Text;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Contracts;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Pricing;

internal static class PricingCommand
{
    internal static int Invoke(string[] args)
    {
        try
        {
            if (!Arguments(args, out var journalPath, out var tariffPath, out var format))
                return Reject("r6_pricing_arguments_invalid", 2);
            var journalBytes = ReadFile(journalPath!, UsageJournalLimits.JsonBytes);
            var journal = journalBytes is null ? null : UsageJournalJson.Read(journalBytes);
            if (journal is null) return Reject("r6_pricing_journal_invalid", 2);
            var tariffBytes = ReadFile(tariffPath!, PricingLimits.TariffBytes);
            if (tariffBytes is null) return Reject("r6_pricing_tariff_invalid", 2);
            var tariff = AdmittedTariff.Read(tariffBytes, out var error);
            if (tariff is null) return Reject(error, 2);
            var report = PricingReport.Create(journal, tariff);
            var json = PricingJson.Write(report);
            if (PricingJson.Read(json) is not { } admitted || !admitted.Matches(journal, tariff))
                return Reject("r6_pricing_report_invalid", 1);
            Console.WriteLine(format == "markdown" ? PricingMarkdown.Write(admitted) : Encoding.UTF8.GetString(json));
            return 0;
        }
        catch (OverflowException) { return Reject("r6_pricing_arithmetic_overflow", 1); }
        catch { return Reject("r6_pricing_infrastructure_failed", 1); }
    }

    private static bool Arguments(string[] args, out string? journal, out string? tariff, out string format)
    {
        journal = tariff = null;
        format = "json";
        if (args.Length is not (5 or 7) || args[0] != "economics-price") return false;
        var hasFormat = false;
        for (var i = 1; i < args.Length; i += 2)
        {
            if (string.IsNullOrWhiteSpace(args[i + 1])) return false;
            if (args[i] == "--journal" && journal is null) journal = args[i + 1];
            else if (args[i] == "--tariff" && tariff is null) tariff = args[i + 1];
            else if (args[i] == "--format" && !hasFormat)
            {
                format = args[i + 1];
                hasFormat = true;
            }
            else return false;
        }
        return journal is not null && tariff is not null && format is "json" or "markdown";
    }

    private static byte[]? ReadFile(string path, int limit)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bytes = new byte[limit + 1];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = stream.Read(bytes, count, bytes.Length - count);
                if (read == 0) return bytes[..count];
                count += read;
            }
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return null; }
    }

    private static int Reject(string code, int exitCode)
    {
        Console.Error.WriteLine(code);
        return exitCode;
    }
}

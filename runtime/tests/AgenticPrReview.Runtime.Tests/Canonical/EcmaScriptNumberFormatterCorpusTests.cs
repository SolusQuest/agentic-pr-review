using System;
using System.IO;
using System.Text.Json;
using AgenticPrReview.Runtime.Canonical;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Canonical;

public sealed class EcmaScriptNumberFormatterCorpusTests
{
    [Fact]
    public void MatchesNodeCorpus()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "prefix-contract", "num-corpus.json");
        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        var failures = 0;
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            var bits = Convert.ToUInt64(entry.GetProperty("bits").GetString(), 16);
            var expected = entry.GetProperty("es").GetString()!;
            var value = BitConverter.UInt64BitsToDouble(bits);
            string actual;
            try
            {
                actual = EcmaScriptNumberFormatter.Format(value);
            }
            catch (Exception ex)
            {
                actual = "EX:" + ex.GetType().Name;
            }

            if (actual != expected)
            {
                failures++;
                if (failures <= 20)
                {
                    Console.WriteLine($"bits={entry.GetProperty("bits").GetString()} expected={expected} actual={actual}");
                }
            }
        }

        Assert.Equal(0, failures);
    }
}

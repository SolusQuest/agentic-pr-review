using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using AgenticPrReview.Runtime.Ledger;

namespace AgenticPrReview.Runtime.Tests.Ledger;

/// <summary>
/// Drives every on-disk provider-session-ledger fixture through
/// <see cref="LedgerParser.ParseAndValidate"/> and asserts that the primary
/// diagnostic code matches the manifest-declared expectation. Also enforces
/// that no fixture in the ledger directory is unreferenced by the manifest.
/// </summary>
public sealed class LedgerFixtureTests
{
    private static readonly string LedgerRoot =
        Path.Combine(AppContext.BaseDirectory, "protocol", "fixtures", "v1", "provider-session-ledger");

    private static readonly string ManifestPath =
        Path.Combine(AppContext.BaseDirectory, "protocol", "fixtures", "v1", "manifest.json");

    public static IEnumerable<object[]> RestoreEntries()
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(ManifestPath));
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            if (entry.GetProperty("type").GetString() != "ledger-restore") continue;
            var file = entry.GetProperty("file").GetString()!;
            if (!file.StartsWith("provider-session-ledger/", StringComparison.Ordinal)) continue;
            var e = entry.GetProperty("expectation");
            var valid = e.GetProperty("valid").GetBoolean();
            string? code = null;
            string? contentSha256 = null;
            if (valid && entry.TryGetProperty("contentSha256", out var sha)) contentSha256 = sha.GetString();
            if (!valid && e.TryGetProperty("code", out var c)) code = c.GetString();
            yield return new object[] { file, valid, code ?? string.Empty, contentSha256 ?? string.Empty };
        }
    }

    [Theory]
    [MemberData(nameof(RestoreEntries))]
    public void ManifestEntry_MatchesParserOutcome(string file, bool valid, string expectedCode, string expectedSha)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "protocol", "fixtures", "v1", file);
        var bytes = File.ReadAllBytes(path);
        var outcome = LedgerParser.ParseAndValidate(bytes);

        if (valid)
        {
            Assert.True(outcome.Diagnostics.IsEmpty,
                $"Expected valid restore for {file}; got diagnostics: " + DiagnosticsToString(outcome.Diagnostics));
            Assert.NotNull(outcome.Ledger);
            Assert.Equal(expectedSha, outcome.Ledger!.ContentSha256);
        }
        else
        {
            Assert.Null(outcome.Ledger);
            Assert.False(outcome.Diagnostics.IsEmpty);
            var primaryCode = outcome.Diagnostics[0].Code;
            Assert.Equal(expectedCode, primaryCode);
        }
    }

    [Fact]
    public void EveryLedgerDirectoryFileIsListedInManifest()
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(ManifestPath));
        var manifestFiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            var type = entry.GetProperty("type").GetString();
            if (type != "ledger-restore" && type != "ledger-transition" && type != "ledger-build") continue;
            manifestFiles.Add(entry.GetProperty("file").GetString()!);
        }
        var missing = Directory.EnumerateFiles(LedgerRoot)
            .Select(p => "provider-session-ledger/" + Path.GetFileName(p))
            .Where(rel => !manifestFiles.Contains(rel))
            .ToArray();
        Assert.True(missing.Length == 0,
            "Files in provider-session-ledger/ not listed in manifest: " + string.Join(", ", missing));
    }

    private static string DiagnosticsToString(ImmutableArray<LedgerDiagnostic> diags)
        => string.Join(", ", diags.Select(d => d.Code));
}

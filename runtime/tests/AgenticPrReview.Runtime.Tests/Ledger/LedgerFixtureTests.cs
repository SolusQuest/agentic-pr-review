using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Ledger;

namespace AgenticPrReview.Runtime.Tests.Ledger;

public sealed class LedgerFixtureTests
{
    [Fact]
    public void LedgerRestoreFixturesMatchDeclaredExpectations()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "protocol", "fixtures", "v1");
        var ledgerRoot = Path.Combine(root, "provider-session-ledger");
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "manifest.json")));
        var registeredFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in manifest.RootElement.EnumerateArray())
        {
            var type = entry.GetProperty("type").GetString();
            if (type is not "ledger-restore")
            {
                continue;
            }

            var relativePath = entry.GetProperty("file").GetString()!;
            var filePath = Path.Combine(root, relativePath);
            var bytes = File.ReadAllBytes(filePath);
            var outcome = LedgerParser.ParseAndValidate(bytes);
            var valid = entry.GetProperty("valid").GetBoolean();

            Assert.Equal(valid, outcome.Ledger is not null);
            if (valid)
            {
                var expectedHash = entry.GetProperty("contentSha256").GetString()!;
                Assert.Equal(expectedHash, outcome.Ledger!.ContentSha256);
            }
            else
            {
                var expectedCode = entry.GetProperty("code").GetString()!;
                Assert.NotEmpty(outcome.Diagnostics);
                Assert.Equal(expectedCode, outcome.Diagnostics[0].Code);
            }

            registeredFiles.Add(relativePath);
        }

        var actualFiles = Directory.EnumerateFiles(ledgerRoot, "*", SearchOption.AllDirectories)
            .Select(path => $"provider-session-ledger/{Path.GetRelativePath(ledgerRoot, path).Replace('\\', '/')}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(actualFiles.Order(), registeredFiles.Order());
    }
}

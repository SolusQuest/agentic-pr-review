using System.Text.Json;

namespace AgenticPrReview.Runtime.Tests.Ledger;

/// <summary>
/// Strict union-shape contract for the three additive ledger entry types in
/// protocol/fixtures/v1/manifest.json (issue #49 §12). The entry-type union is closed:
/// each type owns an exact allowed/required key set, expectation objects are
/// mutually exclusive on valid/invalid, predecessor is tied to the transition kind,
/// file registration is globally unique across ledger entries, and every committed
/// hash oracle is lowercase SHA-256 hex. Non-ledger (fixture/case) entries are only
/// observed for type classification and are intentionally left unconstrained.
/// </summary>
public sealed class LedgerManifestContractTests
{
    private static readonly string[] TransitionKinds = { "bootstrap", "continuation", "reset", "recovery_root" };

    [Fact]
    public void LedgerRestoreEntriesHaveExactShape()
    {
        var entries = LedgerEntriesOfType("ledger-restore");
        Assert.NotEmpty(entries);

        foreach (var entry in entries)
        {
            var context = Context(entry);
            var valid = entry.GetProperty("valid").GetBoolean();
            if (valid)
            {
                // valid → contentSha256 required, code forbidden.
                AssertKeys(entry, context, "type", "file", "valid", "contentSha256");
                AssertSha256Hex(entry.GetProperty("contentSha256").GetString(), context);
            }
            else
            {
                // invalid → code required, contentSha256 forbidden.
                AssertKeys(entry, context, "type", "file", "valid", "code");
                Assert.Equal(JsonValueKind.String, entry.GetProperty("code").ValueKind);
            }

            Assert.Equal(JsonValueKind.String, entry.GetProperty("file").ValueKind);
        }
    }

    [Fact]
    public void LedgerTransitionEntriesHaveExactShape()
    {
        var entries = LedgerEntriesOfType("ledger-transition");
        Assert.NotEmpty(entries);

        foreach (var entry in entries)
        {
            var context = Context(entry);
            var kind = entry.GetProperty("transition").GetProperty("kind").GetString()!;
            var hasPredecessor = entry.TryGetProperty("predecessor", out _);

            // Base keys plus predecessor exactly when the kind chains a predecessor.
            if (hasPredecessor)
            {
                AssertKeys(entry, context, "type", "file", "transition", "parseExpectation", "transitionExpectation", "predecessor");
                Assert.Equal(JsonValueKind.String, entry.GetProperty("predecessor").ValueKind);
            }
            else
            {
                AssertKeys(entry, context, "type", "file", "transition", "parseExpectation", "transitionExpectation");
            }

            Assert.Contains(kind, TransitionKinds);
            Assert.Equal(kind is "continuation" or "reset", hasPredecessor);

            AssertKeys(entry.GetProperty("transition"), context + ".transition", "kind", "expected");

            var parseExpectation = entry.GetProperty("parseExpectation");
            AssertKeys(parseExpectation, context + ".parseExpectation", "valid", "contentSha256");
            Assert.True(parseExpectation.GetProperty("valid").GetBoolean());
            AssertSha256Hex(parseExpectation.GetProperty("contentSha256").GetString(), context + ".parseExpectation");

            var expectation = entry.GetProperty("transitionExpectation");
            if (expectation.GetProperty("valid").GetBoolean())
            {
                AssertKeys(expectation, context + ".transitionExpectation", "valid", "candidateContentSha256");
                AssertSha256Hex(expectation.GetProperty("candidateContentSha256").GetString(), context + ".transitionExpectation");
            }
            else
            {
                AssertKeys(expectation, context + ".transitionExpectation", "valid", "code");
                Assert.Equal(JsonValueKind.String, expectation.GetProperty("code").ValueKind);
            }
        }
    }

    [Fact]
    public void LedgerBuildEntriesHaveExactShape()
    {
        var entries = LedgerEntriesOfType("ledger-build");
        Assert.NotEmpty(entries);

        foreach (var entry in entries)
        {
            var context = Context(entry);
            var kind = entry.GetProperty("transition").GetProperty("kind").GetString()!;
            var hasPredecessor = entry.TryGetProperty("predecessor", out _);

            if (hasPredecessor)
            {
                AssertKeys(entry, context, "type", "file", "transition", "buildExpectation", "predecessor");
                Assert.Equal(JsonValueKind.String, entry.GetProperty("predecessor").ValueKind);
            }
            else
            {
                AssertKeys(entry, context, "type", "file", "transition", "buildExpectation");
            }

            Assert.Contains(kind, TransitionKinds);
            Assert.Equal(kind is "continuation" or "reset", hasPredecessor);

            AssertKeys(entry.GetProperty("transition"), context + ".transition", "kind", "expected");

            var expectation = entry.GetProperty("buildExpectation");
            if (expectation.GetProperty("valid").GetBoolean())
            {
                AssertKeys(expectation, context + ".buildExpectation", "valid", "candidateContentSha256", "expectedCandidateFile");
                AssertSha256Hex(expectation.GetProperty("candidateContentSha256").GetString(), context + ".buildExpectation");
                Assert.Equal(JsonValueKind.String, expectation.GetProperty("expectedCandidateFile").ValueKind);
            }
            else
            {
                // code is required; causeCode is the only optional companion.
                if (expectation.TryGetProperty("causeCode", out _))
                {
                    AssertKeys(expectation, context + ".buildExpectation", "valid", "code", "causeCode");
                    Assert.Equal(JsonValueKind.String, expectation.GetProperty("causeCode").ValueKind);
                }
                else
                {
                    AssertKeys(expectation, context + ".buildExpectation", "valid", "code");
                }

                Assert.Equal(JsonValueKind.String, expectation.GetProperty("code").ValueKind);
            }
        }
    }

    [Fact]
    public void TransitionExpectedAndIdentitiesHaveExactKeySets()
    {
        var identityKeys = new[]
        {
            "repository", "headRepository", "pullRequest", "workflowIdentity", "trustedExecutionDomain",
            "providerId", "modelId", "adapterId", "templateId", "policyId", "toolDefinitionId", "cacheConfigId"
        };

        foreach (var entry in LedgerEntries())
        {
            var type = entry.GetProperty("type").GetString()!;
            if (type is not ("ledger-transition" or "ledger-build"))
            {
                continue;
            }

            var context = Context(entry);
            var transition = entry.GetProperty("transition");
            var kind = transition.GetProperty("kind").GetString()!;
            var expected = transition.GetProperty("expected");

            switch (kind)
            {
                case "bootstrap":
                    AssertKeys(expected, context + ".transition.expected", "identities", "sessionEpoch", "ledgerEpoch", "stateGeneration");
                    break;
                case "continuation":
                    AssertKeys(expected, context + ".transition.expected", "identities", "sessionEpoch", "ledgerEpoch",
                        "stateGeneration", "predecessorLedgerSha256", "predecessorLedgerEpoch", "predecessorStateGeneration");
                    break;
                case "reset":
                    AssertKeys(expected, context + ".transition.expected", "identities", "sessionEpoch", "ledgerEpoch",
                        "stateGeneration", "predecessorLedgerSha256", "predecessorManifestSha256",
                        "predecessorLedgerEpoch", "predecessorStateGeneration", "resetReason");
                    break;
                case "recovery_root":
                    AssertKeys(expected, context + ".transition.expected", "identities", "sessionEpoch", "ledgerEpoch",
                        "stateGeneration", "recoveryReason");
                    break;
                default:
                    throw new InvalidOperationException($"{context}: unknown transition kind '{kind}'.");
            }

            AssertKeys(expected.GetProperty("identities"), context + ".transition.expected.identities", identityKeys);
        }
    }

    [Fact]
    public void LedgerFilesAreGloballyUnique()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in LedgerEntries())
        {
            var file = entry.GetProperty("file").GetString()!;
            Assert.True(seen.Add(file), $"Ledger fixture file registered more than once: {file}");
        }
    }

    [Fact]
    public void ExpectedCandidateFilesReferenceRegisteredValidLedgerFiles()
    {
        // expectedCandidateFile must point at a file already registered as a valid
        // ledger-restore entry or as a ledger-transition candidate.
        var registered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in LedgerEntriesOfType("ledger-restore"))
        {
            if (entry.GetProperty("valid").GetBoolean())
            {
                registered.Add(entry.GetProperty("file").GetString()!);
            }
        }

        foreach (var entry in LedgerEntriesOfType("ledger-transition"))
        {
            registered.Add(entry.GetProperty("file").GetString()!);
        }

        foreach (var entry in LedgerEntriesOfType("ledger-build"))
        {
            var expectation = entry.GetProperty("buildExpectation");
            if (!expectation.GetProperty("valid").GetBoolean())
            {
                continue;
            }

            var expectedCandidateFile = expectation.GetProperty("expectedCandidateFile").GetString()!;
            Assert.True(
                registered.Contains(expectedCandidateFile),
                $"{Context(entry)}: expectedCandidateFile '{expectedCandidateFile}' is not a registered valid ledger-restore or ledger-transition file.");
        }
    }

    [Fact]
    public void NoUnknownLedgerEntryTypes()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(ManifestPath()));
        foreach (var entry in manifest.RootElement.EnumerateArray())
        {
            var type = entry.GetProperty("type").GetString()!;
            if (type.StartsWith("ledger-", StringComparison.Ordinal))
            {
                Assert.True(
                    type is "ledger-restore" or "ledger-transition" or "ledger-build",
                    $"Unknown ledger entry type '{type}'.");
            }
        }
    }

    private static string FixtureRoot()
    {
        return Path.Combine(AppContext.BaseDirectory, "protocol", "fixtures", "v1");
    }

    private static string ManifestPath()
    {
        return Path.Combine(FixtureRoot(), "manifest.json");
    }

    private static List<JsonElement> LedgerEntries()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(ManifestPath()));
        var entries = new List<JsonElement>();
        foreach (var entry in manifest.RootElement.EnumerateArray())
        {
            var type = entry.GetProperty("type").GetString()!;
            if (type is "ledger-restore" or "ledger-transition" or "ledger-build")
            {
                entries.Add(entry.Clone());
            }
        }

        return entries;
    }

    private static List<JsonElement> LedgerEntriesOfType(string type)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(ManifestPath()));
        var entries = new List<JsonElement>();
        foreach (var entry in manifest.RootElement.EnumerateArray())
        {
            if (entry.GetProperty("type").GetString() == type)
            {
                entries.Add(entry.Clone());
            }
        }

        return entries;
    }

    private static string Context(JsonElement entry)
    {
        var file = entry.TryGetProperty("file", out var fileElement) ? fileElement.GetString() : "?";
        return $"{entry.GetProperty("type").GetString()} entry '{file}'";
    }

    private static void AssertKeys(JsonElement element, string context, params string[] expectedKeys)
    {
        var actual = new List<string>();
        foreach (var property in element.EnumerateObject())
        {
            actual.Add(property.Name);
        }

        var expected = new List<string>(expectedKeys);
        actual.Sort(StringComparer.Ordinal);
        expected.Sort(StringComparer.Ordinal);
        Assert.True(
            string.Join(",", actual) == string.Join(",", expected),
            $"{context}: keys [{string.Join(", ", actual)}] != expected [{string.Join(", ", expected)}].");
    }

    private static void AssertSha256Hex(string? value, string context)
    {
        Assert.False(value is null, $"{context}: missing hash oracle.");
        Assert.True(value!.Length == 64, $"{context}: '{value}' is not 64 characters.");
        foreach (var c in value)
        {
            Assert.True(
                (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'),
                $"{context}: '{value}' is not lowercase sha256 hex.");
        }
    }
}

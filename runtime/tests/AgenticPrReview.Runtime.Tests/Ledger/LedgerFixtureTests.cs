using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using AgenticPrReview.Runtime.Ledger;

namespace AgenticPrReview.Runtime.Tests.Ledger;

/// <summary>
/// Drives every on-disk provider-session-ledger fixture through
/// <see cref="LedgerParser.ParseAndValidate"/> (ledger-restore) or
/// <see cref="LedgerAppend"/> (ledger-transition) and asserts that the
/// primary diagnostic code matches the manifest-declared expectation.
/// Also enforces that no fixture in the ledger directory is unreferenced
/// by the manifest.
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

    public static IEnumerable<object[]> TransitionEntries()
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(ManifestPath));
        var entries = new List<object[]>();
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            if (entry.GetProperty("type").GetString() != "ledger-transition") continue;
            var file = entry.GetProperty("file").GetString()!;
            var predecessor = entry.TryGetProperty("predecessor", out var pred) ? pred.GetString() : null;
            var transition = entry.GetProperty("transition");
            var kind = transition.GetProperty("kind").GetString()!;
            var expectedJson = transition.GetProperty("expected").GetRawText();
            var parseValid = entry.GetProperty("parseExpectation").GetProperty("valid").GetBoolean();
            var te = entry.GetProperty("transitionExpectation");
            var transitionValid = te.GetProperty("valid").GetBoolean();
            string? code = transitionValid ? null : te.GetProperty("code").GetString();
            string? candidateSha = transitionValid && te.TryGetProperty("candidateContentSha256", out var csha) ? csha.GetString() : null;
            entries.Add(new object[] { file, predecessor ?? string.Empty, kind, expectedJson, parseValid, transitionValid, code ?? string.Empty, candidateSha ?? string.Empty });
        }
        return entries;
    }

    [Theory]
    [MemberData(nameof(RestoreEntries))]
    public void ManifestRestore_MatchesParserOutcome(string file, bool valid, string expectedCode, string expectedSha)
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

    [Theory]
    [MemberData(nameof(TransitionEntries))]
    public void ManifestTransition_MatchesValidatorOutcome(
        string file, string predecessor, string kind, string expectedJson,
        bool parseValid, bool transitionValid, string code, string candidateSha)
    {
        var fixturesRoot = Path.Combine(AppContext.BaseDirectory, "protocol", "fixtures", "v1");
        var candidatePath = Path.Combine(fixturesRoot, file);
        var candidateBytes = File.ReadAllBytes(candidatePath);
        var candidateOutcome = LedgerParser.ParseAndValidate(candidateBytes);
        Assert.Equal(parseValid, candidateOutcome.Ledger is not null);
        if (!parseValid)
        {
            // Parse failed as expected; no transition validation to run.
            return;
        }

        ValidatedLedger? predecessorLedger = null;
        if (!string.IsNullOrEmpty(predecessor))
        {
            var predBytes = File.ReadAllBytes(Path.Combine(fixturesRoot, predecessor));
            var predOutcome = LedgerParser.ParseAndValidate(predBytes);
            Assert.NotNull(predOutcome.Ledger);
            predecessorLedger = predOutcome.Ledger!;
        }

        var candidate = candidateOutcome.Ledger!;
        var expected = ExpectedTransitionDeserializer.From(kind, expectedJson);
        var transitionOutcome = InvokeValidator(kind, expected, predecessorLedger, candidate);

        if (transitionValid)
        {
            Assert.True(transitionOutcome.Diagnostics.IsEmpty,
                $"Expected valid transition for {file}; diagnostics: " + DiagnosticsToString(transitionOutcome.Diagnostics));
            if (!string.IsNullOrEmpty(candidateSha))
                Assert.Equal(candidateSha, candidate.ContentSha256);
        }
        else
        {
            Assert.False(transitionOutcome.Diagnostics.IsEmpty,
                $"Expected transition failure for {file}");
            Assert.Equal(code, transitionOutcome.Diagnostics[0].Code);
        }
    }

    private static TransitionOutcome InvokeValidator(string kind, ExpectedTransition expected, ValidatedLedger? predecessor, ValidatedLedger candidate)
    {
        return kind switch
        {
            "bootstrap" => LedgerAppend.ValidateBootstrap((BootstrapTransition)expected, candidate),
            "recovery_root" => LedgerAppend.ValidateRecoveryRoot((RecoveryRootTransition)expected, candidate),
            "continuation" => LedgerAppend.ValidateContinuation((ContinuationTransition)expected, predecessor!, candidate),
            "reset" => LedgerAppend.ValidateReset((ResetTransition)expected, predecessor!, candidate),
            _ => throw new InvalidOperationException("unknown transition kind: " + kind),
        };
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

internal static class ExpectedTransitionDeserializer
{
    public static ExpectedTransition From(string kind, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var identities = ParseIdentities(root.GetProperty("identities"));
        var sessionEpoch = root.GetProperty("sessionEpoch").GetString()!;

        return kind switch
        {
            "bootstrap" => new BootstrapTransition(
                identities, sessionEpoch,
                root.GetProperty("stateGeneration").GetInt64(),
                root.GetProperty("ledgerEpoch").GetString()!),
            "recovery_root" => new RecoveryRootTransition(
                identities, sessionEpoch,
                root.GetProperty("ledgerEpoch").GetString()!,
                root.GetProperty("recoveryReason").GetString()!),
            "continuation" => new ContinuationTransition(
                identities, sessionEpoch,
                root.GetProperty("predecessorLedgerSha256").GetString()!,
                root.GetProperty("predecessorStateGeneration").GetInt64(),
                root.GetProperty("predecessorLedgerEpoch").GetString()!,
                root.GetProperty("stateGeneration").GetInt64(),
                root.GetProperty("ledgerEpoch").GetString()!),
            "reset" => new ResetTransition(
                identities, sessionEpoch,
                root.GetProperty("predecessorLedgerSha256").GetString()!,
                root.GetProperty("predecessorManifestSha256").GetString()!,
                root.GetProperty("predecessorStateGeneration").GetInt64(),
                root.GetProperty("predecessorLedgerEpoch").GetString()!,
                root.GetProperty("stateGeneration").GetInt64(),
                root.GetProperty("ledgerEpoch").GetString()!,
                root.GetProperty("resetReason").GetString()!),
            _ => throw new InvalidOperationException("unknown kind: " + kind),
        };
    }

    private static ExpectedIdentities ParseIdentities(JsonElement id) => new(
        Repository: id.GetProperty("repository").GetString()!,
        HeadRepository: id.GetProperty("headRepository").GetString()!,
        PullRequest: id.GetProperty("pullRequest").GetInt32(),
        WorkflowIdentity: id.GetProperty("workflowIdentity").GetString()!,
        TrustedExecutionDomain: id.GetProperty("trustedExecutionDomain").GetString()!,
        ProviderId: id.GetProperty("providerId").GetString()!,
        ModelId: id.GetProperty("modelId").GetString()!,
        AdapterId: id.GetProperty("adapterId").GetString()!,
        TemplateId: id.GetProperty("templateId").GetString()!,
        PolicyId: id.GetProperty("policyId").GetString()!,
        ToolDefinitionId: id.GetProperty("toolDefinitionId").GetString()!,
        CacheConfigId: id.GetProperty("cacheConfigId").GetString()!);
}

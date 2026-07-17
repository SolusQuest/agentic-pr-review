using System.Collections.Immutable;
using System.Text.Json;
using AgenticPrReview.Runtime.Ledger;

namespace AgenticPrReview.Runtime.Tests.Ledger;

public sealed class LedgerFixtureTests
{
    [Fact]
    public void LedgerRestoreFixturesMatchDeclaredExpectations()
    {
        var root = FixtureRoot();
        var ledgerRoot = Path.Combine(root, "provider-session-ledger");
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "manifest.json")));
        var registeredFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in manifest.RootElement.EnumerateArray())
        {
            var type = entry.GetProperty("type").GetString();
            if (type is "ledger-transition" or "ledger-build")
            {
                // Owned by the transition / build runners below; registered here so the
                // directory sweep still accounts for every file exactly once.
                registeredFiles.Add(entry.GetProperty("file").GetString()!);
                continue;
            }

            if (type is not "ledger-restore")
            {
                continue;
            }

            var relativePath = entry.GetProperty("file").GetString()!;
            var bytes = File.ReadAllBytes(Path.Combine(root, relativePath));
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

    [Fact]
    public void LedgerTransitionFixturesMatchDeclaredExpectations()
    {
        var root = FixtureRoot();
        foreach (var entry in ManifestEntriesOfType(root, "ledger-transition"))
        {
            var relativePath = entry.GetProperty("file").GetString()!;
            var candidate = ParseValidLedger(Path.Combine(root, relativePath));

            var parseExpectation = entry.GetProperty("parseExpectation");
            Assert.True(parseExpectation.GetProperty("valid").GetBoolean());
            Assert.Equal(parseExpectation.GetProperty("contentSha256").GetString(), candidate.ContentSha256);

            var transition = entry.GetProperty("transition");
            var kind = transition.GetProperty("kind").GetString()!;
            var expected = ParseExpectedTransition(kind, transition.GetProperty("expected"));

            var hasPredecessor = entry.TryGetProperty("predecessor", out var predecessorElement);
            Assert.Equal(kind is "continuation" or "reset", hasPredecessor);
            var predecessor = hasPredecessor
                ? ParseValidLedger(Path.Combine(root, predecessorElement.GetString()!))
                : null;

            var outcome = kind switch
            {
                "bootstrap" => LedgerTransitionValidator.ValidateBootstrap((BootstrapTransition)expected, candidate),
                "continuation" => LedgerTransitionValidator.ValidateContinuation((ContinuationTransition)expected, predecessor!, candidate),
                "reset" => LedgerTransitionValidator.ValidateReset((ResetTransition)expected, predecessor!, candidate),
                "recovery_root" => LedgerTransitionValidator.ValidateRecoveryRoot((RecoveryRootTransition)expected, candidate),
                _ => throw new InvalidOperationException($"Unknown transition kind {kind} in {relativePath}.")
            };

            var expectation = entry.GetProperty("transitionExpectation");
            if (expectation.GetProperty("valid").GetBoolean())
            {
                Assert.Empty(outcome.Diagnostics);
                Assert.Equal(expectation.GetProperty("candidateContentSha256").GetString(), candidate.ContentSha256);
            }
            else
            {
                Assert.NotEmpty(outcome.Diagnostics);
                Assert.Equal(expectation.GetProperty("code").GetString(), outcome.Diagnostics[0].Code);
            }
        }
    }

    [Fact]
    public void LedgerBuildFixturesMatchDeclaredExpectations()
    {
        var root = FixtureRoot();
        foreach (var entry in ManifestEntriesOfType(root, "ledger-build"))
        {
            var relativePath = entry.GetProperty("file").GetString()!;
            using var scenario = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, relativePath)));

            var transition = entry.GetProperty("transition");
            var kind = transition.GetProperty("kind").GetString()!;
            var expected = ParseExpectedTransition(kind, transition.GetProperty("expected"));

            var hasPredecessor = entry.TryGetProperty("predecessor", out var predecessorElement);
            Assert.Equal(kind is "continuation" or "reset", hasPredecessor);
            var predecessor = hasPredecessor
                ? ParseValidLedger(Path.Combine(root, predecessorElement.GetString()!))
                : null;

            var contextElement = scenario.RootElement.GetProperty("context");
            var context = LedgerBuilder.BuildReviewContext(
                ParseContextSource(contextElement), expected.Identities, ParseInteraction(contextElement));
            Assert.NotNull(context.Value);

            var outcomeElement = scenario.RootElement.GetProperty("outcome");
            var outcome = LedgerBuilder.BuildReviewOutcome(
                ParseOutcomeSource(outcomeElement), ParseInteraction(outcomeElement));
            Assert.NotNull(outcome.Value);

            var candidateOutcome = kind switch
            {
                "bootstrap" => LedgerBuilder.CreateBootstrap((BootstrapTransition)expected, context.Value, outcome.Value),
                "continuation" => LedgerBuilder.AppendContinuation((ContinuationTransition)expected, predecessor!, context.Value, outcome.Value),
                "reset" => LedgerBuilder.CreateReset((ResetTransition)expected, predecessor!, context.Value, outcome.Value),
                "recovery_root" => LedgerBuilder.CreateRecoveryRoot((RecoveryRootTransition)expected, context.Value, outcome.Value),
                _ => throw new InvalidOperationException($"Unknown transition kind {kind} in {relativePath}.")
            };

            var expectation = entry.GetProperty("buildExpectation");
            if (expectation.GetProperty("valid").GetBoolean())
            {
                Assert.NotNull(candidateOutcome.Candidate);
                Assert.Equal(expectation.GetProperty("candidateContentSha256").GetString(), candidateOutcome.Candidate!.ContentSha256);

                var expectedCandidateBytes = File.ReadAllBytes(
                    Path.Combine(root, expectation.GetProperty("expectedCandidateFile").GetString()!));
                Assert.Equal(expectedCandidateBytes, candidateOutcome.Candidate.CanonicalBytes.ToArray());
            }
            else
            {
                Assert.Null(candidateOutcome.Candidate);
                Assert.NotEmpty(candidateOutcome.Diagnostics);
                Assert.Equal(expectation.GetProperty("code").GetString(), candidateOutcome.Diagnostics[0].Code);
                if (expectation.TryGetProperty("causeCode", out var causeCode))
                {
                    Assert.Equal(causeCode.GetString(), candidateOutcome.Diagnostics[0].CauseCode);
                }
            }
        }
    }

    private static string FixtureRoot()
    {
        return Path.Combine(AppContext.BaseDirectory, "protocol", "fixtures", "v1");
    }

    private static List<JsonElement> ManifestEntriesOfType(string root, string type)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "manifest.json")));
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

    private static ValidatedLedger ParseValidLedger(string path)
    {
        var outcome = LedgerParser.ParseAndValidate(File.ReadAllBytes(path));
        Assert.NotNull(outcome.Ledger);
        return outcome.Ledger!;
    }

    private static ExpectedTransition ParseExpectedTransition(string kind, JsonElement expected)
    {
        var identities = ParseIdentities(expected.GetProperty("identities"));
        var sessionEpoch = expected.GetProperty("sessionEpoch").GetString()!;
        var ledgerEpoch = expected.GetProperty("ledgerEpoch").GetString()!;
        var stateGeneration = expected.GetProperty("stateGeneration").GetInt64();

        return kind switch
        {
            "bootstrap" => new BootstrapTransition(identities, sessionEpoch, ledgerEpoch, stateGeneration),
            "continuation" => new ContinuationTransition(
                identities, sessionEpoch, ledgerEpoch,
                expected.GetProperty("predecessorLedgerSha256").GetString()!,
                expected.GetProperty("predecessorLedgerEpoch").GetString()!,
                expected.GetProperty("predecessorStateGeneration").GetInt64(),
                stateGeneration),
            "reset" => new ResetTransition(
                identities, sessionEpoch, ledgerEpoch,
                expected.GetProperty("predecessorLedgerSha256").GetString()!,
                expected.GetProperty("predecessorManifestSha256").GetString()!,
                expected.GetProperty("predecessorLedgerEpoch").GetString()!,
                expected.GetProperty("predecessorStateGeneration").GetInt64(),
                stateGeneration,
                expected.GetProperty("resetReason").GetString()!),
            "recovery_root" => new RecoveryRootTransition(
                identities, sessionEpoch, ledgerEpoch, stateGeneration,
                expected.GetProperty("recoveryReason").GetString()!),
            _ => throw new InvalidOperationException($"Unknown transition kind {kind}.")
        };
    }

    private static ExpectedIdentities ParseIdentities(JsonElement element)
    {
        return new ExpectedIdentities(
            element.GetProperty("repository").GetString()!,
            element.GetProperty("headRepository").GetString()!,
            element.GetProperty("pullRequest").GetInt32(),
            element.GetProperty("workflowIdentity").GetString()!,
            element.GetProperty("trustedExecutionDomain").GetString()!,
            element.GetProperty("providerId").GetString()!,
            element.GetProperty("modelId").GetString()!,
            element.GetProperty("adapterId").GetString()!,
            element.GetProperty("templateId").GetString()!,
            element.GetProperty("policyId").GetString()!,
            element.GetProperty("toolDefinitionId").GetString()!,
            element.GetProperty("cacheConfigId").GetString()!);
    }

    private static InteractionIdentity ParseInteraction(JsonElement recordElement)
    {
        var interaction = recordElement.GetProperty("interaction");
        return new InteractionIdentity(
            interaction.GetProperty("interactionId").GetString()!,
            interaction.GetProperty("interactionOrdinal").GetInt64());
    }

    private static ValidatedContextSource ParseContextSource(JsonElement element)
    {
        var changedFiles = ImmutableArray.CreateBuilder<LedgerChangedFile>();
        foreach (var file in element.GetProperty("changedFiles").EnumerateArray())
        {
            LedgerBoundedPatch? patch = null;
            if (file.TryGetProperty("patch", out var patchElement) && patchElement.ValueKind == JsonValueKind.Object)
            {
                patch = new LedgerBoundedPatch
                {
                    Sha256 = patchElement.GetProperty("sha256").GetString()!,
                    Truncated = patchElement.GetProperty("truncated").GetBoolean(),
                    MaxChars = patchElement.GetProperty("maxChars").GetInt64()
                };
            }

            changedFiles.Add(new LedgerChangedFile
            {
                Path = file.GetProperty("path").GetString()!,
                PreviousPath = ReadNullableString(file, "previousPath"),
                Status = file.GetProperty("status").GetString()!,
                Additions = file.GetProperty("additions").GetInt64(),
                Deletions = file.GetProperty("deletions").GetInt64(),
                Changes = file.GetProperty("changes").GetInt64(),
                Patch = patch
            });
        }

        return new ValidatedContextSource
        {
            SubjectDigest = element.GetProperty("subjectDigest").GetString()!,
            ReviewedHeadSha = element.GetProperty("reviewedHeadSha").GetString()!,
            ReviewedBaseSha = element.GetProperty("reviewedBaseSha").GetString()!,
            ChangedFiles = changedFiles.ToImmutable()
        };
    }

    private static ValidatedOutcomeSource ParseOutcomeSource(JsonElement element)
    {
        var findings = ImmutableArray.CreateBuilder<LedgerFinding>();
        foreach (var finding in element.GetProperty("findings").EnumerateArray())
        {
            findings.Add(new LedgerFinding
            {
                Severity = finding.GetProperty("severity").GetString()!,
                Confidence = finding.GetProperty("confidence").GetString()!,
                Category = finding.GetProperty("category").GetString()!,
                Title = finding.GetProperty("title").GetString()!,
                Body = finding.GetProperty("body").GetString()!,
                Evidence = ReadNullableString(finding, "evidence"),
                Path = ReadNullableString(finding, "path"),
                StartLine = ReadNullableLong(finding, "startLine"),
                EndLine = ReadNullableLong(finding, "endLine"),
                SuggestedAction = ReadNullableString(finding, "suggestedAction"),
                InlinePreference = ReadNullableString(finding, "inlinePreference")
            });
        }

        var limitations = ImmutableArray.CreateBuilder<string>();
        foreach (var limitation in element.GetProperty("limitations").EnumerateArray())
        {
            limitations.Add(limitation.GetString()!);
        }

        return new ValidatedOutcomeSource
        {
            Summary = element.GetProperty("summary").GetString()!,
            Findings = findings.ToImmutable(),
            Limitations = limitations.ToImmutable()
        };
    }

    private static string? ReadNullableString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    private static long? ReadNullableLong(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number)
        {
            return value.GetInt64();
        }

        return null;
    }
}

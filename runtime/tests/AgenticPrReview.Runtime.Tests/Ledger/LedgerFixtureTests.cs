using System.Text.Json;
using AgenticPrReview.Runtime;
using AgenticPrReview.Runtime.Ledger;
using Xunit.Sdk;

namespace AgenticPrReview.Runtime.Tests.Ledger;

/// <summary>
/// Data-driven ledger fixture tests. Each ledger-restore entry exercises
/// <see cref="LedgerParser"/>; each ledger-transition entry parses both
/// predecessor and candidate then invokes the appropriate <see cref="LedgerAppend"/>
/// method; each ledger-build entry constructs the requested transition through
/// <see cref="LedgerBuilder"/>.
/// </summary>
public sealed class LedgerFixtureTests
{
    [Fact]
    public void AllLedgerFixturesMatchTheirDeclaredExpectations()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "protocol", "fixtures", "v1");
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "manifest.json")));
        foreach (var entry in manifest.RootElement.EnumerateArray())
        {
            var type = entry.GetProperty("type").GetString();
            switch (type)
            {
                case "ledger-restore":
                    RunRestore(entry, root);
                    break;
                case "ledger-transition":
                    RunTransition(entry, root);
                    break;
                case "ledger-build":
                    RunBuild(entry, root);
                    break;
                default:
                    // Non-ledger entries are validated by ProtocolFixtureTests.
                    continue;
            }
        }
    }

    private static void RunRestore(JsonElement entry, string root)
    {
        var file = entry.GetProperty("file").GetString()!;
        var bytes = File.ReadAllBytes(Path.Combine(root, file));
        var expectation = entry.GetProperty("expectation");
        var expectedValid = expectation.GetProperty("valid").GetBoolean();
        var result = LedgerParser.ParseAndValidate(bytes);
        if (expectedValid)
        {
            if (result.Ledger is null)
            {
                throw new XunitException($"[{file}] expected valid but got code={result.Failure?.Code} msg={result.Failure?.Message}");
            }
            var sha = expectation.GetProperty("contentSha256").GetString();
            Assert.Equal(sha, result.Ledger.ContentSha256);
        }
        else
        {
            if (result.Ledger is not null)
            {
                throw new XunitException($"[{file}] expected invalid but parser accepted");
            }
            var code = expectation.GetProperty("code").GetString();
            if (code != result.Failure!.Code)
                throw new XunitException($"[{file}] expected code={code} but got {result.Failure.Code} ({result.Failure.Message})");
        }
    }

    private static void RunTransition(JsonElement entry, string root)
    {
        var file = entry.GetProperty("file").GetString()!;
        var candidateBytes = File.ReadAllBytes(Path.Combine(root, file));
        var parse = entry.GetProperty("parseExpectation");
        var expectedParseValid = parse.GetProperty("valid").GetBoolean();
        var parseResult = LedgerParser.ParseAndValidate(candidateBytes);
        if (!expectedParseValid)
        {
            Assert.Null(parseResult.Ledger);
            return;
        }
        if (parseResult.Ledger is null)
        {
            throw new XunitException($"[{file}] parseExpectation valid but got code={parseResult.Failure?.Code}");
        }
        // Optionally validate declared contentSha256.
        if (parse.TryGetProperty("contentSha256", out var sha256Element))
        {
            Assert.Equal(sha256Element.GetString(), parseResult.Ledger.ContentSha256);
        }

        // Load predecessor if applicable.
        ValidatedLedger? predecessor = null;
        if (entry.TryGetProperty("predecessor", out var predEl))
        {
            var predBytes = File.ReadAllBytes(Path.Combine(root, predEl.GetString()!));
            var predResult = LedgerParser.ParseAndValidate(predBytes);
            predecessor = predResult.Ledger ?? throw new XunitException(
                $"[{file}] predecessor failed to parse: {predResult.Failure?.Code}");
        }

        var transition = entry.GetProperty("transition");
        var kind = transition.GetProperty("kind").GetString()!;
        var expected = ParseExpectedTransition(kind, transition.GetProperty("expected"));

        TransitionOutcome outcome = kind switch
        {
            "bootstrap" => LedgerAppend.ValidateBootstrap(parseResult.Ledger, (BootstrapTransition)expected),
            "continuation" => LedgerAppend.ValidateContinuation(predecessor!, parseResult.Ledger, (ContinuationTransition)expected),
            "reset" => LedgerAppend.ValidateReset(predecessor!, parseResult.Ledger, (ResetTransition)expected),
            "recovery" => LedgerAppend.ValidateRecovery(parseResult.Ledger, (RecoveryTransition)expected),
            _ => throw new InvalidOperationException("Unknown transition kind: " + kind),
        };
        var trExpectation = entry.GetProperty("transitionExpectation");
        var expectedTrValid = trExpectation.GetProperty("valid").GetBoolean();
        if (expectedTrValid)
        {
            if (outcome.Candidate is null)
                throw new XunitException($"[{file}] transition expected valid but got code={outcome.Failure?.Code}");
            if (trExpectation.TryGetProperty("candidateContentSha256", out var candSha))
                Assert.Equal(candSha.GetString(), outcome.Candidate.ContentSha256);
        }
        else
        {
            if (outcome.Candidate is not null)
                throw new XunitException($"[{file}] transition expected invalid but validator accepted");
            Assert.Equal(trExpectation.GetProperty("code").GetString(), outcome.Failure!.Code);
        }
    }

    private static void RunBuild(JsonElement entry, string root)
    {
        var file = entry.GetProperty("file").GetString()!;
        var scenarioBytes = File.ReadAllBytes(Path.Combine(root, file));
        using var scenario = JsonDocument.Parse(scenarioBytes);
        var context = ParseValidatedContext(scenario.RootElement.GetProperty("contextSource"));
        var outcome = ParseValidatedOutcome(scenario.RootElement.GetProperty("outcomeSource"));

        ValidatedLedger? predecessor = null;
        if (entry.TryGetProperty("predecessor", out var predEl))
        {
            var predBytes = File.ReadAllBytes(Path.Combine(root, predEl.GetString()!));
            var predResult = LedgerParser.ParseAndValidate(predBytes);
            predecessor = predResult.Ledger ?? throw new XunitException(
                $"[{file}] predecessor failed to parse: {predResult.Failure?.Code}");
        }

        var transition = entry.GetProperty("transition");
        var kind = transition.GetProperty("kind").GetString()!;
        var expected = ParseExpectedTransition(kind, transition.GetProperty("expected"));

        // Build the pair records.
        var ctxOutcome = LedgerBuilder.BuildReviewContext(context, expected.Identities, InteractionId(kind, predecessor));
        if (ctxOutcome.Record is null)
        {
            var ex = entry.GetProperty("buildExpectation");
            AssertBuildFailure(file, ex, ctxOutcome.Failure);
            return;
        }
        var ocOutcome = LedgerBuilder.BuildReviewOutcome(outcome, InteractionId(kind, predecessor));
        if (ocOutcome.Record is null)
        {
            var ex = entry.GetProperty("buildExpectation");
            AssertBuildFailure(file, ex, ocOutcome.Failure);
            return;
        }

        BuildOutcome build = kind switch
        {
            "bootstrap" => LedgerBuilder.CreateBootstrap((BootstrapTransition)expected, ctxOutcome.Record, ocOutcome.Record),
            "continuation" => LedgerBuilder.AppendContinuation(predecessor!, (ContinuationTransition)expected, ctxOutcome.Record, ocOutcome.Record),
            "reset" => LedgerBuilder.CreateReset(predecessor!, (ResetTransition)expected, ctxOutcome.Record, ocOutcome.Record),
            "recovery" => LedgerBuilder.CreateRecovery((RecoveryTransition)expected, ctxOutcome.Record, ocOutcome.Record),
            _ => throw new InvalidOperationException("Unknown build kind: " + kind),
        };

        var expectation = entry.GetProperty("buildExpectation");
        var expectedValid = expectation.GetProperty("valid").GetBoolean();
        if (expectedValid)
        {
            if (build.Ledger is null)
                throw new XunitException($"[{file}] build expected valid but got code={build.Failure?.Code}");
            if (expectation.TryGetProperty("candidateContentSha256", out var candSha))
                Assert.Equal(candSha.GetString(), build.Ledger.ContentSha256);
        }
        else
        {
            AssertBuildFailure(file, expectation, build.Failure);
            Assert.Null(build.Ledger);
        }
    }

    private static InteractionIdentity InteractionId(string kind, ValidatedLedger? predecessor)
    {
        // Deterministic id derivation for build scenarios: build 64-hex id from predecessor
        // hash suffix + ordinal. We only use this in ledger-build fixtures which currently
        // exercise continuation over-bound scenarios; the id itself is unimportant for the
        // over-bound assertion.
        int ordinal = kind switch
        {
            "continuation" => (predecessor?.Model.Records.Length ?? 0) / 2,
            "reset" or "bootstrap" or "recovery" => 0,
            _ => 0,
        };
        var prefix = ordinal.ToString("x8");
        return new InteractionIdentity(prefix + new string('0', 64 - prefix.Length), ordinal);
    }

    private static void AssertBuildFailure(string file, JsonElement expectation, LedgerDiagnostic? failure)
    {
        if (failure is null)
            throw new XunitException($"[{file}] expected build failure but got success");
        Assert.Equal(expectation.GetProperty("code").GetString(), failure.Code);
        if (expectation.TryGetProperty("causeCode", out var cause))
            Assert.Equal(cause.GetString(), failure.CauseCode);
    }

    private static ValidatedContextSource ParseValidatedContext(JsonElement e)
    {
        var files = new List<ValidatedChangedFileSource>();
        foreach (var f in e.GetProperty("changedFiles").EnumerateArray())
        {
            ValidatedPatchSource? patch = null;
            if (f.TryGetProperty("patch", out var pp))
            {
                patch = new ValidatedPatchSource(
                    Sha256: pp.GetProperty("sha256").GetString()!,
                    Truncated: pp.GetProperty("truncated").GetBoolean(),
                    MaxChars: pp.GetProperty("maxChars").GetInt32());
            }
            files.Add(new ValidatedChangedFileSource(
                Path: f.GetProperty("path").GetString()!,
                PreviousPath: f.TryGetProperty("previousPath", out var pv) ? pv.GetString() : null,
                Status: f.GetProperty("status").GetString()!,
                Additions: f.GetProperty("additions").GetInt32(),
                Deletions: f.GetProperty("deletions").GetInt32(),
                Changes: f.GetProperty("changes").GetInt32(),
                Patch: patch));
        }
        return new ValidatedContextSource(
            ReviewedHeadSha: e.GetProperty("reviewedHeadSha").GetString()!,
            ReviewedBaseSha: e.GetProperty("reviewedBaseSha").GetString()!,
            ChangedFiles: files.ToImmutableArray());
    }

    private static ValidatedOutcomeSource ParseValidatedOutcome(JsonElement e)
    {
        var findings = new List<ValidatedFindingSource>();
        if (e.TryGetProperty("findings", out var fs))
        {
            foreach (var f in fs.EnumerateArray())
            {
                findings.Add(new ValidatedFindingSource(
                    Severity: f.GetProperty("severity").GetString()!,
                    Confidence: f.GetProperty("confidence").GetString()!,
                    Category: f.GetProperty("category").GetString()!,
                    Title: f.GetProperty("title").GetString()!,
                    Body: f.GetProperty("body").GetString()!,
                    Path: NullableString(f, "path"),
                    StartLine: NullableInt(f, "startLine"),
                    EndLine: NullableInt(f, "endLine"),
                    Evidence: OptString(f, "evidence"),
                    SuggestedAction: OptString(f, "suggestedAction"),
                    InlinePreference: OptString(f, "inlinePreference")));
            }
        }
        var lims = new List<string>();
        if (e.TryGetProperty("limitations", out var lArr))
        {
            foreach (var l in lArr.EnumerateArray())
            {
                lims.Add(l.GetString()!);
            }
        }
        return new ValidatedOutcomeSource(
            Summary: e.GetProperty("summary").GetString()!,
            Findings: findings.ToImmutableArray(),
            Limitations: lims.ToImmutableArray());
    }

    private static string? NullableString(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : null) : null;
    private static int? NullableInt(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.Number ? v.GetInt32() : (int?)null) : null;
    private static string? OptString(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static ExpectedTransition ParseExpectedTransition(string kind, JsonElement expected)
    {
        var identities = ParseIdentities(expected.GetProperty("identities"));
        return kind switch
        {
            "bootstrap" => new BootstrapTransition(identities,
                expected.GetProperty("stateGeneration").GetInt32(),
                expected.GetProperty("ledgerEpoch").GetInt32()),
            "continuation" => new ContinuationTransition(identities,
                expected.GetProperty("predecessorLedgerSha256").GetString()!,
                expected.GetProperty("predecessorStateGeneration").GetInt32(),
                expected.GetProperty("stateGeneration").GetInt32(),
                expected.GetProperty("ledgerEpoch").GetInt32()),
            "reset" => new ResetTransition(identities,
                expected.GetProperty("predecessorLedgerSha256").GetString()!,
                expected.GetProperty("predecessorManifestSha256").GetString()!,
                expected.GetProperty("predecessorStateGeneration").GetInt32(),
                expected.GetProperty("stateGeneration").GetInt32(),
                expected.GetProperty("ledgerEpoch").GetInt32(),
                expected.GetProperty("resetReason").GetString()!),
            "recovery" => new RecoveryTransition(identities,
                expected.GetProperty("stateGeneration").GetInt32(),
                expected.GetProperty("ledgerEpoch").GetInt32(),
                expected.GetProperty("recoveryReason").GetString()!),
            _ => throw new InvalidOperationException("Unknown kind: " + kind),
        };
    }

    private static ExpectedIdentities ParseIdentities(JsonElement e) => new(
        Repository: e.GetProperty("repository").GetString()!,
        HeadRepository: e.GetProperty("headRepository").GetString()!,
        PullRequest: e.GetProperty("pullRequest").GetInt32(),
        WorkflowIdentity: e.GetProperty("workflowIdentity").GetString()!,
        TrustedExecutionDomain: e.GetProperty("trustedExecutionDomain").GetString()!,
        SessionEpoch: e.GetProperty("sessionEpoch").GetString()!,
        ProviderId: e.GetProperty("providerId").GetString()!,
        ModelId: e.GetProperty("modelId").GetString()!,
        AdapterId: e.GetProperty("adapterId").GetString()!,
        TemplateId: e.GetProperty("templateId").GetString()!,
        PolicyId: e.GetProperty("policyId").GetString()!,
        ToolDefinitionId: e.GetProperty("toolDefinitionId").GetString()!,
        CacheConfigId: e.GetProperty("cacheConfigId").GetString()!);
}


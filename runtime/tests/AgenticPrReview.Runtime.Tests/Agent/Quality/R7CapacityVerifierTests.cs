using System.Security.Cryptography;
using System.Text.Json;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Capacity;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Agent.Quality;

public sealed class R7CapacityVerifierTests
{
    // Authored independently: shrinking the production case inventory also breaks corpus admission.
    private static readonly CapacityCase[] Cases =
    [
        new("default64", "success", 64, 7, 64), new("default_restore", "success", 1, 0, 64),
        new("capacity", "capacity", 1, 0, 64), new("context", "context", 0, 0, 64),
        new("output", "output", 1, 0, 64), new("response", "response", 1, 0, 64), new("deadline", "deadline", 1, 0, 64),
        new("role", "role", 0, 0, 64), new("association", "association", 0, 0, 64),
        new("policy", "policy", 0, 0, 64), new("scope", "scope", 0, 0, 64),
        new("reset", "reset", 2, 1, 64), new("reset_restore", "success", 1, 0, 64),
        new("configured128", "success", 128, 3, 128), new("configured_restore", "success", 1, 0, 128),
    ];
    private static byte[] Corpus => JsonSerializer.SerializeToUtf8Bytes(new CapacityCorpus("r7-capacity-v1", 320, 64, 512, Cases),
        CapacityJson.Default.CapacityCorpus);

    [Fact]
    public void CompleteInventoryIsAdmittedAndUncleanSourceIsRejected()
    {
        var report = Report();
        Assert.Null(CapacityVerifier.Verify(report, Corpus));
        Assert.NotNull(CapacityVerifier.Verify(report with { SourceClean = false }, Corpus));
        Assert.NotNull(CapacityVerifier.Verify(report with { SourceTree = new('d', 40) }, Corpus));
        Assert.NotNull(CapacityVerifier.Verify(report with { Cleanup = false }, Corpus));
        Assert.NotNull(CapacityVerifier.Verify(report, Corpus[..^1]));
    }

    [Fact]
    public void MissingDuplicateOrForgedCapacityProofNeverPasses()
    {
        var report = Report();
        Assert.NotNull(CapacityVerifier.Verify(report with { Cases = report.Cases[..^1] }, Corpus));
        var duplicate = report.Cases.ToArray(); duplicate[1] = duplicate[0];
        Assert.NotNull(CapacityVerifier.Verify(report with { Cases = duplicate }, Corpus));
        foreach (var index in new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14 })
        {
            var cases = report.Cases.ToArray();
            cases[index] = cases[index] with { RolesAndAssociations = false };
            Assert.NotNull(CapacityVerifier.Verify(report with { Cases = cases }, Corpus));
            cases[index] = report.Cases[index] with { Calls = 0, Sends = 0, Code = "completed" };
            Assert.NotNull(CapacityVerifier.Verify(report with { Cases = cases }, Corpus));
        }
    }

    [Theory]
    [InlineData("domain")]
    [InlineData("receipt-domain")]
    [InlineData("wire-bytes")]
    [InlineData("context-count")]
    [InlineData("context-stimulus")]
    [InlineData("response-sentinel")]
    [InlineData("reasoning-stimulus")]
    public void TypedDomainAndActualStimulusBindingsCannotBeForged(string mutation)
    {
        var report = Report();
        var cases = report.Cases.ToArray();
        switch (mutation)
        {
            case "domain": report = report with { ObservationDomain = "provider-wire" }; break;
            case "receipt-domain": cases[0] = cases[0] with { ObservationDomain = "provider-wire" }; break;
            case "wire-bytes": cases[0] = cases[0] with { Metrics = cases[0].Metrics with { ProviderResponseBytes = 0 } }; break;
            case "context-count": cases[3] = cases[3] with { Calls = 1 }; break;
            case "context-stimulus": cases[3] = cases[3] with { Metrics = cases[3].Metrics with { InitialUserContentBytes = 1048576 } }; break;
            case "response-sentinel": cases[5] = cases[5] with { Metrics = cases[5].Metrics with { GenericResponseAdmissionBytes = 2097152 } }; break;
            case "reasoning-stimulus": cases[2] = cases[2] with { Metrics = cases[2].Metrics with { ReasoningContentBytes = 1048576 } }; break;
        }
        Assert.NotNull(CapacityVerifier.Verify(report with { Cases = cases }, Corpus));
    }

    [Fact]
    public void MissingDomainAndOldWireSchemaCannotBeReadAsTypedEvidence()
    {
        var value = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.SerializeToUtf8Bytes(Report(), CapacityJson.Default.CapacityReport))!;
        value.AsObject().Remove("observationDomain");
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(value.ToJsonString(), CapacityJson.Default.CapacityReport));
        value["observationDomain"] = "apr-historical-typed-logical-capacity-v1";
        value["cases"]![0]!["metrics"]!["providerRequestBytes"] = 10000;
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(value.ToJsonString(), CapacityJson.Default.CapacityReport));
    }

    [Fact]
    public void IncompleteHostCannotMasqueradeAsNoFindingsOrPreservedState()
    {
        var report = Report();
        Assert.NotNull(CapacityVerifier.Verify(report with { HostCases = report.HostCases[..^1] }, Corpus));
        foreach (var replacement in new[]
        {
            report.HostCases[1] with { Status = "Reviewed", ExitCode = 0, AgentCode = "completed" },
            report.HostCases[1] with { NoSuccessSummary = false },
            report.HostCases[1] with { NoCandidateMutation = false },
            report.HostCases[1] with { PredecessorPreserved = false },
            report.HostCases[1] with { NoPublicationMutation = false },
        })
        {
            var host = report.HostCases.ToArray(); host[1] = replacement;
            Assert.NotNull(CapacityVerifier.Verify(report with { HostCases = host }, Corpus));
        }
    }

    [Fact]
    public void ParityExcludesObservedCostsButRetainsCapacityAndFailureSemantics()
    {
        var first = Report(); var cases = first.Cases.Select(item => item with
        {
            StartupId = new('b', 32), ProcessId = item.ProcessId + 5000,
            Metrics = item.Metrics with { ElapsedMilliseconds = 10000, AllocatedBytes = 9000000, WorkingSetBytes = 50000,
                PeakWorkingSetBytes = 75000, StoredBytes = item.Metrics.StoredBytes + 17 },
        }).ToArray();
        var second = first with { Cases = cases };
        Assert.True(CapacityVerifier.Equivalent(first, second));
        cases[0] = cases[0] with { Metrics = cases[0].Metrics with { SessionBytes = 2 } };
        Assert.False(CapacityVerifier.Equivalent(first, second));
    }

    [Fact]
    public void CurrentHostEvidenceHasAnIndependentFiniteDomainAndCannotBorrowNativeHistory()
    {
        var historical = Report();
        var original = JsonSerializer.SerializeToUtf8Bytes(historical, CapacityJson.Default.CapacityReport);
        var current = historical with { HostCases = historical.HostCases.Select((item, index) => item with
        {
            Continuation = false,
            CurrentHost = new("apr-s5-current-capacity-host-v1", 409, false,
                historical.SourceCommit, historical.SourceTree, historical.SourceClean,
                index is >= 1 and <= 5 ? index : 0, index == 0 ? 0 : index == 7 ? 2 : 1,
                1, index is 0 or 6 ? 0 : 1, true, true),
        }).ToArray() };
        Assert.Null(CapacityVerifier.Verify(current, Corpus));
        var originalEvidence = current.HostCases[1].CurrentHost!;
        foreach (var evidence in new[]
        {
            originalEvidence with { Domain = "apr-historical-typed-logical-capacity-v1" },
            originalEvidence with { OwnerIssue = 403 },
            originalEvidence with { SdkContextImportQualified = true },
            originalEvidence with { SourceCommit = new('d', 40) },
            originalEvidence with { SourceTree = new('e', 40) },
            originalEvidence with { SourceClean = false },
            originalEvidence with { World = 0 },
            originalEvidence with { Invocation = 2 },
            originalEvidence with { GenuineSeedAcceptances = 0 },
            originalEvidence with { SourceClaims = 0 },
            originalEvidence with { SourceClaims = 2 },
            originalEvidence with { SourceRecordBound = false },
            originalEvidence with { NativeHistoryAbsent = false },
        })
        {
            var host = current.HostCases.ToArray();
            host[1] = host[1] with { CurrentHost = evidence };
            Assert.NotNull(CapacityVerifier.Verify(current with { HostCases = host }, Corpus));
        }
        foreach (var mutation in new[] { "missing", "native-continuation", "wrong-fresh", "candidate-write", "publication-write", "predecessor" })
        {
            var host = current.HostCases.ToArray();
            host[1] = mutation switch
            {
                "missing" => host[1] with { CurrentHost = null },
                "native-continuation" => host[1] with { Continuation = true },
                "wrong-fresh" => host[1] with { FreshSession = true },
                "candidate-write" => host[1] with { NoCandidateMutation = false },
                "publication-write" => host[1] with { NoPublicationMutation = false },
                _ => host[1] with { PredecessorPreserved = false },
            };
            Assert.NotNull(CapacityVerifier.Verify(current with { HostCases = host }, Corpus));
        }
        var serialized = JsonSerializer.SerializeToUtf8Bytes(current, CapacityJson.Default.CapacityReport);
        Assert.Null(CapacityVerifier.Verify(JsonSerializer.Deserialize(serialized, CapacityJson.Default.CapacityReport), Corpus));
        foreach (var field in new[] { "domain", "ownerIssue", "sdkContextImportQualified", "sourceCommit", "sourceTree",
            "sourceClean", "world", "invocation", "genuineSeedAcceptances", "sourceClaims", "sourceRecordBound", "nativeHistoryAbsent" })
        {
            var missing = System.Text.Json.Nodes.JsonNode.Parse(serialized)!;
            missing["hostCases"]![1]!["currentHost"]!.AsObject().Remove(field);
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(missing.ToJsonString(), CapacityJson.Default.CapacityReport));
        }
        Assert.Equal(original, JsonSerializer.SerializeToUtf8Bytes(historical, CapacityJson.Default.CapacityReport));
        Assert.DoesNotContain("currentHost", System.Text.Encoding.UTF8.GetString(original));
    }

    private static CapacityReport Report()
    {
        var codes = new[] { "completed", "completed", "agent_response_invalid", "agent_response_invalid", "agent_token_limit",
            "agent_response_too_large", "agent_deadline_exceeded", "admission_rejected", "admission_rejected", "admission_rejected",
            "admission_rejected", "completed", "completed", "completed", "completed" };
        var tools = new[] { 442, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 1, 382, 1 };
        var receipts = Cases.Select((item, index) => new CapacityReceipt(item.Id, codes[index], item.Calls, tools[index], item.Calls,
            index == 0 ? 63 : index == 1 ? 64 : index == 13 ? 127 : index == 14 ? 128 : 0,
            index == 0 ? 441 : index == 1 ? 442 : index == 13 ? 381 : index == 14 ? 382 : 0,
            index is 0 or 13 ? 320 : 0, index is 0 or 13 ? 1 : 0, index is not (0 or 13), true,
            index is >= 2 and <= 10, index is >= 2 and <= 6, index is >= 2 and <= 6,
            index == 11, index is 11 or 12, true, 1000 + index, index.ToString("x32"), new('a', 40), new('b', 40), true,
            new(1, 1, 1000, 1000, index == 3 ? 0 : 10000, null, index == 5 ? 2097153 : 0,
                index == 2 ? 1048577 : 256, index == 3 ? 1048577 : 100, index == 3 ? 0 : 1100000,
                600, 800, 900, 500000, 550000, 600000, 11_000_000), "apr-historical-typed-logical-capacity-v1")).ToArray();
        var modes = new[] { "seed", "capacity", "context", "output", "response", "deadline", "reset", "reset_restore" };
        var hostCodes = new[] { "completed", "agent_response_invalid", "agent_response_invalid", "agent_token_limit",
            "agent_response_too_large", "agent_deadline_exceeded", "completed", "completed" };
        var host = modes.Select((mode, index) => new CapacityHostReceipt("host_" + mode, hostCodes[index],
            index is 0 or 6 or 7 ? "Reviewed" : index == 5 ? "ProviderFailed" : "AgentResultInvalid", index is 0 or 6 or 7 ? 0 : 1,
            index is 0 or 6 or 7 ? "Accepted" : "NotCommitted", index == 0 ? 64 : index == 6 ? 2 : index == 2 ? 0 : 1,
            index == 0 ? 64 : index == 6 ? 2 : index == 2 ? 0 : 1, index is not (0 or 6), index is >= 1 and <= 5,
            index is >= 1 and <= 5, index is >= 1 and <= 5, index is >= 1 and <= 5, index >= 6)).ToArray();
        return new("r7-historical-typed-logical-capacity-v1", "r7_capacity_passed", Convert.ToHexStringLower(SHA256.HashData(Corpus)),
            new('a', 40), new('b', 40), true, receipts, host, true, "apr-historical-typed-logical-capacity-v1");
    }
}

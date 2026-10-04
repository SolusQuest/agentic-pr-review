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

    private static CapacityReport Report()
    {
        var codes = new[] { "completed", "completed", "agent_response_invalid", "agent_context_limit", "agent_token_limit",
            "agent_response_too_large", "agent_deadline_exceeded", "admission_rejected", "admission_rejected", "admission_rejected",
            "admission_rejected", "completed", "completed", "completed", "completed" };
        var tools = new[] { 442, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 1, 382, 1 };
        var receipts = Cases.Select((item, index) => new CapacityReceipt(item.Id, codes[index], index == 3 ? 1 : item.Calls, tools[index], item.Calls,
            index == 0 ? 63 : index == 1 ? 64 : index == 13 ? 127 : index == 14 ? 128 : 0,
            index == 0 ? 441 : index == 1 ? 442 : index == 13 ? 381 : index == 14 ? 382 : 0,
            index is 0 or 13 ? 320 : 0, index is 0 or 13 ? 1 : 0, index is not (0 or 13), true,
            index is >= 2 and <= 10, index is >= 2 and <= 6, index is >= 2 and <= 6,
            index == 11, index is 11 or 12, true, 1000 + index, index.ToString("x32"), new('a', 40), new('b', 40), true,
            new(1, 1, 1000, 1000, index == 3 ? 0 : 10000, index == 2 ? 1049000 : index == 5 ? 2097153 : 10000,
                1100000, 600, 800, 900, 500000, 550000, 600000, 11_000_000))).ToArray();
        var modes = new[] { "seed", "capacity", "context", "output", "response", "deadline", "reset", "reset_restore" };
        var hostCodes = new[] { "completed", "agent_response_invalid", "agent_context_limit", "agent_token_limit",
            "agent_response_too_large", "agent_deadline_exceeded", "completed", "completed" };
        var host = modes.Select((mode, index) => new CapacityHostReceipt("host_" + mode, hostCodes[index],
            index is 0 or 6 or 7 ? "Reviewed" : index == 5 ? "ProviderFailed" : "AgentResultInvalid", index is 0 or 6 or 7 ? 0 : 1,
            index is 0 or 6 or 7 ? "Accepted" : "NotCommitted", index == 0 ? 64 : index == 6 ? 2 : 1,
            index == 0 ? 64 : index == 6 ? 2 : index == 2 ? 0 : 1, index is not (0 or 6), index is >= 1 and <= 5,
            index is >= 1 and <= 5, index is >= 1 and <= 5, index is >= 1 and <= 5, index >= 6)).ToArray();
        return new("r7-capacity-v1", "r7_capacity_passed", Convert.ToHexStringLower(SHA256.HashData(Corpus)),
            new('a', 40), new('b', 40), true, receipts, host, true);
    }
}

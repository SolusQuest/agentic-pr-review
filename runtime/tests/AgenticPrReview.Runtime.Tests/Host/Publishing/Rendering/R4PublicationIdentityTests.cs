using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Publishing.Rendering;

public sealed class R4PublicationIdentityTests
{
    [Fact]
    public void ScopeGoldenFreezesSevenFieldsAndOpaqueNonHexH1Identity()
    {
        var hash = R4PublicationIdentityV1.ComputeScopeSha256(
            R4PublicationTestData.Scope);

        Assert.Equal(
            "5378fccb420ffa06f6510e5050bb64db7d0751cdab77581d306925c400bcdab4",
            hash);
        Assert.Contains("+payload-build:", R4PublicationTestData.H1Identity);

        var properties = typeof(R4PublicationScopeV1)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            new[]
            {
                "ActionContractPayloadIdentity",
                "PolicyIdentitySha256",
                "PullRequestNumber",
                "RepositoryId",
                "WorkflowPath",
                "WorkflowRef",
                "WorkflowSourceRepositoryId",
            },
            properties);
    }

    [Fact]
    public void EveryIncludedScopeFieldInvalidatesTheDigest()
    {
        var scope = R4PublicationTestData.Scope;
        var baseline = R4PublicationIdentityV1.ComputeScopeSha256(scope);
        var mutations = new[]
        {
            scope with { RepositoryId = scope.RepositoryId + 1 },
            scope with
            {
                WorkflowSourceRepositoryId = scope.WorkflowSourceRepositoryId + 1,
            },
            scope with { WorkflowPath = ".github/workflows/other.yml" },
            scope with { WorkflowRef = "refs/heads/release" },
            scope with { PullRequestNumber = scope.PullRequestNumber + 1 },
            scope with { PolicyIdentitySha256 = new string('f', 64) },
            scope with { ActionContractPayloadIdentity = "opaque build two" },
        };

        foreach (var mutation in mutations)
        {
            Assert.NotEqual(
                baseline,
                R4PublicationIdentityV1.ComputeScopeSha256(mutation));
        }
    }

    [Fact]
    public void HeadAndAgentTerminalIdentityStayOutsideScope()
    {
        var first = R4PublicationTestData.Render();
        var second = R4PublicationTestData.Render(
            identity: R4PublicationTestData.Identity(new string('e', 40)));

        Assert.Equal(first.Identity.ScopeSha256, second.Identity.ScopeSha256);
        Assert.NotEqual(first.Identity.HeadSha, second.Identity.HeadSha);
        Assert.NotEqual(first.Identity.BodySha256, second.Identity.BodySha256);

        var changedTerminal = R4PublicationTestData.Outcome(
            terminalSha256: new string('e', 64));
        Assert.True(R4ValidatedPublicationReview.TryCreate(
            changedTerminal,
            R4PublicationTestData.Scope,
            out var validated));
        Assert.Equal(
            first.Identity.ScopeSha256,
            R4PublicationIdentityV1.ComputeScopeSha256(validated!.Scope));
    }

    [Fact]
    public void PublicationFactoryBindsReviewHeadRepositoryAndPullRequest()
    {
        var outcome = R4PublicationTestData.Outcome();

        Assert.True(R4ValidatedPublicationReview.TryCreate(
            outcome,
            R4PublicationTestData.Scope,
            out var valid));
        Assert.Equal(R4PublicationTestData.HeadSha, valid!.ReviewedIdentity.HeadSha);

        Assert.False(R4ValidatedPublicationReview.TryCreate(
            outcome,
            R4PublicationTestData.Scope with { RepositoryId = 123456788 },
            out _));
        Assert.False(R4ValidatedPublicationReview.TryCreate(
            outcome,
            R4PublicationTestData.Scope with { PullRequestNumber = 41 },
            out _));
        Assert.False(R4ValidatedPublicationReview.TryCreate(
            R4PublicationTestData.Outcome(
                identity: new ReviewedIdentity(
                    "0123456789",
                    R4PublicationTestData.PullRequestNumber,
                    R4PublicationTestData.BaseSha,
                    R4PublicationTestData.HeadSha)),
            R4PublicationTestData.Scope,
            out _));
        Assert.False(R4ValidatedPublicationReview.TryCreate(
            AgentRunOutcome.Failure(
                "agent_chat_failed",
                1,
                0,
                ImmutableArray<AgentLogicalEvent>.Empty),
            R4PublicationTestData.Scope,
            out _));
    }

    [Fact]
    public void FindingFingerprintMatchesIndependentGoldenAndEvidenceOrderMatters()
    {
        var finding = R4PublicationTestData.Finding();
        var reversed = finding with
        {
            Evidence =
            [
                R4PublicationTestData.Evidence('b', "src/lib.ts", 11, 13),
                R4PublicationTestData.Evidence(),
            ],
        };
        var forward = reversed with
        {
            Evidence = reversed.Evidence.Reverse().ToImmutableArray(),
        };

        Assert.Equal(
            "43390faa4068833717f38a47230155790bed811f1e00a3d3caeef6d8b682e1fc",
            R4PublicationIdentityV1.ComputeFindingFingerprint(finding));
        Assert.NotEqual(
            R4PublicationIdentityV1.ComputeFindingFingerprint(forward),
            R4PublicationIdentityV1.ComputeFindingFingerprint(reversed));
    }

    [Fact]
    public void UnicodeNormalizationDoesNotParticipateInFindingIdentity()
    {
        var composed = R4PublicationTestData.Finding(title: "caf\u00e9");
        var decomposed = R4PublicationTestData.Finding(title: "cafe\u0301");

        Assert.NotEqual(
            R4PublicationIdentityV1.ComputeFindingFingerprint(composed),
            R4PublicationIdentityV1.ComputeFindingFingerprint(decomposed));
    }

    [Fact]
    public void IdentifyAndOrderUsesSeverityThenFullFingerprintAndIsPermutationStable()
    {
        var low = R4PublicationTestData.Finding(
            "low",
            "Low",
            "Message",
            [R4PublicationTestData.Evidence('a')]);
        var highA = R4PublicationTestData.Finding(
            "high",
            "Alpha",
            "Message",
            [R4PublicationTestData.Evidence('b')]);
        var highB = R4PublicationTestData.Finding(
            "high",
            "Beta",
            "Message",
            [R4PublicationTestData.Evidence('c')]);
        var critical = R4PublicationTestData.Finding(
            "critical",
            "Critical",
            "Message",
            [R4PublicationTestData.Evidence('d')]);
        var first = R4PublicationIdentityV1.IdentifyAndOrder(
            R4PublicationTestData.Validated(
                findings: [low, highB, critical, highA]));
        var second = R4PublicationIdentityV1.IdentifyAndOrder(
            R4PublicationTestData.Validated(
                findings: [highA, critical, low, highB]));

        Assert.Equal(
            first.Select(item => item.FingerprintSha256),
            second.Select(item => item.FingerprintSha256));
        Assert.Equal("critical", first[0].Finding.Severity);
        Assert.Equal("high", first[1].Finding.Severity);
        Assert.Equal("high", first[2].Finding.Severity);
        Assert.Equal("low", first[3].Finding.Severity);
        Assert.True(StringComparer.Ordinal.Compare(
            first[1].FingerprintSha256,
            first[2].FingerprintSha256) < 0);
    }

    [Fact]
    public void DuplicateFingerprintsFailClosedWithoutAlternateIdentity()
    {
        var duplicate = R4PublicationTestData.Finding();
        var validated = R4PublicationTestData.Validated(
            findings: [duplicate, duplicate]);

        var failure = Assert.Throws<R4PublicationException>(() =>
            R4PublicationIdentityV1.IdentifyAndOrder(validated));

        Assert.Equal(
            R4PublicationFailureCodes.FingerprintDuplicate,
            failure.Code);
    }

    [Fact]
    public void EightEvidenceEntriesAreAcceptedAndNineFailClosed()
    {
        var evidence = Enumerable.Range(0, 9)
            .Select(index => R4PublicationTestData.Evidence(
                "012345678"[index],
                "src/evidence" + index.ToString(CultureInfo.InvariantCulture) + ".cs",
                index + 1,
                index + 2))
            .ToArray();
        var eight = R4PublicationTestData.Finding(evidence: evidence[..8].ToImmutableArray());
        var nine = R4PublicationTestData.Finding(evidence: evidence.ToImmutableArray());

        Assert.Equal(
            64,
            R4PublicationIdentityV1.ComputeFindingFingerprint(eight).Length);
        var failure = Assert.Throws<R4PublicationException>(() =>
            R4PublicationIdentityV1.ComputeFindingFingerprint(nine));
        Assert.Equal(R4PublicationFailureCodes.ReviewInvalid, failure.Code);
    }

    [Fact]
    public void MalformedInternalFindingValuesFailClosed()
    {
        var invalid = new[]
        {
            R4PublicationTestData.Finding(severity: "urgent"),
            R4PublicationTestData.Finding(
                evidence: [R4PublicationTestData.Evidence(path: "src:bad.cs")]),
            R4PublicationTestData.Finding(
                evidence: [R4PublicationTestData.Evidence(startLine: 0)]),
            R4PublicationTestData.Finding(
                evidence: [new AgentEvidence(new string('A', 64), "src/a.cs", 1, 1)]),
        };

        foreach (var finding in invalid)
        {
            var failure = Assert.Throws<R4PublicationException>(() =>
                R4PublicationIdentityV1.ComputeFindingFingerprint(finding));
            Assert.Equal(R4PublicationFailureCodes.ReviewInvalid, failure.Code);
        }
    }

    [Theory]
    [InlineData(0UL, 1UL, 1UL, "path", "ref", "opaque")]
    [InlineData(1UL, 0UL, 1UL, "path", "ref", "opaque")]
    [InlineData(1UL, 1UL, 0UL, "path", "ref", "opaque")]
    [InlineData(1UL, 1UL, 1UL, "", "ref", "opaque")]
    [InlineData(1UL, 1UL, 1UL, "path", "", "opaque")]
    [InlineData(1UL, 1UL, 1UL, "path", "ref", "")]
    public void InvalidScopeDomainsFailClosed(
        ulong repositoryId,
        ulong sourceRepositoryId,
        ulong pullRequestNumber,
        string workflowPath,
        string workflowRef,
        string h1Identity)
    {
        var scope = new R4PublicationScopeV1(
            repositoryId,
            sourceRepositoryId,
            workflowPath,
            workflowRef,
            pullRequestNumber,
            R4PublicationTestData.PolicySha256,
            h1Identity);

        var failure = Assert.Throws<R4PublicationException>(() =>
            R4PublicationIdentityV1.ComputeScopeSha256(scope));

        Assert.Equal(R4PublicationFailureCodes.IdentityInvalid, failure.Code);
    }
}

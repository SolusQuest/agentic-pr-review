using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.ActionHost.Policy;
using AgenticPrReview.Runtime.ActionHost.Snapshot;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Host.Publishing.Inline;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;
using AgenticPrReview.Runtime.Tests.Host.Publishing.Rendering;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Publishing.Inline;

public sealed class InlineCandidateMapperTests
{
    [Fact]
    public async Task AllUnavailableSnapshotRemainsBoundAndMismatchesFailClosed()
    {
        var change = InlineCandidateTestData.Unavailable(
            "src/binary.bin",
            patchStatus: "binary");
        var snapshot = InlineCandidateTestData.Snapshot(
            [change.Path],
            [change],
            []);
        var identities = InlineCandidateTestData.Identities();

        Assert.True(InlineDiffCoordinates.TryCreate(
            snapshot,
            identities,
            out var coordinates));
        Assert.False(InlineDiffCoordinates.TryCreate(
            snapshot,
            identities with { HeadSha = new string('d', 40) },
            out _));
        Assert.False(InlineDiffCoordinates.TryCreate(
            snapshot,
            identities with { DiffSha256 = new string('A', 64) },
            out _));

        var finding = InlineCandidateTestData.Ordered(
            InlineCandidateTestData.Finding(
                title: "Binary target",
                evidence: [InlineCandidateTestData.Evidence(
                    'a',
                    change.Path,
                    1,
                    1)]));
        var policy = await InlineCandidateTestData.Policy(
            "sticky_and_inline",
            "high");

        var mapped = InlineCandidateMapper.Map(
            policy,
            finding,
            coordinates!);

        Assert.Equal(InlineCandidateTestData.Identity, mapped.ReviewedIdentity);
        Assert.Equal(identities.DiffSha256, mapped.DiffSha256);
        Assert.Empty(mapped.Candidates);
        Assert.Equal(
            InlineCandidateReasonCodes.NoCurrentRightSideLocation,
            Assert.Single(mapped.StickyOnlyFindings).ReasonCode);
        Assert.Equal(1, mapped.ReasonCounts.NoCurrentRightSideLocation);
        Assert.Equal(0, mapped.ReasonCounts.CandidateCap);
    }

    [Fact]
    public async Task ExactCurrentPathAdditionAndContextAreTheOnlyTargets()
    {
        var source = InlineCandidateTestData.Source(
            "src/current.cs",
            [new ReviewedDiffHunk(
                10,
                2,
                10,
                2,
                [
                    new("context", 10, 10, "same"),
                    new("deletion", 11, null, "old"),
                    new("addition", null, 11, "new"),
                    new("no_newline", null, null, string.Empty),
                ])],
            previousPath: "src/old.cs",
            status: "renamed");
        var deletionOnly = InlineCandidateTestData.Source(
            "src/removed.cs",
            [new ReviewedDiffHunk(
                50,
                1,
                50,
                0,
                [new("deletion", 50, null, "removed")])],
            status: "removed");
        var coordinates = InlineCandidateTestData.Coordinates(
            source,
            deletionOnly);
        var findings = InlineCandidateTestData.Ordered(
            InlineCandidateTestData.Finding(
                title: "Context",
                evidence: [InlineCandidateTestData.Evidence(
                    'a', source.Path, 10, 10)]),
            InlineCandidateTestData.Finding(
                title: "Addition",
                evidence: [InlineCandidateTestData.Evidence(
                    'b', source.Path, 11, 11)]),
            InlineCandidateTestData.Finding(
                title: "Previous path",
                evidence: [InlineCandidateTestData.Evidence(
                    'c', source.PreviousPath!, 10, 10)]),
            InlineCandidateTestData.Finding(
                title: "Shifted",
                evidence: [InlineCandidateTestData.Evidence(
                    'd', source.Path, 12, 12)]),
            InlineCandidateTestData.Finding(
                title: "Deletion only",
                evidence: [InlineCandidateTestData.Evidence(
                    'e', deletionOnly.Path, 50, 50)]));
        var policy = await InlineCandidateTestData.Policy(
            "sticky_and_inline",
            "high");

        var mapped = InlineCandidateMapper.Map(policy, findings, coordinates);

        Assert.Equal(2, mapped.Candidates.Length);
        Assert.Contains(mapped.Candidates, item =>
            item.FindingIdentity.Finding.Title == "Context" &&
            item.Path == source.Path &&
            item.Line == 10);
        Assert.Contains(mapped.Candidates, item =>
            item.FindingIdentity.Finding.Title == "Addition" &&
            item.Path == source.Path &&
            item.Line == 11);
        Assert.Equal(3, mapped.StickyOnlyFindings.Length);
        Assert.All(mapped.StickyOnlyFindings, item => Assert.Equal(
            InlineCandidateReasonCodes.NoCurrentRightSideLocation,
            item.ReasonCode));
        Assert.DoesNotContain(mapped.Candidates, item =>
            item.Path == source.PreviousPath || item.Line == 12);
    }

    [Fact]
    public async Task EvidenceOrderAndInclusiveRangesSelectTheFirstExactLine()
    {
        var first = InlineCandidateTestData.AdditionSource(
            "src/first.cs",
            20,
            22);
        var second = InlineCandidateTestData.AdditionSource(
            "src/second.cs",
            5);
        var coordinates = InlineCandidateTestData.Coordinates(first, second);
        var findings = InlineCandidateTestData.Ordered(
            InlineCandidateTestData.Finding(
                title: "Earlier evidence wins",
                evidence:
                [
                    InlineCandidateTestData.Evidence(
                        'a', first.Path, 19, 22),
                    InlineCandidateTestData.Evidence(
                        'b', second.Path, 5, 5),
                ]),
            InlineCandidateTestData.Finding(
                title: "Later eligible evidence",
                evidence:
                [
                    InlineCandidateTestData.Evidence(
                        'c', first.Path, 21, 21),
                    InlineCandidateTestData.Evidence(
                        'd', first.Path, 22, 22),
                ]));
        var policy = await InlineCandidateTestData.Policy(
            "sticky_and_inline",
            "high");

        var mapped = InlineCandidateMapper.Map(policy, findings, coordinates);

        var earlier = Assert.Single(mapped.Candidates, item =>
            item.FindingIdentity.Finding.Title == "Earlier evidence wins");
        Assert.Equal(first.Path, earlier.Path);
        Assert.Equal(20, earlier.Line);
        var later = Assert.Single(mapped.Candidates, item =>
            item.FindingIdentity.Finding.Title == "Later eligible evidence");
        Assert.Equal(22, later.Line);
    }

    [Fact]
    public async Task StickyAndSeverityPoliciesExcludeWithoutNewReasons()
    {
        var source = InlineCandidateTestData.AdditionSource("src/app.cs", 7);
        var coordinates = InlineCandidateTestData.Coordinates(source);
        var findings = InlineCandidateTestData.Ordered(
            InlineCandidateTestData.Finding(
                severity: "low",
                title: "Low",
                evidence: [InlineCandidateTestData.Evidence(
                    'a', source.Path, 7, 7)]),
            InlineCandidateTestData.Finding(
                severity: "critical",
                title: "Critical",
                evidence: [InlineCandidateTestData.Evidence(
                    'b', source.Path, 7, 7)]),
            InlineCandidateTestData.Finding(
                severity: "medium",
                title: "Medium",
                evidence: [InlineCandidateTestData.Evidence(
                    'c', source.Path, 7, 7)]),
            InlineCandidateTestData.Finding(
                severity: "high",
                title: "High",
                evidence: [InlineCandidateTestData.Evidence(
                    'd', source.Path, 7, 7)]));
        var sticky = await InlineCandidateTestData.Policy("sticky", "high");
        var high = await InlineCandidateTestData.Policy(
            "sticky_and_inline",
            "high");
        var critical = await InlineCandidateTestData.Policy(
            "sticky_and_inline",
            "critical");

        var stickyMap = InlineCandidateMapper.Map(sticky, findings, coordinates);
        var highMap = InlineCandidateMapper.Map(high, findings, coordinates);
        var criticalMap = InlineCandidateMapper.Map(
            critical,
            findings,
            coordinates);

        Assert.Empty(stickyMap.Candidates);
        Assert.Empty(stickyMap.StickyOnlyFindings);
        Assert.Equal(
            ["Critical", "High"],
            highMap.Candidates.Select(item =>
                item.FindingIdentity.Finding.Title).ToArray());
        Assert.Empty(highMap.StickyOnlyFindings);
        Assert.Equal(
            "Critical",
            Assert.Single(criticalMap.Candidates).FindingIdentity.Finding.Title);
        Assert.Empty(criticalMap.StickyOnlyFindings);
    }

    [Fact]
    public async Task MappingPrecedesFixedCapAndReasonCountsAreStable()
    {
        var source = InlineCandidateTestData.AdditionSource(
            "src/app.cs",
            1,
            2,
            3,
            4,
            5,
            7);
        var coordinates = InlineCandidateTestData.Coordinates(source);
        var findings = Enumerable.Range(1, 7)
            .Select(index => InlineCandidateTestData.Identified(
                index,
                index == 6 ? 99 : index,
                source.Path))
            .ToImmutableArray();
        var policy = await InlineCandidateTestData.Policy(
            "sticky_and_inline",
            "high");

        var first = InlineCandidateMapper.Map(policy, findings, coordinates);
        var repeated = InlineCandidateMapper.Map(policy, findings, coordinates);

        Assert.Equal(5, first.Candidates.Length);
        Assert.Equal(
            InlineCandidateReasonCodes.NoCurrentRightSideLocation,
            Assert.Single(first.StickyOnlyFindings, item =>
                item.FindingIdentity.Finding.Title == "Finding 6").ReasonCode);
        Assert.Equal(
            InlineCandidateReasonCodes.CandidateCap,
            Assert.Single(first.StickyOnlyFindings, item =>
                item.FindingIdentity.Finding.Title == "Finding 7").ReasonCode);
        Assert.Equal(1, first.ReasonCounts.NoCurrentRightSideLocation);
        Assert.Equal(1, first.ReasonCounts.CandidateCap);
        Assert.Equal(
            InlineCandidateTestData.StableProjection(first),
            InlineCandidateTestData.StableProjection(repeated));
    }

    [Fact]
    public async Task DistinctFingerprintsAtOneLocationRemainDistinctCandidates()
    {
        var source = InlineCandidateTestData.AdditionSource("src/app.cs", 7);
        var coordinates = InlineCandidateTestData.Coordinates(source);
        var findings = ImmutableArray.Create(
            InlineCandidateTestData.Identified(1, 7, source.Path),
            InlineCandidateTestData.Identified(2, 7, source.Path));
        var policy = await InlineCandidateTestData.Policy(
            "sticky_and_inline",
            "high");

        var mapped = InlineCandidateMapper.Map(policy, findings, coordinates);

        Assert.Equal(2, mapped.Candidates.Length);
        Assert.All(mapped.Candidates, item =>
        {
            Assert.Equal(source.Path, item.Path);
            Assert.Equal(7, item.Line);
        });
        Assert.Equal(
            2,
            mapped.Candidates.Select(item => item.InlineKey)
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    [Fact]
    public async Task HugeEvidenceRangeSearchesBoundedH5Coordinates()
    {
        var source = InlineCandidateTestData.AdditionSource(
            "src/large.cs",
            1_000_000);
        var coordinates = InlineCandidateTestData.Coordinates(source);
        var findings = InlineCandidateTestData.Ordered(
            InlineCandidateTestData.Finding(
                evidence: [InlineCandidateTestData.Evidence(
                    'a', source.Path, 1, int.MaxValue)]));
        var policy = await InlineCandidateTestData.Policy(
            "sticky_and_inline",
            "high");

        var mapped = InlineCandidateMapper.Map(policy, findings, coordinates);

        Assert.Equal(1_000_000, Assert.Single(mapped.Candidates).Line);
    }

    [Fact]
    public async Task CompleteP1ProjectionIncludesStickyBodyOmissions()
    {
        var source = InlineCandidateTestData.AdditionSource("src/app.cs", 7);
        var coordinates = InlineCandidateTestData.Coordinates(source);
        var findings = Enumerable.Range(0, 12)
            .Select(index => InlineCandidateTestData.Finding(
                title: "Finding " + index.ToString(CultureInfo.InvariantCulture),
                message: new string((char)('a' + index), 7_000),
                evidence: [InlineCandidateTestData.Evidence(
                    index.ToString("x", CultureInfo.InvariantCulture)[0],
                    source.Path,
                    7,
                    7)]))
            .ToImmutableArray();
        var rendered = R4PublicationTestData.Render(findings: findings);
        var policy = await InlineCandidateTestData.Policy(
            "sticky_and_inline",
            "high");

        var mapped = InlineCandidateMapper.Map(
            policy,
            rendered.OrderedFindings,
            coordinates);

        Assert.True(rendered.OmittedFindingCount > 0);
        Assert.Equal(findings.Length, rendered.OrderedFindings.Length);
        Assert.Equal(5, mapped.Candidates.Length);
        Assert.Equal(
            findings.Length - 5,
            mapped.StickyOnlyFindings.Length);
        Assert.All(mapped.StickyOnlyFindings, item => Assert.Equal(
            InlineCandidateReasonCodes.CandidateCap,
            item.ReasonCode));
    }

    [Fact]
    public async Task InvalidOrNonP1OrderedProjectionFailsClosed()
    {
        var coordinates = InlineCandidateTestData.Coordinates(
            InlineCandidateTestData.AdditionSource("src/app.cs", 7));
        var policy = await InlineCandidateTestData.Policy(
            "sticky_and_inline",
            "high");
        var first = InlineCandidateTestData.Identified(1, 7, "src/app.cs");
        var second = InlineCandidateTestData.Identified(2, 7, "src/app.cs");

        Assert.Throws<ArgumentException>(() => InlineCandidateMapper.Map(
            policy,
            default,
            coordinates));
        Assert.Throws<ArgumentException>(() => InlineCandidateMapper.Map(
            policy,
            [second, first],
            coordinates));
        Assert.Throws<ArgumentException>(() => InlineCandidateMapper.Map(
            policy,
            [first with { FingerprintSha256 = new string('A', 64) }],
            coordinates));
    }
}

internal static class InlineCandidateTestData
{
    private const string RootTree = "1111111111111111111111111111111111111111";
    private const string GitHubTree = "2222222222222222222222222222222222222222";
    private const string InstructionsTree =
        "3333333333333333333333333333333333333333";
    private const string ConfigBlob = "4444444444444444444444444444444444444444";
    private const string InstructionsBlob =
        "5555555555555555555555555555555555555555";

    internal static ReviewedIdentity Identity { get; } =
        R4PublicationTestData.Identity();

    internal static AgentEvidence Evidence(
        char observation,
        string path,
        int startLine,
        int endLine) =>
        R4PublicationTestData.Evidence(
            observation,
            path,
            startLine,
            endLine);

    internal static AgentFinding Finding(
        string severity = "high",
        string title = "Title",
        string message = "Message",
        ImmutableArray<AgentEvidence>? evidence = null) =>
        R4PublicationTestData.Finding(
            severity,
            title,
            message,
            evidence);

    internal static ImmutableArray<R4FindingIdentityV1> Ordered(
        params AgentFinding[] findings) =>
        R4PublicationTestData.Render(findings: [.. findings]).OrderedFindings;

    internal static R4FindingIdentityV1 Identified(
        int ordinal,
        int line,
        string path) =>
        new(
            Finding(
                title: "Finding " + ordinal.ToString(CultureInfo.InvariantCulture),
                evidence: [Evidence(
                    ordinal.ToString("x", CultureInfo.InvariantCulture)[0],
                    path,
                    line,
                    line)]),
            ordinal.ToString("x64", CultureInfo.InvariantCulture));

    internal static ReviewedDiffSource Source(
        string path,
        IEnumerable<ReviewedDiffHunk> hunks,
        string? previousPath = null,
        string status = "modified") =>
        new(Identity, path, previousPath, status, false, hunks);

    internal static ReviewedDiffSource AdditionSource(
        string path,
        params int[] lines) =>
        Source(
            path,
            lines.Select(line => new ReviewedDiffHunk(
                line,
                0,
                line,
                1,
                [new ReviewedDiffLine("addition", null, line, "added")])));

    internal static ReviewedChangedFile Unavailable(
        string path,
        string patchStatus = "unavailable") =>
        new(path, null, "modified", 0, 0, 0, patchStatus, null, false);

    internal static ReviewedSnapshot Snapshot(
        IEnumerable<string> tracked,
        IEnumerable<ReviewedChangedFile> changes,
        IEnumerable<ReviewedDiffSource> sources) =>
        new(
            Identity,
            Directory.GetCurrentDirectory(),
            tracked,
            changes,
            sources);

    internal static ReviewedSnapshotIdentities Identities() => new(
        long.Parse(Identity.RepositoryId, CultureInfo.InvariantCulture),
        Identity.ReviewTarget,
        Identity.BaseSha,
        Identity.HeadSha,
        new string('1', 64),
        new string('2', 64),
        new string('3', 64),
        new string('4', 64));

    internal static InlineDiffCoordinates Coordinates(
        params ReviewedDiffSource[] sources)
    {
        var changes = sources.Select(source => new ReviewedChangedFile(
            source.Path,
            source.PreviousPath,
            source.Status,
            source.RepresentedAdditions,
            source.RepresentedDeletions,
            source.RepresentedAdditions + source.RepresentedDeletions,
            "available",
            source.PatchSha256,
            source.SourceTruncated)).ToArray();
        var snapshot = Snapshot(
            sources.Where(source => source.Status != "removed")
                .Select(source => source.Path),
            changes,
            sources);
        Assert.True(InlineDiffCoordinates.TryCreate(
            snapshot,
            Identities(),
            out var coordinates));
        return coordinates!;
    }

    internal static string StableProjection(InlineCandidateMap mapped) =>
        string.Join(
            "\n",
            mapped.Candidates.Select(item => string.Join(
                "|",
                "candidate",
                item.FindingIdentity.FingerprintSha256,
                item.Path,
                item.Line.ToString(CultureInfo.InvariantCulture),
                item.InlineKey)).Concat(mapped.StickyOnlyFindings.Select(item =>
                string.Join(
                    "|",
                    "sticky",
                    item.FindingIdentity.FingerprintSha256,
                    item.ReasonCode))).Append(string.Join(
                "|",
                mapped.ReviewedIdentity.RepositoryId,
                mapped.ReviewedIdentity.ReviewTarget.ToString(
                    CultureInfo.InvariantCulture),
                mapped.ReviewedIdentity.BaseSha,
                mapped.ReviewedIdentity.HeadSha,
                mapped.PolicySha256,
                mapped.DiffSha256,
                mapped.ReasonCounts.NoCurrentRightSideLocation.ToString(
                    CultureInfo.InvariantCulture),
                mapped.ReasonCounts.CandidateCap.ToString(
                    CultureInfo.InvariantCulture))));

    internal static async Task<ActionHostTrustedPolicy> Policy(
        string mode,
        string severity)
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch);
        var authorization = await scenario.CreateAuthorizer().AuthorizeAsync(
            scenario.Launch,
            CancellationToken.None);
        Assert.True(ActionHostTrustedPolicyRequest.TryBind(
            scenario.Launch,
            authorization.Invocation,
            out var request,
            out var failure));
        Assert.Equal(ActionHostTrustedPolicyFailure.None, failure);
        using var transport = new PolicyTransport(Config(mode, severity));

        var result = await ActionHostTrustedPolicy.MaterializeAsync(
            request!,
            transport,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        return result.Policy!;
    }

    private static byte[] Config(string mode, string severity) =>
        Encoding.UTF8.GetBytes(
            "{\"schema\":\"agentic-pr-review.config.v1\"," +
            "\"instructionsPath\":\".github/agentic-pr-review/instructions.md\"," +
            "\"publication\":{\"mode\":\"" + mode +
            "\",\"inlineMinSeverity\":\"" + severity + "\"}}");

    private sealed class PolicyTransport : IActionHostGitObjectTransport
    {
        private readonly byte[] _config;

        internal PolicyTransport(byte[] config)
        {
            _config = config;
        }

        public Task<ActionHostGitObjectResult<ActionHostGitCommitObject>>
            GetCommitObjectAsync(
                string repositoryName,
                string commitSha,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                ActionHostGitObjectResult<ActionHostGitCommitObject>.Success(
                    new(ActionHostAuthorizationScenario.WorkflowSha, RootTree),
                    100));
        }

        public Task<ActionHostGitObjectResult<ActionHostGitTreeObject>>
            GetTreeObjectAsync(
                string repositoryName,
                string treeSha,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tree = treeSha switch
            {
                RootTree => new ActionHostGitTreeObject(
                    RootTree,
                    [new(".github", "040000", "tree", GitHubTree)]),
                GitHubTree => new ActionHostGitTreeObject(
                    GitHubTree,
                    [
                        new(
                            "agentic-pr-review.json",
                            "100644",
                            "blob",
                            ConfigBlob),
                        new(
                            "agentic-pr-review",
                            "040000",
                            "tree",
                            InstructionsTree),
                    ]),
                InstructionsTree => new ActionHostGitTreeObject(
                    InstructionsTree,
                    [new(
                        "instructions.md",
                        "100644",
                        "blob",
                        InstructionsBlob)]),
                _ => null,
            };
            return Task.FromResult(tree is null
                ? ActionHostGitObjectResult<ActionHostGitTreeObject>.Failed(
                    ActionHostGitObjectFailure.NotFound)
                : ActionHostGitObjectResult<ActionHostGitTreeObject>.Success(
                    tree,
                    100));
        }

        public Task<ActionHostGitObjectResult<ActionHostGitBlobObject>>
            GetBlobObjectAsync(
                string repositoryName,
                string blobSha,
                ActionHostGitBlobReadBudget budget,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = blobSha switch
            {
                ConfigBlob => _config,
                InstructionsBlob => Encoding.UTF8.GetBytes("instructions"),
                _ => null,
            };
            return Task.FromResult(bytes is null
                ? ActionHostGitObjectResult<ActionHostGitBlobObject>.Failed(
                    ActionHostGitObjectFailure.NotFound)
                : ActionHostGitObjectResult<ActionHostGitBlobObject>.Success(
                    new(blobSha, (byte[])bytes.Clone()),
                    100));
        }

        public Task<ActionHostGitObjectResult<ActionHostGitArchiveReader>>
            GetHeadArchiveAsync(
                string repositoryName,
                string headSha,
                CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Archive transport was called.");

        public void Dispose()
        {
        }
    }
}

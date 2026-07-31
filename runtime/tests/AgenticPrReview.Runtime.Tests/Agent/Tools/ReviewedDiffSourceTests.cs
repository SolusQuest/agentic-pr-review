using System.Text;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime.Tests.Agent.Tools;

public sealed class ReviewedDiffSourceTests
{
    private static readonly ReviewedIdentity Identity = new(
        "repo",
        1,
        new string('0', 40),
        new string('1', 40));

    [Fact]
    public void ChangedRecordWriterHasOneExactStandaloneAuthority()
    {
        var nulls = new ReviewedChangedFile(
            "a.txt",
            null,
            "modified",
            0,
            0,
            0,
            "unavailable",
            null,
            false);
        var unicode = new ReviewedChangedFile(
            "é.txt",
            "旧.txt",
            "copied",
            1,
            2,
            3,
            "binary",
            null,
            false);

        Assert.Equal(
            "{\"path\":\"a.txt\",\"previous_path\":null,\"status\":\"modified\",\"additions\":0,\"deletions\":0,\"changes\":0,\"patch_status\":\"unavailable\",\"patch_sha256\":null,\"source_truncated\":false}",
            Encoding.UTF8.GetString(ReviewedChangedFileWriter.Write(nulls)));
        Assert.Equal(
            "{\"path\":\"é.txt\",\"previous_path\":\"旧.txt\",\"status\":\"copied\",\"additions\":1,\"deletions\":2,\"changes\":3,\"patch_status\":\"binary\",\"patch_sha256\":null,\"source_truncated\":false}",
            Encoding.UTF8.GetString(ReviewedChangedFileWriter.Write(unicode)));
    }

    [Fact]
    public void MixedSourceMatchesIndependentBytesAndDomainHash()
    {
        var source = new ReviewedDiffSource(
            Identity,
            "new.txt",
            "old.txt",
            "renamed",
            false,
            [
                new ReviewedDiffHunk(
                    1,
                    2,
                    1,
                    2,
                    [
                        new("context", 1, 1, "same"),
                        new("deletion", 2, null, "gone"),
                        new("addition", null, 2, "new"),
                        new("no_newline", null, null, ""),
                    ]),
            ]);
        const string expected =
            "{\"reviewed_identity\":{\"repository_id\":\"repo\",\"review_target\":1,\"base_sha\":\"0000000000000000000000000000000000000000\",\"head_sha\":\"1111111111111111111111111111111111111111\"},\"path\":\"new.txt\",\"previous_path\":\"old.txt\",\"status\":\"renamed\",\"source_truncated\":false,\"hunks\":[{\"old_start\":1,\"old_count\":2,\"new_start\":1,\"new_count\":2,\"lines\":[{\"kind\":\"context\",\"old_line\":1,\"new_line\":1,\"text\":\"same\"},{\"kind\":\"deletion\",\"old_line\":2,\"new_line\":null,\"text\":\"gone\"},{\"kind\":\"addition\",\"old_line\":null,\"new_line\":2,\"text\":\"new\"},{\"kind\":\"no_newline\",\"old_line\":null,\"new_line\":null,\"text\":\"\"}]}]}";

        Assert.Equal(expected, Encoding.UTF8.GetString(ReviewedDiffSourceWriter.Write(source)));
        Assert.Equal(
            "401899d03cc75be399799db6da569de17038868bd332f4068e3abcb63de1b096",
            source.PatchSha256);
    }

    [Fact]
    public void ZeroHunkAvailableSourceHasIndependentBytesAndHash()
    {
        var source = Source("empty.txt", []);
        const string expected =
            "{\"reviewed_identity\":{\"repository_id\":\"repo\",\"review_target\":1,\"base_sha\":\"0000000000000000000000000000000000000000\",\"head_sha\":\"1111111111111111111111111111111111111111\"},\"path\":\"empty.txt\",\"previous_path\":null,\"status\":\"modified\",\"source_truncated\":false,\"hunks\":[]}";

        Assert.Equal(expected, Encoding.UTF8.GetString(source.CanonicalBytes.AsSpan()));
        Assert.Equal(
            "dcb86caa07602521f6a1c0496e425631d00b79de50c8785e47831bd71faedb01",
            source.PatchSha256);
    }

    [Fact]
    public void HunkLimitRejectsThe201stWithoutRequestingThe202nd()
    {
        var hunks = Enumerable.Range(1, AgentLimits.DiffHunksPerFile + 1)
            .Select(index => ContextHunk(index));

        var exception = Assert.Throws<ArgumentException>(() =>
            Source("a.txt", PoisonAfter(hunks)));

        Assert.DoesNotContain("poison", exception.Message, StringComparison.Ordinal);
        Assert.Equal(
            AgentLimits.DiffHunksPerFile,
            Source(
                "a.txt",
                Enumerable.Range(1, AgentLimits.DiffHunksPerFile)
                    .Select(ContextHunk)).Hunks.Length);
    }

    [Fact]
    public void LineLimitRejectsThe1001stWithoutRequestingThe1002nd()
    {
        var lines = Enumerable.Range(1, AgentLimits.DiffLinesPerHunk + 1)
            .Select(index => new ReviewedDiffLine(
                "context",
                index,
                index,
                string.Empty));

        Assert.Throws<ArgumentException>(() => new ReviewedDiffHunk(
            1,
            AgentLimits.DiffLinesPerHunk + 1,
            1,
            AgentLimits.DiffLinesPerHunk + 1,
            PoisonAfter(lines)));
        Assert.Equal(
            AgentLimits.DiffLinesPerHunk,
            new ReviewedDiffHunk(
                1,
                AgentLimits.DiffLinesPerHunk,
                1,
                AgentLimits.DiffLinesPerHunk,
                Enumerable.Range(1, AgentLimits.DiffLinesPerHunk)
                    .Select(index => new ReviewedDiffLine(
                        "context",
                        index,
                        index,
                        string.Empty))).Lines.Length);
    }

    [Fact]
    public void ZeroLineHunkFailsBeforeAConsumerCanBeReached()
    {
        var consumerReached = false;

        Assert.Throws<ArgumentException>(() =>
        {
            _ = new ReviewedDiffHunk(1, 1, 1, 1, []);
            consumerReached = true;
        });
        Assert.False(consumerReached);
    }

    [Fact]
    public void PureAddDeleteAndTouchingHunksAreValid()
    {
        var source = Source(
            "a.txt",
            [
                new ReviewedDiffHunk(
                    0,
                    0,
                    1,
                    1,
                    [new("addition", null, 1, "a")]),
                new ReviewedDiffHunk(
                    1,
                    1,
                    2,
                    0,
                    [new("deletion", 1, null, "b")]),
            ]);

        Assert.Equal(1, source.RepresentedAdditions);
        Assert.Equal(1, source.RepresentedDeletions);
    }

    [Fact]
    public void MarkerIsAdmittedExactlyOnceAfterEveryContentKind()
    {
        _ = new ReviewedDiffHunk(
            1,
            2,
            1,
            2,
            [
                new("context", 1, 1, "c"),
                new("no_newline", null, null, ""),
                new("deletion", 2, null, "d"),
                new("no_newline", null, null, ""),
                new("addition", null, 2, "a"),
                new("no_newline", null, null, ""),
            ]);

        Assert.Throws<ArgumentException>(() => new ReviewedDiffHunk(
            1,
            1,
            1,
            1,
            [new ReviewedDiffLine("no_newline", null, null, "")]));
        Assert.Throws<ArgumentException>(() => new ReviewedDiffHunk(
            1,
            1,
            1,
            1,
            [
                new ReviewedDiffLine("context", 1, 1, "c"),
                new ReviewedDiffLine("no_newline", null, null, ""),
                new ReviewedDiffLine("no_newline", null, null, ""),
            ]));
        Assert.Throws<ArgumentException>(() => new ReviewedDiffHunk(
            1,
            1,
            1,
            1,
            [
                new ReviewedDiffLine("context", 1, 1, "c"),
                new ReviewedDiffLine("no_newline", 1, null, "x"),
            ]));
    }

    [Theory]
    [InlineData("\0")]
    [InlineData("\r")]
    [InlineData("\n")]
    public void LineTextRejectsForbiddenCharacters(string text)
    {
        Assert.Throws<ArgumentException>(() => AdditionHunk(text));
    }

    [Fact]
    public void LineTextUsesStrictUtf8ByteBoundaries()
    {
        _ = AdditionHunk(string.Empty);
        _ = AdditionHunk(new string('x', AgentLimits.DiffLineTextBytes));
        _ = AdditionHunk(new string('é', AgentLimits.DiffLineTextBytes / 2));

        Assert.Throws<ArgumentException>(() =>
            AdditionHunk(new string('x', AgentLimits.DiffLineTextBytes + 1)));
        Assert.Throws<ArgumentException>(() => AdditionHunk("\ud800"));
    }

    [Fact]
    public void LateInvalidLineIsNotMaskedByTheSourceByteCap()
    {
        var lines = Enumerable.Range(1, 128)
            .Select(index => new ReviewedDiffLine(
                "addition",
                null,
                index,
                index == 128
                    ? "bad\0"
                    : new string('x', AgentLimits.DiffLineTextBytes)))
            .ToArray();

        Assert.Throws<ArgumentException>(() => new ReviewedDiffHunk(
            0,
            0,
            1,
            128,
            lines));
    }

    [Fact]
    public void HunkRangeAndProgressionBoundariesFailClosed()
    {
        _ = new ReviewedDiffHunk(
            int.MaxValue - 1,
            1,
            0,
            0,
            [new("deletion", int.MaxValue - 1, null, "x")]);
        _ = new ReviewedDiffHunk(
            4,
            0,
            1,
            1,
            [new("addition", null, 1, "x")]);

        Assert.Throws<ArgumentException>(() => new ReviewedDiffHunk(
            0,
            1,
            1,
            1,
            [new ReviewedDiffLine("context", 0, 1, "x")]));
        Assert.Throws<ArgumentException>(() => new ReviewedDiffHunk(
            0,
            0,
            0,
            0,
            []));
        Assert.Throws<ArgumentException>(() => new ReviewedDiffHunk(
            int.MaxValue,
            1,
            0,
            0,
            [new ReviewedDiffLine("deletion", int.MaxValue, null, "x")]));
        Assert.Throws<ArgumentException>(() => new ReviewedDiffHunk(
            1,
            1_000_001,
            0,
            0,
            []));
        Assert.Throws<ArgumentException>(() => new ReviewedDiffHunk(
            1,
            2,
            1,
            2,
            [new ReviewedDiffLine("context", 1, 1, "x")]));
        Assert.Throws<ArgumentException>(() => new ReviewedDiffHunk(
            1,
            1,
            1,
            1,
            [new ReviewedDiffLine("addition", null, 2, "x")]));
    }

    [Fact]
    public void EveryLineKindEnforcesItsCoordinateMatrix()
    {
        Assert.Throws<ArgumentException>(() => new ReviewedDiffHunk(
            1,
            1,
            1,
            1,
            [new ReviewedDiffLine("context", null, 1, "x")]));
        Assert.Throws<ArgumentException>(() => new ReviewedDiffHunk(
            0,
            0,
            1,
            1,
            [new ReviewedDiffLine("addition", 1, 1, "x")]));
        Assert.Throws<ArgumentException>(() => new ReviewedDiffHunk(
            1,
            1,
            0,
            0,
            [new ReviewedDiffLine("deletion", 1, 1, "x")]));
        Assert.Throws<ArgumentException>(() => new ReviewedDiffHunk(
            1,
            1,
            1,
            1,
            [new ReviewedDiffLine("unknown", 1, 1, "x")]));
    }

    [Fact]
    public void HunkOrderRejectsOverlapOnEitherSideAndReversal()
    {
        var first = ContextHunk(2);
        var oldOverlap = new ReviewedDiffHunk(
            2,
            1,
            3,
            1,
            [new("context", 2, 3, "x")]);
        var newOverlap = new ReviewedDiffHunk(
            3,
            1,
            2,
            1,
            [new("context", 3, 2, "x")]);

        Assert.Throws<ArgumentException>(() => Source("a.txt", [first, oldOverlap]));
        Assert.Throws<ArgumentException>(() => Source("a.txt", [first, newOverlap]));
        Assert.Throws<ArgumentException>(() => Source(
            "a.txt",
            [ContextHunk(3), ContextHunk(2)]));
    }

    [Fact]
    public void EveryCanonicalSourceFieldAffectsHashOrConstruction()
    {
        var baseline = Source("a.txt", [ContextHunk(1)]);
        var mutations = new[]
        {
            new ReviewedDiffSource(
                Identity with { ReviewTarget = 2 },
                "a.txt",
                null,
                "modified",
                false,
                [ContextHunk(1)]),
            Source("b.txt", [ContextHunk(1)]),
            new ReviewedDiffSource(
                Identity,
                "b.txt",
                "a.txt",
                "renamed",
                false,
                [ContextHunk(1)]),
            new ReviewedDiffSource(
                Identity,
                "a.txt",
                null,
                "changed",
                false,
                [ContextHunk(1)]),
            new ReviewedDiffSource(
                Identity,
                "a.txt",
                null,
                "modified",
                true,
                [ContextHunk(1)]),
            Source(
                "a.txt",
                [new ReviewedDiffHunk(
                    1,
                    1,
                    1,
                    1,
                    [new("context", 1, 1, "different")])]),
            Source("a.txt", [ContextHunk(2)]),
            Source(
                "a.txt",
                [new ReviewedDiffHunk(
                    1,
                    1,
                    1,
                    1,
                    [
                        new("addition", null, 1, "a"),
                        new("deletion", 1, null, "d"),
                    ])]),
        };

        Assert.All(
            mutations,
            mutation => Assert.NotEqual(
                baseline.PatchSha256,
                mutation.PatchSha256));
        Assert.Throws<ArgumentException>(() => Source(
            "a.txt",
            [ContextHunk(2), ContextHunk(1)]));
        Assert.Throws<ArgumentException>(() => new ReviewedDiffHunk(
            1,
            1,
            1,
            1,
            [new ReviewedDiffLine("addition", null, 1, "x")]));
    }

    private static ReviewedDiffHunk AdditionHunk(string text) =>
        new(
            0,
            0,
            1,
            1,
            [new ReviewedDiffLine("addition", null, 1, text)]);

    private static ReviewedDiffHunk ContextHunk(int line) =>
        new(
            line,
            1,
            line,
            1,
            [new ReviewedDiffLine("context", line, line, string.Empty)]);

    private static ReviewedDiffSource Source(
        string path,
        IEnumerable<ReviewedDiffHunk> hunks) =>
        new(Identity, path, null, "modified", false, hunks);

    private static IEnumerable<T> PoisonAfter<T>(IEnumerable<T> values)
    {
        foreach (var value in values)
        {
            yield return value;
        }

        throw new InvalidOperationException("poison enumeration requested");
    }
}

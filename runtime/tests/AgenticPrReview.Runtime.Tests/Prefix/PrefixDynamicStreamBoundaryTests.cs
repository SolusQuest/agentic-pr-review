using AgenticPrReview.Runtime.Ledger;
using AgenticPrReview.Runtime.Prefix;
using AgenticPrReview.Runtime.Canonical;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Prefix;

public sealed class PrefixDynamicStreamBoundaryTests
{
    private static readonly string[] CjkChars = { "界", "面", "审", "查", "代", "码" };

    private static string CjkPath(int chars, int seed)
    {
        var buffer = new char[chars];
        for (var i = 0; i < chars; i++)
        {
            buffer[i] = CjkChars[(seed + i) % CjkChars.Length][0];
        }

        return new string(buffer);
    }

    /// <summary>
    /// 1 base file + <paramref name="blocks"/> full 500-char CJK files
    /// (≈1566 bytes each) + two tunable files: one CJK path (3 bytes/char)
    /// and one ASCII path (1 byte/char), giving ~1-byte resolution over a
    /// ~2000-byte window.
    /// </summary>
    private static ValidatedContextSource Source(int blocks, int cjkTunableChars, int asciiTunableChars)
    {
        var files = new List<LedgerChangedFile>
        {
            new()
            {
                Path = "src/" + CjkPath(400, 0) + ".ts",
                Status = "modified",
                Additions = 10,
                Deletions = 2,
                Changes = 12,
                Patch = new LedgerBoundedPatch { Sha256 = new string('a', 64), Truncated = false, MaxChars = 20000 },
            },
        };
        for (var i = 0; i < blocks; i++)
        {
            files.Add(new LedgerChangedFile
            {
                Path = CjkPath(500, i * 7),
                Status = "added",
                Additions = 1,
                Deletions = 0,
                Changes = 1,
            });
        }

        files.Add(new LedgerChangedFile
        {
            Path = CjkPath(cjkTunableChars, 3),
            Status = "added",
            Additions = 1,
            Deletions = 0,
            Changes = 1,
        });
        files.Add(new LedgerChangedFile
        {
            Path = new string('p', asciiTunableChars),
            Status = "added",
            Additions = 1,
            Deletions = 0,
            Changes = 1,
        });

        return new ValidatedContextSource
        {
            SubjectDigest = new string('1', 64),
            ReviewedHeadSha = new string('0', 40),
            ReviewedBaseSha = new string('1', 40),
            ChangedFiles = [.. files],
        };
    }

    private static PrefixMaterializationInput Input(int blocks, int cjkTunableChars, int asciiTunableChars)
    {
        var vector = PrefixFixtureLoader.LoadVector("materialization/bootstrap.json");
        var baseInput = PrefixFixtureLoader.BuildMaterializeInput(vector.GetProperty("input"));
        return baseInput with
        {
            CurrentContext = Source(blocks, cjkTunableChars, asciiTunableChars),
        };
    }

    private static long DynamicPayloadBytes(PrefixMaterializationInput input)
    {
        var outcome = PrefixMaterializer.Materialize(input);
        return outcome.Value is null ? long.MaxValue : outcome.Value.DynamicLogicalStream.Length - 4;
    }

    /// <summary>Finds (blocks, cjkChars, asciiChars) producing exactly the target dynamic payload.</summary>
    private static (int Blocks, int CjkChars, int AsciiChars) SolveForPayload(int target)
    {
        for (var blocks = 0; blocks <= 197; blocks++)
        {
            var baseSize = DynamicPayloadBytes(Input(blocks, 1, 1));
            if (baseSize == long.MaxValue || baseSize > target)
            {
                continue;
            }

            var delta = target - (int)baseSize;
            if (delta > 1996)
            {
                continue;
            }

            // delta = 3*(cjk-1) + (ascii-1); cjk in [1,500], ascii in [1,499]
            // so callers can add one more ASCII byte to cross the cap.
            for (var cjkExtra = Math.Max(0, (delta - 499 + 2) / 3); cjkExtra <= Math.Min(499, delta / 3); cjkExtra++)
            {
                var asciiExtra = delta - 3 * cjkExtra;
                if (asciiExtra < 1 || asciiExtra > 498)
                {
                    continue;
                }

                var cjk = 1 + cjkExtra;
                var ascii = 1 + asciiExtra;
                Assert.Equal((long)target, DynamicPayloadBytes(Input(blocks, cjk, ascii)));
                return (blocks, cjk, ascii);
            }
        }

        Assert.Fail($"no (blocks, cjk, ascii) reaches dynamic payload {target}");
        return (-1, -1, -1);
    }

    [Fact]
    public void DynamicFramedStreamExactlyAtCapSucceeds()
    {
        // payload 262_140 → framed 262_144 = MAX_LOGICAL_DYNAMIC_STREAM_BYTES.
        var (blocks, cjk, ascii) = SolveForPayload(262_140);
        var outcome = PrefixMaterializer.Materialize(Input(blocks, cjk, ascii));
        Assert.NotNull(outcome.Value);
        Assert.Equal(262_144, outcome.Value.DynamicLogicalStream.Length);
    }

    [Fact]
    public void DynamicFramedStreamOverCapFailsWithLogicalDynamic()
    {
        // One ASCII byte beyond the exact 262_140 construction → payload
        // 262_141 → framed 262_145 > cap.
        var (blocks, cjk, ascii) = SolveForPayload(262_140);
        var outcome = PrefixMaterializer.Materialize(Input(blocks, cjk, ascii + 1));
        var diagnostic = Assert.Single(outcome.Diagnostics);
        Assert.Equal("prefix_stream_too_large", diagnostic.Code);
        Assert.Equal("logical-dynamic", diagnostic.CauseCode);
    }

    [Fact]
    public void DynamicPayloadOverSegmentCapFailsWithSegmentTooLarge()
    {
        // 1 base + 196 full CJK blocks + 2 tunables (199 files, ≈308 KB) exceeds the segment cap.
        var outcome = PrefixMaterializer.Materialize(Input(196, 1, 1));
        var diagnostic = Assert.Single(outcome.Diagnostics);
        Assert.Equal("prefix_segment_too_large", diagnostic.Code);
    }

    [Fact]
    public void CurrentEvidenceIsDynamicProviderDataAndDoesNotChangeStablePrefix()
    {
        var baseline = Input(0, 1, 1);
        var changedFiles = baseline.CurrentContext.ChangedFiles.ToArray();
        var patchText = "@@ -1 +1 @@\n-old\n+new";
        changedFiles[0] = new LedgerChangedFile
        {
            Path = changedFiles[0].Path,
            Status = changedFiles[0].Status,
            Additions = changedFiles[0].Additions,
            Deletions = changedFiles[0].Deletions,
            Changes = changedFiles[0].Changes,
            Patch = new LedgerBoundedPatch
            {
                Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(patchText))).ToLowerInvariant(),
                Truncated = false,
                MaxChars = 20_000,
            },
        };
        var withEvidence = baseline with
        {
            CurrentContext = new ValidatedContextSource
            {
                SubjectDigest = baseline.CurrentContext.SubjectDigest,
                ReviewedHeadSha = baseline.CurrentContext.ReviewedHeadSha,
                ReviewedBaseSha = baseline.CurrentContext.ReviewedBaseSha,
                ChangedFiles = [.. changedFiles],
                CurrentEvidence = new CurrentReviewEvidence
                {
                    Subject = "Review subject",
                    Files =
                    [
                        new CurrentEvidenceFile { Path = changedFiles[0].Path, Patch = patchText },
                        new CurrentEvidenceFile { Path = changedFiles[1].Path },
                        new CurrentEvidenceFile { Path = changedFiles[2].Path },
                    ],
                },
            },
        };

        var baselineOutcome = PrefixMaterializer.Materialize(baseline);
        var evidenceOutcome = PrefixMaterializer.Materialize(withEvidence);
        Assert.NotNull(baselineOutcome.Value);
        Assert.NotNull(evidenceOutcome.Value);
        Assert.Equal(baselineOutcome.Value!.StableLogicalStream.Length, evidenceOutcome.Value!.StableLogicalStream.Length);
        for (var i = 0; i < baselineOutcome.Value.StableLogicalStream.Length; i++)
        {
            Assert.True(
                baselineOutcome.Value.StableLogicalStream[i] == evidenceOutcome.Value.StableLogicalStream[i],
                $"stable logical differs at {i}: {baselineOutcome.Value.StableLogicalStream[i]} vs {evidenceOutcome.Value.StableLogicalStream[i]}");
        }
        Assert.Equal(baselineOutcome.Value.StableProviderStream.Length, evidenceOutcome.Value.StableProviderStream.Length);
        for (var i = 0; i < baselineOutcome.Value.StableProviderStream.Length; i++)
        {
            Assert.True(
                baselineOutcome.Value.StableProviderStream[i] == evidenceOutcome.Value.StableProviderStream[i],
                $"stable provider differs at {i}: {baselineOutcome.Value.StableProviderStream[i]} vs {evidenceOutcome.Value.StableProviderStream[i]}");
        }
        Assert.NotEqual(baselineOutcome.Value.DynamicLogicalStream, evidenceOutcome.Value.DynamicLogicalStream);
        Assert.Contains(
            "Review subject",
            System.Text.Encoding.UTF8.GetString(evidenceOutcome.Value.DynamicProviderStream.ToArray()));
        Assert.Contains(
            "@@ -1 +1 @@",
            System.Text.Encoding.UTF8.GetString(evidenceOutcome.Value.DynamicProviderStream.ToArray()));

        var dynamicLogical = evidenceOutcome.Value.DynamicLogicalStream.ToArray();
        var logicalPayloadLength = BinaryPrimitives.ReadUInt32BigEndian(dynamicLogical.AsSpan(0, 4));
        var logicalPayload = dynamicLogical.AsSpan(4, checked((int)logicalPayloadLength)).ToArray();
        using var logicalDocument = JsonDocument.Parse(logicalPayload);
        var canonicalLogical = JsonElementCanonicalizer.Canonicalize(
            logicalDocument.RootElement,
            PrefixBounds.MaxEnvelopeJsonDepth,
            PrefixBounds.MaxEnvelopeObjectProperties,
            PrefixBounds.MaxEnvelopeArrayItems,
            long.MaxValue,
            out var logicalCapped);
        Assert.False(logicalCapped);
        Assert.Equal(canonicalLogical.ToArray(), logicalPayload);

        var dynamicProvider = evidenceOutcome.Value.DynamicProviderStream.ToArray();
        var providerPayloadLength = BinaryPrimitives.ReadUInt32BigEndian(dynamicProvider.AsSpan(0, 4));
        var providerPayload = dynamicProvider.AsSpan(4, checked((int)providerPayloadLength)).ToArray();
        using var providerDocument = JsonDocument.Parse(providerPayload);
        var providerText = providerDocument.RootElement.GetProperty("content")[0].GetProperty("text").GetString()!;
        using var providerTextDocument = JsonDocument.Parse(providerText);
        var canonicalProviderText = JsonElementCanonicalizer.Canonicalize(
            providerTextDocument.RootElement,
            PrefixBounds.MaxEnvelopeJsonDepth,
            PrefixBounds.MaxEnvelopeObjectProperties,
            PrefixBounds.MaxEnvelopeArrayItems,
            long.MaxValue,
            out var providerCapped);
        Assert.False(providerCapped);
        Assert.Equal(canonicalProviderText.ToArray(), Encoding.UTF8.GetBytes(providerText));
    }

    [Fact]
    public void CurrentEvidencePathUsesUnicodeCodePointCap()
    {
        var accepted = PrefixMaterializer.Materialize(EvidenceInput(string.Concat(Enumerable.Repeat("😀", 500))));
        Assert.NotNull(accepted.Value);

        var rejected = PrefixMaterializer.Materialize(EvidenceInput(string.Concat(Enumerable.Repeat("😀", 501))));
        Assert.Null(rejected.Value);
        Assert.Equal("prefix_current_context_invalid", Assert.Single(rejected.Diagnostics).Code);
    }

    [Theory]
    [InlineData("1:a")]
    [InlineData(".foo:bar")]
    [InlineData("-x:y")]
    [InlineData("dir/a:b")]
    public void CurrentEvidencePathMatchesAuthoritativeSchemeRule(string path)
    {
        var outcome = PrefixMaterializer.Materialize(EvidenceInput(path));
        Assert.NotNull(outcome.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/absolute")]
    [InlineData("../parent")]
    [InlineData("src/../escape")]
    [InlineData("src\\file.cs")]
    [InlineData("https://example.test/file")]
    [InlineData("a1+.-:x")]
    public void CurrentEvidencePathRejectsUnsafeShapes(string path)
    {
        var outcome = PrefixMaterializer.Materialize(EvidenceInput(path));
        Assert.Null(outcome.Value);
        Assert.Equal("prefix_current_context_invalid", Assert.Single(outcome.Diagnostics).Code);
    }

    [Fact]
    public void CurrentEvidencePatchMustMatchSourcePresenceAndDigest()
    {
        const string sourcePatch = "@@ -1 +1 @@\n-old\n+new";
        const string otherPatch = "@@ -2 +2 @@\n-old\n+new";

        Assert.Null(PrefixMaterializer.Materialize(EvidenceInput("src/file.cs", sourcePatch, null)).Value);
        Assert.Null(PrefixMaterializer.Materialize(EvidenceInput("src/file.cs", null, sourcePatch)).Value);
        Assert.Null(PrefixMaterializer.Materialize(EvidenceInput("src/file.cs", sourcePatch, otherPatch)).Value);
        Assert.NotNull(PrefixMaterializer.Materialize(EvidenceInput("src/file.cs", sourcePatch, sourcePatch)).Value);
    }

    [Fact]
    public void CurrentEvidencePatchCannotBeMovedToAnotherFile()
    {
        const string firstPatch = "@@ -1 +1 @@\n-first\n+new";
        const string secondPatch = "@@ -2 +2 @@\n-second\n+new";
        var outcome = PrefixMaterializer.Materialize(
            EvidenceInput(
                ("src/first.cs", firstPatch, secondPatch),
                ("src/second.cs", secondPatch, firstPatch)));

        Assert.Null(outcome.Value);
        Assert.Equal("prefix_current_context_invalid", Assert.Single(outcome.Diagnostics).Code);
    }

    private static PrefixMaterializationInput EvidenceInput(string path, string? sourcePatch = null, string? evidencePatch = null)
    {
        return EvidenceInput((path, sourcePatch, evidencePatch));
    }

    private static PrefixMaterializationInput EvidenceInput(params (string Path, string? SourcePatch, string? EvidencePatch)[] entries)
    {
        var vector = PrefixFixtureLoader.LoadVector("materialization/bootstrap.json");
        var baseInput = PrefixFixtureLoader.BuildMaterializeInput(vector.GetProperty("input"));
        var changedFiles = entries.Select(entry => new LedgerChangedFile
        {
            Path = entry.Path,
            Status = "modified",
            Additions = 1,
            Deletions = 0,
            Changes = 1,
            Patch = entry.SourcePatch is null ? null : PatchMetadata(entry.SourcePatch),
        }).ToImmutableArray();
        return baseInput with
        {
            CurrentContext = new ValidatedContextSource
            {
                SubjectDigest = new string('1', 64),
                ReviewedHeadSha = new string('0', 40),
                ReviewedBaseSha = new string('1', 40),
                ChangedFiles = changedFiles,
                CurrentEvidence = new CurrentReviewEvidence
                {
                    Subject = "subject",
                    Files = entries.Select(entry => new CurrentEvidenceFile
                    {
                        Path = entry.Path,
                        Patch = entry.EvidencePatch,
                    }).ToImmutableArray(),
                },
            },
        };
    }

    private static LedgerBoundedPatch PatchMetadata(string patch)
    {
        return new LedgerBoundedPatch
        {
            Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(patch))).ToLowerInvariant(),
            Truncated = false,
            MaxChars = 20_000,
        };
    }
}

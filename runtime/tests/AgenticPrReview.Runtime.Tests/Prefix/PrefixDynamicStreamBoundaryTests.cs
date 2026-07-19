using AgenticPrReview.Runtime.Ledger;
using AgenticPrReview.Runtime.Prefix;
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
}

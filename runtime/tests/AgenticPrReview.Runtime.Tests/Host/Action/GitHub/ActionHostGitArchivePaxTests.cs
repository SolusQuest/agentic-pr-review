using System.Formats.Tar;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action.GitHub;

public sealed partial class ActionHostGitObjectTransportTests
{
    [Fact]
    public async Task CodeloadGlobalCommentIsMetadataButSameNamedFileIsNot()
    {
        var raw = PaxArchive(PaxComment(),
            new PaxTarEntry(TarEntryType.RegularFile, "fixture-root/pax_global_header")
            {
                DataStream = new MemoryStream("content"u8.ToArray()),
            });
        var compressed = Gzip(raw);
        using var reader = CreateArchiveReader(compressed, compressed.Length, raw.Length);
        var entry = Assert.IsType<ActionHostGitArchiveEntry>(
            await reader.GetNextEntryAsync(CancellationToken.None));
        Assert.Equal(ActionHostGitArchiveEntryType.RegularFile, entry.EntryType);
        Assert.Equal("fixture-root/pax_global_header", entry.Name);
        await entry.DataStream!.CopyToAsync(Stream.Null);
        Assert.Null(await reader.GetNextEntryAsync(CancellationToken.None));
        Assert.Equal(raw.Length, reader.CapturedDecodedBytes);
        Assert.Equal(compressed.Length, reader.CapturedResponseBytes);
    }

    [Theory]
    [InlineData(TarEntryType.HardLink)]
    [InlineData(TarEntryType.Fifo)]
    public async Task CodeloadGlobalCommentDoesNotSkipUnsupportedFilesystemTypes(TarEntryType type)
    {
        var member = new PaxTarEntry(type, "fixture-root/unsupported");
        if (type == TarEntryType.HardLink) member.LinkName = "fixture-root/file";
        var raw = PaxArchive(PaxComment(), member);
        var compressed = Gzip(raw);
        using var reader = CreateArchiveReader(compressed, compressed.Length, raw.Length);
        var entry = Assert.IsType<ActionHostGitArchiveEntry>(
            await reader.GetNextEntryAsync(CancellationToken.None));
        Assert.Equal(ActionHostGitArchiveEntryType.Unsupported, entry.EntryType);
        Assert.Equal("fixture-root/unsupported", entry.Name);
    }

    [Fact]
    public async Task CodeloadGlobalMetadataRemainsWithinByteCapsAndCancellation()
    {
        var raw = PaxArchive(PaxComment(new string('x', 2048)),
            new PaxTarEntry(TarEntryType.Directory, "fixture-root/"));
        var compressed = Gzip(raw);
        using (var reader = CreateArchiveReader(compressed, compressed.Length, raw.Length))
        {
            await DrainArchiveAsync(reader);
            Assert.Equal(raw.Length, reader.CapturedDecodedBytes);
        }
        using (var reader = CreateArchiveReader(compressed, compressed.Length, 1024))
        {
            var error = await Assert.ThrowsAsync<ActionHostGitArchiveReadException>(async () =>
                await reader.GetNextEntryAsync(CancellationToken.None));
            Assert.Equal(ActionHostGitArchiveReadFailure.DecodedLimitExceeded, error.Failure);
        }
        using (var reader = CreateArchiveReader(compressed, compressed.Length - 1, raw.Length))
        {
            var error = await Assert.ThrowsAsync<ActionHostGitArchiveReadException>(async () =>
                await DrainArchiveAsync(reader));
            Assert.Equal(ActionHostGitArchiveReadFailure.CompressedLimitExceeded, error.Failure);
        }
        using (var reader = CreateArchiveReader(compressed, compressed.Length, raw.Length - 1))
        {
            var error = await Assert.ThrowsAsync<ActionHostGitArchiveReadException>(async () =>
                await DrainArchiveAsync(reader));
            Assert.Equal(ActionHostGitArchiveReadFailure.DecodedLimitExceeded, error.Failure);
        }
        using (var reader = CreateArchiveReader(compressed, compressed.Length, raw.Length))
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await reader.GetNextEntryAsync(cancellation.Token));
        }
    }

    [Fact]
    public async Task CodeloadGlobalCommentDoesNotBypassTarOrGzipTerminationChecks()
    {
        var raw = PaxArchive(PaxComment(),
            new PaxTarEntry(TarEntryType.Directory, "fixture-root/"));
        var compressed = Gzip(raw);
        var cases = new[]
        {
            Gzip([.. raw, 0x7f]),
            compressed.Concat(Gzip(new byte[1024])).ToArray(),
            compressed.Concat(Gzip("hidden"u8.ToArray())).ToArray(),
            compressed.Concat(new byte[] { 0x7f }).ToArray(),
        };
        foreach (var archive in cases)
        {
            using var reader = CreateArchiveReader(archive, archive.Length, raw.Length + 2048);
            await Assert.ThrowsAsync<InvalidDataException>(async () => await DrainArchiveAsync(reader));
        }
        var padded = Gzip([.. raw, .. new byte[1024]]);
        using var valid = CreateArchiveReader(padded, padded.Length, raw.Length + 1024);
        await DrainArchiveAsync(valid);
    }

    [Fact]
    public async Task CodeloadRejectsTruncatedGlobalMetadata()
    {
        var raw = PaxArchive(PaxComment(new string('x', 2048)));
        var truncated = Gzip(raw[..600]);
        using var reader = CreateArchiveReader(truncated, truncated.Length, raw.Length);
        await Assert.ThrowsAnyAsync<IOException>(async () =>
            await reader.GetNextEntryAsync(CancellationToken.None));
    }

    private static PaxGlobalExtendedAttributesTarEntry PaxComment(string value = "not-an-authority") =>
        new([new("comment", value)]);

    private static byte[] PaxArchive(params TarEntry[] entries)
    {
        using var output = new MemoryStream();
        using (var writer = new TarWriter(output, leaveOpen: true))
        {
            foreach (var entry in entries) writer.WriteEntry(entry);
        }
        return output.ToArray();
    }
}

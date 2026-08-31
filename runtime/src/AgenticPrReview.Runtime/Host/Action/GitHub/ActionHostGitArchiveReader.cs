using System.Formats.Tar;
using System.IO.Compression;

namespace AgenticPrReview.Runtime.ActionHost.GitHub;

// This is deliberately the only gzip/tar reader on the trusted Action-host
// path.  It keeps codeload response ownership and the compressed byte bound at
// the GitHub edge while leaving path, Git identity, aggregate and staging
// decisions to Snapshot.
internal sealed class GitHubCodeloadArchiveReader : ActionHostGitArchiveReader
{
    private readonly HttpResponseMessage _response;
    private readonly ActionHostBoundedReadStream _compressed;
    private readonly RecordingReadStream _recordedCompressed;
    private readonly GZipStream _gzip;
    private readonly ActionHostBoundedReadStream _decoded;
    private readonly TarReader _tar;
    private bool _completed;
    private bool _disposed;

    internal GitHubCodeloadArchiveReader(
        HttpResponseMessage response,
        Stream compressed,
        int maximumCompressedBytes,
        int maximumDecodedBytes)
    {
        _response = response ?? throw new ArgumentNullException(nameof(response));
        _compressed = new ActionHostBoundedReadStream(
            compressed ?? throw new ArgumentNullException(nameof(compressed)),
            maximumCompressedBytes);
        _recordedCompressed = new RecordingReadStream(_compressed);
        _gzip = new GZipStream(_recordedCompressed, CompressionMode.Decompress,
            leaveOpen: false);
        _decoded = new ActionHostBoundedReadStream(_gzip,
            maximumDecodedBytes);
        _tar = new TarReader(_decoded, leaveOpen: false);
    }

    internal override int CapturedResponseBytes => _compressed.CapturedBytes;

    internal int CapturedDecodedBytes => _decoded.CapturedBytes;

    internal override async Task<ActionHostGitArchiveEntry?> GetNextEntryAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        TarEntry? entry;
        try
        {
            entry = await _tar.GetNextEntryAsync(
                cancellationToken: cancellationToken);
        }
        catch (InvalidDataException) when (_compressed.Exceeded)
        {
            throw new ActionHostGitArchiveReadException(
                ActionHostGitArchiveReadFailure.CompressedLimitExceeded);
        }
        catch (InvalidDataException) when (_decoded.Exceeded)
        {
            throw new ActionHostGitArchiveReadException(
                ActionHostGitArchiveReadFailure.DecodedLimitExceeded);
        }
        if (entry is null)
        {
            if (!_completed)
            {
                try
                {
                    await VerifyEndOfArchiveAsync(cancellationToken);
                }
                catch (InvalidDataException) when (_compressed.Exceeded)
                {
                    throw new ActionHostGitArchiveReadException(
                        ActionHostGitArchiveReadFailure.CompressedLimitExceeded);
                }
                catch (InvalidDataException) when (_decoded.Exceeded)
                {
                    throw new ActionHostGitArchiveReadException(
                        ActionHostGitArchiveReadFailure.DecodedLimitExceeded);
                }
                _completed = true;
            }

            return null;
        }

        return new ActionHostGitArchiveEntry(
            entry.Name ?? string.Empty,
            MapEntryType(entry.EntryType),
            (int)entry.Mode,
            entry.Length,
            entry.LinkName,
            entry.DataStream is { } dataStream
                ? new ArchiveEntryDataStream(dataStream, _compressed, _decoded)
                : null);
    }

    public override void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tar.Dispose();
        _decoded.Dispose();
        _gzip.Dispose();
        _recordedCompressed.Dispose();
        _compressed.Dispose();
        _response.Dispose();
    }

    private static ActionHostGitArchiveEntryType MapEntryType(
        TarEntryType entryType) =>
        entryType switch
        {
            TarEntryType.Directory => ActionHostGitArchiveEntryType.Directory,
            TarEntryType.RegularFile or TarEntryType.V7RegularFile =>
                ActionHostGitArchiveEntryType.RegularFile,
            TarEntryType.SymbolicLink =>
                ActionHostGitArchiveEntryType.SymbolicLink,
            _ => ActionHostGitArchiveEntryType.Unsupported,
        };

    private async Task VerifyEndOfArchiveAsync(
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await _decoded.ReadAsync(buffer, cancellationToken)) != 0)
        {
            // Tar permits zero-filled records after its two end-of-archive
            // blocks. TarReader stops at the terminator without necessarily
            // consuming that legal padding, so reject only data that could
            // represent another decoded payload.
            if (buffer.AsSpan(0, read).IndexOfAnyExcept((byte)0) >= 0)
            {
                throw new InvalidDataException(
                    "The codeload archive contains trailing data.");
            }
        }

        // GZipStream is permitted to read ahead, so first complete the raw
        // response through the same bounded/recorded edge. The recorded bytes
        // are then parsed to locate the one permitted gzip-member boundary.
        while (await _recordedCompressed.ReadAsync(buffer, cancellationToken) != 0)
        {
        }

        VerifyExactlyOneGzipMember();
    }

    private void VerifyExactlyOneGzipMember()
    {
        if (GzipMemberBoundary.GetEndOffset(_recordedCompressed.AsSpan()) !=
            _recordedCompressed.RecordedLength)
        {
            throw new InvalidDataException(
                "The codeload archive contains trailing compressed data.");
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(GitHubCodeloadArchiveReader));
        }
    }

    // Tar entry bodies are consumed by Snapshot after GetNextEntryAsync has
    // returned.  Preserve the typed bound cause across that streaming seam so
    // an inflated payload cannot be mistaken for a Git identity mismatch.
    private sealed class ArchiveEntryDataStream : Stream
    {
        private readonly Stream _inner;
        private readonly ActionHostBoundedReadStream _compressed;
        private readonly ActionHostBoundedReadStream _decoded;

        internal ArchiveEntryDataStream(
            Stream inner,
            ActionHostBoundedReadStream compressed,
            ActionHostBoundedReadStream decoded)
        {
            _inner = inner;
            _compressed = compressed;
            _decoded = decoded;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            try
            {
                return _inner.Read(buffer, offset, count);
            }
            catch (InvalidDataException) when (_compressed.Exceeded)
            {
                throw new ActionHostGitArchiveReadException(
                    ActionHostGitArchiveReadFailure.CompressedLimitExceeded);
            }
            catch (InvalidDataException) when (_decoded.Exceeded)
            {
                throw new ActionHostGitArchiveReadException(
                    ActionHostGitArchiveReadFailure.DecodedLimitExceeded);
            }
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            try
            {
                return await _inner.ReadAsync(buffer, cancellationToken);
            }
            catch (InvalidDataException) when (_compressed.Exceeded)
            {
                throw new ActionHostGitArchiveReadException(
                    ActionHostGitArchiveReadFailure.CompressedLimitExceeded);
            }
            catch (InvalidDataException) when (_decoded.Exceeded)
            {
                throw new ActionHostGitArchiveReadException(
                    ActionHostGitArchiveReadFailure.DecodedLimitExceeded);
            }
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    // Retain the bounded response only until end-of-archive verification.
    // This permits a deterministic replay without a second network read and
    // is itself capped by ActionHostBoundedReadStream (currently 16 MiB).
    private sealed class RecordingReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly MemoryStream _recorded = new();

        internal RecordingReadStream(Stream inner)
        {
            _inner = inner;
        }

        internal int RecordedLength => checked((int)_recorded.Length);
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            Record(buffer.AsSpan(offset, read));
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = _inner.Read(buffer);
            Record(buffer[..read]);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await _inner.ReadAsync(buffer, cancellationToken);
            Record(buffer.Span[..read]);
            return read;
        }

        internal ReadOnlySpan<byte> AsSpan() =>
            _recorded.GetBuffer().AsSpan(0, RecordedLength);

        private void Record(ReadOnlySpan<byte> bytes)
        {
            _recorded.Write(bytes);
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _recorded.Dispose();
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    // GZipStream accepts concatenated members and can read ahead into trailing
    // bytes. The archive accepts exactly one member. This parser locates that
    // member's trailer from the gzip/DEFLATE grammar; actual decompression and
    // checksum validation remain the responsibility of GZipStream above.
    private static class GzipMemberBoundary
    {
        private static readonly int[] DistanceExtraBits =
        {
            0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8,
            9, 9, 10, 10, 11, 11, 12, 12, 13, 13, 14, 14,
        };

        private static readonly int[] LengthExtraBits =
        {
            0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2,
            3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0,
        };

        internal static int GetEndOffset(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length < 18 || bytes[0] != 0x1f || bytes[1] != 0x8b ||
                bytes[2] != 8 || (bytes[3] & 0xe0) != 0)
            {
                throw new InvalidDataException("The codeload response is not a gzip member.");
            }

            var offset = 10;
            var flags = bytes[3];
            if ((flags & 0x04) != 0)
            {
                Require(bytes, offset, 2);
                offset = checked(offset + 2 + bytes[offset] + (bytes[offset + 1] << 8));
                Require(bytes, offset, 0);
            }

            if ((flags & 0x08) != 0) offset = SkipZeroTerminated(bytes, offset);
            if ((flags & 0x10) != 0) offset = SkipZeroTerminated(bytes, offset);
            if ((flags & 0x02) != 0)
            {
                offset = checked(offset + 2);
                Require(bytes, offset, 0);
            }

            var bits = new BitReader(bytes, offset);
            var final = false;
            while (!final)
            {
                final = bits.ReadBits(1) != 0;
                switch (bits.ReadBits(2))
                {
                    case 0:
                        bits.AlignToByte();
                        var length = bits.ReadBits(16);
                        if ((length ^ 0xffff) != bits.ReadBits(16))
                        {
                            throw new InvalidDataException("Invalid stored DEFLATE block.");
                        }

                        bits.SkipBits(checked(length * 8));
                        break;
                    case 1:
                        ConsumeCompressedBlock(ref bits, Huffman.FixedLiteralLength,
                            Huffman.FixedDistance);
                        break;
                    case 2:
                        var trees = ReadDynamicTrees(ref bits);
                        ConsumeCompressedBlock(ref bits, trees.LiteralLength,
                            trees.Distance);
                        break;
                    default:
                        throw new InvalidDataException("Reserved DEFLATE block type.");
                }
            }

            var trailerOffset = bits.ByteOffset;
            Require(bytes, trailerOffset, 8);
            return checked(trailerOffset + 8);
        }

        private static (Huffman LiteralLength, Huffman? Distance) ReadDynamicTrees(
            ref BitReader bits)
        {
            var literalCount = checked(bits.ReadBits(5) + 257);
            var distanceCount = checked(bits.ReadBits(5) + 1);
            var codeLengthCount = checked(bits.ReadBits(4) + 4);
            var order = new[]
            {
                16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1,
                15,
            };
            var codeLengths = new int[19];
            for (var index = 0; index < codeLengthCount; index++)
            {
                codeLengths[order[index]] = bits.ReadBits(3);
            }

            var codeLengthTree = Huffman.Create(codeLengths, allowEmpty: false)!;
            var allLengths = new int[literalCount + distanceCount];
            var position = 0;
            while (position < allLengths.Length)
            {
                var symbol = codeLengthTree.Decode(ref bits);
                if (symbol <= 15)
                {
                    allLengths[position++] = symbol;
                    continue;
                }

                var repeat = symbol switch
                {
                    16 when position > 0 => bits.ReadBits(2) + 3,
                    17 => bits.ReadBits(3) + 3,
                    18 => bits.ReadBits(7) + 11,
                    _ => throw new InvalidDataException("Invalid DEFLATE code length."),
                };
                if (repeat > allLengths.Length - position)
                {
                    throw new InvalidDataException("Invalid DEFLATE code length repeat.");
                }

                var value = symbol == 16 ? allLengths[position - 1] : 0;
                for (var index = 0; index < repeat; index++) allLengths[position++] = value;
            }

            if (allLengths[256] == 0)
            {
                throw new InvalidDataException("DEFLATE block has no end marker.");
            }

            return (
                Huffman.Create(allLengths.AsSpan(0, literalCount), allowEmpty: false)!,
                Huffman.Create(allLengths.AsSpan(literalCount), allowEmpty: true));
        }

        private static void ConsumeCompressedBlock(
            ref BitReader bits,
            Huffman literalLength,
            Huffman? distance)
        {
            while (true)
            {
                var symbol = literalLength.Decode(ref bits);
                if (symbol < 256) continue;
                if (symbol == 256) return;
                if (symbol is < 257 or > 285)
                {
                    throw new InvalidDataException("Invalid DEFLATE length code.");
                }

                bits.SkipBits(LengthExtraBits[symbol - 257]);
                var distanceSymbol = distance?.Decode(ref bits) ??
                    throw new InvalidDataException("Missing DEFLATE distance tree.");
                if (distanceSymbol > 29)
                {
                    throw new InvalidDataException("Invalid DEFLATE distance code.");
                }

                bits.SkipBits(DistanceExtraBits[distanceSymbol]);
            }
        }

        private static int SkipZeroTerminated(ReadOnlySpan<byte> bytes, int offset)
        {
            while (offset < bytes.Length)
            {
                if (bytes[offset++] == 0) return offset;
            }

            throw new InvalidDataException("Truncated gzip header.");
        }

        private static void Require(ReadOnlySpan<byte> bytes, int offset, int count)
        {
            if (offset < 0 || count < 0 || offset > bytes.Length - count)
            {
                throw new InvalidDataException("Truncated gzip member.");
            }
        }

        private ref struct BitReader
        {
            private readonly ReadOnlySpan<byte> _bytes;
            private int _bitOffset;

            internal BitReader(ReadOnlySpan<byte> bytes, int byteOffset)
            {
                _bytes = bytes;
                _bitOffset = checked(byteOffset * 8);
            }

            internal int ByteOffset => checked((_bitOffset + 7) / 8);

            internal int ReadBits(int count)
            {
                if (count is < 0 or > 16 || _bitOffset > _bytes.Length * 8 - count)
                {
                    throw new InvalidDataException("Truncated DEFLATE stream.");
                }

                var value = 0;
                for (var bit = 0; bit < count; bit++)
                {
                    value |= ((_bytes[(_bitOffset + bit) / 8] >>
                        ((_bitOffset + bit) % 8)) & 1) << bit;
                }

                _bitOffset += count;
                return value;
            }

            internal void SkipBits(int count)
            {
                if (count < 0 || _bitOffset > _bytes.Length * 8 - count)
                {
                    throw new InvalidDataException("Truncated DEFLATE stream.");
                }

                _bitOffset += count;
            }

            internal void AlignToByte() => _bitOffset = checked(
                (_bitOffset + 7) / 8 * 8);
        }

        private sealed class Huffman
        {
            private readonly Dictionary<int, int> _symbols;

            internal static Huffman FixedLiteralLength { get; } = CreateFixedLiteralLength();
            internal static Huffman FixedDistance { get; } = CreateFixedDistance();

            private Huffman(Dictionary<int, int> symbols) => _symbols = symbols;

            internal static Huffman? Create(ReadOnlySpan<int> lengths, bool allowEmpty)
            {
                var counts = new int[16];
                foreach (var length in lengths)
                {
                    if (length is < 0 or > 15)
                    {
                        throw new InvalidDataException("Invalid DEFLATE code length.");
                    }

                    if (length != 0) counts[length]++;
                }

                var nextCode = new int[16];
                var code = 0;
                for (var bits = 1; bits <= 15; bits++)
                {
                    code = (code + counts[bits - 1]) << 1;
                    nextCode[bits] = code;
                }

                var symbols = new Dictionary<int, int>();
                for (var symbol = 0; symbol < lengths.Length; symbol++)
                {
                    var length = lengths[symbol];
                    if (length == 0) continue;
                    var reversed = ReverseBits(nextCode[length]++, length);
                    if (!symbols.TryAdd(length << 16 | reversed, symbol))
                    {
                        throw new InvalidDataException("Duplicate DEFLATE code.");
                    }
                }

                if (symbols.Count == 0)
                {
                    if (allowEmpty) return null;
                    throw new InvalidDataException("Empty DEFLATE tree.");
                }

                return new Huffman(symbols);
            }

            internal int Decode(ref BitReader bits)
            {
                var code = 0;
                for (var length = 1; length <= 15; length++)
                {
                    code |= bits.ReadBits(1) << (length - 1);
                    if (_symbols.TryGetValue(length << 16 | code, out var symbol)) return symbol;
                }

                throw new InvalidDataException("Invalid DEFLATE Huffman code.");
            }

            private static Huffman CreateFixedLiteralLength()
            {
                var lengths = new int[288];
                for (var symbol = 0; symbol < lengths.Length; symbol++)
                {
                    lengths[symbol] = symbol switch
                    {
                        <= 143 => 8,
                        <= 255 => 9,
                        <= 279 => 7,
                        _ => 8,
                    };
                }

                return Create(lengths, allowEmpty: false)!;
            }

            private static Huffman CreateFixedDistance()
            {
                var lengths = new int[32];
                Array.Fill(lengths, 5);
                return Create(lengths, allowEmpty: false)!;
            }

            private static int ReverseBits(int value, int count)
            {
                var result = 0;
                for (var index = 0; index < count; index++)
                {
                    result = result << 1 | value & 1;
                    value >>= 1;
                }

                return result;
            }
        }
    }
}

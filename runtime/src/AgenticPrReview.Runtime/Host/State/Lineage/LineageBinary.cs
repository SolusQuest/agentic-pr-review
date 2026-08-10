using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace AgenticPrReview.Runtime.Host.State.Lineage;

internal sealed class LineageBinaryWriter
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ArrayBufferWriter<byte> writer = new();

    internal void WriteByte(byte value)
    {
        var span = writer.GetSpan(1);
        span[0] = value;
        writer.Advance(1);
    }

    internal void WriteUInt16(ushort value)
    {
        var span = writer.GetSpan(sizeof(ushort));
        BinaryPrimitives.WriteUInt16LittleEndian(span, value);
        writer.Advance(sizeof(ushort));
    }

    internal void WriteUInt32(uint value)
    {
        var span = writer.GetSpan(sizeof(uint));
        BinaryPrimitives.WriteUInt32LittleEndian(span, value);
        writer.Advance(sizeof(uint));
    }

    internal void WriteUInt64(ulong value)
    {
        var span = writer.GetSpan(sizeof(ulong));
        BinaryPrimitives.WriteUInt64LittleEndian(span, value);
        writer.Advance(sizeof(ulong));
    }

    internal void WriteInt64(long value)
    {
        var span = writer.GetSpan(sizeof(long));
        BinaryPrimitives.WriteInt64LittleEndian(span, value);
        writer.Advance(sizeof(long));
    }

    internal void WriteBytes(ReadOnlySpan<byte> value)
    {
        WriteUInt32(checked((uint)value.Length));
        value.CopyTo(writer.GetSpan(value.Length));
        writer.Advance(value.Length);
    }

    internal void WriteString(string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        try
        {
            WriteBytes(bytes);
        }
        finally
        {
            Array.Clear(bytes);
        }
    }

    internal void WriteOptionalString(string? value)
    {
        WriteByte(value is null ? (byte)0 : (byte)1);
        if (value is not null)
        {
            WriteString(value);
        }
    }

    internal byte[] ToArray() => writer.WrittenMemory.ToArray();
}

internal ref struct LineageBinaryReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ReadOnlySpan<byte> bytes;
    private int offset;

    internal LineageBinaryReader(ReadOnlySpan<byte> bytes)
    {
        this.bytes = bytes;
        offset = 0;
    }

    internal bool IsComplete => offset == bytes.Length;

    internal bool TryReadByte(out byte value)
    {
        value = 0;
        if (bytes.Length - offset < 1)
        {
            return false;
        }

        value = bytes[offset++];
        return true;
    }

    internal bool TryReadUInt16(out ushort value)
    {
        value = 0;
        if (bytes.Length - offset < sizeof(ushort))
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
        offset += sizeof(ushort);
        return true;
    }

    internal bool TryReadUInt32(out uint value)
    {
        value = 0;
        if (bytes.Length - offset < sizeof(uint))
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
        offset += sizeof(uint);
        return true;
    }

    internal bool TryReadUInt64(out ulong value)
    {
        value = 0;
        if (bytes.Length - offset < sizeof(ulong))
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..]);
        offset += sizeof(ulong);
        return true;
    }

    internal bool TryReadInt64(out long value)
    {
        value = 0;
        if (bytes.Length - offset < sizeof(long))
        {
            return false;
        }

        value = BinaryPrimitives.ReadInt64LittleEndian(bytes[offset..]);
        offset += sizeof(long);
        return true;
    }

    internal bool TryReadBytes(int maximumBytes, out byte[] value)
    {
        value = [];
        if (!TryReadUInt32(out var length) ||
            length > maximumBytes ||
            length > bytes.Length - offset)
        {
            return false;
        }

        value = bytes.Slice(offset, checked((int)length)).ToArray();
        offset += checked((int)length);
        return true;
    }

    internal bool TryReadString(int maximumBytes, out string value)
    {
        value = string.Empty;
        if (!TryReadBytes(maximumBytes, out var encoded))
        {
            return false;
        }

        try
        {
            value = StrictUtf8.GetString(encoded);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
        finally
        {
            Array.Clear(encoded);
        }
    }

    internal bool TryReadOptionalString(
        int maximumBytes,
        out string? value)
    {
        value = null;
        if (!TryReadByte(out var present) || present > 1)
        {
            return false;
        }

        if (present == 0)
        {
            return true;
        }

        if (!TryReadString(maximumBytes, out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }
}

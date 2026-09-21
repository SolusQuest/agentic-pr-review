using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Live;

internal static class EconomicsWire
{
    internal static async Task<T> ReadAsync<T>(Stream stream, JsonTypeInfo<T> type, int maximum, CancellationToken token) where T : class
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 1 || length > maximum) throw new IOException();
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token);
        return ReplayWire.Read(bytes, type, maximum) ?? throw new IOException();
    }
    internal static async Task WriteAsync<T>(Stream stream, T value, JsonTypeInfo<T> type, int maximum, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, type);
        if (bytes.Length < 1 || bytes.Length > maximum) throw new IOException();
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(bytes, token); await stream.FlushAsync(token);
    }
}

using System.Security.Cryptography;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;

namespace AgenticPrReview.Runtime.Host.State.GitHubArtifacts;

// Both cache families spend the same allowance. All access is under the
// invocation store lane; no invalidation can race a conditional reconstruction.
internal sealed class ArtifactObservationCache : IDisposable
{
    private const int MaximumBytes = 64 * 1024 * 1024;
    private readonly Dictionary<string, CacheEntry> entries = new(StringComparer.Ordinal);
    private readonly LinkedList<string> order = new();
    private long bytes;
    private bool disposed;

    internal sealed record Observation(byte[] Bytes, string? Etag, OpaqueStoreObjectMetadata? Metadata);
    private sealed record CacheEntry(Observation Value, LinkedListNode<string> Node, int Charge);

    internal Observation? Get(string key)
    {
        if (disposed || !entries.TryGetValue(key, out var entry)) return null;
        order.Remove(entry.Node);
        order.AddLast(entry.Node);
        return entry.Value with { Bytes = (byte[])entry.Value.Bytes.Clone() };
    }

    internal void Put(string key, byte[] value, string? etag = null, OpaqueStoreObjectMetadata? metadata = null)
    {
        if (disposed) return;
        Remove(key);
        var charge = checked(value.Length + (metadata is null ? 0 : 1024));
        if (charge > MaximumBytes) return;
        var prefix = metadata is null ? "json:" : "record:";
        var maximumEntries = metadata is null ? 1024 : 32;
        while (bytes + charge > MaximumBytes || entries.Keys.Count(k => k.StartsWith(prefix, StringComparison.Ordinal)) >= maximumEntries)
        {
            if (order.First is null) return;
            Remove(order.First.Value);
        }
        // Evict and wipe before allocating the defensive successor copy.
        var node = order.AddLast(key);
        entries.Add(key, new(new((byte[])value.Clone(), etag, metadata), node, charge));
        bytes += charge;
    }

    internal void Remove(string key)
    {
        if (!entries.Remove(key, out var entry)) return;
        order.Remove(entry.Node);
        bytes -= entry.Charge;
        CryptographicOperations.ZeroMemory(entry.Value.Bytes);
    }

    internal void RemoveWhere(Func<string, Observation, bool> predicate)
    {
        foreach (var key in entries.Where(item => predicate(item.Key, item.Value.Value)).Select(item => item.Key).ToArray())
            Remove(key);
    }

    public void Dispose()
    {
        disposed = true;
        foreach (var key in entries.Keys.ToArray()) Remove(key);
    }
}

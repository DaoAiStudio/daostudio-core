using System.Collections.Concurrent;
using DaoStudio.Engine.Abstractions;

namespace DaoStudio.Engine;

public sealed class L0MemoryCache : ITranslationCache
{
    private readonly ConcurrentDictionary<CacheKey, string> entries = new();
    private readonly Queue<CacheKey> order = new();
    private readonly object gate = new();
    private readonly int capacity;
    public L0MemoryCache(int maxEntries = 10000)
    {
        if (maxEntries <= 0) throw new ArgumentOutOfRangeException(nameof(maxEntries));
        capacity = maxEntries;
    }
    public int Count => entries.Count;
    public ValueTask<CacheLookupResult> TryGetAsync(CacheKey key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(entries.TryGetValue(key, out var value)
            ? new CacheLookupResult(true, value, "L0") : new CacheLookupResult(false, null, "L0"));
    }
    public ValueTask SetAsync(CacheKey key, string translatedTemplate, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(translatedTemplate);
        lock (gate)
        {
            if (!entries.ContainsKey(key))
            {
                if (entries.Count == capacity) entries.TryRemove(order.Dequeue(), out _);
                order.Enqueue(key);
            }
            entries[key] = translatedTemplate;
        }
        return ValueTask.CompletedTask;
    }
}

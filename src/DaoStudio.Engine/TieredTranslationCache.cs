using DaoStudio.Engine.Abstractions;

namespace DaoStudio.Engine;

// Dependencies remain owned by the caller. Serialize promotion and writes to prevent stale L0 fills.
public sealed class TieredTranslationCache(L0MemoryCache l0, L1SqliteCache l1) : ITranslationCache
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async ValueTask<CacheLookupResult> TryGetAsync(CacheKey key, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await l0.TryGetAsync(key, ct);
            if (result.Hit) return result;
            result = await l1.TryGetAsync(key, ct);
            if (result.Hit) await l0.SetAsync(key, result.TranslatedTemplate!, CancellationToken.None);
            return result;
        }
        finally { gate.Release(); }
    }
    public async ValueTask SetAsync(CacheKey key, string translatedTemplate, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await l1.SetAsync(key, translatedTemplate, ct);
            await l0.SetAsync(key, translatedTemplate, CancellationToken.None);
        }
        finally { gate.Release(); }
    }
}

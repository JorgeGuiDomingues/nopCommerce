using Nop.Core.Caching;
using Nop.Services.Catalog;

namespace Nop.Web.Framework.Infrastructure;

/// <summary>
/// Decorator for IStaticCacheManager that records cache hit/miss metrics.
/// Uses the acquire-callback pattern: if the inner cache manager invokes the acquire
/// function, it means the cache did not have the value (miss). If it never calls
/// acquire, the value was served from cache (hit).
/// </summary>
public class InstrumentedStaticCacheManager : IStaticCacheManager
{
    private readonly IStaticCacheManager _inner;

    public InstrumentedStaticCacheManager(IStaticCacheManager inner)
    {
        _inner = inner;
    }

    /// <summary>
    /// Extracts a prefix from the cache key for the metric tag.
    /// Uses the first segment before the first dot for grouping.
    /// </summary>
    private static string GetKeyPrefix(CacheKey key)
    {
        if (key?.Key == null) return "unknown";
        var k = key.Key;
        var dotIndex = k.IndexOf('.');
        return dotIndex > 0 ? k[..dotIndex] : k;
    }

    public async Task<T> GetAsync<T>(CacheKey key, Func<Task<T>> acquire)
    {
        var wasMiss = false;
        var result = await _inner.GetAsync(key, async () =>
        {
            wasMiss = true;
            return await acquire();
        });

        var prefix = GetKeyPrefix(key);
        if (wasMiss)
            CatalogInstrumentation.CacheMisses.Add(1, new KeyValuePair<string, object>("cache.key_prefix", prefix));
        else
            CatalogInstrumentation.CacheHits.Add(1, new KeyValuePair<string, object>("cache.key_prefix", prefix));

        return result;
    }

    public async Task<T> GetAsync<T>(CacheKey key, Func<T> acquire)
    {
        var wasMiss = false;
        var result = await _inner.GetAsync(key, () =>
        {
            wasMiss = true;
            return acquire();
        });

        var prefix = GetKeyPrefix(key);
        if (wasMiss)
            CatalogInstrumentation.CacheMisses.Add(1, new KeyValuePair<string, object>("cache.key_prefix", prefix));
        else
            CatalogInstrumentation.CacheHits.Add(1, new KeyValuePair<string, object>("cache.key_prefix", prefix));

        return result;
    }

    // --- Delegate-only methods (no hit/miss logic needed) ---

    public Task<T> GetAsync<T>(CacheKey key, T defaultValue = default)
        => _inner.GetAsync(key, defaultValue);

    public Task<object> GetAsync(CacheKey key)
        => _inner.GetAsync(key);

    public Task RemoveAsync(CacheKey cacheKey, params object[] cacheKeyParameters)
        => _inner.RemoveAsync(cacheKey, cacheKeyParameters);

    public Task SetAsync<T>(CacheKey key, T data)
        => _inner.SetAsync(key, data);

    public Task RemoveByPrefixAsync(string prefix, params object[] prefixParameters)
        => _inner.RemoveByPrefixAsync(prefix, prefixParameters);

    public Task ClearAsync()
        => _inner.ClearAsync();

    // --- ICacheKeyService delegations ---

    public CacheKey PrepareKey(CacheKey cacheKey, params object[] cacheKeyParameters)
        => _inner.PrepareKey(cacheKey, cacheKeyParameters);

    public CacheKey PrepareKeyForDefaultCache(CacheKey cacheKey, params object[] cacheKeyParameters)
        => _inner.PrepareKeyForDefaultCache(cacheKey, cacheKeyParameters);

    // --- IDisposable ---

    public void Dispose()
    {
        _inner.Dispose();
        GC.SuppressFinalize(this);
    }
}

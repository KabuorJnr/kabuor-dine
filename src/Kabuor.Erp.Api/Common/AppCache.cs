using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Distributed;

namespace Kabuor.Erp.Common;

/// <summary>Distributed caching abstraction (backed by Redis in production, or memory in development).</summary>
public sealed class AppCache(IDistributedCache cache)
{
    /// <summary>The same JSON the API answers with (camelCase, enums as words). A cached answer is handed back as is,
    /// so it has to look exactly like a fresh one: with .NET's defaults the second load said "Name" instead of "name"
    /// and the screens showed blanks.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerOptions.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>A cached API answer, ready to return, or null when there is none.</summary>
    public async Task<JsonElement?> GetAnswerAsync(string key)
    {
        var json = await GetStringAsync(key);
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<JsonElement>(json, Json); }
        catch { return null; }
    }

    /// <summary>Keeps an API answer for a short while, serialized the way the API would send it.</summary>
    public async Task SetAnswerAsync(string key, object answer, TimeSpan expiry)
    {
        try { await SetStringAsync(key, JsonSerializer.Serialize(answer, Json), expiry); }
        catch { }
    }

    public async Task<T?> GetAsync<T>(string key)
    {
        try
        {
            var bytes = await cache.GetAsync(key);
            if (bytes is null || bytes.Length == 0) return default;
            return JsonSerializer.Deserialize<T>(bytes, Json);
        }
        catch
        {
            return default;
        }
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan? expiry = null)
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
            var options = new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = expiry ?? TimeSpan.FromMinutes(5)
            };
            await cache.SetAsync(key, bytes, options);
        }
        catch
        {
            // Transient cache failures should never crash application requests
        }
    }

    public async Task<string?> GetStringAsync(string key)
    {
        try
        {
            return await cache.GetStringAsync(key);
        }
        catch
        {
            return null;
        }
    }

    public async Task SetStringAsync(string key, string value, TimeSpan? expiry = null)
    {
        try
        {
            var options = new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = expiry ?? TimeSpan.FromMinutes(5)
            };
            await cache.SetStringAsync(key, value, options);
        }
        catch {}
    }

    public async Task RemoveAsync(string key)
    {
        try
        {
            await cache.RemoveAsync(key);
        }
        catch {}
    }

    public async Task<T> GetOrSetAsync<T>(string key, Func<Task<T>> factory, TimeSpan? expiry = null)
    {
        var cached = await GetAsync<T>(key);
        if (cached is not null) return cached;
        var fresh = await factory();
        if (fresh is not null) await SetAsync(key, fresh, expiry);
        return fresh;
    }
}

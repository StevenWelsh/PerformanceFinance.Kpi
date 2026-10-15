using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace Kpi.Web.Data.Kpis;

/// <summary>
/// App-wide (singleton) cache of KPI report results, shared across ALL users.
///
/// ─────────────────────────────────────────────────────────────────────────────
///  ⚠️  SHARED-CACHE RULE — READ BEFORE ADDING A KPI
///      This cache is SHARED between users, so an entry is looked up by its key
///      alone. That is correct ONLY because every KPI today is user-agnostic:
///      its output is a pure function of (parameters + database state + report
///      date), never of who is logged in (verified Aug 2026 across all 15 procs).
///      If you ever add a KPI whose data DIFFERS BY USER (e.g. "my leads"),
///      you MUST include the user id in its cache key — otherwise one user's
///      view leaks to the next. Prefer per-user keys over making the whole cache
///      per-user; the sharing is where the performance comes from.
/// ─────────────────────────────────────────────────────────────────────────────
///
/// Behaviour:
///   • GetOrLoad — returns the cached entry instantly if present; otherwise loads
///     once (single-flight: concurrent callers for the same key await one load).
///   • RefreshAsync — re-runs the loader off the render path, compares a content
///     hash against the cached entry, and either swaps the data (changed) or just
///     advances the timestamps (unchanged). The caller (KpiPanel) drives WHEN this
///     happens: the 10-minute in-view timer, or a return-to-view staleness check.
///
/// Keying: callers build keys via <see cref="Key"/>, which folds in the current
/// business report date. When the date rolls, keys change, so stale-across-a-day
/// entries are naturally superseded (and the old ones expire out).
///
/// Storage: durable — entries survive circuit/tab teardown (this is a singleton,
/// not per-circuit). A 4-hour sliding expiry frees reports nobody has touched;
/// closing a tab alone never drops an entry.
/// </summary>
public sealed class KpiCache(IMemoryCache cache, ReportDateProvider reportDate, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger("Kpi.Web.KpiCache");

    /// <summary>Untouched reports free themselves after this; a touched one keeps sliding.</summary>
    private static readonly TimeSpan SlidingExpiry = TimeSpan.FromHours(4);

    /// <summary>The visible report refreshes on this cadence; also the return-to-view staleness cutoff.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(10);

    // One gate per key so concurrent viewers of the same report load/refresh once, not N times.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();

    /// <summary>
    /// Build a cache key from the KPI name and its parameters, folding in the
    /// current business report date so entries roll over cleanly at a date boundary.
    /// Params should be the same values that drive the proc (year, measure, division…).
    /// </summary>
    public string Key(string kpi, params object?[] parameters)
    {
        var asAt = reportDate.Get().ToString("yyyyMMdd");
        var p = string.Join("|", parameters.Select(x => x?.ToString() ?? ""));
        return $"kpi:{kpi}:{asAt}:{p}";
    }

    /// <summary>
    /// Instant if cached; otherwise loads once. <paramref name="load"/> returns the
    /// data plus its as-at date. Single-flight per key.
    /// </summary>
    public async Task<KpiCacheEntry<T>> GetOrLoad<T>(string key, Func<Task<(T Data, DateTime AsAt)>> load)
    {
        if (cache.TryGetValue(key, out KpiCacheEntry<T>? hit) && hit is not null)
            return hit;

        var gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            // Someone may have loaded it while we waited on the gate.
            if (cache.TryGetValue(key, out hit) && hit is not null)
                return hit;

            var now = DateTime.UtcNow;
            var (data, asAt) = await load();
            var entry = new KpiCacheEntry<T>(data, asAt, Hash(data), now, now);
            Store(key, entry);
            _logger.LogInformation("KPI cache MISS loaded {Key}", key);
            return entry;
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Re-run the loader for an already-cached key and reconcile. Returns the (possibly
    /// unchanged) entry and whether the data actually changed, so the UI knows whether
    /// to fade the chart or just tick "Refreshed on". No-ops to the cached entry if the
    /// key isn't present (nothing to refresh).
    /// </summary>
    public async Task<(KpiCacheEntry<T> Entry, bool Changed)> RefreshAsync<T>(
        string key, Func<Task<(T Data, DateTime AsAt)>> load)
    {
        var gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            var now = DateTime.UtcNow;
            var (data, asAt) = await load();
            var newHash = Hash(data);

            if (cache.TryGetValue(key, out KpiCacheEntry<T>? existing) && existing is not null
                && existing.ContentHash == newHash)
            {
                // Checked, nothing new: advance timestamps only. Chart untouched.
                var ticked = existing.WithChecked(now);
                Store(key, ticked);
                return (ticked, false);
            }

            var fresh = new KpiCacheEntry<T>(data, asAt, newHash, now, now);
            Store(key, fresh);
            _logger.LogInformation("KPI cache REFRESH changed {Key}", key);
            return (fresh, true);
        }
        finally { gate.Release(); }
    }

    private void Store<T>(string key, KpiCacheEntry<T> entry)
        => cache.Set(key, entry, new MemoryCacheEntryOptions { SlidingExpiration = SlidingExpiry });

    // Stable content hash of the materialised result. JSON is a pragmatic, allocation-cheap
    // way to hash arbitrary DTO lists without each DTO implementing equality; row counts here
    // are small (tens–hundreds), so this is negligible next to the proc round-trip it guards.
    private static string Hash<T>(T data)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(data);
        return Convert.ToHexString(MD5.HashData(json));
    }
}

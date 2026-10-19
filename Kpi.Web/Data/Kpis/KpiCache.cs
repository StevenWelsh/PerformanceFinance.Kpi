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
    public Task<KpiCacheEntry<T>> GetOrLoad<T>(string key, Func<Task<(T Data, DateTime AsAt)>> load)
    {
        Remember(key, k => Load(k, load), k => RefreshAsync(k, load));
        return Load(key, load);
    }

    // ---- MORNING PRE-LOADING (Steve, 16 Oct 2026: "things seem a bit slow at times") --------------------------
    // Entries are keyed by the business date, so when the morning's first copy moves the date every report starts
    // cold and its first viewer waits for the full calculation on the small database. So the cache remembers each
    // view people actually open (report + filters, the key with its date taken out, and its loader) and KpiWarmer
    // reloads them under the new date in the background, one at a time - but only views safe to re-run (see
    // SafeToRerun below: a page's loader reads the page's current choices, so re-running it later can load the wrong view). Views
    // nobody has opened for three days are forgotten. Empty after a restart: the first viewers then fill it.
    private readonly ConcurrentDictionary<string, (Func<string, Task> Load, Func<string, Task> Refresh, DateTime LastUsedUtc)> _views = new();
    private static readonly TimeSpan ForgetAfter = TimeSpan.FromDays(3);
    private const string DateSlot = "{date}";

    private void Remember(string key, Func<string, Task> load, Func<string, Task> refresh)
    {
        var parts = key.Split(':', 4);   // kpi:{name}:{yyyyMMdd}:{params}
        if (parts.Length == 4) _views[$"{parts[0]}:{parts[1]}:{DateSlot}:{parts[3]}"] = (load, refresh, DateTime.UtcNow);
    }

    // ONLY THESE VIEWS ARE RE-RUN IN THE BACKGROUND (19 Oct 2026). A report page's loader reads the page's CURRENT
    // choices when it runs, not the ones its key was made from, so re-running a remembered page view after a copy used
    // whatever that page showed by then - Business Analysis "Facility" was refilled with Arrangement's figures (Own
    // Book / Brokered) once its viewer had moved on, and kept them until a restart. Sales Summary / Salesperson Summary
    // cells (SalesSummaryCells) take their choices as arguments, so theirs is safe to re-run; every other view is
    // DROPPED instead, and loads afresh - with the right choices - for its next viewer (an open page is told to
    // reload its own view by KpiFiguresSignal).
    private static readonly string[] SafeToRerun = { "SalesSummaryPanel" };

    private static bool IsSafe(string view) =>
        SafeToRerun.Any(n => view.StartsWith($"kpi:{n}:", StringComparison.Ordinal));

    /// <summary>
    /// After a new dataset (same date, same keys, new figures): re-runs the remembered views that are safe to re-run, one
    /// at a time, and DROPS every other remembered view so its next viewer loads it afresh. Returns how many were re-run.
    /// </summary>
    public async Task<int> RefreshViewsAsync(DateTime reportDate, CancellationToken ct)
    {
        var date = reportDate.ToString("yyyyMMdd");
        var done = 0;
        foreach (var (view, v) in _views.ToArray())
        {
            if (ct.IsCancellationRequested) break;
            var key = view.Replace(DateSlot, date);
            if (DateTime.UtcNow - v.LastUsedUtc > ForgetAfter || !IsSafe(view))
            {
                cache.Remove(key);              // old figures out; the next viewer loads with its own choices
                _views.TryRemove(view, out _);  // and its page re-remembers it then
                continue;
            }
            try { await v.Refresh(key); done++; }
            catch (Exception ex) { _logger.LogWarning(ex, "KPI refresh after a new dataset failed for {View}", view); }
        }
        return done;
    }

    /// <summary>Loads the remembered views that are safe to re-run under <paramref name="reportDate"/>, one at a time;
    /// any other view simply loads for its first viewer of the day. Returns how many were loaded.</summary>
    public async Task<int> WarmAsync(DateTime reportDate, CancellationToken ct)
    {
        var date = reportDate.ToString("yyyyMMdd");
        var warmed = 0;
        foreach (var (view, v) in _views.ToArray())
        {
            if (ct.IsCancellationRequested) break;
            if (DateTime.UtcNow - v.LastUsedUtc > ForgetAfter || !IsSafe(view)) { _views.TryRemove(view, out _); continue; }
            try { await v.Load(view.Replace(DateSlot, date)); warmed++; }
            catch (Exception ex) { _logger.LogWarning(ex, "KPI pre-load failed for {View}", view); }
        }
        return warmed;
    }

    private async Task<KpiCacheEntry<T>> Load<T>(string key, Func<Task<(T Data, DateTime AsAt)>> load)
    {
        if (cache.TryGetValue(key, out KpiCacheEntry<T>? hit) && hit is not null)
            return hit;

        var deadline = DateTime.UtcNow + LoadTimeout;   // one minute for the wait AND the load, not one each
        var gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(LoadTimeout)) throw new TimeoutException(TimeoutMessage);
        try
        {
            // Someone may have loaded it while we waited on the gate.
            if (cache.TryGetValue(key, out hit) && hit is not null)
                return hit;

            var now = DateTime.UtcNow;
            var (data, asAt) = await Bounded(load, deadline);
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
        var deadline = DateTime.UtcNow + LoadTimeout;   // one minute for the wait AND the load, not one each
        var gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(LoadTimeout)) throw new TimeoutException(TimeoutMessage);
        try
        {
            var now = DateTime.UtcNow;
            var (data, asAt) = await Bounded(load, deadline);
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

    /// <summary>
    /// NO REPORT LOADS FOR EVER (16 Oct 2026). A load that hasn't finished by now is given up: the page shows
    /// <see cref="TimeoutMessage"/> (pages already show a failed load's message) and the gate is released, so the next
    /// viewer tries afresh instead of queueing behind it. Before this, one load stuck reaching the database held its
    /// gate, and every user who opened that report afterwards waited on "Loading..." until the site restarted.
    /// </summary>
    public static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(60);

    public const string TimeoutMessage =
        "This report is taking too long to load, so it has stopped trying. Please try again in a minute.";

    // The load runs on the thread pool, not the page's own thread: the reports' database calls are synchronous, and
    // one that hangs there freezes the whole page (menu included). A load past the limit is left to finish or fail
    // on its own; nothing waits for it.
    private static async Task<TResult> Bounded<TResult>(Func<Task<TResult>> load, DateTime deadline)
    {
        var left = deadline - DateTime.UtcNow;
        if (left <= TimeSpan.Zero) throw new TimeoutException(TimeoutMessage);
        try { return await Task.Run(load).WaitAsync(left); }
        catch (TimeoutException) { throw new TimeoutException(TimeoutMessage); }
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

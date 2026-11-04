using System.Globalization;
using System.IO.Compression;
using System.Net;
using MaxMind.GeoIP2;

namespace Kpi.Web.Account;

/// <summary>WHERE AN ADDRESS IS (4 Nov 2026, Steve: "log the location from where someone logs in"). DB-IP's free
/// "IP to City Lite" file (CC BY 4.0: Settings > Audit credits it), kept on the site, so no address leaves it, and needing
/// no account (Steve: "I didn't expect to have to do all this" - MaxMind's needs one). Looked up when Settings > Audit
/// shows a line, so older lines get a place too. Town-level and approximate - a mobile or VPN address can be miles out.
///
/// DB-IP publishes a new file each month. The task "Update the location lookup" fetches it when this month's isn't here
/// yet, and each copy of the site fetches it when it starts without one (each deployment slot has its own disk). The
/// file is memory-mapped (about 130 MB on disk, read as needed), and each month's has its own name so the one open is
/// never overwritten.</summary>
public sealed class IpLocations(IConfiguration config, IHttpClientFactory http, ILogger<IpLocations> log) : IDisposable
{
    public const string HttpClientName = "IpLocations";
    private const string Prefix = "dbip-city-lite-";

    private readonly object _lock = new();
    private readonly SemaphoreSlim _updating = new(1, 1);
    private DatabaseReader? _reader;
    private string? _openFile;
    private bool _opened;

    private string Folder => config["IpLocations:Folder"] is { Length: > 0 } f ? f
        : Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME") is not null ? "/home/data/kpi-geo"      // App Service: survives restarts
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kpi.Web", "geo");

    /// <summary>"Manchester, UK" / "France" / "Private network", or null when unknown (no file yet, or not in it).</summary>
    public string? Find(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip) || !IPAddress.TryParse(ip.Trim(), out var address)) return null;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || IsPrivate(address)) return "Private network";
        var reader = Reader();
        if (reader is null) return null;
        try
        {
            if (!reader.TryCity(address, out var c) || c is null) return null;
            var country = c.Country.IsoCode == "GB" ? "UK" : c.Country.IsoCode;
            var town = c.City.Name;
            return town is { Length: > 0 } ? (country is null ? town : $"{town}, {country}") : c.Country.Name;
        }
        catch (Exception) { return null; }
    }

    private DatabaseReader? Reader()
    {
        lock (_lock)
        {
            if (_opened) return _reader;
            _opened = true;
            var newest = Directory.Exists(Folder)
                ? Directory.GetFiles(Folder, Prefix + "*.mmdb").OrderByDescending(f => f, StringComparer.Ordinal).FirstOrDefault()
                : null;
            if (newest is not null)
            {
                try { _reader = new DatabaseReader(newest, MaxMind.Db.FileAccessMode.MemoryMapped); _openFile = newest; }
                catch (Exception e) { log.LogWarning(e, "The location lookup file {File} could not be opened", newest); }
            }
            if (_reader is null)
                _ = Task.Run(async () =>
                {
                    try { await UpdateAsync(CancellationToken.None); }
                    catch (Exception e) { log.LogWarning(e, "The location lookup file could not be fetched"); }
                });
            return _reader;
        }
    }

    /// <summary>Fetches this month's file from DB-IP (last month's while this month's isn't out) unless it is here
    /// already, and switches to it. Returns one line for the task's History.</summary>
    public async Task<string> UpdateAsync(CancellationToken ct)
    {
        await _updating.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Folder);
            var now = DateTime.UtcNow;
            foreach (var month in new[] { now, now.AddMonths(-1) })
            {
                var stamp = month.ToString("yyyy-MM", CultureInfo.InvariantCulture);
                var path = Path.Combine(Folder, Prefix + stamp + ".mmdb");
                if (File.Exists(path))
                {
                    Switch(path);
                    return $"Up to date: DB-IP's file for {month:MMMM yyyy}.";
                }

                using var response = await http.CreateClient(HttpClientName).GetAsync(
                    $"https://download.db-ip.com/free/{Prefix}{stamp}.mmdb.gz", HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.StatusCode == HttpStatusCode.NotFound) continue;   // not published yet: last month's
                response.EnsureSuccessStatusCode();

                var temp = path + ".part";
                await using (var body = await response.Content.ReadAsStreamAsync(ct))
                await using (var gz = new GZipStream(body, CompressionMode.Decompress))
                await using (var file = File.Create(temp))
                    await gz.CopyToAsync(file, ct);
                using (new DatabaseReader(temp, MaxMind.Db.FileAccessMode.MemoryMapped)) { }   // proves it opens
                File.Move(temp, path, overwrite: true);
                Switch(path);
                return $"Updated: DB-IP's file for {month:MMMM yyyy}.";
            }
            throw new InvalidOperationException("DB-IP has no file for this month or last month.");
        }
        finally { _updating.Release(); }
    }

    // open the given file, close the previous one and delete any older months
    private void Switch(string path)
    {
        DatabaseReader? old = null;
        lock (_lock)
        {
            _opened = true;
            if (_openFile == path && _reader is not null) return;
            var fresh = new DatabaseReader(path, MaxMind.Db.FileAccessMode.MemoryMapped);
            old = _reader;
            _reader = fresh;
            _openFile = path;
        }
        old?.Dispose();
        foreach (var f in Directory.GetFiles(Folder, Prefix + "*"))
        {
            if (string.Equals(f, path, StringComparison.Ordinal)) continue;
            try { File.Delete(f); } catch (Exception) { /* another copy of the site may still have it open */ }
        }
    }

    private static bool IsPrivate(IPAddress a)
    {
        if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            return a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6UniqueLocal;
        var b = a.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }

    public void Dispose() { lock (_lock) _reader?.Dispose(); }
}

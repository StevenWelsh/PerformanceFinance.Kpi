using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Kpi.Data.Cfg;
using Kpi.Web.Integrations;

namespace Kpi.Web.Shire;

/// <summary>The card's plain settings: where Shire's Salesforce is, its name, and a hint of the saved key (never the key).</summary>
public sealed record ShireSalesforceSettings(string Domain, string KeyHint, string? Name = null)
{
    /// <summary>The card's title: "Salesforce", with its name in brackets when it has one.</summary>
    public static string Title(ShireSalesforceSettings? s) =>
        string.IsNullOrWhiteSpace(s?.Name) ? "Salesforce" : $"Salesforce ({s!.Name!.Trim()})";
}

/// <summary>The External Client App's Consumer Key and Secret. Encrypted at rest; read only to sign in.</summary>
public sealed record ShireSalesforceSecrets(string ConsumerKey, string ConsumerSecret);

/// <summary>A signed-in session: the access token, where to send queries, and who it is (the identity URL).</summary>
public sealed record SalesforceToken(string AccessToken, string InstanceUrl, string? IdentityUrl);

/// <summary>Salesforce said no - with its own reason (invalid_client, a query error...), never a secret.</summary>
public sealed class SalesforceException(string message) : Exception(message);

/// <summary>
/// Shire's Salesforce (3 Nov 2026, Steve), set in Settings > Integrations like the SQL data source: the address and the
/// External Client App's key and secret (client credentials, running as pf.integration - read only). Signs in, runs SOQL
/// (following Salesforce's paging), tests the connection and runs the card's Try a query. The copy itself is ShirePull;
/// the queries it runs are ShireQueries.
/// </summary>
public sealed class ShireSalesforce(IntegrationStore store, IHttpClientFactory http)
{
    public const string Provider = "ShireSalesforce";
    public const string HttpClientName = "Salesforce";
    public const string ApiVersion = "v62.0";
    public const int MaxRows = 100;

    /// <summary>"shireprofessionalfunding.my.salesforce.com" or a pasted address, as https://host. Throws when it isn't Salesforce.</summary>
    public static string NormaliseDomain(string input)
    {
        var s = input.Trim();
        if (!s.Contains("://", StringComparison.Ordinal)) s = "https://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.EndsWith(".salesforce.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Enter Shire's Salesforce address, e.g. shireprofessionalfunding.my.salesforce.com.");
        return "https://" + uri.Host.ToLowerInvariant();
    }

    /// <summary>The connection the copy uses now, or null when it's off (not connected, or switched off).</summary>
    public async Task<(ShireSalesforceSettings Settings, ShireSalesforceSecrets Secrets)?> CopyConnectionAsync()
    {
        var row = await store.GetAsync(Provider);
        if (row.Status == IntegrationStatus.NotConnected || !row.Enabled) return null;
        var settings = IntegrationStore.ReadSettings<ShireSalesforceSettings>(row);
        var secrets = store.ReadSecrets<ShireSalesforceSecrets>(row);
        return settings is null || secrets is null ? null : (settings, secrets);
    }

    /// <summary>Client credentials: the key and secret for a token (the app runs as its Run As user).</summary>
    public async Task<SalesforceToken> SignInAsync(string domain, ShireSalesforceSecrets secrets, CancellationToken ct = default)
    {
        using var client = http.CreateClient(HttpClientName);
        using var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = secrets.ConsumerKey,
            ["client_secret"] = secrets.ConsumerSecret
        });
        using var res = await client.PostAsync($"{domain}/services/oauth2/token", body, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new SalesforceException($"Salesforce refused the sign-in: {Explain(text, res)}");
        using var doc = JsonDocument.Parse(text);
        var r = doc.RootElement;
        return new SalesforceToken(
            r.GetProperty("access_token").GetString()!,
            r.TryGetProperty("instance_url", out var iu) && iu.GetString() is { Length: > 0 } instance ? instance : domain,
            r.TryGetProperty("id", out var id) ? id.GetString() : null);
    }

    /// <summary>A query's records (each page in turn, up to <paramref name="max"/>) and Salesforce's total for it.</summary>
    public async Task<(List<JsonElement> Records, int TotalSize)> QueryAsync(SalesforceToken token, string soql, int? max = null,
                                                                            CancellationToken ct = default)
    {
        using var client = http.CreateClient(HttpClientName);
        var records = new List<JsonElement>();
        var total = 0;
        string? url = $"{token.InstanceUrl}/services/data/{ApiVersion}/query?q={Uri.EscapeDataString(soql)}";
        while (url is not null)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
            using var res = await client.SendAsync(req, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) throw new SalesforceException($"Salesforce refused the query: {Explain(text, res)}");
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            total = root.GetProperty("totalSize").GetInt32();
            foreach (var r in root.GetProperty("records").EnumerateArray())
            {
                records.Add(r.Clone());
                if (max is int m && records.Count >= m) return (records, total);
            }
            url = root.TryGetProperty("nextRecordsUrl", out var next) && next.ValueKind == JsonValueKind.String
                ? token.InstanceUrl + next.GetString() : null;
        }
        return (records, total);
    }

    /// <summary>Who the token is (its username), for the card - null if Salesforce won't say.</summary>
    public async Task<string?> UsernameAsync(SalesforceToken token, CancellationToken ct = default)
    {
        if (token.IdentityUrl is null) return null;
        try
        {
            using var client = http.CreateClient(HttpClientName);
            using var req = new HttpRequestMessage(HttpMethod.Get, token.IdentityUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
            using var res = await client.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("username", out var u) ? u.GetString() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException) { return null; }
    }

    /// <summary>Signs in and counts the won deals it can see. Throws with Salesforce's reason when it can't.</summary>
    public async Task<string> TestAsync(string domain, ShireSalesforceSecrets secrets)
    {
        var clock = Stopwatch.StartNew();
        var token = await SignInAsync(domain, secrets);
        var who = await UsernameAsync(token);
        var (_, won) = await QueryAsync(token, ShireQueries.WonCount);
        return $"Connected in {clock.Elapsed.TotalSeconds:0.0} s{(who is null ? "" : $" as {who}")}: {won:N0} won deals visible.";
    }

    /// <summary>
    /// The card's Try a query: SOQL as the copy's own user (so it sees only what the copy can - and can change nothing),
    /// the first <see cref="MaxRows"/> rows, related fields flattened ("Owner.Name"). A COUNT() query shows its count.
    /// </summary>
    public async Task<SourceQueryResult> RunAsync(string domain, ShireSalesforceSecrets secrets, string soql)
    {
        var clock = Stopwatch.StartNew();
        var token = await SignInAsync(domain, secrets);
        var (records, total) = await QueryAsync(token, soql.Trim(), MaxRows + 1);
        if (records.Count == 0)
            return new SourceQueryResult(new[] { "Rows" }, new List<string?[]> { new string?[] { total.ToString("N0", CultureInfo.InvariantCulture) } }, false, clock.Elapsed);

        var rows = records.Take(MaxRows).Select(r => { var d = new Dictionary<string, string?>(); Flatten(r, "", d); return d; }).ToList();
        var columns = rows.SelectMany(r => r.Keys).Distinct().ToList();
        return new SourceQueryResult(columns,
            rows.Select(r => columns.Select(c => r.TryGetValue(c, out var v) ? v : null).ToArray()).ToList(),
            records.Count > MaxRows || total > MaxRows, clock.Elapsed);
    }

    private static void Flatten(JsonElement e, string prefix, Dictionary<string, string?> into)
    {
        foreach (var p in e.EnumerateObject())
        {
            if (p.Name == "attributes") continue;
            var name = prefix + p.Name;
            switch (p.Value.ValueKind)
            {
                case JsonValueKind.Object: Flatten(p.Value, name + ".", into); break;
                case JsonValueKind.Null: into[name] = null; break;
                case JsonValueKind.String: into[name] = p.Value.GetString(); break;
                default: into[name] = p.Value.GetRawText(); break;
            }
        }
    }

    // Salesforce's own reason from the response: {"error", "error_description"} from sign-in, [{"message", "errorCode"}]
    // from a query - else the status line.
    private static string Explain(string body, HttpResponseMessage res)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("error", out var e))
                return r.TryGetProperty("error_description", out var d) ? $"{d.GetString()} ({e.GetString()})" : e.GetString() ?? "";
            if (r.ValueKind == JsonValueKind.Array && r.GetArrayLength() > 0 && r[0].TryGetProperty("message", out var m))
                return r[0].TryGetProperty("errorCode", out var c) ? $"{m.GetString()} ({c.GetString()})" : m.GetString() ?? "";
        }
        catch (JsonException) { }
        return $"{(int)res.StatusCode} {res.ReasonPhrase}";
    }
}

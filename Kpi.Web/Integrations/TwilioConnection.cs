using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kpi.Data.Cfg;

namespace Kpi.Web.Integrations;

/// <summary>Twilio's plain settings: the account and the Verify service, and a hint of the saved token (never the token).</summary>
public sealed record TwilioSettings(string AccountSid, string VerifyServiceSid, string VerifyServiceName, string TokenHint);

/// <summary>Twilio's secret: the Auth Token. Encrypted at rest; read only at the moment of a call.</summary>
public sealed record TwilioSecrets(string AuthToken);

public sealed record TextOutcome(string DeliveredTo, bool Redirected);

/// <summary>
/// TWILIO VERIFY - the sign-in's text codes (Steve, Oct 2026: "Twilio SMS in all cases"), copied from DavidsonHR's
/// TwilioConnection. Verify, not plain SMS: Twilio makes the code, texts it, expires it, checks it and limits guessing,
/// so the site holds no code at all. Three identifiers and one secret: Account SID (AC...), Auth Token (encrypted),
/// Verify Service SID (VA...) - the service's friendly name is what each text says it's from.
///
/// THE TEST NUMBER (the row's RedirectTo, +44...): as Resend's test address, every code goes to it instead of the
/// person's, and outside Production nothing is sent unless it is set. The check is made against the number the code went
/// to, so a redirected code typed from the tester's phone works. Settings are read from the row on every call, so a
/// changed token or test number takes effect at once.
/// </summary>
public sealed partial class TwilioConnection(IntegrationStore store, IWebHostEnvironment env)
{
    private static readonly HttpClient Http = new() { BaseAddress = new Uri("https://verify.twilio.com/v2/"), Timeout = TimeSpan.FromSeconds(15) };

    [GeneratedRegex(@"^AC[0-9a-fA-F]{32}$")] private static partial Regex AccountSidShape();
    [GeneratedRegex(@"^VA[0-9a-fA-F]{32}$")] private static partial Regex ServiceSidShape();

    /// <summary>Connected, switched on and holding a readable token: texts go through Twilio.</summary>
    public async Task<bool> IsUsableAsync()
    {
        var row = await store.GetAsync(IntegrationProviders.Twilio);
        if (row.Status != IntegrationStatus.Connected || !row.Enabled) return false;
        try { return store.ReadSecrets<TwilioSecrets>(row) is { AuthToken.Length: > 0 }; }
        catch (IntegrationSecretsUnreadableException) { return false; }
    }

    /// <summary>What's wrong with the typed details, in words, or null.</summary>
    public static string? ShapeProblem(string accountSid, string? authToken, string verifyServiceSid, bool tokenSaved)
    {
        if (!AccountSidShape().IsMatch(accountSid))
            return "The Account SID starts with AC and is 34 characters long - it's on the Twilio console's home page.";
        if (string.IsNullOrWhiteSpace(authToken) ? !tokenSaved : authToken.Trim().Length < 20)
            return "Paste the Auth Token from the Twilio console's home page (click to reveal it).";
        if (!ServiceSidShape().IsMatch(verifyServiceSid))
            return "The Verify Service SID starts with VA and is 34 characters long - it's under Verify → Services.";
        return null;
    }

    /// <summary>Asks Twilio for the Verify service: proves the three details work, and gives the name texts carry.</summary>
    public static async Task<string> FetchServiceNameAsync(string accountSid, string token, string serviceSid)
    {
        using var req = Request(HttpMethod.Get, $"Services/{serviceSid}", accountSid, token, null);
        using var res = await SendOrExplainAsync(req);
        await ThrowIfFailedAsync(res);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("friendly_name", out var n) ? n.GetString() ?? serviceSid : serviceSid;
    }

    /// <summary>Where a code for this number will actually go, given the test number.</summary>
    public static (string DeliverTo, bool Redirected) Plan(Integration row, string e164, bool isProduction)
    {
        var redirect = string.IsNullOrWhiteSpace(row.RedirectTo) ? null : row.RedirectTo.Trim();
        if (redirect is null)
        {
            if (!isProduction)
                throw new InvalidOperationException("Outside the live site no text is sent unless the Twilio test number is set (Settings → Integrations → Twilio).");
            return (e164, false);
        }
        return (redirect, redirect != e164);
    }

    public async Task<TextOutcome> SendCodeAsync(string e164)
    {
        var (row, settings, token) = await CredentialsAsync();
        var (to, redirected) = Plan(row, e164, env.IsProduction());
        try
        {
            using var req = Request(HttpMethod.Post, $"Services/{settings.VerifyServiceSid}/Verifications", settings.AccountSid, token,
                new Dictionary<string, string> { ["To"] = to, ["Channel"] = "sms" });
            using var res = await SendOrExplainAsync(req);
            await ThrowIfFailedAsync(res);
        }
        catch (InvalidOperationException ex) { await store.RecordCallAsync(IntegrationProviders.Twilio, ex.Message); throw; }
        await store.RecordCallAsync(IntegrationProviders.Twilio, null);
        return new TextOutcome(to, redirected);
    }

    /// <summary>True when Twilio approves the code for this number (checked against where it was sent).</summary>
    public async Task<bool> CheckCodeAsync(string e164, string code)
    {
        var (row, settings, token) = await CredentialsAsync();
        var (to, _) = Plan(row, e164, env.IsProduction());
        using var req = Request(HttpMethod.Post, $"Services/{settings.VerifyServiceSid}/VerificationCheck", settings.AccountSid, token,
            new Dictionary<string, string> { ["To"] = to, ["Code"] = code });
        using var res = await SendOrExplainAsync(req);
        // 404: no live verification for the number - expired, already approved, or too many wrong tries
        if ((int)res.StatusCode == 404) return false;
        await ThrowIfFailedAsync(res);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("status", out var s) && s.GetString() == "approved";
    }

    private async Task<(Integration Row, TwilioSettings Settings, string Token)> CredentialsAsync()
    {
        var row = await store.GetAsync(IntegrationProviders.Twilio);
        if (row.Status == IntegrationStatus.NotConnected) throw new InvalidOperationException("Twilio is not connected - connect it under Settings → Integrations.");
        if (!row.Enabled) throw new InvalidOperationException("Twilio is switched off under Settings → Integrations.");
        var settings = IntegrationStore.ReadSettings<TwilioSettings>(row);
        var secrets = store.ReadSecrets<TwilioSecrets>(row);
        if (settings is null || string.IsNullOrWhiteSpace(secrets?.AuthToken))
            throw new InvalidOperationException("Twilio is missing its details - connect it again under Settings → Integrations.");
        return (row, settings, secrets.AuthToken);
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string accountSid, string token, Dictionary<string, string>? form)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{accountSid}:{token}")));
        if (form is not null) req.Content = new FormUrlEncodedContent(form);
        return req;
    }

    private static async Task<HttpResponseMessage> SendOrExplainAsync(HttpRequestMessage req)
    {
        try { return await Http.SendAsync(req); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new InvalidOperationException("Twilio couldn't be reached just now - try again.", ex);
        }
    }

    // Twilio's own words, never the response body wholesale (it can echo the number).
    private static async Task ThrowIfFailedAsync(HttpResponseMessage res)
    {
        if (res.IsSuccessStatusCode) return;
        string? message = null;
        int? code = null;
        try
        {
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("message", out var m)) message = m.GetString();
            if (doc.RootElement.TryGetProperty("code", out var c) && c.TryGetInt32(out var ci)) code = ci;
        }
        catch (JsonException) { }
        throw new InvalidOperationException((int)res.StatusCode switch
        {
            401 => "Twilio refused the Account SID and Auth Token - check both on the Twilio console's home page.",
            404 when code == 20404 && message?.Contains("Service", StringComparison.OrdinalIgnoreCase) == true
                => "Twilio has no Verify service with that SID on this account.",
            429 => "Twilio is limiting texts to this number just now - wait a few minutes.",
            _ => $"Twilio answered {(int)res.StatusCode}{(code is null ? "" : $" (error {code})")}: {Clip(message ?? res.ReasonPhrase ?? "request failed")}",
        });
    }

    private static string Clip(string s) => s.Length > 300 ? s[..300] : s;
}

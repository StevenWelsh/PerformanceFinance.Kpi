using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Kpi.Data.Cfg;

namespace Kpi.Web.Integrations;

public enum MailOutcome
{
    Sent,
    /// <summary>Resend isn't connected or is switched off: nothing was sent.</summary>
    NotConfigured,
    /// <summary>Outside Production with no test address set: refused, so a test site never emails real people.</summary>
    NoTestAddress,
    Failed
}

/// <summary>
/// The site's email, through Resend (https://resend.com): the four account emails, in fixed wording. Every send
/// honours the test address (all mail goes there, subject marked) and refuses outside Production without one.
/// </summary>
public sealed class Mailer(IntegrationStore store, IHttpClientFactory http, IWebHostEnvironment env, ILogger<Mailer> log)
{
    public const string HttpClientName = "resend";

    public Task<MailOutcome> InvitationAsync(string to, string name, string invitedBy, string link) =>
        SendAsync(to, "You're invited to Performance Finance KPIs",
            $"Hello {name},",
            new[]
            {
                $"{invitedBy} has invited you to the Performance Finance KPI site.",
                "Follow the link to choose a password and set up an authenticator app on your phone (Microsoft Authenticator, Google Authenticator or similar). The link works for 7 days."
            },
            "Accept invitation", link);

    public Task<MailOutcome> ReenrolAsync(string to, string name, string link) =>
        SendAsync(to, "Set up your authenticator again",
            $"Hello {name},",
            new[]
            {
                "Your authenticator for the Performance Finance KPI site has been reset, so the next step is to set it up on your phone again.",
                "Follow the link within 24 hours and sign in with your password. If you didn't ask for this, tell your administrator."
            },
            "Set up my authenticator", link);

    public Task<MailOutcome> PasswordResetAsync(string to, string name, string link) =>
        SendAsync(to, "Reset your password",
            $"Hello {name},",
            new[]
            {
                "Somebody asked to reset the password for your Performance Finance KPI account. The link works for an hour.",
                "If it wasn't you, ignore this email: your password stays as it is."
            },
            "Choose a new password", link);

    public Task<MailOutcome> PasswordChangedAsync(string to, string name) =>
        SendAsync(to, "Your password was changed",
            $"Hello {name},",
            new[]
            {
                "The password for your Performance Finance KPI account has just been changed.",
                "If that wasn't you, tell your administrator straight away."
            },
            null, null);

    /// <summary>Sends a test email to the given address through the saved settings.</summary>
    public Task<MailOutcome> TestAsync(string to) =>
        SendAsync(to, "Test email from Performance Finance KPIs", "Hello,",
            new[] { "This is a test email from the KPI site's Settings. If you can read it, email is working." }, null, null);

    /// <summary>Checks a key without sending anything. A sending-only key can't list domains, and that still counts
    /// as a working key.</summary>
    public async Task<string?> VerifyKeyAsync(string apiKey)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "domains");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var res = await http.CreateClient(HttpClientName).SendAsync(req);
        if (res.IsSuccessStatusCode) return null;
        var body = await res.Content.ReadAsStringAsync();
        if (res.StatusCode == HttpStatusCode.Unauthorized && body.Contains("restricted_api_key")) return null;
        return $"Resend refused the key ({(int)res.StatusCode}).";
    }

    private async Task<MailOutcome> SendAsync(string to, string subject, string greeting, string[] paragraphs,
                                              string? button, string? link)
    {
        var row = await store.GetAsync(IntegrationProviders.Resend);
        if (!row.Enabled || row.Status != IntegrationStatus.Connected) return MailOutcome.NotConfigured;
        var settings = IntegrationStore.ReadSettings<ResendSettings>(row);
        ResendSecrets? secrets;
        try { secrets = store.ReadSecrets<ResendSecrets>(row); }
        catch (IntegrationSecretsUnreadableException ex)
        {
            await store.RecordCallAsync(IntegrationProviders.Resend, ex.Message);
            return MailOutcome.Failed;
        }
        if (settings is null || secrets is null) return MailOutcome.NotConfigured;

        // the test address: every email goes there instead; without one, only Production sends
        if (!string.IsNullOrEmpty(row.RedirectTo))
        {
            subject = $"[TEST — was to {to}] {subject}";
            to = row.RedirectTo;
        }
        else if (!env.IsProduction()) return MailOutcome.NoTestAddress;

        var (html, text) = Render(greeting, paragraphs, button, link);
        var message = new ResendEmail(
            string.IsNullOrEmpty(settings.FromName) ? settings.FromAddress : $"{settings.FromName} <{settings.FromAddress}>",
            new[] { to }, subject, html, text);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "emails") { Content = JsonContent.Create(message) };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secrets.ApiKey);
            using var res = await http.CreateClient(HttpClientName).SendAsync(req);
            if (res.IsSuccessStatusCode)
            {
                await store.RecordCallAsync(IntegrationProviders.Resend, null);
                return MailOutcome.Sent;
            }
            var error = $"Sending \"{subject}\" failed: Resend answered {(int)res.StatusCode} {Trim(await res.Content.ReadAsStringAsync())}";
            log.LogWarning("{Error}", error);
            await store.RecordCallAsync(IntegrationProviders.Resend, error);
            return MailOutcome.Failed;
        }
        catch (HttpRequestException ex)
        {
            log.LogWarning(ex, "Resend could not be reached");
            await store.RecordCallAsync(IntegrationProviders.Resend, "Resend could not be reached: " + ex.Message);
            return MailOutcome.Failed;
        }
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] : s;

    // Plain, table-free HTML that every mail client renders, with the same words as text.
    private static (string Html, string Text) Render(string greeting, string[] paragraphs, string? button, string? link)
    {
        static string e(string s) => WebUtility.HtmlEncode(s);
        var html = new StringBuilder();
        html.Append("<div style=\"font-family:Segoe UI,Arial,sans-serif;font-size:15px;color:#222;max-width:560px\">");
        html.Append("<p style=\"color:#1f3a5f;font-weight:600;font-size:17px\">Performance Finance KPIs</p>");
        html.Append($"<p>{e(greeting)}</p>");
        foreach (var p in paragraphs) html.Append($"<p>{e(p)}</p>");
        if (button is not null && link is not null)
            html.Append($"<p><a href=\"{e(link)}\" style=\"display:inline-block;background:#2b579a;color:#fff;padding:10px 18px;border-radius:4px;text-decoration:none\">{e(button)}</a></p>")
                .Append($"<p style=\"font-size:13px;color:#666\">Or paste this into your browser: {e(link)}</p>");
        html.Append("</div>");

        var text = new StringBuilder();
        text.AppendLine(greeting).AppendLine();
        foreach (var p in paragraphs) text.AppendLine(p).AppendLine();
        if (link is not null) text.AppendLine($"{button}: {link}");
        return (html.ToString(), text.ToString());
    }

    private sealed record ResendEmail(
        [property: JsonPropertyName("from")] string From,
        [property: JsonPropertyName("to")] string[] To,
        [property: JsonPropertyName("subject")] string Subject,
        [property: JsonPropertyName("html")] string Html,
        [property: JsonPropertyName("text")] string Text);
}

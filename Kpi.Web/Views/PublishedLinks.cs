using System.Reflection;
using Microsoft.AspNetCore.Components;

namespace Kpi.Web.Views;

/// <summary>Which page shows a report: the component whose route is /kpis/{slug} (the catalogue's convention).</summary>
public static class PublishedReports
{
    private static readonly Dictionary<string, Type> Pages = typeof(Program).Assembly.GetTypes()
        .Where(t => typeof(IComponent).IsAssignableFrom(t))
        .SelectMany(t => t.GetCustomAttributes<RouteAttribute>().Select(r => (r.Template, Type: t)))
        .Where(x => x.Template.StartsWith("/kpis/", StringComparison.OrdinalIgnoreCase))
        .GroupBy(x => x.Template["/kpis/".Length..], StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First().Type, StringComparer.OrdinalIgnoreCase);

    public static Type? PageFor(string slug) => Pages.GetValueOrDefault(slug);
}

/// <summary>
/// The door for /view/{key}, before the sign-in gate: a known, unrevoked key from an allowed address loads the page (and
/// is counted); anything else gets a plain "not available" page and nothing more - no hint which part was wrong. The
/// page itself (PublishedView) checks the key again, and every minute, so a revoked link stops an open tab.
/// </summary>
public static class PublishedLinkDoor
{
    private const string Unavailable = """
        <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Not available</title></head>
        <body style="font-family:'Segoe UI',system-ui,sans-serif;color:#444;display:flex;align-items:center;justify-content:center;height:90vh;margin:0">
        <p>This link isn't available.</p></body></html>
        """;

    /// <summary>Off since 19 Oct 2026 - see UsePublishedLinks.</summary>
    public static readonly bool PublishedLinksOn = false;

    public static IApplicationBuilder UsePublishedLinks(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/view", out var rest)
                || !(HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)))
            {
                await next();
                return;
            }

            // SWITCHED OFF (19 Oct 2026, Steve): CMS's KPI tabs use the CMS desktop integration (/embed, signed tickets);
            // Settings > Published links only explains what links could be for. Every /view is refused; the code and the
            // table stay, so links can come back if a use appears (a wall screen, someone outside PF).
            if (!PublishedLinksOn)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.WriteAsync(Unavailable);
                return;
            }

            var store = context.RequestServices.GetRequiredService<PublishedLinkStore>();
            var address = PublishedLinkStore.Normalise(context.Connection.RemoteIpAddress);
            var key = rest.Value?.Trim('/');
            var link = await store.FindActiveAsync(key);
            var allowed = await store.AllowedAddressesAsync();

            string? refusal = link is null ? "unknown or revoked link"
                : allowed.Count > 0 && (address is null || !allowed.Contains(address)) ? $"address {address} not allowed"
                : null;
            if (refusal is not null)
            {
                await store.AuditRefusedAsync(link, refusal, address);
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.WriteAsync(Unavailable);
                return;
            }

            await store.RecordUseAsync(link!.Id, address);
            // the key is in the address: never cached, never sent on to another site
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            await next();
        });
}

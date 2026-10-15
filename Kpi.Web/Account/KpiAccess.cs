using System.Reflection;
using System.Security.Claims;
using Kpi.Data.Auth;
using Kpi.Web.Data.Kpis;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Kpi.Web.Account;

// WHO MAY SEE WHAT, decided from the signed-in principal alone (no database per request), as DavidsonHR.
//
// TWO-FACTOR IS COMPULSORY. Until an authenticator is set up the account reaches nothing but the account screens:
// the gate redirects ordinary requests, and every policy below also requires it, which covers the live Blazor
// connection the gate never sees.

/// <summary>The claims the sign-in cookie carries beyond Identity's own.</summary>
public static class KpiClaims
{
    public const string TwoFactor = "kpi:2fa";          // "on" once an authenticator is set up
    public const string Reports = "kpi:reports";        // "*" = every report, else the report slugs, comma separated
    public const string Name = "kpi:name";              // display name, for the avatar
}

/// <summary>
/// Builds the principal: Identity's claims (with roles - the TWO-type-argument base is the one that adds them), plus
/// the set-up state and the report rights. Rebuilt every minute by the security stamp check, so a change to
/// someone's rights reaches their cookie within a minute.
/// </summary>
public sealed class KpiClaimsPrincipalFactory(
    UserManager<KpiUser> users, RoleManager<IdentityRole> roles, IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<KpiUser, IdentityRole>(users, roles, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(KpiUser user)
    {
        var id = await base.GenerateClaimsAsync(user);
        var everything = user.AllReports || await UserManager.IsInRoleAsync(user, KpiRoles.SystemAdmin);
        id.AddClaim(new Claim(KpiClaims.TwoFactor, user.TwoFactorEnabled ? "on" : "off"));
        id.AddClaim(new Claim(KpiClaims.Reports, everything ? KpiReportAccess.Everything : user.Reports ?? ""));
        id.AddClaim(new Claim(KpiClaims.Name, user.DisplayName.Length > 0 ? user.DisplayName : user.Email ?? ""));
        return id;
    }
}

/// <summary>The policies, by the names the KPI pages carry (CMS.Web's, so the copies stay alike).</summary>
public static class KpiPolicies
{
    public const string Reports = "Dashboard.View";
    public const string BadDebt = "OBEnquiryBadDebt.View";
    public const string Exposure = "OBEnquiryExposure.View";
    public const string Settings = "Settings";
    /// <summary>Signed in with an authenticator: My Account.</summary>
    public const string SignedIn = "SignedIn";

    public static void Add(AuthorizationOptions o)
    {
        static AuthorizationPolicyBuilder Base(AuthorizationPolicyBuilder p) =>
            p.RequireAuthenticatedUser().RequireClaim(KpiClaims.TwoFactor, "on");

        o.AddPolicy(SignedIn, p => Base(p));
        // every report page carries this one: the report is worked out from the page it guards (ReportHandler)
        o.AddPolicy(Reports, p => Base(p).AddRequirements(new ReportRequirement()));
        o.AddPolicy(BadDebt, p => Base(p).RequireAssertion(c => KpiReportAccess.CanSee(c.User, "bad-debt")));
        o.AddPolicy(Exposure, p => Base(p).RequireAssertion(c => KpiReportAccess.CanSee(c.User, "exposure")));
        o.AddPolicy(Settings, p => Base(p).RequireRole(KpiRoles.SystemAdmin));
    }
}

/// <summary>Who may open which report: the System Admin and "all reports" see everything; a User sees the reports
/// ticked for them in Settings > Users. The menus ask it to HIDE what isn't theirs; the pages ask it to refuse.</summary>
public static class KpiReportAccess
{
    public const string Everything = "*";

    public static bool CanSee(ClaimsPrincipal user, string slug) =>
        user.FindFirstValue(KpiClaims.Reports) is { } v && (v == Everything || v.Split(',').Contains(slug));

    /// <summary>The report a page or address is: "" for the KPI home, null for anything that isn't a report.</summary>
    public static string? SlugFor(string? path)
    {
        if (path is null || !path.StartsWith("/kpis", StringComparison.OrdinalIgnoreCase)) return null;
        var slug = path.Length <= 5 ? "" : path[5..].Trim('/').Split('/', '?')[0].ToLowerInvariant();
        return slug == "business-analysis-lender" ? "business-analysis" : slug;   // the lender view's old address
    }
}

public sealed class ReportRequirement : IAuthorizationRequirement { }

/// <summary>
/// Decides <see cref="KpiPolicies.Reports"/> from the page being opened - its address on an ordinary request, its
/// route when Blazor navigates - so every copied report page is guarded by the line it already carries
/// (<c>[Authorize(Policy = "Dashboard.View")]</c>), unchanged. The KPI home is open to anyone signed in; an address
/// under /kpis that isn't a known report is refused.
/// </summary>
public sealed class ReportHandler : AuthorizationHandler<ReportRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ReportRequirement requirement)
    {
        var path = context.Resource switch
        {
            HttpContext http => http.Request.Path.Value,
            Microsoft.AspNetCore.Components.RouteData route => route.PageType.GetCustomAttributes<RouteAttribute>().FirstOrDefault()?.Template,
            _ => null
        };
        var slug = KpiReportAccess.SlugFor(path);
        var allowed = slug is null or ""
            || (KpiReports.All.Any(r => r.Slug == slug) && KpiReportAccess.CanSee(context.User, slug));
        if (allowed) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

public static class AccountGate
{
    // What a half-signed-in person (password right, no authenticator yet) may still reach: the account screens,
    // the framework's plumbing and static assets. Everything else is redirected - and refused by the policies.
    private static readonly string[] Open = { "/account", "/_blazor", "/_framework", "/_content", "/css", "/js", "/lib", "/img", "/favicon", "/Error" };

    private static readonly string[] AssetExtensions =
        { ".css", ".js", ".map", ".ico", ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp", ".woff", ".woff2", ".ttf", ".json" };

    private static bool IsAsset(PathString path) =>
        path.HasValue && Path.GetExtension(path.Value) is { Length: > 0 } ext && AssetExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);

    private static bool IsOpen(PathString path) =>
        Open.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)) || IsAsset(path);

    public static IApplicationBuilder UseAccountGate(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var user = context.User;
            var path = context.Request.Path;

            if (user.Identity?.IsAuthenticated != true)
            {
                // Signed out: a page goes to sign in over plain HTTP, before any live Blazor session is started for
                // a stranger. The account screens, plumbing and assets pass.
                var isPage = (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)) && !IsOpen(path);
                if (isPage)
                {
                    var back = path + context.Request.QueryString;
                    context.Response.Redirect(back is "/" or "/kpis" ? "/account/login" : "/account/login?returnUrl=" + Uri.EscapeDataString(back));
                    return;
                }
                await next();
                return;
            }

            if (!IsOpen(path) && user.FindFirstValue(KpiClaims.TwoFactor) != "on")
            {
                context.Response.Redirect("/account/authenticator");
                return;
            }
            await next();
        });

    /// <summary>
    /// A return address is user supplied: anything not plainly on this site collapses to the home page, which is
    /// what stops sign-in being an open redirect (and LocalRedirect throwing after the cookie is written).
    /// </summary>
    public static string LocalReturn(string? returnUrl)
    {
        var v = (returnUrl ?? "").Trim();
        if (v.Length == 0) return "~/";
        if (v.StartsWith("~/", StringComparison.Ordinal)) v = v[1..];
        if (v.Contains(':') || v.Contains('\\') || v.StartsWith("//", StringComparison.Ordinal)) return "~/";
        return v.StartsWith('/') ? "~" + v : "~/" + v;
    }
}

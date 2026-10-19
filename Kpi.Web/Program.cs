using System.Globalization;
using System.Threading.RateLimiting;
using Kpi.Data;
using Kpi.Data.Auth;
using Kpi.Web.Account;
using Kpi.Web.Components;
using Kpi.Web.Data;
using Kpi.Web.Data.Kpis;
using Kpi.Web.Integrations;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// The copied report pages call the database synchronously (KpiDb.Report waits on the thread pool), and on this one-core
// plan .NET adds pool threads slowly - so a page starting many loads queued behind its own waits (18 Oct 2026). Start
// with enough threads that blocked waits don't hold everything else up.
ThreadPool.GetMinThreads(out var minWorkers, out var minIo);
ThreadPool.SetMinThreads(Math.Max(minWorkers, 64), Math.Max(minIo, 64));

var uk = new CultureInfo("en-GB");
CultureInfo.DefaultThreadCurrentCulture = uk;
CultureInfo.DefaultThreadCurrentUICulture = uk;

// A phone that sleeps drops the live connection; its place is kept 30 minutes (default 3) so it comes back where it was,
// not to a reload (js/reconnect.js reconnects the moment the page is looked at again).
builder.Services.AddRazorComponents().AddInteractiveServerComponents(o => o.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(30));
builder.Services.AddDevExpressBlazor(options => options.SizeMode = DevExpress.Blazor.SizeMode.Medium);

// ---- the KPI database: reports through Rpt (Dapper), sign-in and settings through EF Core ------------------------
var kpiConnection = builder.Configuration.GetConnectionString("CmsKpi") is { Length: > 0 } cs ? cs
    : throw new InvalidOperationException(
        "ConnectionStrings:CmsKpi is not set. Set it once with:  dotnet user-secrets set \"ConnectionStrings:CmsKpi\" \"...\" --project Kpi.Web");
builder.Services.AddDbContextFactory<KpiDbContext>(o => o.UseKpi(kpiConnection, retryDroppedConnections: true));
// Identity's stores need a scoped context; one from the factory per scope (a request, or one operation's own scope -
// never the circuit's: UserAdmin and the auth state provider open their own).
builder.Services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<KpiDbContext>>().CreateDbContext());
builder.Services.AddSingleton<KpiSessionFactory>();

// The KPI cache is shared by every viewer (its entries are company-wide figures, never one user's) and outlives
// circuits; the per-circuit pieces are the office choice, the refresh status line and the favourites.
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ReportDateProvider>();
builder.Services.AddSingleton<KpiDataStatus>();
builder.Services.AddSingleton<KpiCache>();
builder.Services.AddScoped<KpiOfficeState>();
builder.Services.AddScoped<KpiRefreshStatus>();
builder.Services.AddScoped<KpiFavourites>();
builder.Services.AddSingleton<KpiScripts>();

// ---- sign-in: ASP.NET Identity with a compulsory text code to the person's mobile (Twilio Verify), as DavidsonHR's portal
// The key ring encrypts the sign-in cookies, the emailed links and the integration secrets. Locally it lives in the
// user profile; in Azure it must be a shared, persisted ring (storage + Key Vault) or every restart signs everyone
// out and the saved Resend key becomes unreadable.
builder.Services.AddDataProtection().SetApplicationName("Kpi.Web");

builder.Services.AddAuthentication(o =>
    {
        o.DefaultScheme = IdentityConstants.ApplicationScheme;
        o.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();
builder.Services.ConfigureApplicationCookie(o =>
{
    o.LoginPath = "/account/login";
    o.AccessDeniedPath = "/account/denied";
    o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
});
foreach (var scheme in new[] { IdentityConstants.ExternalScheme, IdentityConstants.TwoFactorUserIdScheme })
    builder.Services.Configure<CookieAuthenticationOptions>(scheme, o => o.Cookie.SecurePolicy = CookieSecurePolicy.Always);
// "Don't ask again on this device for 30 days" at the code step
builder.Services.Configure<CookieAuthenticationOptions>(IdentityConstants.TwoFactorRememberMeScheme, o =>
{
    o.ExpireTimeSpan = TimeSpan.FromDays(30);
    o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
});
// How soon other sessions notice a password change, a disable or a role change (the stamp rolls): a minute.
builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(1));

builder.Services.AddIdentityCore<KpiUser>(KpiIdentityRules.Apply)
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<KpiDbContext>()
    .AddSignInManager()
    .AddClaimsPrincipalFactory<KpiClaimsPrincipalFactory>()
    .AddDefaultTokenProviders()
    .AddTokenProvider<SmsTokenProvider>(SmsTokenProvider.Name);   // the two-factor step is a texted code
// a forgotten-password link works for an hour
builder.Services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromHours(1));

builder.Services.AddAuthorization(KpiPolicies.Add);
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, ReportHandler>();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, KpiAuthStateProvider>();

builder.Services.AddSingleton<AccountLinks>();
builder.Services.AddSingleton<UserAdmin>();
builder.Services.AddSingleton<Kpi.Web.Views.CmsDesktopStore>();
// who has the site open, for Settings > Users (each page's live connection, noted as it comes and goes)
builder.Services.AddSingleton<OnlineUsers>();
builder.Services.AddSingleton<ReportViews>();        // who opens which report (Settings > Audit > Report views)
builder.Services.AddHostedService<HistoryPruner>();  // keeps the audit trail and report views to 3 months
builder.Services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler, OnlineTracker>();
builder.Services.AddHostedService<ExpireSetupsTask>();
// the pre-load: one instance, also told by the copy (KpiPull) when a new dataset is published
builder.Services.AddSingleton<KpiFiguresSignal>();   // new figures landed: open pages update (KpiPanel, Data sync)
builder.Services.AddSingleton<KpiPullHealth>();      // how the copy is doing, for the alerts
builder.Services.AddSingleton<Kpi.Web.Data.Kpis.KpiWarmer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Kpi.Web.Data.Kpis.KpiWarmer>());
builder.Services.AddSingleton<SyncAlerts>();         // emails the chosen people when the figures stop arriving
builder.Services.AddHostedService(sp => sp.GetRequiredService<SyncAlerts>());
builder.Services.AddHostedService<Kpi.Web.Data.KpiPull>();          // the KPI copy from the office (KPI_Export), every two minutes

// ---- integrations: Resend for email ----------------------------------------------------------------------------
builder.Services.AddSingleton<IntegrationStore>();
builder.Services.AddSingleton<CmsSource>();
builder.Services.AddSingleton<Kpi.Web.Views.PublishedLinkStore>();   // Settings > Published links, and their door            // where the KPI copy reads from (Settings > Integrations)
builder.Services.AddSingleton<Mailer>();
builder.Services.AddSingleton<TwilioConnection>();
builder.Services.AddSingleton<TextCodes>();
builder.Services.AddHttpClient(Mailer.HttpClientName, c =>
{
    c.BaseAddress = new Uri("https://api.resend.com/");
    c.Timeout = TimeSpan.FromSeconds(20);
});

// ---- the sign-in pages are where passwords and codes get guessed: limited per address, under Identity's lockout --
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(AccountEndpoints.SignInLimit, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy(AccountEndpoints.ResetLimit, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(15) }));
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseHttpsRedirection();

// headers every response carries
app.Use(async (context, next) =>
{
    var h = context.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["Referrer-Policy"] = "strict-origin-when-cross-origin";
    h["X-Frame-Options"] = "DENY";
    h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
    await next();
});

app.UseStaticFiles();
app.UseAuthentication();
// a published link (/view/{key}): switched off 19 Oct 2026 (CMS's tabs use the CMS desktop integration) - refused here
Kpi.Web.Views.PublishedLinkDoor.UsePublishedLinks(app);
// a CMS desktop tab (/embed/{report}): its signed ticket, then its session cookie, checked here before the sign-in gate
Kpi.Web.Views.CmsDesktopDoor.UseCmsDesktop(app);
// before authorization: a half-set-up account is sent to finish setting up, not told "no access"
app.UseAccountGate();
app.UseAuthorization();
app.UseRateLimiter();
app.UseAntiforgery();

app.MapGet("/", () => Results.Redirect("/kpis"));

// HEALTH (16 Oct 2026, after a night when the site answered every page but never reached its database): /health = the
// site is running; /health/db = a real round trip to CMS_KPI through the site's own sign-in - what App Service's health
// check pings and the alert watches, and the check after every deploy. Anonymous and terse on purpose (a public
// endpoint must not echo server names or errors); the detail goes to the log.
app.MapGet("/health", () => Results.Text("ok"));
app.MapGet("/health/db", (KpiSessionFactory sessions, ILogger<Program> log) =>
{
    try
    {
        using var db = sessions.Create();
        if (db.Ping()) return Results.Text("ok");
    }
    catch (Exception ex) { log.LogError(ex, "Health check: CMS_KPI could not be reached"); }
    return Results.Text("unavailable", statusCode: StatusCodes.Status503ServiceUnavailable);
});
// Settings > Published links shows the address the System Admin reaches the site from, for the allowed list
app.MapGet("/settings/my-address", (HttpContext http) =>
        Results.Text(Kpi.Web.Views.PublishedLinkStore.Normalise(http.Connection.RemoteIpAddress) ?? ""))
    .RequireAuthorization(KpiPolicies.Settings);
app.MapAccountEndpoints();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

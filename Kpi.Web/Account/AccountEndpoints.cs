using System.Text;
using Kpi.Data;
using Kpi.Data.Auth;
using Kpi.Web.Integrations;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Kpi.Web.Account;

/// <summary>
/// Every sign-in step, as DavidsonHR: the account pages (static SSR under /account) post plain forms here, so the
/// sign-in cookie is written in an ordinary request, never inside a live Blazor connection. Antiforgery is checked on
/// every one (form binding does it), the group is rate limited per address, and the answer to a refused sign-in is
/// always the same, so it never says which part was wrong.
/// </summary>
public static class AccountEndpoints
{
    public const string SignInLimit = "signin";
    public const string ResetLimit = "reset";

    // Hashing a password for an address with no account takes as long as checking a real one.
    private static readonly Lazy<string> DummyHash = new(() => new PasswordHasher<KpiUser>().HashPassword(new KpiUser(), Guid.NewGuid().ToString()));

    public static void MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var account = app.MapGroup("/account").RequireRateLimiting(SignInLimit);

        // ---- sign in: the password ---------------------------------------------------------------------------
        account.MapPost("/login", async (HttpContext http, [FromForm] string email, [FromForm] string password,
            [FromForm] string? returnUrl, [FromForm] bool? rememberMe,
            UserManager<KpiUser> users, SignInManager<KpiUser> signIn, KpiDbContext db, ILoggerFactory logs) =>
        {
            var user = await users.FindByEmailAsync(email ?? "");
            if (user is null || user.DisabledUtc is not null)
            {
                users.PasswordHasher.VerifyHashedPassword(new KpiUser(), DummyHash.Value, password ?? "");
                if (user is not null) await Audit(db, http, AuditEvents.SignInFailed, user, "Refused: the account is disabled");
                return Results.LocalRedirect("~/account/login?error=1");
            }

            // A PASSWORD NEVER LEADS TO REGISTERING A MOBILE - except, once, the seeded first account of a brand-new
            // database: there is no email to send a link with until somebody signed in connects Resend.
            var firstSetup = user.IsSeeded && user.ActivatedUtc is null && !user.TwoFactorEnabled;
            if (!firstSetup && !user.TwoFactorEnabled)
            {
                var check = await signIn.CheckPasswordSignInAsync(user, password ?? "", lockoutOnFailure: true);
                await Audit(db, http, check.IsLockedOut ? AuditEvents.LockedOut : AuditEvents.SignInFailed, user,
                    check.Succeeded ? "Refused: right password, but no mobile registered yet - that is done only from an emailed link"
                    : check.IsLockedOut ? "Refused: locked after repeated failed attempts" : "Wrong password");
                return Results.LocalRedirect("~/account/login?error=1");   // the same answer as a wrong password
            }

            var result = await signIn.PasswordSignInAsync(user, password ?? "", rememberMe == true, lockoutOnFailure: true);
            if (result.RequiresTwoFactor)
            {
                var sent = await SendSignInCodeAsync(users, user, logs);
                return Results.LocalRedirect($"~/account/two-factor?rememberMe={(rememberMe == true ? "true" : "false")}{(sent ? "" : "&sendfailed=1")}{ReturnQuery(returnUrl)}");
            }

            if (result.Succeeded)
            {
                if (firstSetup)
                {
                    var raw = await new AccountSetup(db, users).StartAsync(user, "first sign-in to a new database");
                    SetupCookie.Write(http, raw);
                    return Results.LocalRedirect("~/account/mobile");
                }
                // no code asked for: this browser was remembered at the code step
                await SignedIn(db, http, user, "Signed in - this browser is remembered, so no code was asked for");
                return Results.LocalRedirect(AccountGate.LocalReturn(returnUrl));
            }

            await Audit(db, http, result.IsLockedOut ? AuditEvents.LockedOut : AuditEvents.SignInFailed, user,
                result.IsLockedOut ? "Refused: locked after repeated failed attempts" : "Wrong password");
            return Results.LocalRedirect("~/account/login?error=1");
        });

        // ---- sign in: the code texted to their mobile -------------------------------------------------------------
        account.MapPost("/two-factor", async (HttpContext http, [FromForm] string code, [FromForm] string? returnUrl,
            [FromForm] bool? rememberMe, [FromForm] bool? rememberDevice, SignInManager<KpiUser> signIn, KpiDbContext db, ILoggerFactory logs) =>
        {
            var user = await signIn.GetTwoFactorAuthenticationUserAsync();
            if (user is null) return Results.LocalRedirect("~/account/login");
            if (user.DisabledUtc is not null)
            {
                await http.SignOutAsync(IdentityConstants.TwoFactorUserIdScheme);
                return Results.LocalRedirect("~/account/login?error=1");
            }
            Microsoft.AspNetCore.Identity.SignInResult result;
            try { result = await signIn.TwoFactorSignInAsync(SmsTokenProvider.Name, Clean(code), rememberMe == true, rememberDevice == true); }
            catch (InvalidOperationException ex)
            {
                logs.CreateLogger("Kpi.Web.Account").LogError(ex, "A sign-in code could not be checked");
                return Results.LocalRedirect($"~/account/two-factor?error=check&rememberMe={(rememberMe == true ? "true" : "false")}{ReturnQuery(returnUrl)}");
            }
            if (result.Succeeded)
            {
                await SignedIn(db, http, user, rememberDevice == true
                    ? "Signed in with password and texted code, and chose to remember this browser"
                    : "Signed in with password and texted code");
                return Results.LocalRedirect(AccountGate.LocalReturn(returnUrl));
            }
            // the most telling line in the audit: the password was RIGHT
            await Audit(db, http, result.IsLockedOut ? AuditEvents.LockedOut : AuditEvents.SignInFailed, user,
                result.IsLockedOut ? "Refused: locked after repeated failed attempts" : "Right password, wrong texted code");
            return result.IsLockedOut
                ? Results.LocalRedirect("~/account/login?error=1")
                : Results.LocalRedirect($"~/account/two-factor?error=1&rememberMe={(rememberMe == true ? "true" : "false")}{ReturnQuery(returnUrl)}");
        });

        // ---- a registered account whose cookie missed it (the confirm step's answer never arrived): re-issue it ------
        account.MapGet("/refresh", async (HttpContext http, UserManager<KpiUser> users, SignInManager<KpiUser> signIn) =>
        {
            var user = await users.GetUserAsync(http.User);
            if (user is null) return Results.LocalRedirect("~/account/login");
            if (user.TwoFactorEnabled && user.DisabledUtc is null) await signIn.RefreshSignInAsync(user);
            return Results.LocalRedirect("~/");
        });

        // ---- "Text me a new code" ----------------------------------------------------------------------------------
        account.MapPost("/two-factor/resend", async ([FromForm] string? returnUrl, [FromForm] bool? rememberMe,
            SignInManager<KpiUser> signIn, UserManager<KpiUser> users, ILoggerFactory logs) =>
        {
            var user = await signIn.GetTwoFactorAuthenticationUserAsync();
            if (user is null) return Results.LocalRedirect("~/account/login");
            var sent = await SendSignInCodeAsync(users, user, logs);
            return Results.LocalRedirect($"~/account/two-factor?rememberMe={(rememberMe == true ? "true" : "false")}&{(sent ? "resent=1" : "sendfailed=1")}{ReturnQuery(returnUrl)}");
        });

        account.MapPost("/logout", async (HttpContext http, SignInManager<KpiUser> signIn, UserManager<KpiUser> users, KpiDbContext db) =>
        {
            if (await users.GetUserAsync(http.User) is { } user)
                await Audit(db, http, AuditEvents.SignOut, user, "Signed out");
            await signIn.SignOutAsync();
            return Results.LocalRedirect("~/account/login?signedout=1");
        }).WithMetadata(new RequireAntiforgeryTokenAttribute());   // reads no form field, so it is not inferred: a page you merely visited mustn't sign you out

        // ---- an invitation: choose a password, then straight on to registering their mobile ----------------------
        account.MapPost("/invitation", async (HttpContext http, [FromForm] string userId, [FromForm] string code,
            [FromForm] string password, [FromForm] string confirmPassword,
            UserManager<KpiUser> users, SignInManager<KpiUser> signIn, KpiDbContext db, AccountLinks links) =>
        {
            var back = $"~/account/invitation?userId={Uri.EscapeDataString(userId ?? "")}&code={Uri.EscapeDataString(code ?? "")}";
            var user = await users.FindByIdAsync(userId ?? "");
            if (user is null || !InvitationUsable(user, code, links)) return Results.LocalRedirect("~/account/invitation");
            if (password != confirmPassword) return Results.LocalRedirect(back + "&error=match");

            if (await users.HasPasswordAsync(user)) await users.RemovePasswordAsync(user);   // back again within the hour
            var added = await users.AddPasswordAsync(user, password ?? "");
            if (!added.Succeeded) return Results.LocalRedirect(back + "&error=rules");

            await Audit(db, http, AuditEvents.InviteAccepted, user, "Accepted the invitation and chose a password");
            var raw = await new AccountSetup(db, users).StartAsync(user, "from their invitation");
            SetupCookie.Write(http, raw);
            await signIn.SignInAsync(user, isPersistent: false);
            return Results.LocalRedirect("~/account/mobile");
        });

        // ---- the emailed "register your mobile again" link (a System Admin's reset): the password, then the mobile
        account.MapPost("/setup-mobile", async (HttpContext http, [FromForm] string userId, [FromForm] string code,
            [FromForm] string password, UserManager<KpiUser> users, SignInManager<KpiUser> signIn, KpiDbContext db, AccountLinks links) =>
        {
            var back = $"~/account/setup-mobile?userId={Uri.EscapeDataString(userId ?? "")}&code={Uri.EscapeDataString(code ?? "")}";
            var user = await users.FindByIdAsync(userId ?? "");
            if (user is null || !ReenrolUsable(user, code, links)) return Results.LocalRedirect("~/account/setup-mobile");
            var check = await signIn.CheckPasswordSignInAsync(user, password ?? "", lockoutOnFailure: true);
            if (!check.Succeeded)
            {
                await Audit(db, http, check.IsLockedOut ? AuditEvents.LockedOut : AuditEvents.SignInFailed, user,
                    "Wrong password on the set-up-again link");
                return Results.LocalRedirect(back + "&error=1");
            }
            var raw = await new AccountSetup(db, users).StartAsync(user, "after a System Admin reset it");
            SetupCookie.Write(http, raw);
            await signIn.SignInAsync(user, isPersistent: false);
            return Results.LocalRedirect("~/account/mobile");
        });

        // ---- registering their mobile: only inside the one-hour window this browser opened --------------------------
        account.MapPost("/mobile/send", async (HttpContext http, [FromForm] string? mobile,
            UserManager<KpiUser> users, SignInManager<KpiUser> signIn, KpiDbContext db, TextCodes codes, ILoggerFactory logs) =>
        {
            var user = await users.GetUserAsync(http.User);
            if (user is null) return Results.LocalRedirect("~/account/login");
            if (await SetupClosedAsync(http, user, db, users, signIn)) return Results.LocalRedirect("~/account/login?setupexpired=1");
            var e164 = UkMobile.Normalise(mobile);
            if (e164 is null) return Results.LocalRedirect("~/account/mobile?error=number");
            if (user.PhoneNumber != e164 || user.PhoneNumberConfirmed)
            {
                user.PhoneNumber = e164;
                user.PhoneNumberConfirmed = false;
                await users.UpdateAsync(user);
            }
            try { await codes.SendAsync(users, user, e164); }
            catch (InvalidOperationException ex)
            {
                logs.CreateLogger("Kpi.Web.Account").LogError(ex, "A registration code could not be texted");
                return Results.LocalRedirect(ex.Message == TextCodes.NotSetUp ? "~/account/mobile?error=notsetup" : "~/account/mobile?error=send");
            }
            return Results.LocalRedirect("~/account/mobile?sent=1");
        });

        account.MapPost("/mobile/confirm", async (HttpContext http, [FromForm] string code,
            UserManager<KpiUser> users, SignInManager<KpiUser> signIn, KpiDbContext db, TextCodes codes, ILoggerFactory logs) =>
        {
            var user = await users.GetUserAsync(http.User);
            if (user is null) return Results.LocalRedirect("~/account/login");
            if (await SetupClosedAsync(http, user, db, users, signIn)) return Results.LocalRedirect("~/account/login?setupexpired=1");
            if (string.IsNullOrEmpty(user.PhoneNumber)) return Results.LocalRedirect("~/account/mobile");
            bool ok;
            try { ok = await codes.CheckAsync(users, user, user.PhoneNumber, Clean(code)); }
            catch (InvalidOperationException ex)
            {
                logs.CreateLogger("Kpi.Web.Account").LogError(ex, "A registration code could not be checked");
                return Results.LocalRedirect("~/account/mobile?sent=1&error=check");
            }
            if (!ok)
            {
                await users.AccessFailedAsync(user);   // inside the window this box is all that stands between a link and the account
                return Results.LocalRedirect("~/account/mobile?sent=1&error=code");
            }
            user.PhoneNumberConfirmed = true;
            await users.UpdateAsync(user);
            await users.SetTwoFactorEnabledAsync(user, true);
            await new AccountSetup(db, users).FinishAsync(user.Id);
            SetupCookie.Delete(http);
            await signIn.RefreshSignInAsync(user);   // enabling rolled the stamp: rewrite the cookie or this session dies
            await Audit(db, http, AuditEvents.AuthenticatorSetUp, user, $"Registered their mobile (ending {UkMobile.LastFour(user.PhoneNumber)}) for sign-in codes");
            await SignedIn(db, http, user, "Signed in on registering their mobile");
            return Results.LocalRedirect("~/");
        });

        // ---- My Account: a new mobile. Needs the password - an unlocked machine must not be enough. -----------
        account.MapPost("/mobile/change", async (HttpContext http, [FromForm] string? currentPassword,
            UserManager<KpiUser> users, SignInManager<KpiUser> signIn, KpiDbContext db) =>
        {
            var user = await users.GetUserAsync(http.User);
            if (user is null) return Results.LocalRedirect("~/account/login");
            if (!await users.CheckPasswordAsync(user, currentPassword ?? ""))
            {
                await users.AccessFailedAsync(user);
                return Results.LocalRedirect("~/my-account?newphone=wrong");
            }
            await users.SetTwoFactorEnabledAsync(user, false);
            user.PhoneNumberConfirmed = false;
            await users.UpdateAsync(user);
            await users.UpdateSecurityStampAsync(user);      // every other session, and remembered browsers
            await signIn.SignOutAsync();
            await signIn.ForgetTwoFactorClientAsync();
            await Audit(db, http, AuditEvents.AuthenticatorReset, user, "Started changing their mobile number");
            var raw = await new AccountSetup(db, users).StartAsync(user, "to change their mobile number");
            SetupCookie.Write(http, raw);
            await signIn.SignInAsync(user, isPersistent: false);
            return Results.LocalRedirect("~/account/mobile");
        });

        // ---- My Account: change password ------------------------------------------------------------------------
        account.MapPost("/change-password", async (HttpContext http, [FromForm] string? currentPassword,
            [FromForm] string? newPassword, [FromForm] string? confirmPassword,
            UserManager<KpiUser> users, SignInManager<KpiUser> signIn, KpiDbContext db, Mailer mailer, ILoggerFactory logs) =>
        {
            var user = await users.GetUserAsync(http.User);
            if (user is null) return Results.LocalRedirect("~/account/login");
            if (newPassword != confirmPassword) return Results.LocalRedirect("~/my-account?password=match");
            var result = await users.ChangePasswordAsync(user, currentPassword ?? "", newPassword ?? "");
            if (!result.Succeeded)
            {
                var wrong = result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.PasswordMismatch));
                if (wrong) await users.AccessFailedAsync(user);
                return Results.LocalRedirect(wrong ? "~/my-account?password=wrong" : "~/my-account?password=rules");
            }
            await signIn.RefreshSignInAsync(user);   // the stamp rolled: other sessions end, this one carries on
            await Audit(db, http, AuditEvents.PasswordChanged, user, "Changed their password");
            try { await mailer.PasswordChangedAsync(user.Email!, user.DisplayName); }
            catch (Exception ex) { logs.CreateLogger("Kpi.Web.Account").LogError(ex, "The password-changed email could not be sent"); }
            return Results.LocalRedirect("~/my-account?password=changed");
        });

        // ---- forgotten password, step one: the same answer, given before the work, whatever the address -------
        app.MapPost("/account/forgot-password", (HttpContext http, [FromForm] string? email,
            IServiceScopeFactory scopes, ILoggerFactory logs) =>
        {
            var baseUrl = $"{http.Request.Scheme}://{http.Request.Host}/";
            var ip = AuditLog.Ip(http);
            _ = Task.Run(async () =>
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var sp = scope.ServiceProvider;
                    var users = sp.GetRequiredService<UserManager<KpiUser>>();
                    var db = sp.GetRequiredService<KpiDbContext>();
                    var user = await users.FindByEmailAsync(email ?? "");
                    if (user is null || user.DisabledUtc is not null || !await users.HasPasswordAsync(user)) return;
                    // three an hour per account, so the address can't be flooded
                    var since = DateTime.UtcNow.AddHours(-1);
                    if (await db.AuditEntries.CountAsync(a => a.SubjectUserId == user.Id && a.Event == AuditEvents.PasswordResetRequested && a.OccurredUtc > since) >= 3) return;
                    var token = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(await users.GeneratePasswordResetTokenAsync(user)));
                    var link = $"{baseUrl}account/reset-password?userId={Uri.EscapeDataString(user.Id)}&code={token}";
                    var sent = await sp.GetRequiredService<Mailer>().PasswordResetAsync(user.Email!, user.DisplayName, link);
                    AuditLog.Add(db, AuditEvents.PasswordResetRequested, user, user, $"Asked for a password reset link (email: {sent})", ip);
                    await db.SaveChangesAsync();
                }
                catch (Exception ex) { logs.CreateLogger("Kpi.Web.Account").LogError(ex, "A password reset could not be completed"); }
            });
            return Results.LocalRedirect("~/account/forgot-password?sent=1");
        }).RequireRateLimiting(ResetLimit);

        // ---- forgotten password, step two: the new password. Signing in afterwards still asks for the texted code ---
        account.MapPost("/reset-password", async (HttpContext http, [FromForm] string userId, [FromForm] string code,
            [FromForm] string password, [FromForm] string confirmPassword,
            UserManager<KpiUser> users, SignInManager<KpiUser> signIn, KpiDbContext db, Mailer mailer, ILoggerFactory logs) =>
        {
            var back = $"~/account/reset-password?userId={Uri.EscapeDataString(userId ?? "")}&code={Uri.EscapeDataString(code ?? "")}";
            var user = await users.FindByIdAsync(userId ?? "");
            if (user is null || user.DisabledUtc is not null) return Results.LocalRedirect("~/account/reset-password");
            if (password != confirmPassword) return Results.LocalRedirect(back + "&error=match");
            string token;
            try { token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code ?? "")); }
            catch (FormatException) { return Results.LocalRedirect("~/account/reset-password"); }
            var result = await users.ResetPasswordAsync(user, token, password ?? "");
            if (!result.Succeeded)
                return Results.LocalRedirect(result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.InvalidToken))
                    ? "~/account/reset-password" : back + "&error=rules");

            await Audit(db, http, AuditEvents.PasswordReset, user, "Reset their password from an emailed link");
            try { await mailer.PasswordChangedAsync(user.Email!, user.DisplayName); }
            catch (Exception ex) { logs.CreateLogger("Kpi.Web.Account").LogError(ex, "The password-changed email could not be sent"); }

            // part-way through setting up: the email proved the mailbox, as an invitation does, so set up the phone now
            if (!user.TwoFactorEnabled)
            {
                var raw = await new AccountSetup(db, users).StartAsync(user, "after resetting their password");
                SetupCookie.Write(http, raw);
                await signIn.SignInAsync(user, isPersistent: false);
                return Results.LocalRedirect("~/account/mobile");
            }
            return Results.LocalRedirect("~/account/login?reset=1");
        });
    }

    public static bool InvitationUsable(KpiUser user, string? code, AccountLinks links) =>
        user.ActivatedUtc is null && user.DisabledUtc is null
        && user.InviteExpiresUtc is { } exp && exp > DateTime.UtcNow
        && links.Check(code, user.Id, UserAdmin.InvitePurpose, user.InviteNonce, AccountLinks.InviteLifespan);

    public static bool ReenrolUsable(KpiUser user, string? code, AccountLinks links) =>
        user.DisabledUtc is null && !user.TwoFactorEnabled
        && user.ReenrolExpiresUtc is { } exp && exp > DateTime.UtcNow
        && links.Check(code, user.Id, UserAdmin.ReenrolPurpose, user.ReenrolNonce, AccountLinks.ReenrolLifespan);

    // Texts the sign-in code. False (and the reason in the log, never on screen) when it couldn't be sent.
    private static async Task<bool> SendSignInCodeAsync(UserManager<KpiUser> users, KpiUser user, ILoggerFactory logs)
    {
        try { await users.GenerateTwoFactorTokenAsync(user, SmsTokenProvider.Name); return true; }
        catch (InvalidOperationException ex)
        {
            logs.CreateLogger("Kpi.Web.Account").LogError(ex, "A sign-in code could not be texted");
            return false;
        }
    }

    // Registering a mobile only happens inside the one-hour window an emailed link (or My Account) opened in this browser.
    private static async Task<bool> SetupClosedAsync(HttpContext http, KpiUser user, KpiDbContext db, UserManager<KpiUser> users, SignInManager<KpiUser> signIn)
    {
        if (user.DisabledUtc is null && await new AccountSetup(db, users).IsOpenAsync(user.Id, SetupCookie.Read(http))) return false;
        SetupCookie.Delete(http);
        await signIn.SignOutAsync();
        return true;
    }

    // spaces are what people type reading six digits off a phone
    private static string Clean(string? code) => (code ?? "").Replace(" ", "").Trim();

    private static string ReturnQuery(string? returnUrl) =>
        string.IsNullOrEmpty(returnUrl) ? "" : "&returnUrl=" + Uri.EscapeDataString(returnUrl);

    private static async Task Audit(KpiDbContext db, HttpContext http, string auditEvent, KpiUser user, string detail)
    {
        AuditLog.Add(db, auditEvent, user, user, detail, AuditLog.Ip(http));
        await db.SaveChangesAsync();
    }

    private static async Task SignedIn(KpiDbContext db, HttpContext http, KpiUser user, string detail)
    {
        var row = await db.Users.SingleAsync(u => u.Id == user.Id);
        row.LastSignInUtc = DateTime.UtcNow;
        AuditLog.Add(db, AuditEvents.SignIn, row, row, detail, AuditLog.Ip(http));
        await db.SaveChangesAsync();
        // "KPIs work best as an app": the bar shows on the first page after EACH sign-in, then not again until the next
        // (js/appInstall.js reads and clears this; it says nothing, so the browser's script may see it)
        http.Response.Cookies.Append(AppTipCookie, "1", new CookieOptions
            { HttpOnly = false, Secure = true, SameSite = SameSiteMode.Lax, Path = "/", IsEssential = true });
    }

    /// <summary>Set at each sign-in for js/appInstall.js (the Home Screen bar).</summary>
    public const string AppTipCookie = "kpi-app-tip";
}

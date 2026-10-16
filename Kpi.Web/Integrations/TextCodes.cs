using Kpi.Data.Auth;
using Microsoft.AspNetCore.Identity;

namespace Kpi.Web.Integrations;

/// <summary>
/// The sign-in's text codes: through Twilio Verify when it is connected (<see cref="TwilioConnection"/>). Until then the
/// code is written to the SITE'S LOG instead - on a developer's machine for anyone, and on the live site only for the
/// account made by `Kpi.Tools seed` before it is first used: a brand-new database has no Twilio yet, and whoever can read
/// the log already runs the site. That code is Identity's own phone token (made from the number and the account's stamp),
/// so nothing is stored either way. Everyone else is told texts aren't set up.
/// </summary>
public sealed class TextCodes(TwilioConnection twilio, IWebHostEnvironment env, ILogger<TextCodes> log)
{
    public const string NotSetUp = "Text messages aren't set up yet, so a code can't be sent. Ask your System Admin.";

    /// <summary>Texts a code to the number. Throws InvalidOperationException in words when it can't.</summary>
    public async Task SendAsync(UserManager<KpiUser> users, KpiUser user, string e164)
    {
        if (await twilio.IsUsableAsync()) { await twilio.SendCodeAsync(e164); return; }
        if (!MayLog(user)) throw new InvalidOperationException(NotSetUp);
        var code = await users.GenerateChangePhoneNumberTokenAsync(user, e164);
        log.LogWarning("Text code for {Name} (mobile ending {LastFour}): {Code} - Twilio isn't connected, so it is written here instead",
            user.DisplayName, UkMobile.LastFour(e164), code);
    }

    public async Task<bool> CheckAsync(UserManager<KpiUser> users, KpiUser user, string e164, string code)
    {
        if (await twilio.IsUsableAsync()) return await twilio.CheckCodeAsync(e164, code);
        if (!MayLog(user)) throw new InvalidOperationException(NotSetUp);
        return await users.VerifyChangePhoneNumberTokenAsync(user, code, e164);
    }

    private bool MayLog(KpiUser user) => env.IsDevelopment() || (user.IsSeeded && user.ActivatedUtc is null);
}

/// <summary>
/// The two-factor step as a text: Identity's sign-in asks this provider to send and check the code, so
/// SignInManager.TwoFactorSignInAsync, lockout and "don't ask again on this device" all work as before.
/// </summary>
public sealed class SmsTokenProvider(TextCodes codes) : IUserTwoFactorTokenProvider<KpiUser>
{
    public const string Name = "Sms";

    public Task<bool> CanGenerateTwoFactorTokenAsync(UserManager<KpiUser> manager, KpiUser user) =>
        Task.FromResult(user.PhoneNumberConfirmed && !string.IsNullOrEmpty(user.PhoneNumber));

    // Twilio (or the log) holds the code; there is nothing to hand back
    public async Task<string> GenerateAsync(string purpose, UserManager<KpiUser> manager, KpiUser user)
    {
        await codes.SendAsync(manager, user, user.PhoneNumber!);
        return "";
    }

    public Task<bool> ValidateAsync(string purpose, string token, UserManager<KpiUser> manager, KpiUser user) =>
        codes.CheckAsync(manager, user, user.PhoneNumber!, token);
}

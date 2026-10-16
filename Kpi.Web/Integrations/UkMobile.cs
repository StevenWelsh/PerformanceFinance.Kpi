using System.Text.RegularExpressions;

namespace Kpi.Web.Integrations;

// A UK MOBILE NUMBER, as the sign-in takes one for its text codes (DavidsonHR's UkMobile). UK only, stored in
// international form (+447...) because that is what Twilio wants; shown back in the familiar 07... form. A typed
// "07700 900482", "(07700) 900-482", "+44 7700 900482" or "00447700900482" all come out as +447700900482. Anything that
// is not then a UK mobile is refused and the person corrects it on screen - a landline cannot receive a code.
public static partial class UkMobile
{
    [GeneratedRegex(@"^\+447\d{9}$")]
    private static partial Regex E164Shape();

    /// <summary>The number in +447... form, or null when it isn't a UK mobile.</summary>
    public static string? Normalise(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed)) return null;
        var plus = typed.TrimStart().StartsWith('+');
        var digits = new string(typed.Where(char.IsDigit).ToArray());
        string candidate =
            plus ? "+" + digits
            : digits.StartsWith("0044") ? "+" + digits[2..]
            : digits.StartsWith("44") && digits.Length == 12 ? "+" + digits
            : digits.StartsWith('0') ? "+44" + digits[1..]
            : "";
        return E164Shape().IsMatch(candidate) ? candidate : null;
    }

    /// <summary>+447700900482 → "07700 900 482".</summary>
    public static string Display(string e164)
    {
        if (!E164Shape().IsMatch(e164)) return e164;
        var local = "0" + e164[3..];
        return $"{local[..5]} {local[5..8]} {local[8..]}";
    }

    /// <summary>The last four digits - all a code screen may show, and only after the password.</summary>
    public static string LastFour(string e164) => e164.Length >= 4 ? e164[^4..] : e164;
}

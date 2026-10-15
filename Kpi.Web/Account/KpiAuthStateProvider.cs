using System.Security.Claims;
using Kpi.Data.Auth;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Kpi.Web.Account;

/// <summary>
/// Checks every open page's sign-in once a minute, as DavidsonHR. A disabled account, a new password or a change of
/// role (each rolls the security stamp) ends the session; a change of report rights is brought in without a sign-out,
/// so the menu and the reports follow it.
/// </summary>
public sealed class KpiAuthStateProvider(ILoggerFactory loggers, IServiceScopeFactory scopes, IOptions<IdentityOptions> options)
    : RevalidatingServerAuthenticationStateProvider(loggers)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState state, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<KpiUser>>();
        var user = await users.GetUserAsync(state.User);
        if (user is null || user.DisabledUtc is not null) return false;

        var stamp = state.User.FindFirstValue(options.Value.ClaimsIdentity.SecurityStampClaimType);
        if (stamp != await users.GetSecurityStampAsync(user)) return false;

        var fresh = await scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<KpiUser>>().CreateAsync(user);
        if (Rights(fresh) != Rights(state.User))
            SetAuthenticationState(Task.FromResult(new AuthenticationState(fresh)));
        return true;
    }

    private static string Rights(ClaimsPrincipal p) => string.Join("|",
        p.FindFirstValue(KpiClaims.TwoFactor), p.FindFirstValue(KpiClaims.Reports),
        p.FindFirstValue(KpiClaims.Name), string.Join(",", p.FindAll(ClaimTypes.Role).Select(c => c.Value).Order()));
}

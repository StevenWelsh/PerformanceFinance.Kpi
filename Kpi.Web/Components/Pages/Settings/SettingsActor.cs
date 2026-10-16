using Kpi.Data.Auth;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;

namespace Kpi.Web.Components.Pages.Settings;

/// <summary>The System Admin making a change on a Settings card - the integrations are theirs alone.</summary>
public static class SettingsActor
{
    public static async Task<KpiUser> GetAsync(Task<AuthenticationState>? authState, IServiceScopeFactory scopes)
    {
        var principal = (await authState!).User;
        if (!principal.IsInRole(KpiRoles.SystemAdmin)) throw new InvalidOperationException("Only a System Admin can change integrations.");
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<UserManager<KpiUser>>().GetUserAsync(principal)
               ?? throw new InvalidOperationException("Your account could not be found.");
    }
}

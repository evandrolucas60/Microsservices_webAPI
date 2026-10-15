using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BuildingBlocks.Shared.Security;

/// <summary>
/// Política do painel de gestão: [Authorize(Policy = AdminPolicies.SuperAdmin)].
/// Papéis aceitos em "Administracao:Roles" (padrão: superAdmin), sem diferenciar maiúsculas.
/// </summary>
public static class AdminPolicies
{
    public const string SuperAdmin = "SuperAdmin";

    public static WebApplicationBuilder AddAdminPolicies(this WebApplicationBuilder builder)
    {
        var roles = builder.Configuration.GetSection("Administracao:Roles").Get<string[]>() is { Length: > 0 } configured
            ? configured
            : new[] { "superAdmin" };

        builder.Services.AddAuthorization(options =>
            options.AddPolicy(SuperAdmin, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(context => context.User.HasAnyRole(roles))));

        return builder;
    }
}

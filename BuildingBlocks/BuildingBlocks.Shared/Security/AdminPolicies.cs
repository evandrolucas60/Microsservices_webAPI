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

    /// <summary>Papéis de superAdmin configurados (padrão: superAdmin).</summary>
    public static string[] Roles(IConfiguration configuration) =>
        configuration.GetSection("Administracao:Roles").Get<string[]>() is { Length: > 0 } configured
            ? configured
            : new[] { "superAdmin" };

    public static WebApplicationBuilder AddAdminPolicies(this WebApplicationBuilder builder)
    {
        var roles = Roles(builder.Configuration);

        builder.Services.AddAuthorization(options =>
            options.AddPolicy(SuperAdmin, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(context => context.User.HasAnyRole(roles))));

        return builder;
    }
}

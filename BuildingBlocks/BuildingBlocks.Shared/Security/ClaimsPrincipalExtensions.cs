using System.Security.Claims;

namespace BuildingBlocks.Shared.Security;

public static class ClaimsPrincipalExtensions
{
    /// <summary>Claim "id" do JWT emitido pelo AuthAPI (id do usuário).</summary>
    public static string? GetUserId(this ClaimsPrincipal user) => user.FindFirst("id")?.Value;

    /// <summary>Se o usuário tem algum dos papéis (roles) informados, sem diferenciar maiúsculas.</summary>
    public static bool HasAnyRole(this ClaimsPrincipal user, IEnumerable<string> roles)
    {
        if (user.Identity?.IsAuthenticated != true) return false;
        var allowed = roles.Select(r => r.Trim()).Where(r => r.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return user.Claims
            .Where(c => c.Type == ClaimTypes.Role || c.Type == "role")
            .Any(c => allowed.Contains(c.Value.Trim()));
    }
}

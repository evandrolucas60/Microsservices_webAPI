using System.Security.Claims;

namespace Intranet.Services.GerenciamentoCategoriaAPI.Services;

/// <summary>
/// Quem pode criar/alterar/remover categorias e itens: usuários com algum dos papéis de
/// "Categorias:RolesGerenciamento" (padrão: superAdmin e GC), comparados sem diferenciar
/// maiúsculas de minúsculas.
/// </summary>
public class CategoriaPermissoes
{
    public static readonly string[] RolesPadrao = { "superAdmin", "GC" };

    private readonly HashSet<string> _roles;

    public CategoriaPermissoes(IConfiguration configuration)
    {
        var configuradas = configuration.GetSection("Categorias:RolesGerenciamento").Get<string[]>();
        var roles = configuradas is { Length: > 0 } ? configuradas : RolesPadrao;
        _roles = new HashSet<string>(
            roles.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()),
            StringComparer.OrdinalIgnoreCase);
    }

    public bool PodeGerenciar(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true &&
        user.Claims
            .Where(c => c.Type == ClaimTypes.Role || c.Type == "role")
            .Any(c => _roles.Contains(c.Value.Trim()));
}

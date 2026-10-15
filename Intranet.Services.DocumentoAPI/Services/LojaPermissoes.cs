using System.Security.Claims;

namespace Intranet.Services.DocumentoAPI.Services;

/// <summary>
/// Quem pode criar/alterar/remover lojas e arquivos: usuários com algum dos papéis de
/// "Lojas:RolesGerenciamento" (padrão: superAdmin e GC), comparados sem diferenciar
/// maiúsculas de minúsculas.
/// </summary>
public class LojaPermissoes
{
    public static readonly string[] RolesPadrao = { "superAdmin", "GC" };

    private readonly HashSet<string> _roles;

    public LojaPermissoes(IConfiguration configuration)
    {
        var configuradas = configuration.GetSection("Lojas:RolesGerenciamento").Get<string[]>();
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

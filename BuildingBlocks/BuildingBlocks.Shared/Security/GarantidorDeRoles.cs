using System.Text.RegularExpressions;
using BuildingBlocks.Contracts.Administracao;

namespace BuildingBlocks.Shared.Security;

/// <summary>Resultado da criação de um papel no armazenamento (Identity).</summary>
public enum CriacaoRole
{
    Criada,
    /// <summary>Outro pedido criou o mesmo papel ao mesmo tempo (nome normalizado duplicado): conta como existente.</summary>
    JaExistia,
    Falhou,
}

/// <summary>Onde os papéis ficam (no AuthAPI, o RoleManager do Identity). Comparação sem diferenciar maiúsculas.</summary>
public interface IArmazemRoles
{
    Task<bool> ExisteAsync(string nome, CancellationToken ct);

    /// <summary>Cria o papel; <c>Erro</c> preenchido quando <see cref="CriacaoRole.Falhou"/>.</summary>
    Task<(CriacaoRole Resultado, string? Erro)> CriarAsync(string nome, CancellationToken ct);
}

/// <summary>
/// Regra do comando <see cref="GarantirRoles"/> (consumido pelo AuthAPI), separada para teste: valida cada nome com o
/// mesmo padrão do painel de gestão, ignora repetidos (sem diferenciar maiúsculas/espaços nas pontas) e cria só os que
/// faltam. Idempotente: rodar de novo devolve tudo em "Existentes". Nunca apaga nem altera papel existente.
/// </summary>
public static class GarantidorDeRoles
{
    /// <summary>Mesmo limite e padrão de nome do painel de gestão (AdminUsuariosService.CriarRoleAsync).</summary>
    public const int MaxNome = 50;
    private static readonly Regex NomeValido = new("^[A-Za-z0-9._-]+$", RegexOptions.Compiled);

    /// <summary>Motivo para recusar o nome (null = válido).</summary>
    public static string? Validar(string? nome)
    {
        if (string.IsNullOrWhiteSpace(nome)) return "nome vazio";
        if (nome.Length > MaxNome) return $"mais de {MaxNome} caracteres";
        if (!NomeValido.IsMatch(nome)) return "aceita apenas letras, números, ponto, hífen e sublinhado";
        return null;
    }

    public static async Task<RolesGarantidas> GarantirAsync(IEnumerable<string>? nomes, IArmazemRoles armazem, CancellationToken ct = default)
    {
        var criadas = new List<string>();
        var existentes = new List<string>();
        var recusadas = new List<string>();
        var vistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var bruto in nomes ?? Array.Empty<string>())
        {
            var nome = bruto?.Trim() ?? string.Empty;
            if (Validar(nome) is { } motivo)
            {
                recusadas.Add($"{(nome.Length == 0 ? "(vazio)" : nome)}: {motivo}");
                continue;
            }
            if (!vistos.Add(nome)) continue;

            if (await armazem.ExisteAsync(nome, ct))
            {
                existentes.Add(nome);
                continue;
            }
            var (resultado, erro) = await armazem.CriarAsync(nome, ct);
            switch (resultado)
            {
                case CriacaoRole.Criada: criadas.Add(nome); break;
                case CriacaoRole.JaExistia: existentes.Add(nome); break;
                default: recusadas.Add($"{nome}: {erro ?? "não foi possível criar"}"); break;
            }
        }

        return new RolesGarantidas
        {
            Criadas = criadas.ToArray(),
            Existentes = existentes.ToArray(),
            Recusadas = recusadas.ToArray(),
        };
    }
}

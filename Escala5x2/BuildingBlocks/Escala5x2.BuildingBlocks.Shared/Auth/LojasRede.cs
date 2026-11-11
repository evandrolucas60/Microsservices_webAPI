using BuildingBlocks.Contracts.Identidade;

namespace Escala5x2.BuildingBlocks.Shared.Auth;

/// <summary>Loja da lista oficial da intranet já separada em código e nome ("28 - ASA BRANCA" → "28", "ASA BRANCA").</summary>
public sealed record LojaRede(string Code, string Name);

/// <summary>
/// Lojas da rede Arco-Mix/Arco-Vita pela lista oficial da intranet (<see cref="DadosCadastroColaborador.Lojas"/>,
/// a mesma do perfil do colaborador). O Store.Api cadastra na inicialização as que ainda não existem, para o
/// vínculo usuário → loja (<see cref="CodigoLoja"/>) funcionar em toda a rede.
/// </summary>
public static class LojasRede
{
    /// <summary>Nome da loja sem descrição na lista ("CD").</summary>
    public const string NomeCentroDistribuicao = "CENTRO DE DISTRIBUIÇÃO";

    public static IReadOnlyList<LojaRede> Oficiais { get; } =
        DadosCadastroColaborador.Lojas.Select(Converter).OfType<LojaRede>().ToList();

    /// <summary>"28 - ASA BRANCA" → ("28", "ASA BRANCA"); "CD" → ("CD", "CENTRO DE DISTRIBUIÇÃO"); vazio → null.</summary>
    public static LojaRede? Converter(string? loja)
    {
        var codigo = CodigoLoja.DaLoja(loja);
        if (codigo is null) return null;
        var partes = loja!.Split(" - ", 2);
        var nome = partes.Length > 1 ? partes[1].Trim() : string.Empty;
        if (nome.Length == 0) nome = codigo.Equals("CD", StringComparison.OrdinalIgnoreCase) ? NomeCentroDistribuicao : codigo;
        return new LojaRede(codigo, nome);
    }

    /// <summary>
    /// Lojas oficiais sem loja cadastrada com o mesmo código normalizado (<see cref="CodigoLoja.Normalizar"/>:
    /// "04" casa com "4"). Não repete código dentro da própria lista.
    /// </summary>
    public static List<LojaRede> Faltantes(IEnumerable<string> codigosCadastrados, IEnumerable<LojaRede>? oficiais = null)
    {
        var existentes = codigosCadastrados.Select(CodigoLoja.Normalizar).OfType<string>().ToHashSet();
        var result = new List<LojaRede>();
        foreach (var loja in oficiais ?? Oficiais)
            if (CodigoLoja.Normalizar(loja.Code) is { } n && existentes.Add(n))
                result.Add(loja);
        return result;
    }
}

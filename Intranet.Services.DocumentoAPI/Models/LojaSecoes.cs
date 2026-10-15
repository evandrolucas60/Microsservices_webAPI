namespace Intranet.Services.DocumentoAPI.Models;

/// <summary>Seções fixas de toda loja (nomes exatos, nesta ordem).</summary>
public static class LojaSecoes
{
    public const int NomeMaxLength = 50;

    public static readonly IReadOnlyList<string> Todas = new[]
    {
        "500",
        "Aves",
        "Bazar",
        "Bebidas",
        "Carnes",
        "Higiene",
        "Horti",
        "Limpeza",
        "Mercearia Doce",
        "Mercearia Salgada",
        "Padaria",
        "Pereciveis",
        "Pet",
        "Salgados e Defumados",
        "Institucional",
    };

    /// <summary>Nome oficial da seção (ignora maiúsculas/minúsculas e espaços nas pontas), ou null se não existir.</summary>
    public static string? Encontrar(string? secao)
    {
        if (string.IsNullOrWhiteSpace(secao)) return null;
        var valor = secao.Trim();
        return Todas.FirstOrDefault(s => string.Equals(s, valor, StringComparison.OrdinalIgnoreCase));
    }
}

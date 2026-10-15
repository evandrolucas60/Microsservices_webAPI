using System.Net;
using System.Text.RegularExpressions;

namespace Intranet.Services.ConteudoAPI.Services;

/// <summary>Resumo de texto para listas da administração: sem HTML, espaços normalizados, tamanho limitado.</summary>
public static partial class ConteudoResumo
{
    public const int MaxLength = 200;

    public static string? De(string? texto, int maxLength = MaxLength)
    {
        if (string.IsNullOrWhiteSpace(texto)) return null;

        var semHtml = WebUtility.HtmlDecode(TagsHtml().Replace(texto, " "));
        var limpo = Espacos().Replace(semHtml, " ").Trim();
        if (limpo.Length == 0) return null;

        return limpo.Length <= maxLength ? limpo : limpo[..(maxLength - 1)].TrimEnd() + "…";
    }

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex TagsHtml();

    [GeneratedRegex("\\s+")]
    private static partial Regex Espacos();
}

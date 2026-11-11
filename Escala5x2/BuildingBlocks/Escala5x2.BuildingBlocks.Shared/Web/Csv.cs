using System.Globalization;
using System.Text;

namespace Escala5x2.BuildingBlocks.Shared.Web;

/// <summary>
/// CSV para abrir direto no Excel em português: separador ";", números com vírgula decimal (pt-BR) e UTF-8 com BOM
/// (sem o BOM o Excel lê os acentos errado). Campo com ";", aspas ou quebra de linha vai entre aspas.
/// </summary>
public static class Csv
{
    public const char Separador = ';';
    public const string ContentType = "text/csv; charset=utf-8";
    public static readonly CultureInfo Cultura = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>UTF-8 com BOM (o StreamWriter escreve o preâmbulo no início do arquivo).</summary>
    public static readonly Encoding Codificacao = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    public static string Campo(string? valor)
    {
        if (string.IsNullOrEmpty(valor)) return string.Empty;
        // Fórmula no início do campo (=, +, -, @) seria executada pelo Excel: prefixa com apóstrofo
        if (valor[0] is '=' or '+' or '@' or '\t' or '\r' || (valor[0] == '-' && !double.TryParse(valor, NumberStyles.Number, Cultura, out _)))
            valor = "'" + valor;
        return valor.IndexOfAny([Separador, '"', '\n', '\r']) >= 0 ? $"\"{valor.Replace("\"", "\"\"")}\"" : valor;
    }

    public static string Numero(double valor, string formato = "0.###") => valor.ToString(formato, Cultura);

    public static string Numero(long valor) => valor.ToString(Cultura);

    public static string Linha(IEnumerable<string?> campos) => string.Join(Separador, campos.Select(Campo));
}

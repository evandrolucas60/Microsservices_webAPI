using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Escala5x2.BuildingBlocks.Shared.Lg;

/// <summary>Código e descrição de um cadastro da LG (Cargo, CentroDeCusto, Empresa, Situação...).</summary>
public sealed record LgItem(string? Codigo, string? Descricao);

/// <summary>Uma página de uma consulta "PorDemanda" da LG, com os itens já convertidos.</summary>
public sealed record LgPaginaDe<T>(IReadOnlyList<T> Itens, int NumeroDaPagina, int QuantidadePorPagina, int TotalDePaginas, int TotalGeral);

/// <summary>Resultado de todas as páginas de uma consulta.</summary>
public sealed record LgConsultaDe<T>(IReadOnlyList<T> Itens, int Paginas, int TotalGeral);

/// <summary>
/// Erro da integração com mensagem clara para a tela (sem credenciais nem dados pessoais).
/// <paramref name="transitorio"/>: vale tentar de novo (rede, timeout, HTTP 5xx sem SOAP Fault).
/// </summary>
public sealed class LgIntegracaoException(string message, bool transitorio = false, Exception? inner = null) : Exception(message, inner)
{
    public bool Transitorio { get; } = transitorio;
}

/// <summary>
/// Núcleo SOAP da LG compartilhado pelos serviços: namespaces, envelope (LGAutenticacao + LGContextoAmbiente),
/// leitura do resultado (SOAP Fault, CodigoDoErro/Mensagens/Tipo, Retorno e paginação) e utilitários de XML.
/// </summary>
public static partial class LgXml
{
    public static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    public static readonly XNamespace Dto = "lg.com.br/svc/dto";
    /// <summary>Namespace da operação de colaboradores e centros de custo (lg.com.br/api/v2).</summary>
    public static readonly XNamespace V2 = LgOperacao.ApiV2;
    /// <summary>Namespace dos DTOs/filtros (lg.com.br/api/dto/v1) — prefixo "v1" no envelope das operações v2.</summary>
    public static readonly XNamespace V1 = LgOperacao.DtoV1;
    /// <summary>Namespace da operação de órgãos responsáveis (lg.com.br/api/v1) — não confundir com <see cref="V1"/> (DTO).</summary>
    public static readonly XNamespace ApiV1 = LgOperacao.ApiV1;
    public static readonly XNamespace Arr = "http://schemas.microsoft.com/2003/10/Serialization/Arrays";
    public static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    // ---------- Envelope ----------

    /// <summary>Namespace da operação (elemento da operação e "filtro").</summary>
    public static XNamespace NamespaceOperacao(LgOperacao op) => op.NamespaceOperacao;

    /// <summary>Namespace dos elementos do filtro (DTO) da operação.</summary>
    public static XNamespace NamespaceDto(LgOperacao op) => op.NamespaceDto;

    /// <summary>
    /// Prefixos do envelope como nos exemplos da LG: operação v2 → "v2" (operação) e "v1" (DTO); operação v1 → "v1"
    /// (operação) e "v11" (DTO). Só estética: o que vale é o namespace.
    /// </summary>
    private static (string Operacao, string Dto) Prefixos(LgOperacao op)
    {
        static string Ultimo(string ns, string padrao)
        {
            var s = ns.TrimEnd('/').Split('/')[^1];
            return s.Length > 0 && char.IsLetter(s[0]) && s.All(char.IsLetterOrDigit) ? s : padrao;
        }
        var operacao = Ultimo(op.NamespaceOperacao, "op");
        var dto = Ultimo(op.NamespaceDto, "d");
        return (operacao, dto == operacao ? dto + "1" : dto);
    }

    /// <summary>
    /// Envelope SOAP 1.1 da operação: credenciais SÓ no cabeçalho (LGAutenticacao/TokenUsuario) e o filtro em
    /// {operação}/filtro no namespace da operação (v2 para colaboradores/centros de custo, v1 para órgãos responsáveis);
    /// os elementos do filtro vêm prontos, no namespace do DTO (<see cref="NamespaceDto"/>).
    /// </summary>
    public static string MontarEnvelope(LgOptions o, LgOperacao op, IEnumerable<XElement> filtro)
    {
        XNamespace nsOp = op.NamespaceOperacao;
        XNamespace nsDto = op.NamespaceDto;
        var (prefixoOp, prefixoDto) = Prefixos(op);
        var declaracoes = new List<XAttribute>
        {
            new(XNamespace.Xmlns + "soapenv", Soap),
            new(XNamespace.Xmlns + "dto", Dto),
            new(XNamespace.Xmlns + prefixoOp, nsOp)
        };
        if (nsDto != nsOp) declaracoes.Add(new XAttribute(XNamespace.Xmlns + prefixoDto, nsDto));
        declaracoes.Add(new XAttribute(XNamespace.Xmlns + "arr", Arr));
        var envelope = new XElement(Soap + "Envelope",
            declaracoes,
            new XElement(Soap + "Header",
                new XElement(Dto + "LGAutenticacao",
                    new XElement(Dto + "TokenUsuario",
                        new XElement(Dto + "Senha", o.Senha),
                        new XElement(Dto + "Usuario", o.Usuario),
                        new XElement(Dto + "GuidTenant", o.GuidTenant))),
                new XElement(Dto + "LGContextoAmbiente",
                    new XElement(Dto + "Ambiente", o.Ambiente))),
            new XElement(Soap + "Body",
                new XElement(nsOp + op.Nome, new XElement(nsOp + "filtro", filtro))));
        return envelope.ToString(SaveOptions.DisableFormatting);
    }

    // ---------- Resposta ----------

    /// <summary>
    /// Lê uma página da resposta da operação: <paramref name="elementoItem"/> = nome dos itens dentro de Retorno
    /// (ex.: ContratoDeTrabalhoParcial, CentroDeCustoCompleto). XML mal formado, SOAP Fault, CodigoDoErro/Mensagens
    /// preenchidos ou Tipo ≠ 0 viram <see cref="LgIntegracaoException"/> com a mensagem da LG (nada é gravado).
    /// </summary>
    public static LgPaginaDe<T> LerPagina<T>(string xml, LgOperacao op, string elementoItem, Func<XElement, T> converter)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (XmlException ex)
        {
            throw new LgIntegracaoException($"Resposta inválida da LG (XML mal formado na linha {ex.LineNumber}).", inner: ex);
        }

        if (doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Fault") is { } fault)
            throw new LgIntegracaoException($"A LG recusou a consulta (SOAP Fault): {DescreverFault(fault)}");

        var result = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == op.Nome + "Result")
            ?? throw new LgIntegracaoException(
                $"Resposta da LG sem {op.Nome}Result (verifique LG:Url e o SOAPAction da operação {op.Chave}).");

        var tipo = Texto(Filho(result, "Tipo"));
        var codigoErro = Texto(Filho(result, "CodigoDoErro"));
        var mensagens = Filho(result, "Mensagens") is { } m && !Nulo(m)
            ? string.Join(" ", m.DescendantsAndSelf().Where(x => !x.HasElements && !Nulo(x)).Select(x => x.Value.Trim()).Where(x => x.Length > 0))
            : string.Empty;
        var tipoErro = int.TryParse(tipo, out var t) && t != 0;
        if (tipoErro || !string.IsNullOrWhiteSpace(codigoErro) && codigoErro != "0" || mensagens.Length > 0 && Filho(result, "Retorno") is null)
        {
            var partes = new List<string>();
            if (tipoErro) partes.Add($"tipo {t}");
            if (!string.IsNullOrWhiteSpace(codigoErro)) partes.Add($"código {codigoErro}");
            throw new LgIntegracaoException(
                $"A LG retornou erro ({string.Join(", ", partes.DefaultIfEmpty("sem código"))}): {(mensagens.Length > 0 ? mensagens : "sem mensagem")}");
        }

        var itens = Filho(result, "Retorno") is { } retorno && !Nulo(retorno)
            ? retorno.Elements().Where(e => e.Name.LocalName == elementoItem).Select(converter).ToList()
            : [];
        return new LgPaginaDe<T>(itens,
            Inteiro(Filho(result, "NumeroDaPagina")) ?? 1,
            Inteiro(Filho(result, "QuantidadePorPagina")) ?? itens.Count,
            Inteiro(Filho(result, "TotalDePaginas")) ?? 1,
            Inteiro(Filho(result, "TotalGeral")) ?? itens.Count);
    }

    private static string DescreverFault(XElement fault)
    {
        var texto = Texto(fault.Descendants().FirstOrDefault(e => e.Name.LocalName is "faultstring" or "Text" or "Reason"))
                    ?? fault.Value.Trim();
        var codigo = Texto(fault.Elements().FirstOrDefault(e => e.Name.LocalName == "faultcode")) ?? "";
        return string.IsNullOrEmpty(codigo) ? texto : $"{texto} ({codigo})";
    }

    /// <summary>
    /// Elemento XML → JSON genérico: i:nil → null; folha → texto; filhos com nomes repetidos (listas) → array.
    /// Guarda TODOS os campos do registro, inclusive os que a Escala não conhece.
    /// </summary>
    public static JsonNode? ParaJson(XElement e)
    {
        if (Nulo(e)) return null;
        if (!e.HasElements) return JsonValue.Create(e.Value);
        var filhos = e.Elements().ToList();
        if (filhos.Count > 1 && filhos.All(f => f.Name.LocalName == filhos[0].Name.LocalName))
            return new JsonArray(filhos.Select(ParaJson).ToArray());
        var obj = new JsonObject();
        foreach (var g in filhos.GroupBy(f => f.Name.LocalName))
        {
            var itens = g.ToList();
            obj[g.Key] = itens.Count == 1 ? ParaJson(itens[0]) : new JsonArray(itens.Select(ParaJson).ToArray());
        }
        return obj;
    }

    /// <summary>Primeiro filho com o nome local (ignora o namespace).</summary>
    public static XElement? Filho(XElement? e, string nome) => e?.Elements().FirstOrDefault(x => x.Name.LocalName == nome);
    public static bool Nulo(XElement e) => (string?)e.Attribute(Xsi + "nil") == "true";
    public static string? Texto(XElement? e) => e is null || Nulo(e) ? null : e.Value;
    public static int? Inteiro(XElement? e) => int.TryParse(Texto(e), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    public static bool? Logico(XElement? e) => bool.TryParse(Texto(e), out var v) ? v : null;

    public static LgItem? Item(XElement? e) =>
        e is null || Nulo(e) ? null : new LgItem(Texto(Filho(e, "Codigo"))?.Trim(), Texto(Filho(e, "Descricao"))?.Trim());

    public static DateOnly? Data(XElement? e) =>
        DateTime.TryParse(Texto(e), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? DateOnly.FromDateTime(d) : null;

    // ---------- Texto ----------

    [GeneratedRegex(@"\s+")]
    private static partial Regex Espacos();

    /// <summary>Maiúsculas, sem acento e sem espaços repetidos (comparação de descrições da LG e nomes).</summary>
    public static string Normalizar(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return string.Empty;
        var decomposto = texto.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposto.Length);
        foreach (var ch in decomposto)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return Espacos().Replace(sb.ToString().Normalize(NormalizationForm.FormC).ToUpperInvariant(), " ").Trim();
    }
}

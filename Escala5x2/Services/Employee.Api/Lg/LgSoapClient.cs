using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Escala5x2.BuildingBlocks.Shared.Lg;
using Microsoft.Extensions.Options;

namespace Employee.Api.Lg;

/// <summary>Resultado de todas as páginas de uma consulta.</summary>
public sealed record LgConsulta(IReadOnlyList<LgContrato> Contratos, int Paginas, int TotalGeral);

/// <summary>
/// Cliente da operação ConsultarListaHistoricosVigenteNaDataPorDemanda. O transporte SOAP (envelope com autenticação,
/// SOAPAction, retry/backoff, SOAP Fault, CodigoDoErro/Mensagens/Tipo e paginação) é o núcleo compartilhado
/// <see cref="LgSoapCore"/>/<see cref="LgXml"/> (BuildingBlocks.Shared/Lg), o mesmo das lojas no Store.Api.
/// Credenciais, envelope e resposta (que trazem CPF) NUNCA vão para o log.
/// </summary>
public sealed class LgSoapClient(HttpClient http, IOptionsMonitor<LgOptions> options, ILogger<LgSoapClient> logger)
{
    public static readonly XNamespace Soap = LgXml.Soap;
    public static readonly XNamespace Dto = LgXml.Dto;
    public static readonly XNamespace V2 = LgXml.V2;
    public static readonly XNamespace V1 = LgXml.V1;
    public static readonly XNamespace Arr = LgXml.Arr;
    public static readonly XNamespace Xsi = LgXml.Xsi;

    private static readonly LgOperacao Operacao = LgOperacao.Colaboradores;

    private readonly LgSoapCore _core = new(http, options, logger);

    /// <summary>Espera entre tentativas (substituível nos testes).</summary>
    public Func<TimeSpan, CancellationToken, Task> Esperar
    {
        get => _core.Esperar;
        set => _core.Esperar = value;
    }

    // ---------- Envelope ----------

    /// <summary>Envelope da requisição de uma página (mesma ordem de elementos do exemplo validado com a LG).</summary>
    public static string MontarEnvelope(LgOptions o, int pagina, DateOnly dataVigente)
    {
        var filtro = new List<XElement> { new(V1 + "PaginaAtual", pagina) };
        if (o.TamanhoPagina > 0) filtro.Add(new XElement(V1 + "QuantidadePorPagina", o.TamanhoPagina));
        filtro.Add(new XElement(V1 + "Empresas", o.EmpresasEfetivas.Select(e =>
            new XElement(V1 + "FiltroComCodigoNumerico", new XElement(V1 + "Codigo", e)))));
        filtro.Add(new XElement(V1 + "TiposDeSituacoes", o.SituacoesEfetivas.Select(s => new XElement(Arr + "int", s))));
        filtro.Add(new XElement(V1 + "DataVigente", dataVigente.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        return LgXml.MontarEnvelope(o, Operacao, filtro);
    }

    // ---------- Resposta ----------

    /// <summary>
    /// Lê uma página da resposta. SOAP Fault, CodigoDoErro/Mensagens preenchidos ou Tipo ≠ 0 viram
    /// <see cref="LgIntegracaoException"/> com a mensagem da LG.
    /// </summary>
    public static LgPagina LerResposta(string xml)
    {
        var p = LgXml.LerPagina(xml, Operacao, "ContratoDeTrabalhoParcial", LerContrato);
        return new LgPagina(p.Itens, p.NumeroDaPagina, p.QuantidadePorPagina, p.TotalDePaginas, p.TotalGeral);
    }

    private static LgContrato LerContrato(XElement c)
    {
        var json = ParaJson(c) as JsonObject ?? new JsonObject();
        return new LgContrato(
            LgXml.Texto(LgXml.Filho(c, "Matricula"))?.Trim() ?? string.Empty,
            LgXml.Texto(LgXml.Filho(LgXml.Filho(c, "Pessoa"), "Nome")),
            LgXml.Item(LgXml.Filho(c, "Empresa")),
            LgXml.Item(LgXml.Filho(c, "Cargo")),
            LgXml.Item(LgXml.Filho(c, "CentroDeCusto")),
            LgXml.Item(LgXml.Filho(c, "SituacaoDoColaborador")),
            LgXml.Item(LgXml.Filho(c, "OrgaoResponsavel")),
            LgXml.Data(LgXml.Filho(c, "DataAdmissao")),
            LgXml.Data(LgXml.Filho(c, "DataInicioSituacaoAtual")),
            json);
    }

    /// <summary>Elemento XML → JSON genérico (ver <see cref="LgXml.ParaJson"/>).</summary>
    public static JsonNode? ParaJson(XElement e) => LgXml.ParaJson(e);

    // ---------- HTTP ----------

    /// <summary>Uma página, com retry curto (backoff exponencial) para erro transitório.</summary>
    public Task<LgPagina> ConsultarPaginaAsync(int pagina, DateOnly dataVigente, CancellationToken ct) =>
        _core.ConsultarPaginaAsync(Operacao, pagina, o => MontarEnvelope(o, pagina, dataVigente), LerResposta, ct);

    /// <summary>Todas as páginas (PaginaAtual 1..TotalDePaginas). <paramref name="progresso"/>(página, total).</summary>
    public async Task<LgConsulta> ConsultarTodosAsync(DateOnly dataVigente, Action<int, int>? progresso, CancellationToken ct)
    {
        var consulta = await _core.ConsultarTodasAsync(Operacao, async (p, token) =>
            {
                var pagina = await ConsultarPaginaAsync(p, dataVigente, token);
                return new LgPaginaDe<LgContrato>(pagina.Contratos, pagina.NumeroDaPagina, pagina.QuantidadePorPagina,
                    pagina.TotalDePaginas, pagina.TotalGeral);
            },
            progresso, ct, $"vigente em {dataVigente:yyyy-MM-dd}");
        return new LgConsulta(consulta.Itens, consulta.Paginas, consulta.TotalGeral);
    }
}

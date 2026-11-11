using System.Text.Json.Nodes;
using System.Xml.Linq;
using Escala5x2.BuildingBlocks.Shared.Lg;
using Microsoft.Extensions.Options;

namespace Store.Api.Lg;

/// <summary>ValorDaInformacaoAdicional do centro de custo (ex.: Codigo 83, TipoDaEntidade 1000, Valor "0000000071").</summary>
public sealed record LgInformacaoAdicional(string? Codigo, string? TipoDaEntidade, string? Valor);

/// <summary>
/// CentroDeCustoCompleto da LG com os campos usados pela sincronização de lojas. <see cref="EmpresasHabilitadas"/>:
/// Habilitacoes/Empresas; <see cref="Json"/>: registro completo (para diagnóstico; não é gravado).
/// </summary>
public sealed record LgCentroDeCusto(
    string Codigo,
    string? Descricao,
    int? Status,
    DateOnly? DataInicio,
    DateOnly? DataFim,
    bool? PermiteCadastrarColaborador,
    string? CodigoDeIntegracao,
    IReadOnlyList<int> EmpresasHabilitadas,
    IReadOnlyList<LgInformacaoAdicional> InformacoesAdicionais,
    JsonObject Json);

/// <summary>Todos os centros de custo das empresas de LG:Empresas (sem repetir código), páginas somadas.</summary>
public sealed record LgConsultaCentros(IReadOnlyList<LgCentroDeCusto> Centros, IReadOnlyList<int> Empresas, int Paginas, int TotalLg);

/// <summary>
/// Cliente da operação ConsultarListaCentroDeCustoCompletoPorDemanda (filtro: PaginaAtual + Empresa/Codigo; uma consulta
/// por empresa de LG:Empresas). Transporte, autenticação, retry, erros e paginação: núcleo compartilhado
/// <see cref="LgSoapCore"/>/<see cref="LgXml"/>. Endpoint: LG:Operacoes:CentrosDeCusto:Url (vazio = LG:Url);
/// SOAPAction: LG:Operacoes:CentrosDeCusto:SoapAction. Credenciais nunca vão para o log.
/// </summary>
public sealed class LgCentrosDeCustoClient(HttpClient http, IOptionsMonitor<LgOptions> options, ILogger<LgCentrosDeCustoClient> logger)
{
    public static readonly LgOperacao Operacao = LgOperacao.CentrosDeCusto;
    private readonly LgSoapCore _core = new(http, options, logger);

    /// <summary>Espera entre tentativas (substituível nos testes).</summary>
    public Func<TimeSpan, CancellationToken, Task> Esperar
    {
        get => _core.Esperar;
        set => _core.Esperar = value;
    }

    /// <summary>Envelope de uma página (mesma ordem do exemplo validado com a LG: PaginaAtual, Empresa/Codigo).</summary>
    public static string MontarEnvelope(LgOptions o, int pagina, int empresa) =>
        LgXml.MontarEnvelope(o, Operacao,
        [
            new XElement(LgXml.V1 + "PaginaAtual", pagina),
            new XElement(LgXml.V1 + "Empresa", new XElement(LgXml.V1 + "Codigo", empresa))
        ]);

    /// <summary>Uma página da resposta. XML mal formado, SOAP Fault ou erro da LG → <see cref="LgIntegracaoException"/>.</summary>
    public static LgPaginaDe<LgCentroDeCusto> LerResposta(string xml) =>
        LgXml.LerPagina(xml, Operacao, "CentroDeCustoCompleto", LerCentro);

    private static LgCentroDeCusto LerCentro(XElement c)
    {
        var habilitacoes = LgXml.Filho(c, "Habilitacoes");
        var empresas = LgXml.Filho(habilitacoes, "Empresas") is { } e && !LgXml.Nulo(e)
            ? e.Elements().Select(x => LgXml.Inteiro(LgXml.Filho(x, "Codigo"))).OfType<int>().ToList()
            : [];
        var adicionais = LgXml.Filho(c, "ListaValorDaInformacaoAdicional") is { } l && !LgXml.Nulo(l)
            ? l.Elements().Where(x => x.Name.LocalName == "ValorDaInformacaoAdicional")
                .Select(x => new LgInformacaoAdicional(
                    LgXml.Texto(LgXml.Filho(x, "Codigo"))?.Trim(),
                    LgXml.Texto(LgXml.Filho(x, "TipoDaEntidade"))?.Trim(),
                    LgXml.Texto(LgXml.Filho(x, "Valor"))?.Trim()))
                .ToList()
            : [];
        return new LgCentroDeCusto(
            LgXml.Texto(LgXml.Filho(c, "Codigo"))?.Trim() ?? string.Empty,
            LgXml.Texto(LgXml.Filho(c, "Descricao"))?.Trim(),
            LgXml.Inteiro(LgXml.Filho(c, "Status")),
            LgXml.Data(LgXml.Filho(c, "DataInicio")),
            LgXml.Data(LgXml.Filho(c, "DataFim")),
            LgXml.Logico(LgXml.Filho(c, "PermiteCadastrarColaborador")),
            LgXml.Texto(LgXml.Filho(c, "CodigoDeIntegracao"))?.Trim(),
            empresas,
            adicionais,
            LgXml.ParaJson(c) as JsonObject ?? new JsonObject());
    }

    /// <summary>Uma página de uma empresa, com retry curto para erro transitório.</summary>
    public Task<LgPaginaDe<LgCentroDeCusto>> ConsultarPaginaAsync(int pagina, int empresa, CancellationToken ct) =>
        _core.ConsultarPaginaAsync(Operacao, pagina, o => MontarEnvelope(o, pagina, empresa), LerResposta, ct);

    /// <summary>
    /// Todas as páginas de todas as empresas de LG:Empresas. O mesmo centro de custo em mais de uma empresa vale uma vez
    /// (o primeiro). <paramref name="progresso"/>(etapa, página, total).
    /// </summary>
    public async Task<LgConsultaCentros> ConsultarTodosAsync(Action<string, int, int>? progresso, CancellationToken ct)
    {
        var o = options.CurrentValue;
        var empresas = o.EmpresasEfetivas.Distinct().ToList();
        var centros = new List<LgCentroDeCusto>();
        var vistos = new HashSet<string>(StringComparer.Ordinal);
        int paginas = 0, total = 0;
        foreach (var empresa in empresas)
        {
            var etapa = empresas.Count > 1 ? $"Consultando a LG (empresa {empresa})" : "Consultando a LG";
            progresso?.Invoke(etapa, 0, 0);
            var consulta = await _core.ConsultarTodasAsync(Operacao, (p, token) => ConsultarPaginaAsync(p, empresa, token),
                (p, t) => progresso?.Invoke(etapa, p, t), ct, $"empresa {empresa}");
            paginas += consulta.Paginas;
            total += consulta.Itens.Count;
            foreach (var c in consulta.Itens)
                if (c.Codigo.Length == 0 || vistos.Add(c.Codigo))
                    centros.Add(c);
        }
        return new LgConsultaCentros(centros, empresas, paginas, total);
    }
}

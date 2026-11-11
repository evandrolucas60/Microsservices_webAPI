using System.Xml.Linq;
using Escala5x2.BuildingBlocks.Shared.Lg;
using Microsoft.Extensions.Options;

namespace Store.Api.Lg;

/// <summary>
/// OrgaoResponsavelCompleto da LG (catálogo de setores da empresa): <see cref="Codigo"/> (null = sem código numérico),
/// <see cref="Descricao"/> como veio, <see cref="Status"/> (0 = ativo) e as empresas de ListaHabilitacaoPorEmpresas.
/// </summary>
public sealed record LgOrgaoResponsavel(int? Codigo, string? Descricao, int? Status, IReadOnlyList<int> EmpresasHabilitadas);

/// <summary>Catálogo de órgãos das empresas de LG:Empresas (sem repetir código), páginas somadas.</summary>
public sealed record LgConsultaOrgaos(IReadOnlyList<LgOrgaoResponsavel> Orgaos, IReadOnlyList<int> Empresas, int Paginas, int TotalLg);

/// <summary>
/// Cliente da operação ConsultarListaPorDemanda do serviço de órgãos responsáveis (namespace da operação
/// "lg.com.br/api/v1", diferente das demais, que são v2; filtro em "lg.com.br/api/dto/v1": PaginaAtual, CodigoDaEmpresa e
/// ConsultarListaValorDaInformacaoAdicional = 0). Uma consulta por empresa de LG:Empresas, páginas 1..TotalDePaginas;
/// o mesmo órgão vindo de mais de uma empresa vale uma vez (habilitações somadas). Endpoint:
/// LG:Operacoes:OrgaosResponsaveis:Url (vazio = LG:Url — em geral é outro .svc); SOAPAction:
/// LG:Operacoes:OrgaosResponsaveis:SoapAction (obrigatório). Credenciais nunca vão para o log.
/// </summary>
public sealed class LgOrgaosResponsaveisClient(HttpClient http, IOptionsMonitor<LgOptions> options, ILogger<LgOrgaosResponsaveisClient> logger)
{
    public static readonly LgOperacao Operacao = LgOperacao.OrgaosResponsaveis;
    private static readonly XNamespace Dto = LgXml.NamespaceDto(Operacao);
    private readonly LgSoapCore _core = new(http, options, logger);

    /// <summary>Espera entre tentativas (substituível nos testes).</summary>
    public Func<TimeSpan, CancellationToken, Task> Esperar
    {
        get => _core.Esperar;
        set => _core.Esperar = value;
    }

    /// <summary>Envelope de uma página (mesma ordem do exemplo da LG: PaginaAtual, CodigoDaEmpresa, ConsultarListaValorDaInformacaoAdicional).</summary>
    public static string MontarEnvelope(LgOptions o, int pagina, int empresa) =>
        LgXml.MontarEnvelope(o, Operacao,
        [
            new XElement(Dto + "PaginaAtual", pagina),
            new XElement(Dto + "CodigoDaEmpresa", empresa),
            // 0 = não traz as informações adicionais (não usadas pela Escala)
            new XElement(Dto + "ConsultarListaValorDaInformacaoAdicional", 0)
        ]);

    /// <summary>Uma página da resposta. XML mal formado, SOAP Fault ou erro da LG → <see cref="LgIntegracaoException"/>.</summary>
    public static LgPaginaDe<LgOrgaoResponsavel> LerResposta(string xml) =>
        LgXml.LerPagina(xml, Operacao, "OrgaoResponsavelCompleto", LerOrgao);

    private static LgOrgaoResponsavel LerOrgao(XElement e)
    {
        var empresas = LgXml.Filho(e, "ListaHabilitacaoPorEmpresas") is { } l && !LgXml.Nulo(l)
            ? l.Elements().Select(x => LgXml.Inteiro(LgXml.Filho(x, "Codigo"))).OfType<int>().ToList()
            : [];
        var descricao = LgXml.Texto(LgXml.Filho(e, "Descricao"))?.Trim();
        return new LgOrgaoResponsavel(
            LgXml.Inteiro(LgXml.Filho(e, "Codigo")),
            string.IsNullOrEmpty(descricao) ? null : descricao,
            LgXml.Inteiro(LgXml.Filho(e, "Status")),
            empresas);
    }

    /// <summary>Uma página de uma empresa, com retry curto para erro transitório.</summary>
    public Task<LgPaginaDe<LgOrgaoResponsavel>> ConsultarPaginaAsync(int pagina, int empresa, CancellationToken ct) =>
        _core.ConsultarPaginaAsync(Operacao, pagina, o => MontarEnvelope(o, pagina, empresa), LerResposta, ct);

    /// <summary>
    /// Todas as páginas de todas as empresas de LG:Empresas. Órgão repetido (mesmo código) vale uma vez: o primeiro com
    /// descrição, com as habilitações de todas as ocorrências. <paramref name="progresso"/>(etapa, página, total).
    /// </summary>
    public async Task<LgConsultaOrgaos> ConsultarTodosAsync(Action<string, int, int>? progresso, CancellationToken ct)
    {
        var o = options.CurrentValue;
        var empresas = o.EmpresasEfetivas.Distinct().ToList();
        var lidos = new List<LgOrgaoResponsavel>();
        int paginas = 0, total = 0;
        foreach (var empresa in empresas)
        {
            var etapa = empresas.Count > 1 ? $"Consultando os órgãos na LG (empresa {empresa})" : "Consultando os órgãos na LG";
            progresso?.Invoke(etapa, 0, 0);
            var consulta = await _core.ConsultarTodasAsync(Operacao, (p, token) => ConsultarPaginaAsync(p, empresa, token),
                (p, t) => progresso?.Invoke(etapa, p, t), ct, $"empresa {empresa}");
            paginas += consulta.Paginas;
            total += consulta.Itens.Count;
            lidos.AddRange(consulta.Itens);
        }
        return new LgConsultaOrgaos(Deduplicar(lidos), empresas, paginas, total);
    }

    /// <summary>Um órgão por código (sem código: mantidos para o resumo de ignorados), habilitações somadas.</summary>
    public static IReadOnlyList<LgOrgaoResponsavel> Deduplicar(IEnumerable<LgOrgaoResponsavel> orgaos)
    {
        var lista = orgaos.ToList();
        var semCodigo = lista.Where(x => x.Codigo is null);
        var porCodigo = lista.Where(x => x.Codigo is not null).GroupBy(x => x.Codigo!.Value).Select(g =>
        {
            var primeiro = g.FirstOrDefault(x => x.Descricao is not null) ?? g.First();
            return primeiro with { EmpresasHabilitadas = g.SelectMany(x => x.EmpresasHabilitadas).Distinct().ToList() };
        });
        return porCodigo.Concat(semCodigo).ToList();
    }
}

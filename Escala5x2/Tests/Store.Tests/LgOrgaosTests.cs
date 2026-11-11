using System.Xml.Linq;
using Escala5x2.BuildingBlocks.Shared.Demo;
using Escala5x2.BuildingBlocks.Shared.Lg;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Store.Api.Data;
using Store.Api.Domain;
using Store.Api.Lg;
using Xunit;

namespace Store.Tests;

file sealed class Monitor(LgOptions value) : IOptionsMonitor<LgOptions>
{
    public LgOptions CurrentValue => value;
    public LgOptions Get(string? name) => value;
    public IDisposable? OnChange(Action<LgOptions, string?> listener) => null;
}

/// <summary>Catálogo de órgãos responsáveis (ConsultarListaPorDemanda) → setores de todas as lojas.</summary>
public class LgOrgaosTests
{
    private static readonly DateTime Agora = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
    private static readonly int[] Empresa1 = [1];

    private static LgOptions Configurada() => new()
    {
        Url = "https://lg.exemplo.invalid/ServicoDeContratoDeTrabalho.svc",
        Operacoes = new LgOperacoesOptions
        {
            OrgaosResponsaveis = new LgOperacaoOptions { Url = "https://lg.exemplo.invalid/ServicoDeOrgaoResponsavel.svc", SoapAction = LgOrgaosFixtures.SoapAction }
        },
        Usuario = "usuario-teste",
        Senha = "senha-teste",
        GuidTenant = "00000000-0000-0000-0000-000000000001",
        EsperaInicialMs = 0
    };

    private static LgOrgaosResponsaveisClient Cliente(FakeLgHandler handler, LgOptions? o = null) =>
        new(new HttpClient(handler), new Monitor(o ?? Configurada()), NullLogger<LgOrgaosResponsaveisClient>.Instance)
        {
            Esperar = (_, _) => Task.CompletedTask
        };

    private static IReadOnlyList<LgOrgaoResponsavel> Orgaos(params string[] xml) =>
        LgOrgaosResponsaveisClient.LerResposta(LgOrgaosFixtures.Resposta(1, 1, xml.Length, xml)).Itens;

    private static LgCatalogoSetores Catalogo(params string[] xml) => LgSetoresCatalogoRegras.Filtrar(Orgaos(xml), Empresa1);

    private static Api.Domain.Store Loja(string code, bool active = true) => new() { Code = code, Name = $"VAREJO LOJA {code}", Active = active };

    // ---------- Parse da resposta ----------

    [Fact]
    public void Le_os_quatro_orgaos_com_namespace_v1_paginacao_e_nil()
    {
        var pagina = LgOrgaosResponsaveisClient.LerResposta(LgOrgaosFixtures.Resposta(1, 2, 63, LgOrgaosFixtures.Exemplo));

        Assert.Equal(4, pagina.Itens.Count);
        Assert.Equal(1, pagina.NumeroDaPagina);
        Assert.Equal(50, pagina.QuantidadePorPagina);
        Assert.Equal(2, pagina.TotalDePaginas);
        Assert.Equal(63, pagina.TotalGeral);
        Assert.Equal([1, 2, 3, 4], pagina.Itens.Select(o => o.Codigo!.Value));
        Assert.Equal(["GERENCIA", "FRENTE DE LOJA", "AÇOUGUE", "FRIOS"], pagina.Itens.Select(o => o.Descricao));
        Assert.All(pagina.Itens, o =>
        {
            Assert.Equal(0, o.Status);
            // O Codigo do TipoDeLogradouro (0, dentro de Endereco) não é confundido com o do órgão; CodigoCnae vazio é ignorado
            Assert.Equal(LgOrgaosFixtures.EmpresasExemplo, o.EmpresasHabilitadas);
        });
    }

    [Fact]
    public void Resposta_sem_o_result_da_operacao_vira_erro_claro()
    {
        var ex = Assert.Throws<LgIntegracaoException>(() => LgOrgaosResponsaveisClient.LerResposta(LgCentrosFixtures.Resposta(1, 1, 0)));
        Assert.Contains("ConsultarListaPorDemandaResult", ex.Message);
        Assert.Contains("OrgaosResponsaveis", ex.Message);
    }

    // ---------- Envelope, SOAPAction, URL e paginação ----------

    [Fact]
    public void Envelope_com_namespace_v1_codigo_da_empresa_e_sem_informacao_adicional()
    {
        var texto = LgOrgaosResponsaveisClient.MontarEnvelope(Configurada(), 2, 4);
        var xml = XDocument.Parse(texto);
        var header = xml.Root!.Element(LgXml.Soap + "Header")!;
        Assert.Equal("senha-teste", header.Descendants(LgXml.Dto + "Senha").Single().Value);
        Assert.Equal("705", header.Descendants(LgXml.Dto + "Ambiente").Single().Value);
        // Prefixos como no exemplo da LG: v1 = api/v1 (operação), v11 = api/dto/v1 (filtro)
        Assert.Equal("lg.com.br/api/v1", xml.Root!.Attribute(XNamespace.Xmlns + "v1")!.Value);
        Assert.Equal("lg.com.br/api/dto/v1", xml.Root!.Attribute(XNamespace.Xmlns + "v11")!.Value);

        var body = xml.Root!.Element(LgXml.Soap + "Body")!;
        Assert.DoesNotContain("senha-teste", body.ToString());
        Assert.Null(body.Element(LgXml.V2 + "ConsultarListaPorDemanda"));
        var filtro = body.Element(LgXml.ApiV1 + "ConsultarListaPorDemanda")!.Element(LgXml.ApiV1 + "filtro")!;
        Assert.Equal(["PaginaAtual", "CodigoDaEmpresa", "ConsultarListaValorDaInformacaoAdicional"], filtro.Elements().Select(e => e.Name.LocalName));
        Assert.All(filtro.Elements(), e => Assert.Equal(LgXml.V1, e.Name.Namespace));
        Assert.Equal("2", filtro.Element(LgXml.V1 + "PaginaAtual")!.Value);
        Assert.Equal("4", filtro.Element(LgXml.V1 + "CodigoDaEmpresa")!.Value);
        Assert.Equal("0", filtro.Element(LgXml.V1 + "ConsultarListaValorDaInformacaoAdicional")!.Value);
    }

    [Fact]
    public void Operacoes_antigas_continuam_em_v2()
    {
        var xml = XDocument.Parse(LgCentrosDeCustoClient.MontarEnvelope(Configurada(), 1, 1));
        Assert.Equal("lg.com.br/api/v2", xml.Root!.Attribute(XNamespace.Xmlns + "v2")!.Value);
        Assert.Equal("lg.com.br/api/dto/v1", xml.Root!.Attribute(XNamespace.Xmlns + "v1")!.Value);
        Assert.NotNull(xml.Descendants(LgXml.V2 + "ConsultarListaCentroDeCustoCompletoPorDemanda").SingleOrDefault());
    }

    [Fact]
    public async Task Percorre_as_paginas_de_cada_empresa_com_soapaction_e_deduplica_por_codigo()
    {
        var o = Configurada();
        o.Empresas = [1, 5];
        o.Operacoes.OrgaosResponsaveis.Url = ""; // vazio = LG:Url
        var handler = new FakeLgHandler((_, body, _) =>
        {
            var doc = XDocument.Parse(body);
            var pagina = int.Parse(doc.Descendants(LgXml.V1 + "PaginaAtual").Single().Value);
            var empresa = int.Parse(doc.Descendants(LgXml.V1 + "CodigoDaEmpresa").Single().Value);
            return FakeLgHandler.Xml((empresa, pagina) switch
            {
                (1, 1) => LgOrgaosFixtures.Resposta(1, 2, 63, LgOrgaosFixtures.Exemplo),
                (1, _) => LgOrgaosFixtures.Resposta(2, 2, 63, LgOrgaosFixtures.Orgao(25, "DEPOSITO")),
                // A empresa 5 devolve de novo o órgão 2 (habilitações somadas) e um novo
                _ => LgOrgaosFixtures.Resposta(1, 1, 2, LgOrgaosFixtures.Orgao(2, "FRENTE DE LOJA", empresas: [5]),
                    LgOrgaosFixtures.Orgao(13, "TELEFONIA", empresas: [5]))
            });
        });
        var etapas = new List<(string, int, int)>();

        var consulta = await Cliente(handler, o).ConsultarTodosAsync((e, p, t) => etapas.Add((e, p, t)), CancellationToken.None);

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(3, consulta.Paginas);
        Assert.Equal(7, consulta.TotalLg);
        Assert.Equal([1, 2, 3, 4, 25, 13], consulta.Orgaos.Select(x => x.Codigo!.Value));
        Assert.Contains(5, consulta.Orgaos.Single(x => x.Codigo == 2).EmpresasHabilitadas);
        Assert.Contains(1, consulta.Orgaos.Single(x => x.Codigo == 2).EmpresasHabilitadas);
        Assert.All(handler.Requests, r =>
        {
            Assert.Equal($"\"{LgOrgaosFixtures.SoapAction}\"", r.SoapAction);
            Assert.Equal(o.Url, r.Url?.ToString());
        });
        Assert.Contains(("Consultando os órgãos na LG (empresa 1)", 2, 2), etapas);
    }

    [Fact]
    public async Task Sem_soapaction_dos_orgaos_nao_chama_a_LG()
    {
        var o = Configurada();
        o.Operacoes.OrgaosResponsaveis.SoapAction = "";
        o.SoapAction = "acao-dos-colaboradores";
        var handler = new FakeLgHandler((_, _, _) => throw new InvalidOperationException("não deveria chamar"));
        var ex = await Assert.ThrowsAsync<LgIntegracaoException>(() => Cliente(handler, o).ConsultarPaginaAsync(1, 1, CancellationToken.None));
        Assert.Contains("LG:Operacoes:OrgaosResponsaveis:SoapAction", ex.Message);
        Assert.Empty(handler.Requests);
        Assert.Equal("https://lg.exemplo.invalid/ServicoDeOrgaoResponsavel.svc", o.UrlDe(LgOperacao.OrgaosResponsaveis));
    }

    [Fact]
    public async Task Falha_na_LG_nao_grava_nada()
    {
        // Banco inalcançável: se a sincronização tocasse o banco antes de ler a LG inteira, o erro seria de conexão
        var db = new StoreDbContext(new DbContextOptionsBuilder<StoreDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=nada;Username=x;Password=x;Timeout=1").Options);
        var handler = new FakeLgHandler((_, _, _) => FakeLgHandler.Xml(
            """<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><s:Fault><faultcode>s:Client</faultcode><faultstring>Usuário inválido</faultstring></s:Fault></s:Body></s:Envelope>""",
            System.Net.HttpStatusCode.InternalServerError));
        var o = Configurada();
        var sincronizador = new LgSetoresSincronizador(db, Cliente(handler, o),
            new LgSetoresService(db, null!, NullLogger<LgSetoresService>.Instance), new Monitor(o), NullLogger<LgSetoresSincronizador>.Instance);

        var ex = await Assert.ThrowsAsync<LgIntegracaoException>(() => sincronizador.SincronizarAsync("Admin Teste", null, CancellationToken.None));
        Assert.Contains("Usuário inválido", ex.Message);
        Assert.Single(handler.Requests); // SOAP Fault não é transitório
        Assert.Empty(db.ChangeTracker.Entries());
    }

    // ---------- Catálogo → setores de todas as lojas ----------

    [Fact]
    public void Garante_um_setor_por_orgao_em_cada_loja_ativa_sem_duplicar_e_sem_tocar_na_DEMO()
    {
        var catalogo = Catalogo(LgOrgaosFixtures.Exemplo);
        var demo = new Api.Domain.Store { Id = DemoData.StoreId, Code = "DEMO", Name = "Loja Demonstração" };
        var lojas = new[] { Loja("01"), Loja("02"), Loja("09", active: false), demo };
        var alvo = lojas.Where(LgSetoresCatalogoRegras.LojaAlvo).ToList();
        Assert.Equal(["01", "02"], alvo.Select(l => l.Code));

        var setores = alvo.ToDictionary(l => l.Id, _ => new List<Sector>());
        foreach (var l in alvo)
        {
            var r = LgSetoresCatalogoRegras.AplicarNaLoja(catalogo, setores[l.Id], l.Id, Agora);
            Assert.Equal(4, r.Criados);
            Assert.Equal(4, r.Novos.Count);
        }
        Assert.All(setores.Values, lista =>
        {
            Assert.Equal(["AÇOUGUE", "FRENTE DE LOJA", "FRIOS", "GERENCIA"], lista.Select(s => s.Name).Order());
            Assert.All(lista, s => Assert.True(s.Active && s.Origem == SectorOrigem.Lg));
        });
        // Setores distintos por loja (o órgão é da empresa; o setor é da loja)
        Assert.NotEqual(setores[alvo[0].Id][0].Id, setores[alvo[1].Id][0].Id);

        // Segunda execução: idempotente
        foreach (var l in alvo)
        {
            var r = LgSetoresCatalogoRegras.AplicarNaLoja(catalogo, setores[l.Id], l.Id, Agora.AddDays(1));
            Assert.Empty(r.Novos);
            Assert.Empty(r.Alteracoes);
            Assert.False(r.Mudou);
            Assert.Equal(4, setores[l.Id].Count);
        }
        Assert.Empty(demo.Sectors);
    }

    [Fact]
    public void Adota_o_setor_manual_com_o_mesmo_nome()
    {
        var loja = Guid.NewGuid();
        var manual = new Sector { StoreId = loja, Name = "Açougue" };
        var setores = new List<Sector> { manual };

        var r = LgSetoresCatalogoRegras.AplicarNaLoja(Catalogo(LgOrgaosFixtures.Exemplo), setores, loja, Agora);

        Assert.Equal(1, r.Adotados);
        Assert.Equal(3, r.Criados);
        Assert.Equal(4, setores.Count);
        Assert.Equal(3, manual.LgOrgaoCodigo);
        Assert.Equal("Açougue", manual.Name);
        Assert.Equal(SectorOrigem.Manual, manual.Origem);
    }

    [Fact]
    public void Orgao_com_status_inativo_inativa_o_setor_da_LG_e_reativa_quando_volta()
    {
        var loja = Guid.NewGuid();
        var setores = new List<Sector>();
        LgSetoresCatalogoRegras.AplicarNaLoja(Catalogo(LgOrgaosFixtures.Exemplo), setores, loja, Agora);
        var frios = setores.Single(s => s.LgOrgaoCodigo == 4);

        var inativo = Catalogo(LgOrgaosFixtures.Orgao(1, "GERENCIA"), LgOrgaosFixtures.Orgao(2, "FRENTE DE LOJA"),
            LgOrgaosFixtures.Orgao(3, "AÇOUGUE"), LgOrgaosFixtures.Orgao(4, "FRIOS", status: 1));
        Assert.Contains(inativo.Ignorados, i => i.Codigo == 4 && i.Motivo.Contains("Status 1"));
        var r = LgSetoresCatalogoRegras.AplicarNaLoja(inativo, setores, loja, Agora);

        Assert.False(frios.Active);
        Assert.Equal(1, r.Inativados);
        Assert.Equal(4, setores.Count); // nunca apaga
        Assert.Equal(("inativado", frios), (r.Alteracoes.Single().Acao, r.Alteracoes.Single().Setor));
        // Segunda vez inativo: nada muda
        Assert.Empty(LgSetoresCatalogoRegras.AplicarNaLoja(inativo, setores, loja, Agora).Alteracoes);

        var r2 = LgSetoresCatalogoRegras.AplicarNaLoja(Catalogo(LgOrgaosFixtures.Exemplo), setores, loja, Agora);
        Assert.True(frios.Active);
        Assert.Equal(1, r2.Reativados);
        Assert.Equal(4, setores.Count);
    }

    [Fact]
    public void Orgao_que_sumiu_do_catalogo_inativa_so_o_setor_da_LG()
    {
        var loja = Guid.NewGuid();
        var manualAdotado = new Sector { StoreId = loja, Name = "Gerência" };
        var manualPuro = new Sector { StoreId = loja, Name = "Hortifruti" };
        var setores = new List<Sector> { manualAdotado, manualPuro };
        LgSetoresCatalogoRegras.AplicarNaLoja(Catalogo(LgOrgaosFixtures.Exemplo), setores, loja, Agora);
        Assert.Equal(1, manualAdotado.LgOrgaoCodigo);

        // Só o órgão 2 continua no catálogo
        var r = LgSetoresCatalogoRegras.AplicarNaLoja(Catalogo(LgOrgaosFixtures.Orgao(2, "FRENTE DE LOJA")), setores, loja, Agora);

        Assert.Equal(2, r.Inativados); // AÇOUGUE e FRIOS (criados pela LG)
        Assert.False(setores.Single(s => s.LgOrgaoCodigo == 3).Active);
        Assert.False(setores.Single(s => s.LgOrgaoCodigo == 4).Active);
        Assert.True(setores.Single(s => s.LgOrgaoCodigo == 2).Active);
        Assert.True(manualAdotado.Active); // setor manual nunca é inativado pela LG
        Assert.True(manualPuro.Active);
        Assert.Equal(5, setores.Count);
    }

    [Fact]
    public void So_vira_setor_o_orgao_habilitado_para_a_empresa()
    {
        var loja = Guid.NewGuid();
        var setores = new List<Sector>();
        var catalogo = Catalogo(LgOrgaosFixtures.Orgao(2, "FRENTE DE LOJA"), LgOrgaosFixtures.Orgao(30, "INDUSTRIA", empresas: [2, 4]),
            LgOrgaosFixtures.Orgao(31, "SEM HABILITACAO", empresas: []));

        var r = LgSetoresCatalogoRegras.AplicarNaLoja(catalogo, setores, loja, Agora);

        Assert.Equal("FRENTE DE LOJA", Assert.Single(setores).Name);
        Assert.Equal(1, r.Criados);
        Assert.Equal([30, 31], catalogo.Ignorados.Select(i => i.Codigo!.Value));
        Assert.All(catalogo.Ignorados, i => Assert.Contains("sem habilitação", i.Motivo));

        // Setor de um órgão que perdeu a habilitação é inativado
        var industria = new Sector { StoreId = loja, Name = "INDUSTRIA", Origem = SectorOrigem.Lg, LgOrgaoCodigo = 30, LgOrgaoDescricao = "INDUSTRIA" };
        setores.Add(industria);
        LgSetoresCatalogoRegras.AplicarNaLoja(catalogo, setores, loja, Agora);
        Assert.False(industria.Active);
    }

    [Fact]
    public void Orgao_sem_descricao_nao_cria_setor_mas_nao_inativa_o_existente()
    {
        var loja = Guid.NewGuid();
        var existente = new Sector { StoreId = loja, Name = "TELEFONIA", Origem = SectorOrigem.Lg, LgOrgaoCodigo = 13, LgOrgaoDescricao = "TELEFONIA" };
        var setores = new List<Sector> { existente };
        var catalogo = Catalogo(LgOrgaosFixtures.Orgao(13, " "), LgOrgaosFixtures.Orgao(14, ""));

        var r = LgSetoresCatalogoRegras.AplicarNaLoja(catalogo, setores, loja, Agora);

        Assert.Single(setores);
        Assert.True(existente.Active);
        Assert.Equal("TELEFONIA", existente.Name);
        Assert.Empty(r.Alteracoes);
        Assert.Equal(2, catalogo.Ignorados.Count(i => i.Motivo.Contains("sem descrição")));
    }
}

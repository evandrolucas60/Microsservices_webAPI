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

public class LgLojasTests
{
    private const string SoapActionCentros = "lg.com.br/api/v2/ServicoDeCentroDeCusto/ConsultarListaCentroDeCustoCompletoPorDemanda";
    private static readonly DateOnly Hoje = new(2026, 10, 30);
    private static readonly DateTime Agora = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

    private static LgOptions Configurada() => new()
    {
        Url = "https://lg.exemplo.invalid/ServicoDeContratoDeTrabalho.svc",
        Operacoes = new LgOperacoesOptions
        {
            CentrosDeCusto = new LgOperacaoOptions { Url = "https://lg.exemplo.invalid/ServicoDeCentroDeCusto.svc", SoapAction = SoapActionCentros }
        },
        Usuario = "usuario-teste",
        Senha = "senha-teste",
        GuidTenant = "00000000-0000-0000-0000-000000000001",
        EsperaInicialMs = 0
    };

    private static LgCentrosDeCustoClient Cliente(FakeLgHandler handler, LgOptions? o = null) =>
        new(new HttpClient(handler), new Monitor(o ?? Configurada()), NullLogger<LgCentrosDeCustoClient>.Instance)
        {
            Esperar = (_, _) => Task.CompletedTask
        };

    private static IReadOnlyList<LgCentroDeCusto> Centros(params string[] xml) =>
        LgCentrosDeCustoClient.LerResposta(LgCentrosFixtures.Resposta(1, 1, xml.Length, xml)).Itens;

    private static LgLojasResultado Aplicar(IReadOnlyList<LgCentroDeCusto> centros, List<Api.Domain.Store> lojas, LgLojasOptions? o = null) =>
        LgLojasRegras.Aplicar(centros, lojas, o ?? new LgLojasOptions(), Hoje, "Admin Teste", Agora);

    // ---------- Parse da resposta ----------

    [Fact]
    public void Le_os_quatro_centros_com_paginacao_nil_e_informacao_adicional()
    {
        var pagina = LgCentrosDeCustoClient.LerResposta(LgCentrosFixtures.Resposta(1, 2, 84, LgCentrosFixtures.Exemplo));

        Assert.Equal(4, pagina.Itens.Count);
        Assert.Equal(1, pagina.NumeroDaPagina);
        Assert.Equal(50, pagina.QuantidadePorPagina);
        Assert.Equal(2, pagina.TotalDePaginas);
        Assert.Equal(84, pagina.TotalGeral);
        Assert.Equal(["000001", "000010401", "001020100", "201000"], pagina.Itens.Select(c => c.Codigo));
        Assert.Equal("RESIDÊNCIA", pagina.Itens[1].Descricao);

        var cabo = pagina.Itens[3];
        Assert.Equal("VAREJO LOJA 01 - CABO", cabo.Descricao);
        Assert.Equal(0, cabo.Status);
        Assert.Null(cabo.DataFim);
        Assert.Null(cabo.CodigoDeIntegracao);
        Assert.Equal(new DateOnly(1900, 1, 1), cabo.DataInicio);
        Assert.True(cabo.PermiteCadastrarColaborador);
        Assert.Equal([4, 2, 7, 103, 105, 1, 6, 104, 102, 106], cabo.EmpresasHabilitadas);
        var adicional = Assert.Single(cabo.InformacoesAdicionais);
        Assert.Equal(new LgInformacaoAdicional("83", "1000", "0000000071"), adicional);
        Assert.Empty(pagina.Itens[0].InformacoesAdicionais); // i:nil
        Assert.Null(cabo.Json["Empresa"]);
    }

    [Fact]
    public void Xml_mal_formado_vira_erro_claro()
    {
        var ex = Assert.Throws<LgIntegracaoException>(() => LgCentrosDeCustoClient.LerResposta(LgCentrosFixtures.RespostaMalFormada()));
        Assert.StartsWith("Resposta inválida da LG", ex.Message);
        Assert.False(ex.Transitorio);
    }

    [Fact]
    public async Task Xml_mal_formado_nao_grava_nada()
    {
        // Banco inalcançável: se a sincronização tocasse o banco antes de ler a LG inteira, o erro seria de conexão
        var db = new StoreDbContext(new DbContextOptionsBuilder<StoreDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=nada;Username=x;Password=x;Timeout=1").Options);
        var handler = new FakeLgHandler((_, _, _) => FakeLgHandler.Xml(LgCentrosFixtures.RespostaMalFormada()));
        var o = Configurada();
        var sincronizador = new LgLojasSincronizador(db, Cliente(handler, o), null!, new Monitor(o), null!, NullLogger<LgLojasSincronizador>.Instance);

        var ex = await Assert.ThrowsAsync<LgIntegracaoException>(() => sincronizador.SincronizarAsync("Admin Teste", null, CancellationToken.None));
        Assert.StartsWith("Resposta inválida da LG", ex.Message);
        Assert.Single(handler.Requests); // XML inválido não é transitório: não repete
        Assert.Empty(db.ChangeTracker.Entries());
    }

    // ---------- Envelope, SOAPAction, URL e paginação ----------

    [Fact]
    public void Envelope_com_credenciais_no_header_e_empresa_no_filtro()
    {
        var xml = XDocument.Parse(LgCentrosDeCustoClient.MontarEnvelope(Configurada(), 2, 1));
        var header = xml.Root!.Element(LgXml.Soap + "Header")!;
        Assert.Equal("senha-teste", header.Descendants(LgXml.Dto + "Senha").Single().Value);
        Assert.Equal("705", header.Descendants(LgXml.Dto + "Ambiente").Single().Value);
        var body = xml.Root!.Element(LgXml.Soap + "Body")!;
        Assert.DoesNotContain("senha-teste", body.ToString());
        var filtro = body.Element(LgXml.V2 + "ConsultarListaCentroDeCustoCompletoPorDemanda")!.Element(LgXml.V2 + "filtro")!;
        Assert.Equal(["PaginaAtual", "Empresa"], filtro.Elements().Select(e => e.Name.LocalName));
        Assert.Equal("2", filtro.Element(LgXml.V1 + "PaginaAtual")!.Value);
        Assert.Equal("1", filtro.Element(LgXml.V1 + "Empresa")!.Element(LgXml.V1 + "Codigo")!.Value);
    }

    [Fact]
    public async Task Percorre_as_paginas_de_cada_empresa_com_url_e_soapaction_da_operacao()
    {
        var o = Configurada();
        o.Empresas = [1, 5];
        var handler = new FakeLgHandler((_, body, _) =>
        {
            var doc = XDocument.Parse(body);
            var pagina = int.Parse(doc.Descendants(LgXml.V1 + "PaginaAtual").Single().Value);
            var empresa = int.Parse(doc.Descendants(LgXml.V1 + "Empresa").Single().Value);
            return FakeLgHandler.Xml((empresa, pagina) switch
            {
                (1, 1) => LgCentrosFixtures.Resposta(1, 2, 84, LgCentrosFixtures.Exemplo),
                (1, _) => LgCentrosFixtures.Resposta(2, 2, 84, LgCentrosFixtures.Centro("202000", "VAREJO LOJA 02 - COHAB")),
                // A empresa 5 devolve de novo um centro já visto (habilitado nas duas): vale uma vez
                _ => LgCentrosFixtures.Resposta(1, 1, 2, LgCentrosFixtures.Centro("201000", "VAREJO LOJA 01 - CABO"),
                    LgCentrosFixtures.Centro("305000", "ATACAREJO LOJA 05 - ARCO VITA"))
            });
        });
        var etapas = new List<(string, int, int)>();

        var consulta = await Cliente(handler, o).ConsultarTodosAsync((e, p, t) => etapas.Add((e, p, t)), CancellationToken.None);

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(3, consulta.Paginas);
        Assert.Equal(7, consulta.TotalLg);
        Assert.Equal(["000001", "000010401", "001020100", "201000", "202000", "305000"], consulta.Centros.Select(c => c.Codigo));
        Assert.All(handler.Requests, r =>
        {
            Assert.Equal($"\"{SoapActionCentros}\"", r.SoapAction);
            Assert.Equal("https://lg.exemplo.invalid/ServicoDeCentroDeCusto.svc", r.Url?.ToString());
        });
        Assert.Contains(("Consultando a LG (empresa 1)", 2, 2), etapas);
    }

    [Fact]
    public async Task Sem_soapaction_dos_centros_nao_chama_a_LG()
    {
        var o = Configurada();
        o.Operacoes.CentrosDeCusto.SoapAction = "";
        o.SoapAction = "acao-dos-colaboradores"; // a de colaboradores não vale para centros de custo
        var handler = new FakeLgHandler((_, _, _) => throw new InvalidOperationException("não deveria chamar"));
        var ex = await Assert.ThrowsAsync<LgIntegracaoException>(() => Cliente(handler, o).ConsultarPaginaAsync(1, 1, CancellationToken.None));
        Assert.Contains("LG:Operacoes:CentrosDeCusto:SoapAction", ex.Message);
        Assert.Empty(handler.Requests);
        Assert.True(o.Configurada); // colaboradores continuam configurados (LG:SoapAction)
    }

    [Fact]
    public void Url_da_operacao_vazia_usa_a_geral()
    {
        var o = Configurada();
        o.Operacoes.CentrosDeCusto.Url = "";
        Assert.Equal(o.Url, o.UrlDe(LgOperacao.CentrosDeCusto));
        o.Operacoes.Colaboradores.SoapAction = "nova";
        o.SoapAction = "antiga";
        Assert.Equal("nova", o.SoapActionDe(LgOperacao.Colaboradores));
    }

    // ---------- Quais centros são lojas ----------

    [Fact]
    public void So_o_centro_de_varejo_vira_loja_01_com_o_nome_completo()
    {
        var centros = LgCentrosDeCustoClient.LerResposta(LgCentrosFixtures.Resposta(1, 2, 84, LgCentrosFixtures.Exemplo)).Itens;
        var demo = new Api.Domain.Store { Id = DemoData.StoreId, Code = "DEMO", Name = "Loja Demonstração", City = "Cabo de Santo Agostinho" };
        var lojas = new List<Api.Domain.Store> { demo };

        var r = Aplicar(centros, lojas);

        var loja = Assert.Single(r.Novas);
        Assert.Equal("01", loja.Code);
        Assert.Equal("VAREJO LOJA 01 - CABO", loja.Name);
        Assert.Equal("201000", loja.LgCentroDeCustoCodigo);
        Assert.Equal(StoreFormat.Varejo, loja.Format);
        Assert.True(loja.Active);
        Assert.Equal(string.Empty, loja.City);
        Assert.Equal(1, r.Criadas);
        Assert.Equal(1, r.Lojas);
        Assert.Equal(["000001", "000010401", "001020100"], r.Ignorados.Select(i => i.Codigo));
        Assert.All(r.Ignorados, i => Assert.Contains("fora do padrão", i.Motivo));
        Assert.Empty(r.Conflitos);
        var ev = Assert.Single(r.Eventos);
        Assert.Equal("criada", ev.Acao);
        // DEMO intacta
        Assert.Equal("Loja Demonstração", demo.Name);
        Assert.Null(demo.LgCentroDeCustoCodigo);
    }

    [Theory]
    [InlineData("VAREJO LOJA 01 - CABO", "01")]
    [InlineData("Varejo Loja 27 Nossa Senhora do Ó", "27")]
    [InlineData("ATACAREJO LOJA 05 - ARCO VITA", "05")]
    [InlineData("ATACADO LOJA 3", "3")]
    [InlineData("PRESIDENCIA", null)]
    [InlineData("RESIDÊNCIA", null)]
    [InlineData("INDUSTRIA ALIMENTICIA SELO REAL LTDA EPP", null)]
    [InlineData("DEPOSITO LOJA 02", null)]
    [InlineData("VAREJOS LOJA 02", null)]
    public void Padrao_padrao_de_loja(string descricao, string? numero) =>
        Assert.Equal(numero, LgLojasRegras.NumeroDaLoja(descricao, LgLojasRegras.Padrao(LgLojasOptions.PadraoPadrao)));

    [Theory]
    [InlineData("VAREJO LOJA 01 - CABO", StoreFormat.Varejo)]
    [InlineData("ATACAREJO LOJA 05 - ARCO VITA", StoreFormat.Atacarejo)]
    [InlineData("atacado loja 9", StoreFormat.Atacado)]
    [InlineData("CENTRO DE DISTRIBUICAO", null)]
    public void Formato_pela_primeira_palavra(string descricao, StoreFormat? esperado) =>
        Assert.Equal(esperado, LgLojasRegras.Formato(descricao));

    [Theory]
    [InlineData("1", "01")]
    [InlineData("05", "05")]
    [InlineData("27", "27")]
    [InlineData("123", "123")]
    public void Codigo_com_dois_digitos(string numero, string code) => Assert.Equal(code, LgLojasRegras.CodigoDaLoja(numero));

    [Fact]
    public void Padrao_invalido_vira_erro_de_configuracao()
    {
        var ex = Assert.Throws<LgIntegracaoException>(() => LgLojasRegras.Padrao("LOJA (?<numero>"));
        Assert.Contains("LG:Lojas:Padrao", ex.Message);
    }

    // ---------- Conflito e mapeamento ----------

    [Fact]
    public void Mesmo_numero_em_varejo_e_atacarejo_e_conflito_sem_duplicar()
    {
        var centros = Centros(LgCentrosFixtures.Centro("205000", "VAREJO LOJA 05 - CAMPO GRANDE"),
            LgCentrosFixtures.Centro("305000", "ATACAREJO LOJA 05 - ARCO VITA"));
        var semeada = new Api.Domain.Store { Code = "05", Name = "CAMPO GRANDE" };
        var lojas = new List<Api.Domain.Store> { semeada };

        var r = Aplicar(centros, lojas);

        Assert.Empty(r.Novas);
        Assert.Single(lojas);
        Assert.Null(semeada.LgCentroDeCustoCodigo); // nada aplicado até o mapeamento
        var conflito = Assert.Single(r.Conflitos);
        Assert.Equal("05", conflito.CodigoLoja);
        Assert.Equal(2, conflito.CentrosDeCusto.Count);
        Assert.Contains("LG:Lojas:Mapeamento", conflito.Mensagem);

        // Com o mapeamento manual do atacarejo: a 05 adota o varejo e o atacarejo vira a 35
        var o = new LgLojasOptions { Mapeamento = new() { ["305000"] = "35" } };
        var r2 = Aplicar(centros, lojas, o);
        Assert.Empty(r2.Conflitos);
        Assert.Equal("205000", semeada.LgCentroDeCustoCodigo);
        Assert.Equal("VAREJO LOJA 05 - CAMPO GRANDE", semeada.Name);
        var nova = Assert.Single(r2.Novas);
        Assert.Equal("35", nova.Code);
        Assert.Equal(StoreFormat.Atacarejo, nova.Format);
        Assert.Equal("305000", nova.LgCentroDeCustoCodigo);

        // Depois de vinculado, um terceiro centro com o mesmo número não tira a loja de quem já é dono
        var r3 = Aplicar(Centros(LgCentrosFixtures.Centro("205000", "VAREJO LOJA 05 - CAMPO GRANDE"),
            LgCentrosFixtures.Centro("405000", "ATACADO LOJA 05")), lojas, o);
        Assert.Equal(1, r3.Inalteradas);
        Assert.Equal(["405000 ATACADO LOJA 05"], Assert.Single(r3.Conflitos).CentrosDeCusto);
    }

    [Fact]
    public void Mapeamento_vazio_exclui_e_DEMO_e_reservado()
    {
        var centros = Centros(LgCentrosFixtures.Centro("201000", "VAREJO LOJA 01 - CABO"), LgCentrosFixtures.Centro("900100", "CENTRO DE DISTRIBUICAO"));
        var o = new LgLojasOptions { Mapeamento = new() { ["201000"] = "", ["900100"] = "DEMO" } };
        var r = Aplicar(centros, [], o);
        Assert.Empty(r.Novas);
        Assert.Equal(2, r.Ignorados.Count);

        var cd = Aplicar(centros, [], new LgLojasOptions { Mapeamento = new() { ["900100"] = "CD" } });
        Assert.Equal(["01", "CD"], cd.Novas.Select(l => l.Code));
        Assert.Equal(StoreFormat.Varejo, cd.Novas[1].Format); // fora do padrão: Varejo (editável)
    }

    // ---------- Upsert ----------

    [Fact]
    public void Adota_a_loja_semeada_da_intranet_sem_mexer_no_que_o_admin_editou()
    {
        var semeada = new Api.Domain.Store
        {
            Code = "1", Name = "CABO", City = "Cabo de Santo Agostinho", State = "PE", Format = StoreFormat.Atacarejo, HasExtendedDayAgreement = true
        };
        var setor = new Sector { StoreId = semeada.Id, Name = "Frente de caixa" };
        semeada.Sectors.Add(setor);
        var lojas = new List<Api.Domain.Store> { semeada };

        var r = Aplicar(Centros(LgCentrosFixtures.Exemplo), lojas);

        Assert.Empty(r.Novas);
        Assert.Equal(1, r.Atualizadas);
        Assert.Equal("1", semeada.Code); // código não muda (vínculo com o perfil "01 - CABO")
        Assert.Equal("VAREJO LOJA 01 - CABO", semeada.Name);
        Assert.Equal("201000", semeada.LgCentroDeCustoCodigo);
        Assert.Equal(StoreFormat.Varejo, semeada.Format);
        Assert.Equal("Cabo de Santo Agostinho", semeada.City);
        Assert.True(semeada.HasExtendedDayAgreement);
        Assert.Same(setor, Assert.Single(semeada.Sectors));
        Assert.Equal(Agora, semeada.LgSincronizadoEm);
        var ev = Assert.Single(r.Eventos);
        Assert.Equal("atualizada", ev.Acao);
        Assert.Equal("1 - CABO, Atacarejo, ativa", ev.Anterior);

        // Segunda vez: casa pelo código LG e nada muda
        var r2 = Aplicar(Centros(LgCentrosFixtures.Exemplo), lojas);
        Assert.Equal(1, r2.Inalteradas);
        Assert.Empty(r2.Eventos);
    }

    [Fact]
    public void Loja_ja_vinculada_a_outro_centro_nao_e_adotada()
    {
        var loja = new Api.Domain.Store { Code = "01", Name = "VAREJO LOJA 01 - CABO", LgCentroDeCustoCodigo = "201000" };
        var r = Aplicar(Centros(LgCentrosFixtures.Centro("201000", "VAREJO LOJA 01 - CABO"), LgCentrosFixtures.Centro("201999", "VAREJO LOJA 1 - CABO NOVO")),
            [loja]);
        Assert.Empty(r.Novas);
        Assert.Equal("201000", loja.LgCentroDeCustoCodigo);
        Assert.Single(r.Conflitos);
    }

    [Fact]
    public void Inativa_por_data_fim_ou_status_e_reativa_sem_apagar()
    {
        var cabo = new Api.Domain.Store { Code = "01", Name = "VAREJO LOJA 01 - CABO", LgCentroDeCustoCodigo = "201000" };
        var cohab = new Api.Domain.Store { Code = "02", Name = "VAREJO LOJA 02 - COHAB", LgCentroDeCustoCodigo = "202000" };
        var lojas = new List<Api.Domain.Store> { cabo, cohab };

        var r = Aplicar(Centros(
            LgCentrosFixtures.Centro("201000", "VAREJO LOJA 01 - CABO", dataFim: "2026-10-29T00:00:00"),
            LgCentrosFixtures.Centro("202000", "VAREJO LOJA 02 - COHAB", status: 1),
            // Encerrado e sem loja: não cria
            LgCentrosFixtures.Centro("203000", "VAREJO LOJA 03 - PONTEZINHA", dataFim: "2020-01-31T00:00:00")), lojas);

        Assert.False(cabo.Active);
        Assert.False(cohab.Active);
        Assert.Equal(2, r.Inativadas);
        Assert.Equal(2, lojas.Count);
        Assert.Empty(r.Novas);
        Assert.Contains(r.Ignorados, i => i.Codigo == "203000" && i.Motivo.Contains("encerrado"));
        Assert.All(r.Eventos, e => Assert.Equal("inativada", e.Acao));

        // DataFim hoje ainda vale; Status volta a 0: reativa
        var r2 = Aplicar(Centros(LgCentrosFixtures.Centro("201000", "VAREJO LOJA 01 - CABO", dataFim: "2026-10-30T00:00:00"),
            LgCentrosFixtures.Centro("202000", "VAREJO LOJA 02 - COHAB")), lojas);
        Assert.True(cabo.Active);
        Assert.True(cohab.Active);
        Assert.Equal(2, r2.Reativadas);
    }

    [Fact]
    public void Loja_vinculada_que_nao_veio_na_LG_so_gera_aviso()
    {
        var loja = new Api.Domain.Store { Code = "09", Name = "VAREJO LOJA 09", LgCentroDeCustoCodigo = "209000" };
        var r = Aplicar(Centros(LgCentrosFixtures.Exemplo), [loja]);
        Assert.True(loja.Active);
        Assert.Contains(r.Avisos, a => a.Contains("209000"));
    }

    // ---------- Banco ----------

    [Fact]
    public void Migrations_cobrem_o_modelo()
    {
        using var db = new StoreDbContext(new DbContextOptionsBuilder<StoreDbContext>().UseNpgsql("Host=127.0.0.1;Port=1").Options);
        Assert.False(db.Database.HasPendingModelChanges());
    }
}

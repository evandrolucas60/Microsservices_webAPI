using System.Net;
using System.Xml.Linq;
using Employee.Api.Domain;
using Employee.Api.Endpoints;
using Employee.Api.Lg;
using Escala5x2.BuildingBlocks.Contracts.Http;
using Escala5x2.BuildingBlocks.Contracts.Planning;
using Escala5x2.BuildingBlocks.Shared.Lg;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Employee.Tests;

file sealed class Monitor(LgOptions value) : IOptionsMonitor<LgOptions>
{
    public LgOptions CurrentValue => value;
    public LgOptions Get(string? name) => value;
    public IDisposable? OnChange(Action<LgOptions, string?> listener) => null;
}

/// <summary>Handler concorrente: responde pela matrícula do envelope e mede quantas chamadas ficaram simultâneas.</summary>
file sealed class HandlerParalelo(Func<string, HttpResponseMessage> responder) : HttpMessageHandler
{
    private int _emCurso;
    public int MaximoSimultaneo;
    public int Chamadas;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var corpo = await request.Content!.ReadAsStringAsync(cancellationToken);
        var matricula = XDocument.Parse(corpo).Descendants().First(e => e.Name.LocalName == "Matricula").Value;
        Interlocked.Increment(ref Chamadas);
        var agora = Interlocked.Increment(ref _emCurso);
        int max;
        while ((max = Volatile.Read(ref MaximoSimultaneo)) < agora && Interlocked.CompareExchange(ref MaximoSimultaneo, agora, max) != max) { }
        try
        {
            await Task.Delay(15, cancellationToken);
            return responder(matricula);
        }
        finally
        {
            Interlocked.Decrement(ref _emCurso);
        }
    }
}

public class LgFeriasTests
{
    private static LgOptions Configurada() => new()
    {
        Url = "https://lg.exemplo.invalid/v2/servicodecontratodetrabalho",
        SoapAction = "lg.com.br/api/v2/ServicoDeContratoDeTrabalho/ConsultarListaHistoricosVigenteNaDataPorDemanda",
        Operacoes = new LgOperacoesOptions
        {
            Ferias = new LgOperacaoOptions
            {
                Url = "https://lg.exemplo.invalid/v2/servicodeferias",
                SoapAction = "lg.com.br/api/v2/ServicoDeFerias/ConsultarLista"
            }
        },
        Usuario = "usuario-teste",
        Senha = "senha-teste",
        GuidTenant = "00000000-0000-0000-0000-000000000001",
        EsperaInicialMs = 0
    };

    private static LgFeriasClient Cliente(HttpMessageHandler handler, LgOptions? o = null) =>
        new(new HttpClient(handler), new Monitor(o ?? Configurada()), NullLogger<LgFeriasClient>.Instance) { Esperar = (_, _) => Task.CompletedTask };

    private static string Ferias(string inicio, string fim, string matricula = "12345") => $"""
        <a:FeriasParcial><a:DataDaOcorrencia>{inicio}</a:DataDaOcorrencia><a:IdentificacaoDeContrato><a:Empresa><a:Codigo>1</a:Codigo><a:CodigoDeIntegracao i:nil="true"/></a:Empresa><a:Matricula>{matricula}</a:Matricula></a:IdentificacaoDeContrato><a:DataFim>{fim}</a:DataFim></a:FeriasParcial>
        """;

    private static string Resposta(string retorno) => $"""
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><ConsultarListaResponse xmlns="lg.com.br/api/v2"><ConsultarListaResult xmlns:a="lg.com.br/api/dto/v1" xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
         <a:CodigoDoErro i:nil="true"/><a:Mensagens i:nil="true"/><a:Tipo>0</a:Tipo>
         {retorno}
        </ConsultarListaResult></ConsultarListaResponse></s:Body></s:Envelope>
        """;

    /// <summary>Exemplo real da LG (matrícula fictícia): três períodos.</summary>
    private static readonly string Exemplo = Resposta("<a:Retorno>" +
        Ferias("2024-11-04T00:00:00", "2024-12-03T00:00:00") +
        Ferias("2025-11-03T00:00:00", "2025-12-02T00:00:00") +
        Ferias("2026-11-16T00:00:00", "2026-12-15T00:00:00") + "</a:Retorno>");

    // ---------- Resposta ----------

    [Fact]
    public void Le_os_periodos_com_namespaces_e_nil()
    {
        var c = LgFeriasClient.LerResposta(Exemplo);

        Assert.Equal(new[]
        {
            new LgFeriasPeriodo(new DateOnly(2024, 11, 4), new DateOnly(2024, 12, 3)),
            new LgFeriasPeriodo(new DateOnly(2025, 11, 3), new DateOnly(2025, 12, 2)),
            new LgFeriasPeriodo(new DateOnly(2026, 11, 16), new DateOnly(2026, 12, 15))
        }, c.Periodos);
        Assert.Equal(0, c.Invalidos);
    }

    [Theory]
    [InlineData("<a:Retorno/>")]
    [InlineData("<a:Retorno i:nil=\"true\"/>")]
    [InlineData("")]
    public void Retorno_vazio_e_sem_ferias(string retorno)
    {
        var c = LgFeriasClient.LerResposta(Resposta(retorno));
        Assert.Empty(c.Periodos);
    }

    [Fact]
    public void Periodo_sem_data_ou_invertido_e_ignorado()
    {
        var xml = Resposta("<a:Retorno>" +
            Ferias("2026-11-16T00:00:00", "2026-12-15T00:00:00") +
            "<a:FeriasParcial><a:DataDaOcorrencia>2026-01-10T00:00:00</a:DataDaOcorrencia><a:IdentificacaoDeContrato i:nil=\"true\"/><a:DataFim i:nil=\"true\"/></a:FeriasParcial>" +
            Ferias("2026-03-10T00:00:00", "2026-03-01T00:00:00") + "</a:Retorno>");

        var c = LgFeriasClient.LerResposta(xml);

        Assert.Single(c.Periodos);
        Assert.Equal(2, c.Invalidos);
    }

    [Fact]
    public void Erro_da_LG_vira_mensagem_clara()
    {
        var xml = """
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><ConsultarListaResponse xmlns="lg.com.br/api/v2"><ConsultarListaResult xmlns:a="lg.com.br/api/dto/v1" xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
             <a:CodigoDoErro>404</a:CodigoDoErro><a:Mensagens xmlns:b="http://schemas.microsoft.com/2003/10/Serialization/Arrays"><b:string>Contrato não encontrado</b:string></a:Mensagens><a:Tipo>1</a:Tipo>
             <a:Retorno i:nil="true"/>
            </ConsultarListaResult></ConsultarListaResponse></s:Body></s:Envelope>
            """;

        var ex = Assert.Throws<LgIntegracaoException>(() => LgFeriasClient.LerResposta(xml));

        Assert.Contains("Contrato não encontrado", ex.Message);
        Assert.Contains("404", ex.Message);
        Assert.False(ex.Transitorio);
    }

    // ---------- Envelope e configuração ----------

    [Fact]
    public void Envelope_do_WSDL_com_credenciais_so_no_header()
    {
        var xml = XDocument.Parse(LgFeriasClient.MontarEnvelope(Configurada(), 1, "12345"));
        XNamespace soap = "http://schemas.xmlsoap.org/soap/envelope/";
        XNamespace v2 = "lg.com.br/api/v2";
        XNamespace dto = "lg.com.br/api/dto/v1";

        var body = xml.Root!.Element(soap + "Body")!;
        Assert.DoesNotContain("senha-teste", body.ToString());
        var colaborador = body.Element(v2 + "ConsultarLista")!.Element(v2 + "filtro")!.Element(dto + "Colaborador")!;
        Assert.Equal("1", colaborador.Element(dto + "Empresa")!.Element(dto + "Codigo")!.Value);
        Assert.Equal("12345", colaborador.Element(dto + "Matricula")!.Value);
        // DataContract: Empresa antes de Matricula
        Assert.Equal(new[] { "Empresa", "Matricula" }, colaborador.Elements().Select(e => e.Name.LocalName));
        Assert.Contains("senha-teste", xml.Root!.Element(soap + "Header")!.ToString());
    }

    [Fact]
    public void Namespace_do_filtro_configuravel()
    {
        var o = Configurada();
        o.Ferias.NamespaceFiltro = "lg.com.br/api/v1";
        var xml = XDocument.Parse(LgFeriasClient.MontarEnvelope(o, 1, "12345"));
        XNamespace apiV1 = "lg.com.br/api/v1";
        Assert.NotNull(xml.Descendants(apiV1 + "Colaborador").SingleOrDefault());
    }

    [Fact]
    public void Ferias_exige_url_propria_e_nao_usa_a_dos_colaboradores()
    {
        var o = Configurada();
        o.Operacoes.Ferias.Url = "";

        Assert.False(o.ConfiguradaPara(LgOperacao.Ferias));
        Assert.Contains("LG:Operacoes:Ferias:Url", o.Faltando(LgOperacao.Ferias));
        Assert.Equal("", o.UrlDe(LgOperacao.Ferias));
        // As outras operações continuam caindo no LG:Url
        Assert.Equal(o.Url, o.UrlDe(LgOperacao.CentrosDeCusto));
        Assert.True(Configurada().ConfiguradaPara(LgOperacao.Ferias));
    }

    [Fact]
    public async Task Consulta_envia_soapaction_e_le_os_periodos()
    {
        var lg = new FakeLgHandler((_, _, _) => FakeLgHandler.Xml(Exemplo));

        var c = await Cliente(lg).ConsultarAsync(1, "12345", CancellationToken.None);

        Assert.Equal(3, c.Periodos.Count);
        var (body, action) = Assert.Single(lg.Requests);
        Assert.Equal("\"lg.com.br/api/v2/ServicoDeFerias/ConsultarLista\"", action);
        Assert.Contains("<v1:Matricula>12345</v1:Matricula>", body);
    }

    // ---------- Várias matrículas: paralelismo limitado e falhas isoladas ----------

    [Fact]
    public async Task Falha_de_uma_matricula_nao_interrompe_as_outras_e_respeita_o_paralelismo()
    {
        var handler = new HandlerParalelo(m => m == "13"
            ? FakeLgHandler.Xml("erro interno", HttpStatusCode.InternalServerError)
            : FakeLgHandler.Xml(Resposta("<a:Retorno>" + Ferias("2026-11-16T00:00:00", "2026-12-15T00:00:00", m) + "</a:Retorno>")));
        var chaves = Enumerable.Range(1, 40).Select(i => (1, i.ToString())).ToList();

        var rodada = await Cliente(handler).ConsultarVariosAsync(chaves, 4, 20, null, CancellationToken.None);

        Assert.False(rodada.Interrompida);
        Assert.Equal(40, rodada.Resultados.Count);
        Assert.Equal(39, rodada.Resultados.Values.Count(r => r.Consulta is not null));
        var falha = rodada.Resultados[(1, "13")];
        Assert.Null(falha.Consulta);
        Assert.Contains("HTTP 500", falha.Erro);
        Assert.True(handler.MaximoSimultaneo <= 4, $"máximo simultâneo {handler.MaximoSimultaneo}");
        // 3 tentativas para a que falhou (erro transitório), 1 para as demais
        Assert.Equal(39 + 3, handler.Chamadas);
    }

    [Fact]
    public async Task Falhas_seguidas_interrompem_a_rodada()
    {
        var handler = new HandlerParalelo(_ => FakeLgHandler.Xml(LgFixtures.Fault));
        var chaves = Enumerable.Range(1, 200).Select(i => (1, i.ToString())).ToList();

        var rodada = await Cliente(handler).ConsultarVariosAsync(chaves, 2, 5, null, CancellationToken.None);

        Assert.True(rodada.Interrompida);
        Assert.Contains("SOAP Fault", rodada.Motivo);
        Assert.Equal(200, rodada.Resultados.Count);
        Assert.All(rodada.Resultados.Values, r => Assert.Null(r.Consulta));
        Assert.True(handler.Chamadas < 20, $"{handler.Chamadas} chamadas");
        Assert.Contains(rodada.Resultados.Values, r => r.Erro!.StartsWith("não consultada"));
    }

    // ---------- Regras: upsert idempotente, manuais intocados, bordas ----------

    private static readonly DateOnly Hoje = new(2026, 11, 6);
    private static readonly DateTime Agora = new(2026, 11, 6, 8, 0, 0, DateTimeKind.Utc);

    private static Employee.Api.Domain.Employee Colaborador(params Unavailability[] ausencias)
    {
        var e = new Employee.Api.Domain.Employee { Registration = "12345", Name = "Teste", LgEmpresaCodigo = 1 };
        foreach (var u in ausencias)
        {
            u.EmployeeId = e.Id;
            e.Unavailabilities.Add(u);
        }
        return e;
    }

    private static Unavailability Lg(string inicio, string fim) => new()
    {
        Start = DateOnly.Parse(inicio), End = DateOnly.Parse(fim), Type = UnavailabilityType.Ferias, Origem = Unavailability.OrigemLg, CreatedBy = "Integração LG"
    };

    private static Unavailability Manual(string inicio, string fim, UnavailabilityType tipo) => new()
    {
        Start = DateOnly.Parse(inicio), End = DateOnly.Parse(fim), Type = tipo, CreatedBy = "RH Teste", Note = "manual"
    };

    [Fact]
    public void Importa_os_periodos_da_janela_e_e_idempotente()
    {
        var e = Colaborador();
        var periodos = LgFeriasClient.LerResposta(Exemplo).Periodos;

        var primeira = LgFeriasRegras.Aplicar(e, periodos, Hoje, 12, Agora);

        // 2024 terminou há mais de 12 meses: não entra; 2025 e 2026 entram
        Assert.Equal(2, primeira.Novas.Count);
        Assert.All(primeira.Novas, u => Assert.Equal(Unavailability.OrigemLg, u.Origem));
        Assert.All(primeira.Novas, u => Assert.Equal(UnavailabilityType.Ferias, u.Type));
        Assert.Contains(e.Unavailabilities, u => u.Start == new DateOnly(2026, 11, 16) && u.End == new DateOnly(2026, 12, 15));
        Assert.All(primeira.Mudancas, m => Assert.Equal("registrada", m.Acao));

        var segunda = LgFeriasRegras.Aplicar(e, periodos, Hoje, 12, Agora.AddHours(1));
        Assert.False(segunda.Mudou);
        Assert.Equal(2, e.Unavailabilities.Count);
    }

    [Fact]
    public void Fim_alterado_na_LG_ajusta_o_mesmo_registro()
    {
        var existente = Lg("2026-11-16", "2026-12-15");
        var e = Colaborador(existente);

        var r = LgFeriasRegras.Aplicar(e, [new LgFeriasPeriodo(new DateOnly(2026, 11, 16), new DateOnly(2026, 12, 5))], Hoje, 12, Agora);

        var m = Assert.Single(r.Mudancas);
        Assert.Equal("alterada", m.Acao);
        Assert.Same(existente, m.Ausencia);
        Assert.Equal(new DateOnly(2026, 12, 15), m.FimAnterior);
        Assert.Equal(new DateOnly(2026, 12, 5), existente.End);
        Assert.Empty(r.Novas);
    }

    [Fact]
    public void Periodo_futuro_que_sumiu_da_LG_sai_e_o_passado_fica()
    {
        var futuro = Lg("2026-12-20", "2027-01-18");
        var emAndamento = Lg("2026-11-01", "2026-11-30");
        var passado = Lg("2026-07-01", "2026-07-30");
        var e = Colaborador(futuro, emAndamento, passado);

        var r = LgFeriasRegras.Aplicar(e, [], Hoje, 12, Agora);

        Assert.Equal(2, r.Removidas.Count);
        Assert.Contains(futuro, r.Removidas);
        Assert.Contains(emAndamento, r.Removidas);
        Assert.Equal(new[] { passado }, e.Unavailabilities);
        Assert.All(r.Mudancas, m => Assert.Equal("excluida", m.Acao));
    }

    [Fact]
    public void Ausencias_manuais_nunca_sao_tocadas()
    {
        var atestado = Manual("2026-11-20", "2026-11-22", UnavailabilityType.AtestadoMedico);
        var feriasManual = Manual("2026-12-20", "2027-01-05", UnavailabilityType.Ferias);
        var igual = Manual("2027-03-01", "2027-03-30", UnavailabilityType.Ferias);
        var e = Colaborador(atestado, feriasManual, igual);

        var r = LgFeriasRegras.Aplicar(e,
        [
            new LgFeriasPeriodo(new DateOnly(2026, 11, 16), new DateOnly(2026, 12, 15)),
            new LgFeriasPeriodo(new DateOnly(2027, 3, 1), new DateOnly(2027, 3, 30))
        ], Hoje, 12, Agora);

        // O período idêntico ao manual não é duplicado; o outro é criado ao lado do atestado
        var nova = Assert.Single(r.Novas);
        Assert.Equal(new DateOnly(2026, 11, 16), nova.Start);
        Assert.Equal(1, r.ManuaisIguais);
        Assert.Empty(r.Removidas);
        Assert.Equal(new DateOnly(2026, 11, 22), atestado.End);
        Assert.Equal(UnavailabilityType.AtestadoMedico, atestado.Type);
        Assert.Equal(new DateOnly(2027, 1, 5), feriasManual.End);
        Assert.All(new[] { atestado, feriasManual, igual }, u => Assert.Null(u.Origem));
        Assert.Equal(4, e.Unavailabilities.Count);

        // Rodada seguinte sem os períodos: só o da LG sai, os manuais ficam
        var depois = LgFeriasRegras.Aplicar(e, [], Hoje, 12, Agora);
        Assert.Equal(new[] { nova }, depois.Removidas);
        Assert.Equal(3, e.Unavailabilities.Count);
    }

    [Fact]
    public void Bordas_vespera_livre_inicio_e_fim_bloqueados_dia_seguinte_livre()
    {
        var e = Colaborador();
        LgFeriasRegras.Aplicar(e, [new LgFeriasPeriodo(new DateOnly(2026, 11, 16), new DateOnly(2026, 12, 15))], Hoje, 12, Agora);

        var dias = EmployeeEndpoints.AbsencesOf(e, new DateOnly(2026, 11, 1), new DateOnly(2026, 12, 31));

        Assert.DoesNotContain(dias, d => d.Date == new DateOnly(2026, 11, 15));
        Assert.Contains(dias, d => d.Date == new DateOnly(2026, 11, 16) && d.Tipo == UnavailabilityKinds.Ferias);
        Assert.Contains(dias, d => d.Date == new DateOnly(2026, 12, 15) && d.Tipo == UnavailabilityKinds.Ferias);
        Assert.DoesNotContain(dias, d => d.Date == new DateOnly(2026, 12, 16));
        Assert.Equal(30, dias.Count);
    }

    [Fact]
    public void Ferias_da_LG_resolvem_a_pendencia_de_periodo()
    {
        var e = Colaborador();
        e.LgSituacaoCodigo = 9;
        e.LgSituacaoDescricao = "FERIAS NORMAIS";
        e.LgDataInicioSituacao = new DateOnly(2026, 11, 16);
        Assert.True(LgRegras.PendenciaDePeriodo(e));

        LgFeriasRegras.Aplicar(e, [new LgFeriasPeriodo(new DateOnly(2026, 11, 16), new DateOnly(2026, 12, 15))], Hoje, 12, Agora);

        Assert.False(LgRegras.PendenciaDePeriodo(e));
    }

    // ---------- Aviso da geração: a LG nunca bloqueia ----------

    [Fact]
    public void Aviso_da_geracao_quando_a_LG_falha_ou_demora()
    {
        Assert.Equal("Férias da LG não puderam ser atualizadas agora; usando os períodos de férias já registrados.", FeriasLgStatus.Aviso(null));
        Assert.Equal("Férias da LG não puderam ser atualizadas agora; usando a última sincronização de 06/11 05:30.",
            FeriasLgStatus.Aviso(new FeriasLgAtualizacaoResponse(FeriasLgStatus.EmAndamento, 0, 0, 0, 0, null, "06/11 05:30", null)));
        Assert.StartsWith("Férias da LG não puderam",
            FeriasLgStatus.Aviso(new FeriasLgAtualizacaoResponse(FeriasLgStatus.Falha, 3, 0, 3, 0, null, null, "SOAP Fault")));
        Assert.Equal("Férias da LG de 2 colaboradores não puderam ser atualizadas agora; usando a última sincronização de 05/11 05:30.",
            FeriasLgStatus.Aviso(new FeriasLgAtualizacaoResponse(FeriasLgStatus.Parcial, 10, 8, 2, 1, null, "05/11 05:30", null)));
        Assert.Null(FeriasLgStatus.Aviso(new FeriasLgAtualizacaoResponse(FeriasLgStatus.Atualizada, 10, 10, 0, 1, null, "06/11 08:00", null)));
        Assert.Null(FeriasLgStatus.Aviso(new FeriasLgAtualizacaoResponse(FeriasLgStatus.Recente, 10, 0, 0, 0, null, "06/11 08:00", null)));
        Assert.Null(FeriasLgStatus.Aviso(new FeriasLgAtualizacaoResponse(FeriasLgStatus.NaoConfigurada, 0, 0, 0, 0, null, null, "x")));
    }

    [Fact]
    public void Horario_da_ultima_sincronizacao_no_fuso_de_Recife()
    {
        var o = Configurada();
        Assert.Equal("06/11 05:30", LgFeriasAtualizador.Texto(o, new DateTime(2026, 11, 6, 8, 30, 0, DateTimeKind.Utc)));
        Assert.Null(LgFeriasAtualizador.Texto(o, null));
    }

    [Fact]
    public async Task Sem_configuracao_de_ferias_a_atualizacao_da_geracao_nao_bloqueia()
    {
        var o = Configurada();
        o.Operacoes.Ferias = new LgOperacaoOptions();
        var atualizador = new LgFeriasAtualizador(null!, new Monitor(o), null!, NullLogger<LgFeriasAtualizador>.Instance);

        var r = await atualizador.AtualizarAsync(new LgFeriasFiltro(Guid.NewGuid()), TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal(FeriasLgStatus.NaoConfigurada, r.Status);
        Assert.Contains("LG:Operacoes:Ferias:Url", r.Mensagem);
        Assert.Null(FeriasLgStatus.Aviso(r));
    }
}

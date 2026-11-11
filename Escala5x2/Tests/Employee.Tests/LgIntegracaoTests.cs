using System.Net;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Employee.Api.Domain;
using Employee.Api.Lg;
using Escala5x2.BuildingBlocks.Shared.Auth;
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

public class LgIntegracaoTests
{
    private static LgOptions Configurada() => new()
    {
        Url = "https://lg.exemplo.invalid/ServicoDeContratoDeTrabalho.svc",
        SoapAction = "lg.com.br/api/v2/ServicoDeContratoDeTrabalho/ConsultarListaHistoricosVigenteNaDataPorDemanda",
        Usuario = "usuario-teste",
        Senha = "senha-teste",
        GuidTenant = "00000000-0000-0000-0000-000000000001",
        EsperaInicialMs = 0
    };

    private static LgSoapClient Cliente(FakeLgHandler handler, LgOptions? o = null) =>
        new(new HttpClient(handler), new Monitor(o ?? Configurada()), NullLogger<LgSoapClient>.Instance)
        {
            Esperar = (_, _) => Task.CompletedTask
        };

    // ---------- Parse da resposta ----------

    [Fact]
    public void Le_os_tres_contratos_com_paginacao_e_nil()
    {
        var pagina = LgSoapClient.LerResposta(LgFixtures.Resposta(1, 50, 2489, LgFixtures.Edilene, LgFixtures.Joyce, LgFixtures.Luiz));

        Assert.Equal(3, pagina.Contratos.Count);
        Assert.Equal(1, pagina.NumeroDaPagina);
        Assert.Equal(50, pagina.QuantidadePorPagina);
        Assert.Equal(50, pagina.TotalDePaginas);
        Assert.Equal(2489, pagina.TotalGeral);

        var edilene = pagina.Contratos[0];
        Assert.Equal("37", edilene.Matricula);
        Assert.Equal("EDILENE da silva da costa", edilene.Nome);
        Assert.Equal(1, edilene.EmpresaCodigo);
        Assert.Equal(1, edilene.SituacaoCodigo);
        Assert.Equal("ATIVIDADE NORMAL", edilene.Situacao?.Descricao);
        Assert.Equal(new LgItem("202000", "VAREJO LOJA 02 - COHAB"), edilene.CentroDeCusto);
        Assert.Equal(new LgItem("452", "OPERADOR DE LOJA"), edilene.Cargo);
        Assert.Equal("FRENTE DE LOJA", edilene.OrgaoResponsavel?.Descricao);
        Assert.Equal(new DateOnly(1997, 6, 1), edilene.DataAdmissao);
        Assert.Equal(new DateOnly(2026, 7, 1), edilene.DataInicioSituacaoAtual);

        // Registro completo em JSON: todos os campos, i:nil → null, objetos aninhados (Cargo/CBO, Pessoa...)
        var json = edilene.Json;
        Assert.True(json.ContainsKey("NaturezaProfissional"));
        Assert.Null(json["NaturezaProfissional"]);
        Assert.Null(json["Posicao"]);
        Assert.Null(json["NumeroDoProcessoSeletivo"]);
        Assert.Equal("521110", json["Cargo"]?["Cbo"]?["Codigo"]?.GetValue<string>());
        Assert.Equal("88888888888", json["Pessoa"]?["Cpf"]?.GetValue<string>());
        Assert.Equal("SESSEPE", json["Sindicato"]?["Descricao"]?.GetValue<string>());
        Assert.Equal("true", json["MarcaPonto"]?.GetValue<string>());
        Assert.Equal("2", json["TipoPonto"]?.GetValue<string>());
        Assert.Equal("101", json["CategoriaDoColaborador"]?.GetValue<string>());
        Assert.Equal("10", json["VinculoEmpregaticio"]?.GetValue<string>());
        Assert.Equal("0", json["OrgaoResponsavel"]?["Status"]?.GetValue<string>());
        Assert.Null(json["Estabelecimento"]?["CodigoDeIntegracao"]);

        Assert.Equal(8, pagina.Contratos[1].SituacaoCodigo);
        Assert.Equal("9489", pagina.Contratos[2].Matricula);
    }

    [Fact]
    public void Erro_de_negocio_da_LG_vira_mensagem_clara()
    {
        var ex = Assert.Throws<LgIntegracaoException>(() =>
            LgSoapClient.LerResposta(LgFixtures.RespostaComErro("1", "401", "Usuário ou senha inválidos")));
        Assert.Contains("Usuário ou senha inválidos", ex.Message);
        Assert.Contains("401", ex.Message);
        Assert.False(ex.Transitorio);
    }

    [Fact]
    public void Soap_fault_vira_mensagem_clara()
    {
        var ex = Assert.Throws<LgIntegracaoException>(() => LgSoapClient.LerResposta(LgFixtures.Fault));
        Assert.Contains("SOAP Fault", ex.Message);
        Assert.Contains("não pode ser processada", ex.Message);
    }

    // ---------- Envelope ----------

    [Fact]
    public void Envelope_leva_credenciais_no_header_e_filtro_no_body()
    {
        var o = Configurada();
        o.Empresas = [1, 5];
        var xml = XDocument.Parse(LgSoapClient.MontarEnvelope(o, 3, new DateOnly(2026, 10, 27)));

        var header = xml.Root!.Element(LgSoapClient.Soap + "Header")!;
        var token = header.Element(LgSoapClient.Dto + "LGAutenticacao")!.Element(LgSoapClient.Dto + "TokenUsuario")!;
        Assert.Equal("senha-teste", token.Element(LgSoapClient.Dto + "Senha")!.Value);
        Assert.Equal("usuario-teste", token.Element(LgSoapClient.Dto + "Usuario")!.Value);
        Assert.Equal("00000000-0000-0000-0000-000000000001", token.Element(LgSoapClient.Dto + "GuidTenant")!.Value);
        Assert.Equal("705", header.Element(LgSoapClient.Dto + "LGContextoAmbiente")!.Element(LgSoapClient.Dto + "Ambiente")!.Value);

        var body = xml.Root!.Element(LgSoapClient.Soap + "Body")!;
        // Credenciais só no header
        Assert.DoesNotContain("senha-teste", body.ToString());
        var filtro = body.Element(LgSoapClient.V2 + "ConsultarListaHistoricosVigenteNaDataPorDemanda")!.Element(LgSoapClient.V2 + "filtro")!;
        Assert.Equal(["PaginaAtual", "Empresas", "TiposDeSituacoes", "DataVigente"], filtro.Elements().Select(e => e.Name.LocalName));
        Assert.Equal("3", filtro.Element(LgSoapClient.V1 + "PaginaAtual")!.Value);
        Assert.Equal(["1", "5"], filtro.Descendants(LgSoapClient.V1 + "Codigo").Select(e => e.Value));
        Assert.Equal(["2", "3"], filtro.Element(LgSoapClient.V1 + "TiposDeSituacoes")!.Elements(LgSoapClient.Arr + "int").Select(e => e.Value));
        Assert.Equal("2026-10-27", filtro.Element(LgSoapClient.V1 + "DataVigente")!.Value);
        // Núcleo generalizado (operações v1 e v2): colaboradores continuam com a operação em api/v2 e o filtro em api/dto/v1
        Assert.Equal("lg.com.br/api/v2", xml.Root!.Attribute(XNamespace.Xmlns + "v2")!.Value);
        Assert.Equal("lg.com.br/api/dto/v1", xml.Root!.Attribute(XNamespace.Xmlns + "v1")!.Value);
        Assert.Null(xml.Root!.Attribute(XNamespace.Xmlns + "v11"));
    }

    [Fact]
    public void Operacao_de_orgaos_responsaveis_nao_afeta_a_configuracao_dos_colaboradores()
    {
        var o = new LgOptions { Url = "https://lg.exemplo.invalid/x.svc", SoapAction = "acao", Usuario = "u", Senha = "s", GuidTenant = "g" };
        Assert.True(o.Configurada);
        Assert.False(o.ConfiguradaPara(LgOperacao.OrgaosResponsaveis)); // SoapAction próprio obrigatório
        Assert.Contains("LG:Operacoes:OrgaosResponsaveis:SoapAction", o.MensagemNaoConfigurada(LgOperacao.OrgaosResponsaveis));
        Assert.Equal("lg.com.br/api/v1", LgOperacao.OrgaosResponsaveis.NamespaceOperacao);
        Assert.Equal("lg.com.br/api/v2", LgOperacao.Colaboradores.NamespaceOperacao);
        Assert.Equal(LgOperacao.Colaboradores.NamespaceDto, LgOperacao.OrgaosResponsaveis.NamespaceDto);
    }

    // ---------- HTTP: paginação, retry e configuração ----------

    [Fact]
    public async Task Percorre_todas_as_paginas_com_soapaction()
    {
        var handler = new FakeLgHandler((_, body, _) =>
        {
            var pagina = int.Parse(XDocument.Parse(body).Descendants(LgSoapClient.V1 + "PaginaAtual").Single().Value);
            return FakeLgHandler.Xml(pagina switch
            {
                1 => LgFixtures.Resposta(1, 3, 3, LgFixtures.Edilene),
                2 => LgFixtures.Resposta(2, 3, 3, LgFixtures.Joyce),
                _ => LgFixtures.Resposta(3, 3, 3, LgFixtures.Luiz)
            });
        });
        var progresso = new List<(int, int)>();

        var consulta = await Cliente(handler).ConsultarTodosAsync(new DateOnly(2026, 10, 30), (p, t) => progresso.Add((p, t)), CancellationToken.None);

        Assert.Equal(3, consulta.Paginas);
        Assert.Equal(["37", "6576", "9489"], consulta.Contratos.Select(c => c.Matricula));
        Assert.Equal([(1, 3), (2, 3), (3, 3)], progresso);
        Assert.All(handler.Requests, r => Assert.Equal($"\"{Configurada().SoapAction}\"", r.SoapAction));
    }

    [Fact]
    public async Task Repete_erro_transitorio_e_nao_repete_fault()
    {
        var handler = new FakeLgHandler((_, _, n) => n == 1
            ? FakeLgHandler.Xml("indisponível", HttpStatusCode.ServiceUnavailable)
            : FakeLgHandler.Xml(LgFixtures.Resposta(1, 1, 1, LgFixtures.Edilene)));
        var pagina = await Cliente(handler).ConsultarPaginaAsync(1, new DateOnly(2026, 10, 30), CancellationToken.None);
        Assert.Single(pagina.Contratos);
        Assert.Equal(2, handler.Requests.Count);

        var fault = new FakeLgHandler((_, _, _) => FakeLgHandler.Xml(LgFixtures.Fault, HttpStatusCode.InternalServerError));
        var ex = await Assert.ThrowsAsync<LgIntegracaoException>(() =>
            Cliente(fault).ConsultarPaginaAsync(1, new DateOnly(2026, 10, 30), CancellationToken.None));
        Assert.Contains("SOAP Fault", ex.Message);
        Assert.Single(fault.Requests);
    }

    [Fact]
    public async Task Sem_configuracao_nao_chama_a_LG()
    {
        var handler = new FakeLgHandler((_, _, _) => throw new InvalidOperationException("não deveria chamar"));
        var ex = await Assert.ThrowsAsync<LgIntegracaoException>(() =>
            Cliente(handler, new LgOptions()).ConsultarPaginaAsync(1, new DateOnly(2026, 10, 30), CancellationToken.None));
        Assert.StartsWith("Integração LG não configurada", ex.Message);
        Assert.Contains("LG:Url", ex.Message);
        Assert.Contains("LG:SoapAction", ex.Message);
        Assert.Empty(handler.Requests);
    }

    // ---------- Loja pelo centro de custo ----------

    [Theory]
    [InlineData("VAREJO LOJA 02 - COHAB", "2")]
    [InlineData("VAREJO LOJA 27 NOSSA SENHORA DO O", "27")]
    [InlineData("VAREJO LOJA 03 - PONTEZINHA", "3")]
    [InlineData("ATACAREJO LOJA 05 - ARCO VITA", "5")]
    [InlineData("varejo loja 4 prazeres", "4")]
    [InlineData("ADMINISTRATIVO - FINANCEIRO", null)]
    [InlineData("CENTRO DE DISTRIBUICAO", null)]
    public void Extrai_a_loja_do_centro_de_custo(string descricao, string? esperado) =>
        Assert.Equal(esperado, LgRegras.CodigoLojaDoCentroDeCusto(new LgItem("999", descricao)));

    [Fact]
    public void Mapeamento_configurado_tem_precedencia()
    {
        var mapa = new Dictionary<string, string> { ["900100"] = "CD", ["202000"] = "05" };
        Assert.Equal("CD", LgRegras.CodigoLojaDoCentroDeCusto(new LgItem("900100", "CENTRO DE DISTRIBUICAO"), mapa));
        Assert.Equal("5", LgRegras.CodigoLojaDoCentroDeCusto(new LgItem("202000", "VAREJO LOJA 02 - COHAB"), mapa));
    }

    [Fact]
    public void Casa_a_loja_primeiro_pelo_codigo_do_centro_de_custo()
    {
        var cabo = new LojaEscala(Guid.NewGuid(), "01", "VAREJO LOJA 01 - CABO", "201000");
        var cohab = new LojaEscala(Guid.NewGuid(), "02", "VAREJO LOJA 02 - COHAB", "202000");
        var semeada = new LojaEscala(Guid.NewGuid(), "03", "PONTEZINHA");
        var arcoVita = new LojaEscala(Guid.NewGuid(), "35", "ATACAREJO LOJA 05 - ARCO VITA", "305000", Active: false);
        var lojas = new[] { cabo, cohab, semeada, arcoVita };

        // 1) Código LG da loja: vale mesmo com descrição diferente/sem "LOJA nn" (e para loja inativa)
        Assert.Equal((cabo, (string?)null), LgRegras.LojaDoCentroDeCusto(new LgItem("201000", "QUALQUER DESCRICAO"), lojas));
        Assert.Equal(arcoVita, LgRegras.LojaDoCentroDeCusto(new LgItem("305000", "ATACAREJO LOJA 05 - ARCO VITA"), lojas).Loja);
        // 2) LG:CentrosDeCusto
        var mapa = new Dictionary<string, string> { ["900100"] = "03" };
        Assert.Equal(semeada, LgRegras.LojaDoCentroDeCusto(new LgItem("900100", "CENTRO DE DISTRIBUICAO"), lojas, mapa).Loja);
        // 3) "LOJA nn" só com loja ainda sem código LG
        Assert.Equal(semeada, LgRegras.LojaDoCentroDeCusto(new LgItem("203000", "VAREJO LOJA 03 - PONTEZINHA"), lojas).Loja);

        // Loja do número vinculada a OUTRO centro de custo (ex.: atacarejo com o mesmo número): ignora com aviso
        var (outra, avisoOutra) = LgRegras.LojaDoCentroDeCusto(new LgItem("302000", "ATACAREJO LOJA 02 - COHAB"), lojas);
        Assert.Null(outra);
        Assert.Contains("202000", avisoOutra);
        // Centro de custo de loja que ainda não existe no Store.Api
        var (nenhuma, aviso) = LgRegras.LojaDoCentroDeCusto(new LgItem("227000", "VAREJO LOJA 27 NOSSA SENHORA DO O"), lojas);
        Assert.Null(nenhuma);
        Assert.Contains("rode a sincronização de lojas", aviso);
        // Não é loja: sem aviso
        var (presidencia, semAviso) = LgRegras.LojaDoCentroDeCusto(new LgItem("001020100", "PRESIDENCIA"), lojas);
        Assert.Null(presidencia);
        Assert.Null(semAviso);
    }

    [Fact]
    public async Task SoapAction_por_operacao_tem_precedencia_e_LG_SoapAction_continua_valendo()
    {
        var o = Configurada();
        Assert.Equal(o.SoapAction, o.SoapActionDe(LgOperacao.Colaboradores));
        o.Operacoes.Colaboradores.SoapAction = "lg.com.br/api/v2/Outro/ConsultarListaHistoricosVigenteNaDataPorDemanda";
        o.Operacoes.Colaboradores.Url = "https://lg.exemplo.invalid/Outro.svc";
        var handler = new FakeLgHandler((_, _, _) => FakeLgHandler.Xml(LgFixtures.Resposta(1, 1, 1, LgFixtures.Edilene)));
        await Cliente(handler, o).ConsultarPaginaAsync(1, new DateOnly(2026, 10, 30), CancellationToken.None);
        Assert.Equal($"\"{o.Operacoes.Colaboradores.SoapAction}\"", Assert.Single(handler.Requests).SoapAction);
        Assert.False(o.ConfiguradaPara(LgOperacao.CentrosDeCusto)); // centros de custo têm SOAPAction próprio
    }

    [Theory]
    [InlineData("EDILENE da silva da costa", "Edilene da Silva da Costa")]
    [InlineData("JOYCE BARBOSA DA SILVA", "Joyce Barbosa da Silva")]
    [InlineData("MARIA DOS ANJOS E SOUZA", "Maria dos Anjos e Souza")]
    [InlineData("JOÃO D'ÁVILA", "João D'Ávila")]
    public void Nome_em_title_case_pt_br(string lg, string esperado) => Assert.Equal(esperado, LgRegras.NomeProprio(lg));

    // ---------- Situação e pendência de período ----------

    [Theory]
    [InlineData("FERIAS NORMAIS", UnavailabilityType.Ferias)]
    [InlineData("FERIAS NOR AD 13 SALARIO", UnavailabilityType.Ferias)]
    [InlineData("Férias coletivas", UnavailabilityType.Ferias)]
    [InlineData("LICENCA MATERNIDADE", UnavailabilityType.Licenca)]
    [InlineData("SALARIO MATERNIDADE", UnavailabilityType.Licenca)]
    [InlineData("AFASTADO AUXILIO DOENCA", UnavailabilityType.Afastamento)]
    [InlineData("ACIDENTE DE TRABALHO", UnavailabilityType.Afastamento)]
    [InlineData("SUSPENSAO", UnavailabilityType.Afastamento)]
    public void Classifica_o_tipo_sugerido(string descricao, UnavailabilityType esperado) =>
        Assert.Equal(esperado, LgRegras.TipoSugerido(descricao));

    [Fact]
    public void Situacao_1_disponivel_e_demais_indisponiveis()
    {
        Assert.True(LgRegras.Disponivel(1));
        Assert.True(LgRegras.Disponivel(null));
        Assert.False(LgRegras.Disponivel(8));
        Assert.False(LgRegras.Disponivel(9));
    }

    [Fact]
    public void Pendencia_de_periodo_ate_registrar_a_ausencia()
    {
        var e = new Employee.Api.Domain.Employee
        {
            Name = "Joyce Barbosa da Silva", LgSituacaoCodigo = 8, LgSituacaoDescricao = "FERIAS NOR AD 13 SALARIO",
            LgDataInicioSituacao = new DateOnly(2026, 10, 5), LgSincronizadoEm = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc)
        };
        Assert.True(LgRegras.PendenciaDePeriodo(e));
        Assert.Equal(new DateOnly(2026, 10, 5), LgRegras.InicioSugerido(e));

        // Ausência que não cobre o início da situação não resolve
        e.Unavailabilities.Add(new Unavailability { Start = new DateOnly(2026, 11, 1), End = new DateOnly(2026, 11, 3), Type = UnavailabilityType.Ferias });
        Assert.True(LgRegras.PendenciaDePeriodo(e));

        // Operador registra o período: a pendência some (indisponível só no período registrado)
        e.Unavailabilities.Add(new Unavailability { Start = new DateOnly(2026, 10, 5), End = new DateOnly(2026, 11, 3), Type = UnavailabilityType.Ferias });
        Assert.False(LgRegras.PendenciaDePeriodo(e));

        // LG volta a 1 (ATIVIDADE NORMAL): sem pendência mesmo sem ausência
        var volta = new Employee.Api.Domain.Employee { LgSituacaoCodigo = 1, LgDataInicioSituacao = new DateOnly(2026, 10, 5) };
        Assert.False(LgRegras.PendenciaDePeriodo(volta));
        // Inativo não pesa
        Assert.False(LgRegras.PendenciaDePeriodo(new Employee.Api.Domain.Employee { Active = false, LgSituacaoCodigo = 9 }));
        // Cadastro só da Escala (sem LG): nunca pendente
        Assert.False(LgRegras.PendenciaDePeriodo(new Employee.Api.Domain.Employee()));
    }

    [Fact]
    public void Aplicar_preenche_nome_cargo_e_situacao()
    {
        var c = LgSoapClient.LerResposta(LgFixtures.Resposta(1, 1, 1, LgFixtures.Luiz)).Contratos[0];
        var e = new Employee.Api.Domain.Employee { Registration = "9489" };
        var agora = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(LgSincronizador.Aplicar(e, c, 1, agora));
        Assert.Equal("Luiz Felipe Pereira Silva", e.Name);
        Assert.Equal("ENC DE DEPOSITO ATACAREJO I", e.JobTitle);
        Assert.Equal(9, e.LgSituacaoCodigo);
        Assert.Equal("FERIAS NORMAIS", e.LgSituacaoDescricao);
        Assert.Equal(new DateOnly(2026, 10, 1), e.LgDataInicioSituacao);
        Assert.Equal("227000", e.LgCentroDeCusto);
        Assert.Equal(agora, e.LgSincronizadoEm);
        Assert.True(LgRegras.PendenciaDePeriodo(e));
        Assert.Equal(UnavailabilityType.Ferias, LgRegras.TipoSugerido(e.LgSituacaoDescricao));

        // Mesmo registro de novo (inclusive JSON reordenado como o jsonb devolve): nada mudou
        e.LgRegistroJson = JsonNode.Parse(e.LgRegistroJson!)!.ToJsonString();
        Assert.False(LgSincronizador.Aplicar(e, c, 1, agora.AddDays(1)));
    }

    // ---------- LGPD ----------

    [Fact]
    public void Lgpd_cpf_e_nascimento_por_papel()
    {
        var registro = LgSoapClient.LerResposta(LgFixtures.Resposta(1, 1, 1, LgFixtures.Joyce)).Contratos[0].Json;

        var rh = LgRegras.AplicarLgpd(registro, Roles.RH);
        Assert.Equal("77777777777", rh["Pessoa"]?["Cpf"]?.GetValue<string>());
        Assert.Equal("1970-04-06T00:00:00", rh["Pessoa"]?["DataDeNascimento"]?.GetValue<string>());
        Assert.Equal("77777777777", LgRegras.AplicarLgpd(registro, Roles.Admin)["Pessoa"]?["Cpf"]?.GetValue<string>());

        var gestor = LgRegras.AplicarLgpd(registro, Roles.Gestor);
        Assert.Equal("***.***.***-77", gestor["Pessoa"]?["Cpf"]?.GetValue<string>());
        Assert.Equal("06/04", gestor["Pessoa"]?["DataDeNascimento"]?.GetValue<string>());
        Assert.Equal("FERIAS NOR AD 13 SALARIO", gestor["SituacaoDoColaborador"]?["Descricao"]?.GetValue<string>());

        foreach (var papel in new[] { Roles.Juridico, Roles.Colaborador, null })
        {
            var outro = LgRegras.AplicarLgpd(registro, papel);
            var pessoa = Assert.IsType<JsonObject>(outro["Pessoa"]);
            Assert.False(pessoa.ContainsKey("Cpf"));
            Assert.False(pessoa.ContainsKey("DataDeNascimento"));
            Assert.Equal("JOYCE BARBOSA DA SILVA", pessoa["Nome"]?.GetValue<string>());
            Assert.DoesNotContain("77777777777", outro.ToJsonString());
        }
        // Colaborador também não vê a descrição da situação (pode indicar dado de saúde)
        Assert.Equal("Afastado", LgRegras.AplicarLgpd(registro, Roles.Colaborador)["SituacaoDoColaborador"]?["Descricao"]?.GetValue<string>());

        // O original não muda
        Assert.Equal("77777777777", registro["Pessoa"]?["Cpf"]?.GetValue<string>());
    }

    [Fact]
    public void Mascaras()
    {
        Assert.Equal("***.***.***-88", LgRegras.MascararCpf("888.888.888-88"));
        Assert.Null(LgRegras.MascararCpf(null));
        Assert.Equal("06/04", LgRegras.MascararNascimento("1970-04-06T00:00:00"));
        Assert.Null(LgRegras.MascararNascimento("x"));
    }

    [Fact]
    public void Configuracao_padrao()
    {
        var o = new LgOptions();
        Assert.False(o.Configurada);
        Assert.Equal([1], o.EmpresasEfetivas);
        Assert.Equal([2, 3], o.SituacoesEfetivas);
        Assert.Equal(705, o.Ambiente);
        Assert.Null(o.HoraDiaria());
        Assert.Equal(new DateOnly(2026, 10, 29), o.Hoje(new DateTime(2026, 10, 30, 2, 30, 0, DateTimeKind.Utc)));
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using Employee.Api.Data;
using Employee.Api.Domain;
using Employee.Api.Lg;
using Escala5x2.BuildingBlocks.Contracts.Stores;
using Escala5x2.BuildingBlocks.Shared.Auth;
using Escala5x2.BuildingBlocks.Shared.Lg;
using Microsoft.EntityFrameworkCore;
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

file sealed class Lojas(params LojaEscala[] lojas) : ILojasEscala
{
    public Task<IReadOnlyList<LojaEscala>?> ObterLojasAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LojaEscala>?>(lojas);
}

file sealed class Fabrica(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("https://store.exemplo.invalid") };
}

/// <summary>Setor padrão pelo OrgaoResponsavel da LG e a regra de edição manual.</summary>
public class LgSetoresTests
{
    private static readonly Guid FrenteDeLoja = Guid.NewGuid();
    private static readonly Guid Deposito = Guid.NewGuid();
    private static readonly Guid Telefonia = Guid.NewGuid();
    private static readonly LojaEscala Cohab = new(Guid.NewGuid(), "02", "VAREJO LOJA 02 - COHAB", "202000");
    private static readonly LojaEscala Pontezinha = new(Guid.NewGuid(), "03", "VAREJO LOJA 03 - PONTEZINHA", "203000");

    private static Api.Domain.Employee Novo() => new() { Registration = "37", StoreId = Cohab.Id };

    private static List<Guid> Setores(Api.Domain.Employee e) => e.Sectors.Select(s => s.SectorId).ToList();

    // ---------- Atribuição padrão ----------

    [Fact]
    public void Colaborador_novo_recebe_o_setor_do_orgao_como_padrao()
    {
        var e = Novo();
        var (mudou, removidos) = LgRegras.AplicarSetorLg(e, 2, "FRENTE DE LOJA", FrenteDeLoja);

        Assert.True(mudou);
        Assert.Empty(removidos);
        Assert.Equal([FrenteDeLoja], Setores(e));
        Assert.Equal(e.Id, e.Sectors[0].EmployeeId);
        Assert.Equal(2, e.LgOrgaoCodigo);
        Assert.Equal("FRENTE DE LOJA", e.LgOrgaoDescricao);
        Assert.Equal(FrenteDeLoja, e.LgSetorId);
        Assert.False(e.SetoresManuais);
        Assert.False(LgRegras.SetorDivergente(e));

        // Mesma LG de novo: nada muda
        Assert.False(LgRegras.AplicarSetorLg(e, 2, "FRENTE DE LOJA", FrenteDeLoja).Mudou);
    }

    [Fact]
    public void Orgao_nulo_fica_sem_setor()
    {
        var e = Novo();
        LgRegras.AplicarSetorLg(e, null, null, null);
        Assert.Empty(e.Sectors);
        Assert.Null(e.LgOrgaoCodigo);
    }

    [Fact]
    public void Mudanca_de_orgao_na_LG_antes_da_edicao_manual_troca_o_setor()
    {
        var e = Novo();
        LgRegras.AplicarSetorLg(e, 2, "FRENTE DE LOJA", FrenteDeLoja);
        e.Sectors.Add(new EmployeeSector { EmployeeId = e.Id, SectorId = Telefonia }); // ex.: dado antigo

        var (mudou, removidos) = LgRegras.AplicarSetorLg(e, 25, "DEPOSITO", Deposito);

        Assert.True(mudou);
        Assert.Equal([Deposito], Setores(e));
        Assert.Equal(new[] { FrenteDeLoja, Telefonia }.Order(), removidos.Select(s => s.SectorId).Order());
        Assert.Equal(25, e.LgOrgaoCodigo);
    }

    [Fact]
    public void Depois_da_edicao_manual_a_LG_nao_sobrescreve_e_mostra_a_divergencia()
    {
        var e = Novo();
        LgRegras.AplicarSetorLg(e, 2, "FRENTE DE LOJA", FrenteDeLoja);
        // Gestor troca para Telefonia (PUT /api/employees/{id})
        e.Sectors.Clear();
        e.Sectors.Add(new EmployeeSector { EmployeeId = e.Id, SectorId = Telefonia });
        e.SetoresManuais = true;
        e.SetoresAlteradosPor = "Gestor Teste";
        e.SetoresAlteradosEm = new DateTime(2026, 10, 29, 10, 0, 0, DateTimeKind.Utc);

        // A LG continua com Frente de Loja e depois muda para Depósito: o setor manual fica
        var (mudou, removidos) = LgRegras.AplicarSetorLg(e, 2, "FRENTE DE LOJA", FrenteDeLoja);
        Assert.False(mudou);
        Assert.Empty(removidos);
        LgRegras.AplicarSetorLg(e, 25, "DEPOSITO", Deposito);
        Assert.Equal([Telefonia], Setores(e));
        Assert.Equal(Deposito, e.LgSetorId); // o órgão atual fica guardado
        Assert.Equal("DEPOSITO", e.LgOrgaoDescricao);
        Assert.True(LgRegras.SetorDivergente(e));

        // "Voltar a seguir o setor da LG": limpa a marca e aplica o setor do órgão atual
        e.SetoresManuais = false;
        var (_, saiu) = LgRegras.AplicarSetorLg(e, e.LgOrgaoCodigo, e.LgOrgaoDescricao, e.LgSetorId);
        Assert.Equal([Deposito], Setores(e));
        Assert.Equal([Telefonia], saiu.Select(s => s.SectorId));
        Assert.False(LgRegras.SetorDivergente(e));
        // E volta a acompanhar a LG
        LgRegras.AplicarSetorLg(e, 2, "FRENTE DE LOJA", FrenteDeLoja);
        Assert.Equal([FrenteDeLoja], Setores(e));
    }

    [Fact]
    public void Manual_igual_ao_setor_da_LG_nao_e_divergente()
    {
        var e = Novo();
        e.Sectors.Add(new EmployeeSector { EmployeeId = e.Id, SectorId = FrenteDeLoja });
        e.SetoresManuais = true;
        e.SetoresAlteradosPor = "RH Teste";
        e.SetoresAlteradosEm = DateTime.UtcNow;
        LgRegras.AplicarSetorLg(e, 2, "FRENTE DE LOJA", FrenteDeLoja);
        Assert.True(e.SetoresManuais); // alteração manual de verdade continua manual
        Assert.False(LgRegras.SetorDivergente(e));
    }

    [Fact]
    public void Marca_da_migracao_vira_seguir_a_LG_so_quando_o_setor_ja_e_o_do_orgao()
    {
        // Marcados pela migration SetoresLg (sem autor/data)
        var igual = Novo();
        igual.Sectors.Add(new EmployeeSector { EmployeeId = igual.Id, SectorId = FrenteDeLoja });
        igual.SetoresManuais = true;
        var diferente = Novo();
        diferente.Sectors.Add(new EmployeeSector { EmployeeId = diferente.Id, SectorId = Telefonia });
        diferente.SetoresManuais = true;

        LgRegras.AplicarSetorLg(igual, 2, "FRENTE DE LOJA", FrenteDeLoja);
        LgRegras.AplicarSetorLg(diferente, 2, "FRENTE DE LOJA", FrenteDeLoja);

        Assert.False(igual.SetoresManuais);
        Assert.True(diferente.SetoresManuais);
        Assert.Equal([Telefonia], Setores(diferente));
        Assert.True(LgRegras.SetorDivergente(diferente));
    }

    // ---------- Órgãos por loja (uma chamada ao Store.Api por loja) ----------

    [Fact]
    public void Coleta_os_orgaos_por_loja_sem_repetir()
    {
        var contratos = LgSoapClient.LerResposta(LgFixtures.Resposta(1, 1, 6,
            LgFixtures.Edilene, // loja 02, órgão 2
            LgFixtures.Colaborador("100", "202000", "VAREJO LOJA 02 - COHAB", "2", "FRENTE DE LOJA"),
            LgFixtures.Colaborador("101", "202000", "VAREJO LOJA 02 - COHAB", "25", "DEPOSITO"),
            LgFixtures.Colaborador("102", "202000", "VAREJO LOJA 02 - COHAB", null, null),
            LgFixtures.Joyce, // loja 03, órgão 13
            LgFixtures.Colaborador("103", "001020100", "PRESIDENCIA", "99", "DIRETORIA"))).Contratos;

        var r = LgSincronizador.OrgaosPorLoja(contratos, [Cohab, Pontezinha], null);

        Assert.Equal(2, r.Count);
        Assert.Equal([new LgOrgaoSetor(2, "FRENTE DE LOJA", 0), new LgOrgaoSetor(25, "DEPOSITO", 0)], r[Cohab.Id]);
        Assert.Equal([new LgOrgaoSetor(13, "TELEFONIA", 0)], r[Pontezinha.Id]);
        Assert.Null(contratos[3].OrgaoCodigo);
    }

    // ---------- Store.Api (HttpMessageHandler falso) ----------

    [Fact]
    public async Task Garantir_chama_o_endpoint_interno_e_le_o_mapa()
    {
        var resposta = new GarantirSetoresLgResponse(Cohab.Id, new Dictionary<int, Guid> { [2] = FrenteDeLoja, [25] = Deposito }, 1, 1, 0,
            [new LgSetorGarantido(2, FrenteDeLoja, "FRENTE DE LOJA", "criado"), new LgSetorGarantido(25, Deposito, "Depósito", "adotado")], []);
        string? corpo = null;
        Uri? url = null;
        var handler = new FakeLgHandler((req, body, _) =>
        {
            url = req.RequestUri;
            corpo = body;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(resposta, JsonSerializerOptions.Web), Encoding.UTF8, "application/json")
            };
        });
        var cliente = new SetoresLgLojaHttp(new Fabrica(handler), NullLogger<SetoresLgLojaHttp>.Instance);

        var r = await cliente.GarantirAsync(Cohab.Id, [new LgOrgaoSetor(2, "FRENTE DE LOJA", 0), new LgOrgaoSetor(25, "DEPOSITO", 0)], "RH Teste",
            CancellationToken.None);

        Assert.Equal($"/internal/stores/{Cohab.Id}/sectors/garantir-lg", url?.AbsolutePath);
        Assert.Contains("\"codigo\":25", corpo);
        Assert.Contains("RH Teste", corpo);
        Assert.Equal(FrenteDeLoja, r.Setores[2]);
        Assert.Equal(Deposito, r.Setores[25]);
        Assert.Equal(1, r.Adotados);
    }

    [Fact]
    public async Task Store_Api_fora_falha_a_sincronizacao_antes_de_gravar()
    {
        // Banco inalcançável: se a sincronização tocasse o banco antes dos setores, o erro seria de conexão
        var db = new EmployeeDbContext(new DbContextOptionsBuilder<EmployeeDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=nada;Username=x;Password=x;Timeout=1").Options);
        var o = new LgOptions
        {
            Url = "https://lg.exemplo.invalid/ServicoDeContratoDeTrabalho.svc",
            SoapAction = "lg.com.br/api/v2/ServicoDeContratoDeTrabalho/ConsultarListaHistoricosVigenteNaDataPorDemanda",
            Usuario = "usuario-teste", Senha = "senha-teste", GuidTenant = "00000000-0000-0000-0000-000000000001", EsperaInicialMs = 0
        };
        var lg = new FakeLgHandler((_, _, _) => FakeLgHandler.Xml(LgFixtures.Resposta(1, 1, 1, LgFixtures.Edilene)));
        var soap = new LgSoapClient(new HttpClient(lg), new Monitor(o), NullLogger<LgSoapClient>.Instance) { Esperar = (_, _) => Task.CompletedTask };
        var storeFora = new FakeLgHandler((_, _, _) => throw new HttpRequestException("Conexão recusada"));
        var setores = new SetoresLgLojaHttp(new Fabrica(storeFora), NullLogger<SetoresLgLojaHttp>.Instance);
        var sincronizador = new LgSincronizador(db, soap, new Lojas(Cohab), setores, null!, null!, new Monitor(o), NullLogger<LgSincronizador>.Instance);

        var ex = await Assert.ThrowsAsync<LgIntegracaoException>(() => sincronizador.SincronizarAsync("RH Teste", null, CancellationToken.None));

        Assert.Contains("Store.Api", ex.Message);
        Assert.Contains("nada foi alterado", ex.Message);
        Assert.True(ex.Transitorio);
        Assert.Single(storeFora.Requests);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public void Migrations_cobrem_o_modelo()
    {
        using var db = new EmployeeDbContext(new DbContextOptionsBuilder<EmployeeDbContext>().UseNpgsql("Host=127.0.0.1;Port=1").Options);
        Assert.False(db.Database.HasPendingModelChanges());
    }
}

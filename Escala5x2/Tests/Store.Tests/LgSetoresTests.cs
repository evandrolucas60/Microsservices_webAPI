using Escala5x2.BuildingBlocks.Contracts.Stores;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Store.Api.Domain;
using Store.Api.Endpoints;
using Store.Api.Lg;
using Xunit;

namespace Store.Tests;

/// <summary>Setores pelo OrgaoResponsavel da LG (garantir-lg): dedupe por código e por nome, adoção, renomeação e corrida.</summary>
public class LgSetoresTests
{
    private static readonly Guid Loja = Guid.NewGuid();
    private static readonly DateTime Agora = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

    private static LgOrgaoSetor Orgao(int codigo, string? descricao, int? status = 0) => new(codigo, descricao, status);

    [Fact]
    public void Cria_um_setor_por_orgao_com_a_descricao_da_LG()
    {
        var setores = new List<Sector>();

        var r = LgSetoresRegras.Garantir([Orgao(2, "FRENTE DE LOJA"), Orgao(13, "TELEFONIA"), Orgao(25, " DEPOSITO ")], setores, Loja, Agora);

        Assert.Equal(3, r.Criados);
        Assert.Equal(3, r.Novos.Count);
        Assert.Equal(["DEPOSITO", "FRENTE DE LOJA", "TELEFONIA"], setores.Select(s => s.Name).Order());
        Assert.All(setores, s =>
        {
            Assert.Equal(SectorOrigem.Lg, s.Origem);
            Assert.Equal(Loja, s.StoreId);
            Assert.Equal(Agora, s.LgSincronizadoEm);
            Assert.Equal(s.Id, r.Mapa[s.LgOrgaoCodigo!.Value]);
        });
        Assert.All(r.Alteracoes, a => Assert.Equal("criado", a.Acao));
    }

    [Fact]
    public void Segunda_vez_casa_pelo_codigo_e_nao_duplica()
    {
        var setores = new List<Sector>();
        var primeira = LgSetoresRegras.Garantir([Orgao(2, "FRENTE DE LOJA")], setores, Loja, Agora);

        var r = LgSetoresRegras.Garantir([Orgao(2, "FRENTE DE LOJA")], setores, Loja, Agora.AddDays(1));

        Assert.Single(setores);
        Assert.Empty(r.Novos);
        Assert.Empty(r.Alteracoes);
        Assert.False(r.Mudou);
        Assert.Equal(primeira.Mapa[2], r.Mapa[2]);
        Assert.Equal("existente", Assert.Single(r.Detalhes).Acao);
        Assert.Equal(Agora.AddDays(1), setores[0].LgSincronizadoEm);
    }

    [Fact]
    public void Codigo_repetido_no_pedido_vale_uma_vez()
    {
        var setores = new List<Sector>();
        var r = LgSetoresRegras.Garantir([Orgao(2, null), Orgao(2, "FRENTE DE LOJA"), Orgao(2, "FRENTE DE LOJA")], setores, Loja, Agora);
        Assert.Equal("FRENTE DE LOJA", Assert.Single(setores).Name);
        Assert.Single(r.Mapa);
    }

    [Fact]
    public void Adota_o_setor_manual_com_o_mesmo_nome_normalizado_sem_renomear()
    {
        var manual = new Sector { StoreId = Loja, Name = "Depósito" };
        var caixa = new Sector { StoreId = Loja, Name = "Frente  de loja" };
        var setores = new List<Sector> { manual, caixa };

        var r = LgSetoresRegras.Garantir([Orgao(25, "DEPOSITO"), Orgao(2, "FRENTE DE LOJA")], setores, Loja, Agora);

        Assert.Empty(r.Novos);
        Assert.Equal(2, setores.Count);
        Assert.Equal(2, r.Adotados);
        Assert.Equal(25, manual.LgOrgaoCodigo);
        Assert.Equal("Depósito", manual.Name); // nome do gestor fica
        Assert.Equal(SectorOrigem.Manual, manual.Origem);
        Assert.Equal("DEPOSITO", manual.LgOrgaoDescricao);
        Assert.Equal(manual.Id, r.Mapa[25]);
        Assert.Equal(caixa.Id, r.Mapa[2]);

        // A LG muda a descrição: setor manual adotado não é renomeado (só aviso)
        var r2 = LgSetoresRegras.Garantir([Orgao(25, "DEPOSITO CENTRAL")], setores, Loja, Agora);
        Assert.Equal("Depósito", manual.Name);
        Assert.Equal("DEPOSITO CENTRAL", manual.LgOrgaoDescricao);
        Assert.Contains(r2.Avisos, a => a.Contains("mantém o nome"));
        Assert.Empty(r2.Alteracoes);
    }

    [Fact]
    public void Setor_criado_pela_LG_acompanha_a_descricao_da_LG()
    {
        var setores = new List<Sector>();
        LgSetoresRegras.Garantir([Orgao(13, "TELEFONIA")], setores, Loja, Agora);
        var id = setores[0].Id;

        var r = LgSetoresRegras.Garantir([Orgao(13, "TELEFONIA E ELETRO")], setores, Loja, Agora);

        Assert.Equal(id, Assert.Single(setores).Id); // mesmo setor (demandas e colaboradores continuam ligados)
        Assert.Equal("TELEFONIA E ELETRO", setores[0].Name);
        var (_, acao, anterior) = Assert.Single(r.Alteracoes);
        Assert.Equal("renomeado", acao);
        Assert.Equal("TELEFONIA", anterior);
        Assert.Contains(r.Avisos, a => a.Contains("renomeado"));

        // Nome novo que colide com outro setor: não renomeia
        setores.Add(new Sector { StoreId = Loja, Name = "Eletro" });
        var r2 = LgSetoresRegras.Garantir([Orgao(13, "ELETRO")], setores, Loja, Agora);
        Assert.Equal("TELEFONIA E ELETRO", setores[0].Name);
        Assert.Contains(r2.Avisos, a => a.Contains("outro setor"));
    }

    [Fact]
    public void Dois_orgaos_com_o_mesmo_nome_usam_o_mesmo_setor()
    {
        var setores = new List<Sector>();
        var r = LgSetoresRegras.Garantir([Orgao(2, "FRENTE DE LOJA"), Orgao(40, "Frente de Loja")], setores, Loja, Agora);
        var setor = Assert.Single(setores);
        Assert.Equal(2, setor.LgOrgaoCodigo);
        Assert.Equal(setor.Id, r.Mapa[40]);
        Assert.Contains(r.Avisos, a => a.Contains("mesmo nome"));
    }

    [Fact]
    public void Orgao_sem_descricao_nao_cria_setor()
    {
        var setores = new List<Sector>();
        var r = LgSetoresRegras.Garantir([Orgao(7, "  ")], setores, Loja, Agora);
        Assert.Empty(setores);
        Assert.False(r.Mapa.ContainsKey(7));
        Assert.Single(r.Avisos);
    }

    // ---------- Corrida: índice único (loja, LgOrgaoCodigo) + repetição ----------

    private sealed class ViolacaoFalsa() : Exception("23505");

    [Fact]
    public async Task Corrida_viola_o_indice_e_repete_casando_pelo_codigo()
    {
        // "Banco": a outra sincronização grava o órgão 2 entre a leitura e a gravação desta
        var banco = new List<Sector> { new() { StoreId = Loja, Name = "Depósito" } };
        var concorrente = new Sector { StoreId = Loja, Name = "FRENTE DE LOJA", Origem = SectorOrigem.Lg, LgOrgaoCodigo = 2, LgOrgaoDescricao = "FRENTE DE LOJA" };
        var tentativas = 0;
        Task<List<Sector>> Carregar(CancellationToken _) =>
            Task.FromResult(banco.Select(s => new Sector
            {
                Id = s.Id, StoreId = s.StoreId, Name = s.Name, Origem = s.Origem, LgOrgaoCodigo = s.LgOrgaoCodigo, LgOrgaoDescricao = s.LgOrgaoDescricao
            }).ToList());
        Task Salvar(LgSetoresResultado r, CancellationToken _)
        {
            if (++tentativas == 1) banco.Add(concorrente);
            // Índice único filtrado: mesmo código na mesma loja
            if (r.Novos.Any(n => banco.Any(b => b.StoreId == n.StoreId && b.LgOrgaoCodigo == n.LgOrgaoCodigo))) throw new ViolacaoFalsa();
            banco.AddRange(r.Novos);
            foreach (var (s, _, _) in r.Alteracoes.Where(a => a.Acao != "criado"))
            {
                var b = banco.Single(x => x.Id == s.Id);
                b.LgOrgaoCodigo = s.LgOrgaoCodigo;
                b.Name = s.Name;
            }
            return Task.CompletedTask;
        }

        var r = await LgSetoresRegras.GarantirComRepeticaoAsync([Orgao(2, "FRENTE DE LOJA"), Orgao(25, "DEPOSITO")], Loja, Agora,
            Carregar, Salvar, ex => ex is ViolacaoFalsa, CancellationToken.None);

        Assert.Equal(2, tentativas);
        Assert.Equal(concorrente.Id, r.Mapa[2]); // usa o setor da outra sincronização
        Assert.Single(banco, s => s.LgOrgaoCodigo == 2);
        Assert.Equal(2, banco.Count); // o "Depósito" manual foi adotado, não duplicado
        Assert.Equal(25, banco.Single(s => s.Name == "Depósito").LgOrgaoCodigo);
        Assert.Equal(0, r.Criados);
        Assert.Equal(1, r.Adotados);
    }

    [Fact]
    public async Task Corrida_persistente_desiste_depois_das_tentativas()
    {
        var chamadas = 0;
        await Assert.ThrowsAsync<ViolacaoFalsa>(() => LgSetoresRegras.GarantirComRepeticaoAsync([Orgao(2, "FRENTE DE LOJA")], Loja, Agora,
            _ => Task.FromResult(new List<Sector>()), (_, _) => { chamadas++; throw new ViolacaoFalsa(); },
            ex => ex is ViolacaoFalsa, CancellationToken.None));
        Assert.Equal(3, chamadas);
    }

    [Fact]
    public void Reconhece_a_violacao_de_indice_unico_do_PostgreSQL()
    {
        var unica = new DbUpdateException("x", new PostgresException("duplicate key", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation));
        var outra = new DbUpdateException("x", new PostgresException("fk", "ERROR", "ERROR", PostgresErrorCodes.ForeignKeyViolation));
        Assert.True(LgSetoresService.ViolacaoUnica(unica));
        Assert.False(LgSetoresService.ViolacaoUnica(outra));
        Assert.False(LgSetoresService.ViolacaoUnica(new InvalidOperationException()));
    }

    // ---------- Cadastro manual (POST /api/stores/{id}/sectors) ----------

    [Fact]
    public void Setor_manual_com_nome_repetido_e_recusado()
    {
        var existentes = new[]
        {
            new Sector { StoreId = Loja, Name = "DEPOSITO", Origem = SectorOrigem.Lg, LgOrgaoCodigo = 25 },
            new Sector { StoreId = Loja, Name = "Frente de caixa" }
        };

        var lg = Assert.IsType<ProblemHttpResult>(StoreEndpoints.ValidateSectorName("  Depósito ", existentes));
        var erro = Assert.IsType<HttpValidationProblemDetails>(lg.ProblemDetails).Errors["name"][0];
        Assert.Contains("DEPOSITO", erro);
        Assert.Contains("órgão 25", erro);
        Assert.NotNull(StoreEndpoints.ValidateSectorName("FRENTE  DE CAIXA", existentes));
        Assert.NotNull(StoreEndpoints.ValidateSectorName("   ", existentes));
        Assert.NotNull(StoreEndpoints.ValidateSectorName(new string('x', 101), existentes));
        Assert.Null(StoreEndpoints.ValidateSectorName("Hortifruti", existentes));
    }
}

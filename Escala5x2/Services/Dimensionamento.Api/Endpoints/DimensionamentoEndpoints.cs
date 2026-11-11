using System.Reflection;
using System.Text.Json;
using Dimensionamento.Api.Calculo;
using Dimensionamento.Api.Clients;
using Dimensionamento.Api.Data;
using Dimensionamento.Api.Domain;
using Dimensionamento.Api.Services;
using Escala5x2.BuildingBlocks.Shared.Auth;
using Escala5x2.BuildingBlocks.Shared.Web;
using Microsoft.EntityFrameworkCore;

namespace Dimensionamento.Api.Endpoints;

/// <summary>
/// /api/dimensionamento (gateway: /api/escala5x2/dimensionamento). Leitura e simulação: logados com acesso à loja
/// (Admin, RH e Jurídico todas; Gestor e Colaborador a própria). Gravação (perfis e cenários): Admin ou Gestor da loja.
/// </summary>
public static class DimensionamentoEndpoints
{
    /// <summary>Exemplo embutido: relatório Consinco "Análise ABC de vendas — distribuição por check-out + hora".</summary>
    public const string ExemploNome = "28-ASABRANCA — Consinco 01 a 27/10/2026 (referência)";
    public static readonly DateOnly ExemploInicio = new(2026, 10, 1);
    public static readonly DateOnly ExemploFim = new(2026, 10, 27);
    public const int ExemploDias = 27;

    /// <summary>Admin, ou Gestor da loja (papel efetivo por loja: perfil ∪ roles de loja, ver EscopoLojas).</summary>
    internal static bool PodeGerenciar(ICurrentUser user, Guid storeId) => user.CanManageStore(storeId);

    private static IResult SemPermissaoGravar() =>
        Results.Problem("Somente o Admin ou o Gestor da loja alteram perfis de demanda e cenários.", statusCode: StatusCodes.Status403Forbidden);

    public static string LerExemploCsv()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Dimensionamento.Api.Seed.asabranca.csv")
            ?? throw new InvalidOperationException("CSV de exemplo não embutido no serviço.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static void MapDimensionamentoEndpoints(this IEndpointRouteBuilder app)
    {
        // Dimensionamento: só o superAdmin da intranet (Admin) visualiza e simula. Nenhum outro serviço chama esta API.
        var group = app.MapGroup("/api/dimensionamento").RequireAuthorization(Policies.Admin);

        // ---------- Perfis de demanda ----------
        group.MapGet("/perfis", async (Guid storeId, Guid? sectorId, DimensionamentoDbContext db, ICurrentUser user, CancellationToken ct) =>
        {
            if (!user.CanAccessStore(storeId)) return Results.Forbid();
            var q = db.Perfis.AsNoTracking().Where(p => p.StoreId == storeId);
            if (sectorId is { } s) q = q.Where(p => p.SectorId == null || p.SectorId == s);
            var lista = await q.OrderBy(p => p.Nome).ToListAsync(ct);
            return Results.Ok(lista.Select(PerfilMapper.Item));
        });

        group.MapGet("/perfis/{id:guid}", async (Guid id, DimensionamentoDbContext db, ICurrentUser user, CancellationToken ct) =>
        {
            var p = await db.Perfis.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (p is null) return Results.NotFound();
            if (!user.CanAccessStore(p.StoreId)) return Results.Forbid();
            return Results.Ok(PerfilMapper.Detalhe(p));
        });

        group.MapPost("/perfis", async (PerfilRequest req, DimensionamentoDbContext db, ICurrentUser user, CancellationToken ct) =>
        {
            if (!PodeGerenciar(user, req.StoreId)) return SemPermissaoGravar();
            if (Validar(req.Nome, req.Curva, req.FatoresDiaSemana) is { } erro) return erro;
            var p = new PerfilDemanda
            {
                StoreId = req.StoreId, SectorId = req.SectorId, Nome = req.Nome.Trim(), Observacao = Texto(req.Observacao), Origem = OrigemPerfil.Manual,
                CriadoPor = user.Display,
            };
            AplicarCurva(p, req.Curva, req.FatoresDiaSemana);
            db.Perfis.Add(p);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/dimensionamento/perfis/{p.Id}", PerfilMapper.Detalhe(p));
        });

        group.MapPut("/perfis/{id:guid}", async (Guid id, PerfilUpdateRequest req, DimensionamentoDbContext db, ICurrentUser user, CancellationToken ct) =>
        {
            var p = await db.Perfis.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (p is null) return Results.NotFound();
            if (!PodeGerenciar(user, p.StoreId)) return SemPermissaoGravar();
            if (Validar(req.Nome, req.Curva, req.FatoresDiaSemana) is { } erro) return erro;
            p.Nome = req.Nome.Trim();
            p.Observacao = Texto(req.Observacao);
            p.SectorId = req.SectorId;
            // Curva editada: o total do período importado deixa de corresponder (passa a ser a soma da curva × dias)
            var antes = p.CurvaJson;
            AplicarCurva(p, req.Curva, req.FatoresDiaSemana);
            if (p.Origem != OrigemPerfil.Manual && antes != p.CurvaJson) p.Origem = OrigemPerfil.Manual;
            p.AtualizadoPor = user.Display;
            p.AtualizadoEm = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(PerfilMapper.Detalhe(p));
        });

        group.MapDelete("/perfis/{id:guid}", async (Guid id, DimensionamentoDbContext db, ICurrentUser user, CancellationToken ct) =>
        {
            var p = await db.Perfis.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (p is null) return Results.NotFound();
            if (!PodeGerenciar(user, p.StoreId)) return SemPermissaoGravar();
            db.Perfis.Remove(p);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        group.MapPost("/perfis/importar-csv", async (ImportarCsvRequest req, DimensionamentoDbContext db, ICurrentUser user, CancellationToken ct) =>
        {
            if (!PodeGerenciar(user, req.StoreId)) return SemPermissaoGravar();
            if (string.IsNullOrWhiteSpace(req.Nome)) return ServiceDefaults.ValidationError("nome", "Informe o nome do perfil.");
            if (req.Dias is < 1 or > 366) return ServiceDefaults.ValidationError("dias", "Informe o número de dias do período (1 a 366).");
            if (req.Conteudo is null || req.Conteudo.Length > 2_000_000) return ServiceDefaults.ValidationError("conteudo", "Arquivo vazio ou maior que 2 MB.");
            return await ImportarAsync(db, user, req.StoreId, req.SectorId, req.Nome.Trim(), req.Conteudo, req.Dias, req.PeriodoInicio, req.PeriodoFim,
                Texto(req.Observacao), OrigemPerfil.Csv, ct);
        });

        // Demanda de referência da 28-ASABRANCA (CSV embutido no serviço): cria/atualiza o perfil na loja escolhida
        group.MapPost("/perfis/exemplo-asabranca", async (ExemploRequest req, DimensionamentoDbContext db, ICurrentUser user, CancellationToken ct) =>
        {
            if (!PodeGerenciar(user, req.StoreId)) return SemPermissaoGravar();
            var existente = await db.Perfis.FirstOrDefaultAsync(p => p.StoreId == req.StoreId && p.Origem == OrigemPerfil.Exemplo && p.Nome == ExemploNome, ct);
            if (existente is not null) db.Perfis.Remove(existente);
            return await ImportarAsync(db, user, req.StoreId, req.SectorId, ExemploNome, LerExemploCsv(), ExemploDias, ExemploInicio, ExemploFim,
                "Transcrito do relatório Consinco \"Análise ABC de vendas — distribuição por check-out + hora\" da loja 28-ASABRANCA (56.414 documentos em 27 dias).",
                OrigemPerfil.Exemplo, ct);
        });

        // ---------- Simulação ----------
        group.MapGet("/padroes", async (Guid storeId, Guid? sectorId, SimulacaoService service, ICurrentUser user, CancellationToken ct) =>
        {
            if (!user.CanAccessStore(storeId)) return Results.Forbid();
            try
            {
                var ctx = await service.ResolverAsync(new SimulacaoRequest { StoreId = storeId, SectorId = sectorId }, ct);
                return Results.Ok(new { parametros = ctx.Parametros, origens = ctx.Origens, avisos = ctx.Avisos, loja = ctx.Loja, servicos = ctx.Servicos,
                    ativos = ctx.Entrada.Disponibilidade?.Ativos, podeGravar = PodeGerenciar(user, storeId) });
            }
            catch (ValidacaoException ex)
            {
                return ServiceDefaults.ValidationError(ex.Campo, ex.Message);
            }
        });

        group.MapPost("/simular", async (SimulacaoRequest req, SimulacaoService service, ICurrentUser user, CancellationToken ct) =>
        {
            if (!user.CanAccessStore(req.StoreId)) return Results.Forbid();
            try
            {
                return Results.Ok(await service.SimularAsync(req, ct));
            }
            catch (ValidacaoException ex)
            {
                return ServiceDefaults.ValidationError(ex.Campo, ex.Message);
            }
        });

        // ---------- Cenários salvos ----------
        group.MapGet("/cenarios", async (Guid storeId, Guid? sectorId, DimensionamentoDbContext db, ICurrentUser user, CancellationToken ct) =>
        {
            if (!user.CanAccessStore(storeId)) return Results.Forbid();
            var q = db.Cenarios.AsNoTracking().Where(c => c.StoreId == storeId);
            if (sectorId is { } s) q = q.Where(c => c.SectorId == null || c.SectorId == s);
            var lista = await q.OrderByDescending(c => c.AtualizadoEm ?? c.CriadoEm).ToListAsync(ct);
            return Results.Ok(lista.Select(Item));
        });

        group.MapGet("/cenarios/{id:guid}", async (Guid id, DimensionamentoDbContext db, ICurrentUser user, CancellationToken ct) =>
        {
            var c = await db.Cenarios.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (c is null) return Results.NotFound();
            if (!user.CanAccessStore(c.StoreId)) return Results.Forbid();
            return Results.Ok(new CenarioDetalhe(Item(c),
                c.ResultadoJson is null ? null : JsonSerializer.Deserialize<SimulacaoResponse>(c.ResultadoJson, Json.Options)));
        });

        group.MapPost("/cenarios", async (CenarioRequest req, DimensionamentoDbContext db, SimulacaoService service, ICurrentUser user, CancellationToken ct) =>
        {
            if (req.Parametros is null) return ServiceDefaults.ValidationError("parametros", "Informe os parâmetros do cenário.");
            if (!PodeGerenciar(user, req.Parametros.StoreId)) return SemPermissaoGravar();
            if (string.IsNullOrWhiteSpace(req.Nome) || req.Nome.Length > 120) return ServiceDefaults.ValidationError("nome", "Informe o nome do cenário (até 120 caracteres).");
            var c = new Cenario { StoreId = req.Parametros.StoreId, CriadoPor = user.Display };
            return await SalvarAsync(c, req, db, service, user, ct, criar: true);
        });

        group.MapPut("/cenarios/{id:guid}", async (Guid id, CenarioRequest req, DimensionamentoDbContext db, SimulacaoService service, ICurrentUser user, CancellationToken ct) =>
        {
            var c = await db.Cenarios.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (c is null) return Results.NotFound();
            if (!PodeGerenciar(user, c.StoreId)) return SemPermissaoGravar();
            if (req.Parametros is null) return ServiceDefaults.ValidationError("parametros", "Informe os parâmetros do cenário.");
            if (req.Parametros.StoreId != c.StoreId) return ServiceDefaults.ValidationError("storeId", "O cenário não pode mudar de loja.");
            if (string.IsNullOrWhiteSpace(req.Nome) || req.Nome.Length > 120) return ServiceDefaults.ValidationError("nome", "Informe o nome do cenário (até 120 caracteres).");
            c.AtualizadoPor = user.Display;
            c.AtualizadoEm = DateTime.UtcNow;
            return await SalvarAsync(c, req, db, service, user, ct, criar: false);
        });

        // Recalcula com os dados atuais (demanda, colaboradores, regras) e grava o novo resultado
        group.MapPost("/cenarios/{id:guid}/recalcular", async (Guid id, DimensionamentoDbContext db, SimulacaoService service, ICurrentUser user, CancellationToken ct) =>
        {
            var c = await db.Cenarios.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (c is null) return Results.NotFound();
            if (!PodeGerenciar(user, c.StoreId)) return SemPermissaoGravar();
            var parametros = JsonSerializer.Deserialize<SimulacaoRequest>(c.ParametrosJson, Json.Options)!;
            return await SalvarAsync(c, new CenarioRequest(c.Nome, c.Descricao, parametros), db, service, user, ct, criar: false);
        });

        group.MapDelete("/cenarios/{id:guid}", async (Guid id, DimensionamentoDbContext db, ICurrentUser user, CancellationToken ct) =>
        {
            var c = await db.Cenarios.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (c is null) return Results.NotFound();
            if (!PodeGerenciar(user, c.StoreId)) return SemPermissaoGravar();
            db.Cenarios.Remove(c);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    private static async Task<IResult> SalvarAsync(Cenario c, CenarioRequest req, DimensionamentoDbContext db, SimulacaoService service, ICurrentUser user,
        CancellationToken ct, bool criar)
    {
        SimulacaoResponse resultado;
        try
        {
            resultado = await service.SimularAsync(req.Parametros, ct);
        }
        catch (ValidacaoException ex)
        {
            return ServiceDefaults.ValidationError(ex.Campo, ex.Message);
        }
        c.Nome = req.Nome.Trim();
        c.Descricao = Texto(req.Descricao);
        c.SectorId = req.Parametros.SectorId;
        c.PerfilDemandaId = req.Parametros.PerfilDemandaId;
        c.ParametrosJson = JsonSerializer.Serialize(req.Parametros, Json.Options);
        c.ResumoJson = JsonSerializer.Serialize(resultado.Resumo(), Json.Options);
        c.ResultadoJson = JsonSerializer.Serialize(resultado, Json.Options);
        c.CalculadoEm = resultado.CalculadoEm;
        if (criar) db.Cenarios.Add(c);
        await db.SaveChangesAsync(ct);
        var detalhe = new CenarioDetalhe(Item(c), resultado);
        return criar ? Results.Created($"/api/dimensionamento/cenarios/{c.Id}", detalhe) : Results.Ok(detalhe);
    }

    private static CenarioListItem Item(Cenario c) => new(c.Id, c.StoreId, c.SectorId, c.PerfilDemandaId, c.Nome, c.Descricao,
        c.ResumoJson is null ? null : JsonSerializer.Deserialize<ResumoCenario>(c.ResumoJson, Json.Options),
        JsonSerializer.Deserialize<SimulacaoRequest>(c.ParametrosJson, Json.Options)!, c.CriadoPor, c.CriadoEm, c.AtualizadoPor, c.AtualizadoEm);

    private static async Task<IResult> ImportarAsync(DimensionamentoDbContext db, ICurrentUser user, Guid storeId, Guid? sectorId, string nome,
        string conteudo, int dias, DateOnly? inicio, DateOnly? fim, string? observacao, string origem, CancellationToken ct)
    {
        ImportacaoCsv importacao;
        try
        {
            importacao = CurvaDemanda.LerCsv(conteudo, dias);
        }
        catch (FormatException ex)
        {
            return ServiceDefaults.ValidationError("conteudo", ex.Message);
        }
        if (inicio is { } i && fim is { } f && f < i) return ServiceDefaults.ValidationError("periodoFim", "O fim do período é anterior ao início.");
        var p = new PerfilDemanda
        {
            StoreId = storeId, SectorId = sectorId, Nome = nome.Length > 120 ? nome[..120] : nome, Origem = origem, Observacao = observacao,
            PeriodoInicio = inicio, PeriodoFim = fim, Dias = dias, TotalDocumentos = importacao.TotalDocumentos,
            CurvaJson = JsonSerializer.Serialize(importacao.Curva(), Json.Options),
            CheckoutsJson = JsonSerializer.Serialize(importacao.Checkouts, Json.Options),
            CriadoPor = user.Display,
        };
        db.Perfis.Add(p);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new ImportacaoResponse(PerfilMapper.Detalhe(p), importacao.Linhas, importacao.TotalDocumentos, importacao.Avisos));
    }

    private static void AplicarCurva(PerfilDemanda p, List<PontoCurva> curva, List<double>? fatores)
    {
        p.CurvaJson = JsonSerializer.Serialize(curva.OrderBy(c => c.DiaSemana is null ? -1 : CurvaDemanda.IndiceDia(c.DiaSemana.Value)).ThenBy(c => c.Hora).ToList(), Json.Options);
        p.FatoresDiaJson = fatores is { Count: 7 } && fatores.Any(f => Math.Abs(f - 1) > 1e-9) ? JsonSerializer.Serialize(fatores, Json.Options) : null;
        // Total equivalente do período: documentos por semana (média dos dias × 7) quando não há período importado
        p.TotalDocumentos = Math.Round(CurvaDemanda.Matriz(curva, fatores).Sum(d => d.Sum()) * (p.Dias ?? 7) / 7.0, 2);
    }

    private static IResult? Validar(string? nome, List<PontoCurva>? curva, List<double>? fatores)
    {
        if (string.IsNullOrWhiteSpace(nome) || nome.Length > 120) return ServiceDefaults.ValidationError("nome", "Informe o nome do perfil (até 120 caracteres).");
        if (curva is null) return ServiceDefaults.ValidationError("curva", "Informe a curva por hora.");
        if (CurvaDemanda.Validar(curva) is { } erro) return ServiceDefaults.ValidationError("curva", erro);
        if (fatores is { } f && (f.Count != 7 || f.Any(v => v < 0 || v > 10)))
            return ServiceDefaults.ValidationError("fatoresDiaSemana", "Informe 7 fatores (segunda a domingo) entre 0 e 10.");
        return null;
    }

    private static string? Texto(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length > 500 ? s.Trim()[..500] : s.Trim();
}

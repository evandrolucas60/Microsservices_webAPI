using System.Globalization;
using System.Text.RegularExpressions;
using Escala5x2.BuildingBlocks.Contracts.Compliance;
using Escala5x2.BuildingBlocks.Shared.Auth;
using Escala5x2.BuildingBlocks.Shared.Web;
using Compliance.Api.Data;
using Compliance.Api.Domain;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Compliance.Api.Endpoints;

public sealed record ComplianceRuleRequest(
    string Categoria, string Chave, decimal Valor, string Unidade, string Descricao, string? BaseLegal,
    DateOnly? VigenciaInicio, DateOnly? VigenciaFim, RuleApplication Aplicacao, bool? ConfirmarAbaixoDoPisoLegal);

/// <summary>Corpo opcional de bloquear/desbloquear: confirmação para regra com piso/teto legal e motivo (vai para a auditoria).</summary>
public sealed record RuleStatusRequest(bool? ConfirmarAbaixoDoPisoLegal, string? Motivo);

public sealed record ComplianceRuleResponse(
    int Id, string Categoria, string Chave, decimal Valor, string Unidade, string Descricao, string? BaseLegal,
    decimal? PisoLegal, decimal? TetoLegal, DateOnly? VigenciaInicio, DateOnly? VigenciaFim,
    RuleStatus Status, RuleApplication Aplicacao, string AtualizadoPor, DateTime AtualizadoEm);

/// <summary>
/// Limites que a lei não deixa afrouxar, por chave conhecida. Vale o limite mais restritivo dentro da vigência
/// da linha (ex.: carga semanal sem data de fim precisa respeitar as 40h da fase definitiva da EC 141).
/// </summary>
public static class LegalLimits
{
    private static readonly DateOnly Transicao = new(2026, 12, 16);
    private static readonly DateOnly Definitiva = new(2027, 12, 16);

    public static (decimal? Piso, decimal? Teto)? For(string chave, DateOnly? inicio, DateOnly? fim) => chave switch
    {
        RuleKeys.CargaHorariaSemanal => (null, fim is null || fim >= Definitiva ? 40m : fim >= Transicao ? 42m : 44m),
        RuleKeys.LimiteDiario => (null, 480m),
        RuleKeys.LimiteDiarioComAcordo => (null, 504m),
        RuleKeys.HoraExtraMaxDiaria => (null, 120m),
        // DSR (CF art. 7º, XV; CLT art. 67; Lei 605/1949): 1 folga; a partir de 16/12/2026, 2 (EC 141/2026, art. 10, I)
        RuleKeys.FolgasMinimasSemana => (fim is not null && fim < Transicao ? 1m : 2m, null),
        // Regime 6x1: 1 folga (DSR); 5x2: o mesmo piso do DSR da vigência (2 a partir de 16/12/2026)
        RuleKeys.FolgasSemana6x1 => (1m, null),
        RuleKeys.FolgasSemana5x2 => (fim is not null && fim < Transicao ? 1m : 2m, null),
        // OJ 410 da SDI-1 do TST: repouso depois do 7º dia seguido viola o art. 7º, XV (máximo 6 dias seguidos)
        RuleKeys.DiasConsecutivosMax6x1 or RuleKeys.DiasConsecutivosMax5x2 => (null, 6m),
        RuleKeys.InterjornadaMinima => (660m, null),
        RuleKeys.FolgaIsoladaHorasLivres => (35m, null),
        RuleKeys.DomingoFolgaJanela => (null, 3m),
        RuleKeys.DomingoQuinzenalJanela => (null, 2m),
        RuleKeys.IntervaloMinimo => (60m, null),
        RuleKeys.IntervaloMaximo => (null, 120m),
        RuleKeys.IntervaloPadrao => (60m, 120m),
        // CLT art. 71: acima de 6h de trabalho contínuo o intervalo é obrigatório (subir o limite afrouxa a lei)
        RuleKeys.IntervaloObrigatorioAcima => (null, 360m),
        RuleKeys.JornadaContinuaMaxima => (null, 360m),
        // CLT art. 71, §1º: 15 min para jornada acima de 4h até 6h
        RuleKeys.IntervaloJornada4a6h => (15m, null),
        RuleKeys.FeriadoExigeAutorizacao => (1m, null),
        _ => null
    };
}

/// <summary>Valor vigente de uma regra liberada a não-Admin (GET /api/rules/vigentes/{chave}): só chave, valor e unidade.</summary>
public sealed record RuleValueResponse(string Chave, decimal Valor, string Unidade);

public static partial class RuleEndpoints
{
    /// <summary>Chaves cujo valor vigente qualquer usuário logado pode ler (o cadastro completo é só do Admin).</summary>
    private static readonly HashSet<string> ChavesPublicas = [RuleKeys.IntervaloPadrao];

    [GeneratedRegex("^[a-z][a-z0-9_]{1,79}$")]
    private static partial Regex ChaveRegex();

    private static ComplianceRuleResponse ToResponse(ComplianceRule r) => new(
        r.Id, r.Categoria, r.Chave, r.Valor, r.Unidade, r.Descricao, r.BaseLegal, r.PisoLegal, r.TetoLegal,
        r.VigenciaInicio, r.VigenciaFim, r.Status, r.Aplicacao, r.AtualizadoPor, r.AtualizadoEm);

    private static string N(decimal v) => v.ToString("0.##", CultureInfo.GetCultureInfo("pt-BR"));

    /// <summary>Validação de forma (sem os limites legais, conferidos em <see cref="CheckLegalLimit"/>).</summary>
    private static async Task<IResult?> ValidateAsync(ComplianceRuleRequest r, int? id, ComplianceDbContext db)
    {
        var chave = r.Chave?.Trim() ?? string.Empty;
        if (!ChaveRegex().IsMatch(chave)) return ServiceDefaults.ValidationError("chave", "A chave usa letras minúsculas sem acento, números e _ (ex.: carga_horaria_semanal).");
        if (string.IsNullOrWhiteSpace(r.Categoria)) return ServiceDefaults.ValidationError("categoria", "Informe a categoria.");
        if (string.IsNullOrWhiteSpace(r.Descricao)) return ServiceDefaults.ValidationError("descricao", "Informe a descrição.");
        if (!RuleKeys.Unidades.Contains(r.Unidade?.Trim().ToLowerInvariant())) return ServiceDefaults.ValidationError("unidade", $"Unidade inválida. Use: {string.Join(", ", RuleKeys.Unidades)}.");
        if (r.Valor < 0) return ServiceDefaults.ValidationError("valor", "O valor não pode ser negativo.");
        var unidade = r.Unidade!.Trim().ToLowerInvariant();
        if (unidade != "horas" && r.Valor != decimal.Truncate(r.Valor)) return ServiceDefaults.ValidationError("valor", $"Valor em {unidade} precisa ser inteiro.");
        if (unidade == "booleano" && r.Valor is not (0 or 1)) return ServiceDefaults.ValidationError("valor", "Valor booleano é 0 ou 1.");
        if (r.VigenciaInicio is { } i && r.VigenciaFim is { } f && f < i) return ServiceDefaults.ValidationError("vigenciaFim", "O fim da vigência é anterior ao início.");
        // Aposentadas pelo regime 6x1/5x2: cadastrá-las de novo criaria duas regras conflitantes
        if (RuleKeys.Aposentadas.Contains(chave))
            return ServiceDefaults.ValidationError("chave", chave == RuleKeys.FolgasMaximasSemana
                ? "A regra folgas_maximas_semana foi substituída pelo regime da semana: use folgas_semana_6x1 e folgas_semana_5x2 (número exato de folgas)."
                : "A regra dias_consecutivos_max foi substituída pelo regime da semana: use dias_consecutivos_max_6x1 e dias_consecutivos_max_5x2.");
        switch (chave)
        {
            case RuleKeys.CoberturaFaixa when r.Valor < 5 || r.Valor > 240 || 1440 % (int)r.Valor != 0:
                return ServiceDefaults.ValidationError("valor", "A faixa de cobertura precisa dividir o dia (ex.: 15, 30, 60) e ficar entre 5 e 240 minutos.");
            case RuleKeys.DiasConsecutivosMax6x1 or RuleKeys.DiasConsecutivosMax5x2 or RuleKeys.DomingoFolgaJanela or RuleKeys.DomingoQuinzenalJanela when r.Valor < 1:
                return ServiceDefaults.ValidationError("valor", "O valor mínimo é 1.");
            case RuleKeys.FolgasMinimasSemana or RuleKeys.FolgasSemana6x1 or RuleKeys.FolgasSemana5x2 when r.Valor > 7:
                return ServiceDefaults.ValidationError("valor", "A semana tem 7 dias.");
            case RuleKeys.MaxFolgasSeguidas when r.Valor < 1:
                return ServiceDefaults.ValidationError("valor", "O máximo de folgas seguidas é pelo menos 1.");
            case RuleKeys.MaxFolgasSeguidas when r.Unidade?.Trim().ToLowerInvariant() != "dias":
                return ServiceDefaults.ValidationError("unidade", "max_folgas_seguidas é em dias.");
            case RuleKeys.FolgasConsecutivas5x2 when r.Unidade?.Trim().ToLowerInvariant() != "booleano":
                return ServiceDefaults.ValidationError("unidade", "folgas_consecutivas_5x2 é booleana (1 = exigir as 2 folgas seguidas no 5x2).");
            case RuleKeys.CompensacaoJornada5x2 when r.Unidade?.Trim().ToLowerInvariant() != "booleano":
                return ServiceDefaults.ValidationError("unidade",
                    "compensacao_jornada_5x2 é booleana (1 = na semana 5x2 o limite diário é a carga semanal ÷ dias de trabalho, até 10h).");
            case RuleKeys.CargaMinimaRegime6x1 when r.Valor <= 0 || r.Valor > 168:
                return ServiceDefaults.ValidationError("valor", "Informe a carga semanal (horas) a partir da qual a semana é 6x1, entre 0 e 168.");
            case RuleKeys.CargaMinimaRegime6x1 when r.Unidade?.Trim().ToLowerInvariant() != "horas":
                return ServiceDefaults.ValidationError("unidade", "O limiar do regime 6x1 é em horas (como a carga semanal).");
        }

        var sameKey = await db.Rules.AsNoTracking().Where(x => x.Chave == chave && x.Id != (id ?? 0)).ToListAsync();
        if (sameKey.FirstOrDefault(x => x.Sobrepoe(r.VigenciaInicio, r.VigenciaFim)) is { } other)
        {
            var periodo = other.VigenciaInicio is null && other.VigenciaFim is null
                ? "sem vigência (vale sempre)"
                : $"de {other.VigenciaInicio?.ToString("dd/MM/yyyy") ?? "início"} a {other.VigenciaFim?.ToString("dd/MM/yyyy") ?? "sem fim"}";
            return ServiceDefaults.ValidationError("vigenciaInicio", $"A regra {chave} já tem a linha {other.Id} {periodo}; as vigências não podem se sobrepor.");
        }
        return null;
    }

    /// <summary>Valor abaixo do piso / acima do teto legal só com confirmação explícita (confirmarAbaixoDoPisoLegal).</summary>
    private static IResult? CheckLegalLimit(ComplianceRule rule, decimal valor, bool confirmed)
    {
        if (!rule.ForaDoLimiteLegal(valor) || confirmed) return null;
        var detalhe = valor < rule.PisoLegal
            ? $"O valor {N(valor)} fica abaixo do piso legal ({N(rule.PisoLegal!.Value)} {rule.Unidade})"
            : $"O valor {N(valor)} passa do teto legal ({N(rule.TetoLegal!.Value)} {rule.Unidade})";
        return ServiceDefaults.ValidationError("confirmarAbaixoDoPisoLegal",
            $"{detalhe}{(rule.BaseLegal is { Length: > 0 } b ? $" — {b}" : "")}. Para gravar mesmo assim, confirme (confirmarAbaixoDoPisoLegal = true).");
    }

    private static void Apply(ComplianceRule rule, ComplianceRuleRequest r, ICurrentUser user)
    {
        rule.Categoria = r.Categoria.Trim().ToLowerInvariant();
        rule.Chave = r.Chave.Trim();
        rule.Valor = r.Valor;
        rule.Unidade = r.Unidade.Trim().ToLowerInvariant();
        rule.Descricao = r.Descricao.Trim();
        rule.BaseLegal = string.IsNullOrWhiteSpace(r.BaseLegal) ? null : r.BaseLegal.Trim();
        rule.VigenciaInicio = r.VigenciaInicio;
        rule.VigenciaFim = r.VigenciaFim;
        rule.Aplicacao = r.Aplicacao;
        if (LegalLimits.For(rule.Chave, rule.VigenciaInicio, rule.VigenciaFim) is { } limits)
        {
            rule.PisoLegal = limits.Piso;
            rule.TetoLegal = limits.Teto;
        }
        rule.AtualizadoPor = user.Display;
        rule.AtualizadoEm = DateTime.UtcNow;
    }

    private static ComplianceRuleChanged Event(ComplianceRule r, string acao, decimal? valorAnterior, RuleStatus? statusAnterior,
        bool foraDoLimite, bool confirmado, string? motivo, ICurrentUser user) => new(
        r.Id, r.Chave, acao, valorAnterior, r.Valor, statusAnterior?.ToString(), r.Status.ToString(), r.Aplicacao.ToString(),
        r.VigenciaInicio, r.VigenciaFim, foraDoLimite, confirmado, motivo, user.Display, DateTime.UtcNow);

    public static void MapRuleEndpoints(this IEndpointRouteBuilder app)
    {
        // Cadastro de regras (listar, visualizar, criar, editar, bloquear, desbloquear): só Admin (superAdmin da intranet).
        // Os outros serviços leem as regras por /internal/rules (X-Internal-Key), sem o token do usuário.
        var group = app.MapGroup("/api/rules").RequireAuthorization();

        group.MapGet("/", async (ComplianceDbContext db) =>
            Results.Ok((await db.Rules.AsNoTracking().OrderBy(r => r.Categoria).ThenBy(r => r.Chave).ThenBy(r => r.VigenciaInicio).ToListAsync())
                .Select(ToResponse))).RequireAuthorization(Policies.Admin);

        group.MapGet("/{id:int}", async (int id, ComplianceDbContext db) =>
            await db.Rules.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id) is { } rule ? Results.Ok(ToResponse(rule)) : Results.NotFound())
            .RequireAuthorization(Policies.Admin);

        // Valor vigente hoje (regra ativa) de uma das chaves liberadas a qualquer usuário logado — hoje só o intervalo padrão,
        // usado pela tela da loja (Gestor) ao criar turno. Sem regra ativa vigente: 404.
        group.MapGet("/vigentes/{chave}", async (string chave, ComplianceDbContext db, CancellationToken ct) =>
        {
            if (!ChavesPublicas.Contains(chave)) return Results.NotFound();
            var hoje = DateOnly.FromDateTime(DateTime.Today);
            var regra = await db.Rules.AsNoTracking()
                .Where(r => r.Chave == chave && r.Status == RuleStatus.Ativo
                    && (r.VigenciaInicio == null || r.VigenciaInicio <= hoje) && (r.VigenciaFim == null || r.VigenciaFim >= hoje))
                .OrderByDescending(r => r.VigenciaInicio).FirstOrDefaultAsync(ct);
            return regra is null ? Results.NotFound() : Results.Ok(new RuleValueResponse(regra.Chave, regra.Valor, regra.Unidade));
        });

        group.MapPost("/", async (ComplianceRuleRequest req, ComplianceDbContext db, IPublishEndpoint bus, ICurrentUser user) =>
        {
            if (await ValidateAsync(req, null, db) is { } invalid) return invalid;
            var rule = new ComplianceRule { Status = RuleStatus.Ativo };
            Apply(rule, req, user);
            var confirmed = req.ConfirmarAbaixoDoPisoLegal == true;
            if (CheckLegalLimit(rule, rule.Valor, confirmed) is { } limit) return limit;

            db.Rules.Add(rule);
            await db.SaveChangesAsync();
            await bus.Publish(Event(rule, "criada", null, null, rule.ForaDoLimiteLegal(rule.Valor), confirmed, null, user));
            return Results.Created($"/api/rules/{rule.Id}", ToResponse(rule));
        }).RequireAuthorization(Policies.Admin);

        group.MapPut("/{id:int}", async (int id, ComplianceRuleRequest req, ComplianceDbContext db, IPublishEndpoint bus, ICurrentUser user) =>
        {
            var rule = await db.Rules.FirstOrDefaultAsync(r => r.Id == id);
            if (rule is null) return Results.NotFound();
            if (await ValidateAsync(req, id, db) is { } invalid) return invalid;
            var before = rule.Valor;
            Apply(rule, req, user);
            var confirmed = req.ConfirmarAbaixoDoPisoLegal == true;
            if (CheckLegalLimit(rule, rule.Valor, confirmed) is { } limit) return limit;

            await db.SaveChangesAsync();
            await bus.Publish(Event(rule, "alterada", before, rule.Status, rule.ForaDoLimiteLegal(rule.Valor), confirmed, null, user));
            return Results.Ok(ToResponse(rule));
        }).RequireAuthorization(Policies.Admin);

        group.MapPost("/{id:int}/bloquear", async (int id, [FromBody] RuleStatusRequest? req, ComplianceDbContext db, IPublishEndpoint bus, ICurrentUser user) =>
        {
            var rule = await db.Rules.FirstOrDefaultAsync(r => r.Id == id);
            if (rule is null) return Results.NotFound();
            if (rule.Status == RuleStatus.Bloqueado) return Results.Ok(ToResponse(rule));
            var legal = rule.PisoLegal is not null || rule.TetoLegal is not null;
            var confirmed = req?.ConfirmarAbaixoDoPisoLegal == true;
            if (legal && !confirmed)
                return ServiceDefaults.ValidationError("confirmarAbaixoDoPisoLegal",
                    $"A regra {rule.Chave} tem limite legal{(rule.BaseLegal is { Length: > 0 } b ? $" ({b})" : "")}: bloqueá-la deixa a escala sem essa proteção. " +
                    "Para bloquear mesmo assim, confirme (confirmarAbaixoDoPisoLegal = true).");

            rule.Status = RuleStatus.Bloqueado;
            rule.AtualizadoPor = user.Display;
            rule.AtualizadoEm = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await bus.Publish(Event(rule, "bloqueada", rule.Valor, RuleStatus.Ativo, legal, confirmed, req?.Motivo, user));
            return Results.Ok(ToResponse(rule));
        }).RequireAuthorization(Policies.Admin);

        group.MapPost("/{id:int}/desbloquear", async (int id, [FromBody] RuleStatusRequest? req, ComplianceDbContext db, IPublishEndpoint bus, ICurrentUser user) =>
        {
            var rule = await db.Rules.FirstOrDefaultAsync(r => r.Id == id);
            if (rule is null) return Results.NotFound();
            if (rule.Status == RuleStatus.Ativo) return Results.Ok(ToResponse(rule));
            rule.Status = RuleStatus.Ativo;
            rule.AtualizadoPor = user.Display;
            rule.AtualizadoEm = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await bus.Publish(Event(rule, "desbloqueada", rule.Valor, RuleStatus.Bloqueado, false, false, req?.Motivo, user));
            return Results.Ok(ToResponse(rule));
        }).RequireAuthorization(Policies.Admin);

        // ---------- Interno: fotografia da escala — linhas (ativas e bloqueadas) com vigência no horizonte ----------
        // As bloqueadas vão junto para o avaliador mostrá-las como "bloqueadas"; motor e avaliador só aplicam as ativas.
        app.MapInternalGroup().MapGet("/rules", async (DateOnly from, DateOnly to, ComplianceDbContext db) =>
        {
            var rules = await db.Rules.AsNoTracking()
                .Where(r => (r.VigenciaInicio == null || r.VigenciaInicio <= to) && (r.VigenciaFim == null || r.VigenciaFim >= from))
                .OrderBy(r => r.Chave).ThenBy(r => r.VigenciaInicio).ToListAsync();
            return Results.Ok(rules.Select(r => r.ToSnapshot()));
        });
    }
}

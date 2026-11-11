using Escala5x2.BuildingBlocks.Compliance;
using Escala5x2.BuildingBlocks.Contracts.Compliance;
using Escala5x2.BuildingBlocks.Contracts.Http;
using Escala5x2.BuildingBlocks.Contracts.Planning;
using Escala5x2.BuildingBlocks.Contracts.Stores;
using Escala5x2.BuildingBlocks.Shared.Auth;
using Escala5x2.BuildingBlocks.Shared.Web;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Store.Api.Data;
using Store.Api.Domain;
using Store.Api.Lg;

namespace Store.Api.Endpoints;

public sealed record CreateStoreRequest(string Code, string Name, string City, string State, StoreFormat Format, bool HasExtendedDayAgreement);
/// <summary>Edição da loja (Admin). O código não muda: é o vínculo com a loja do perfil da intranet.</summary>
public sealed record UpdateStoreRequest(string Name, string? City, string State, StoreFormat Format, bool HasExtendedDayAgreement);
public sealed record CreateSectorRequest(string Name);
/// <summary>
/// Setor no detalhe da loja. <paramref name="Origem"/>: Manual (gestor) ou Lg (criado pela integração a partir do
/// OrgaoResponsavel); <paramref name="LgOrgaoCodigo"/>/<paramref name="LgOrgaoDescricao"/>: órgão da LG ligado ao setor
/// (também num setor manual adotado pelo nome). <paramref name="Active"/>: false = setor da LG inativado pelo catálogo de
/// órgãos (fora de novas escalas/edição; escalas e colaboradores que já o usam continuam).
/// </summary>
public sealed record SectorResponse(Guid SectorId, string Name, string Origem, int? LgOrgaoCodigo, string? LgOrgaoDescricao, DateTime? LgSincronizadoEm,
    bool Active = true)
{
    public static SectorResponse Of(Sector s) => new(s.Id, s.Name, s.Origem.ToString(), s.LgOrgaoCodigo, s.LgOrgaoDescricao, s.LgSincronizadoEm, s.Active);
}
/// <summary>Criação e edição de turno. BreakMinutes vazio = intervalo_padrao_minutos do cadastro de regras de conformidade.</summary>
public sealed record CreateShiftRequest(string Code, TimeOnly Start, TimeOnly End, int? BreakMinutes);
public sealed record CreateDemandRequest(Guid SectorId, DayOfWeek DayOfWeek, TimeOnly Start, TimeOnly End, int MinStaff);

/// <summary>
/// <paramref name="Tipo"/>: classificação pela operação da loja (Abertura | Fechamento | Flexivel; null = sem operação ou
/// fora dela, só escala manual). Calculada pelo horário (OperationPlan.Classify), no detalhe da loja.
/// </summary>
public sealed record ShiftResponse(Guid Id, string Code, TimeOnly Start, TimeOnly End, int BreakMinutes, int PaidMinutes, bool Active, string? Tipo = null);
/// <summary>
/// Referência mínima da loja para os serviços 5x2 casarem o código da loja do perfil da intranet e o centro de custo da
/// LG (<paramref name="LgCentroDeCustoCodigo"/>, usado primeiro pelo Employee.Api).
/// </summary>
public sealed record StoreReference(Guid Id, string Code, string Name, string? LgCentroDeCustoCodigo, bool Active);
/// <summary>
/// Loja na lista/seletor. <paramref name="Active"/>: false = centro de custo encerrado na LG (a tela só mostra ao Admin,
/// marcada "inativa"); <paramref name="LgCentroDeCustoCodigo"/>: centro de custo LG que define a loja (null = cadastro manual
/// ou da lista da intranet ainda não sincronizado).
/// </summary>
public sealed record StoreSummary(Guid Id, string Code, string Name, string City, string State, StoreFormat Format, bool HasExtendedDayAgreement,
    bool Active = true, string? LgCentroDeCustoCodigo = null, DateTime? LgSincronizadoEm = null, string? CodigoIbge = null);
/// <summary>
/// Detalhe da loja. Feriados: GET /api/stores/{id}/holidays?ano= (HolidayEndpoints).
/// <paramref name="Operacao"/>: operação da loja (null = não configurada: modelo anterior, só demanda);
/// <paramref name="OperacoesSetor"/>: exceções por setor (o setor sem exceção usa a da loja).
/// <paramref name="Cadastro"/>: endereço, localização e horário de funcionamento (StoreCadastroEndpoints);
/// <paramref name="PicosManuais"/>: horários de pico cadastrados à mão.
/// </summary>
public sealed record StoreDetail(
    StoreSummary Store, List<SectorResponse> Sectors, List<ShiftResponse> Shifts,
    List<StaffDemand> Demands, OperationResponse? Operacao = null, List<SectorOperationResponse>? OperacoesSetor = null,
    StoreCadastroResponse? Cadastro = null, List<ManualPeakResponse>? PicosManuais = null);

/// <summary>
/// Operação (horários "HH:mm"). Entradas flexíveis e intervalo já resolvidos (efetivos); <paramref name="EntradaFlexivelAutomatica"/>
/// indica que a janela vem do padrão (abertura + incremento / fechamento − duração − incremento) e
/// <paramref name="IntervaloDaRegra"/> que o intervalo vem de intervalo_padrao_minutos (null se a regra não pôde ser lida).
/// <paramref name="RodizioAberturaFechamento"/>: rodízio entre os colaboradores do setor quando faltar gente com perfil
/// Abertura/Fechamento (ver <see cref="OperationSnapshot"/>). <paramref name="FolgasFixasSetorDuasPessoas"/>: setor com 2
/// colaboradores recebe folgas fixas e intercaladas. <paramref name="CoberturaComoMetaQuadroInsuficiente"/>: gerar mesmo
/// quando o quadro não cobre o mínimo (cobertura como meta). <paramref name="TurnosAutomaticos"/>: turnos derivados do
/// horário de cada dia e das regras (prévia em GET /api/stores/{id}/turnos-automaticos); false = "Personalizar turnos"
/// (valem a duração, o intervalo e a janela flexível acima).
/// </summary>
public sealed record OperationResponse(
    string Abertura, string Fechamento, int CoberturaMinima, int MinimoAbertura, int MinimoFechamento, int IncrementoFlexivelMinutos,
    string EntradaFlexivelMaisCedo, string EntradaFlexivelMaisTarde, int DuracaoJornadaMinutos, int? IntervaloPadraoMinutos,
    bool IntervaloDaRegra, bool EntradaFlexivelAutomatica, IReadOnlyList<string> EntradasFlexiveis, string AtualizadoPor, DateTime AtualizadoEm,
    bool RodizioAberturaFechamento = true, bool ExpedienteDoHorarioDaLoja = false, bool FolgasFixasSetorDuasPessoas = true,
    bool CoberturaComoMetaQuadroInsuficiente = true, bool TurnosAutomaticos = true);

public sealed record SectorOperationResponse(Guid SectorId, string SectorName, OperationResponse Operacao);

/// <summary>
/// Edição da operação. Horários "HH:mm"; entradas flexíveis/duração/intervalo vazios = padrão.
/// <paramref name="RodizioAberturaFechamento"/>, <paramref name="FolgasFixasSetorDuasPessoas"/>,
/// <paramref name="CoberturaComoMetaQuadroInsuficiente"/> e <paramref name="TurnosAutomaticos"/>: null = mantém o valor
/// atual (nova operação: ligado). Com turnos automáticos a duração/intervalo/janela flexível não são exigidos; vazios
/// mantêm os personalizados já gravados (voltam a valer em "Personalizar turnos").
/// </summary>
public sealed record OperationRequest(
    string? Abertura, string? Fechamento, int CoberturaMinima, int MinimoAbertura, int MinimoFechamento, int IncrementoFlexivelMinutos,
    string? EntradaFlexivelMaisCedo, string? EntradaFlexivelMaisTarde, int? DuracaoJornadaMinutos, int? IntervaloMinutos,
    bool? RodizioAberturaFechamento = null, bool? FolgasFixasSetorDuasPessoas = null, bool? CoberturaComoMetaQuadroInsuficiente = null,
    bool? TurnosAutomaticos = null);

/// <summary>Turno automático da prévia: tipo (Abertura | Fechamento | Flexivel), horário "HH:mm", intervalo e minutos pagos.</summary>
public sealed record AutoShiftResponse(string Tipo, string Codigo, string Inicio, string Fim, int IntervaloMinutos, int PagosMinutos);

/// <summary>
/// Dias do mês com o mesmo expediente e a mesma jornada: <paramref name="Dias"/> ("seg, ter, qua" ou as datas quando o
/// grupo não pega todos aqueles dias da semana), <paramref name="Base"/> (de onde vem a jornada: "42h ÷ 5 dias (5x2) =
/// 8h24; limite de domingo 8h"), <paramref name="Feriado"/> (feriado com horário especial no grupo) e os turnos.
/// </summary>
public sealed record AutoShiftGroup(string Dias, List<DateOnly> Datas, string Expediente, int JornadaPagaMinutos, int IntervaloMinutos,
    string Base, string? Regime, string? Feriado, List<AutoShiftResponse> Turnos);

/// <summary>
/// Prévia dos turnos do mês (somente leitura), exatamente como a geração os monta (PlanningContext.ForOperation).
/// <paramref name="Aviso"/>: regras indisponíveis, sem operação, dias abertos sem turno possível.
/// </summary>
public sealed record AutoShiftPreview(string Mes, bool TurnosAutomaticos, string? Aviso, List<AutoShiftGroup> Grupos);

public static class StoreEndpoints
{
    private static StoreSummary Summary(Domain.Store s) =>
        new(s.Id, s.Code, s.Name, s.City, s.State, s.Format, s.HasExtendedDayAgreement, s.Active, s.LgCentroDeCustoCodigo, s.LgSincronizadoEm,
            s.CodigoIbge);
    private static ShiftResponse ToResponse(Shift s, OperationSnapshot? op = null) =>
        new(s.Id, s.Code, s.Start, s.End, s.BreakMinutes, s.PaidMinutes, s.Active, op is null ? null : OperationPlan.Classify(op, s.Start, s.End));

    private static string Describe(Shift s) => $"{s.Code} {s.Start:HH\\:mm}–{s.End:HH\\:mm}, intervalo {s.BreakMinutes} min";

    /// <summary>
    /// Valida o turno (criação e edição) e resolve o intervalo (vazio = intervalo_padrao_minutos): código obrigatório e
    /// único entre os turnos ativos da loja, início diferente do fim (fim antes do início = vira o dia) e intervalo da
    /// CLT art. 71. Devolve o intervalo ou o erro de validação.
    /// </summary>
    private static async Task<(int BreakMinutes, IResult? Error)> ValidateShiftAsync(Guid storeId, Guid? shiftId, string code,
        TimeOnly start, TimeOnly end, int? breakMinutes, StoreDbContext db, ComplianceRulesClient rules, bool checkCode = true)
    {
        if (code.Length == 0) return (0, ServiceDefaults.ValidationError("code", "Informe o código do turno."));
        if (code.Length > 20) return (0, ServiceDefaults.ValidationError("code", "O código tem no máximo 20 caracteres."));
        if (start == end) return (0, ServiceDefaults.ValidationError("end", "O fim precisa ser diferente do início."));
        if (checkCode && await db.Shifts.AnyAsync(s => s.StoreId == storeId && s.Active && s.Id != shiftId && s.Code == code))
            return (0, ServiceDefaults.ValidationError("code", $"Já existe turno ativo com o código {code} nesta loja."));
        if (breakMinutes is null)
        {
            // Sem intervalo informado: usa o padrão do cadastro de regras (o avaliador aponta turnos fora do padrão)
            var standard = await rules.ActiveValueAsync(RuleKeys.IntervaloPadrao, DateOnly.FromDateTime(DateTime.Today));
            if (standard is null)
                return (0, ServiceDefaults.ValidationError("breakMinutes", "Informe o intervalo: não foi possível ler a regra intervalo_padrao_minutos."));
            breakMinutes = (int)standard.Value;
        }
        var probe = new Shift { Start = start, End = end, BreakMinutes = breakMinutes.Value };
        if (ValidateBreak(probe.DurationMinutes, probe.BreakMinutes) is { } error)
            return (0, ServiceDefaults.ValidationError("breakMinutes", error));
        return (breakMinutes.Value, null);
    }

    /// <summary>
    /// Nome do setor manual: obrigatório, até 100 caracteres e sem repetir outro setor da loja pelo nome normalizado
    /// (sem acento/maiúsculas/espaços extras: "Depósito" = "DEPOSITO"), inclusive os criados pela LG.
    /// </summary>
    public static IResult? ValidateSectorName(string? name, IEnumerable<Sector> existentes)
    {
        var nome = LgSetoresRegras.NomeDoOrgao(name);
        if (nome.Length == 0) return ServiceDefaults.ValidationError("name", "Informe o nome do setor.");
        if ((name ?? string.Empty).Trim().Length > LgSetoresRegras.MaxNome)
            return ServiceDefaults.ValidationError("name", $"O nome tem no máximo {LgSetoresRegras.MaxNome} caracteres.");
        var chave = LgSetoresRegras.Chave(nome);
        return existentes.FirstOrDefault(s => LgSetoresRegras.Chave(s.Name) == chave) is { } igual
            ? ServiceDefaults.ValidationError("name", igual.Origem == SectorOrigem.Lg || igual.LgOrgaoCodigo is not null
                ? $"Já existe o setor {igual.Name} nesta loja (órgão {igual.LgOrgaoCodigo} da LG{(igual.Active ? "" : ", inativo")})."
                : $"Já existe o setor {igual.Name} nesta loja.")
            : null;
    }

    /// <summary>CLT art. 71: acima de 6h, 1h a 2h de intervalo; acima de 4h até 6h, 15 min.</summary>
    public static string? ValidateBreak(int durationMinutes, int breakMinutes)
    {
        var worked = durationMinutes - breakMinutes;
        if (breakMinutes < 0) return "Intervalo não pode ser negativo.";
        if (worked > 360 && (breakMinutes < 60 || breakMinutes > 120))
            return "Jornada acima de 6h exige intervalo entre 60 e 120 minutos (CLT art. 71).";
        if (worked > 240 && worked <= 360 && breakMinutes < 15)
            return "Jornada acima de 4h até 6h exige intervalo de pelo menos 15 minutos (CLT art. 71, §1º).";
        return null;
    }

    /// <summary>
    /// Gestor só altera as lojas em que é Gestor (rota {id}: a do perfil e as das roles de loja, ver EscopoLojas); Admin
    /// qualquer uma (os endpoints com este filtro exigem a política Manager: Admin ou Gestor).
    /// </summary>
    private static async ValueTask<object?> OwnStoreOnly(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var user = ctx.HttpContext.RequestServices.GetRequiredService<ICurrentUser>();
        if (Guid.TryParse(ctx.HttpContext.Request.RouteValues["id"]?.ToString(), out var storeId) && !user.CanManageStore(storeId))
            return Results.Forbid();
        return await next(ctx);
    }

    public static void MapStoreEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/stores").RequireAuthorization();

        group.MapGet("/", async (StoreDbContext db) =>
            Results.Ok((await db.Stores.OrderBy(s => s.Code).ToListAsync()).Select(Summary)));

        group.MapGet("/{id:guid}", async (Guid id, StoreDbContext db, ComplianceRulesClient rules) =>
        {
            var s = await db.Stores.Include(x => x.Sectors).Include(x => x.Shifts).Include(x => x.Demands)
                .AsSplitQuery().FirstOrDefaultAsync(x => x.Id == id);
            if (s is null) return Results.NotFound();
            var ops = await db.Operations.AsNoTracking().Where(o => o.StoreId == id).ToListAsync();
            var standard = ops.Any(o => o.IntervaloMinutos is null)
                ? await rules.ActiveValueAsync(RuleKeys.IntervaloPadrao, DateOnly.FromDateTime(DateTime.Today))
                : null;
            var names = s.Sectors.ToDictionary(x => x.Id, x => x.Name);
            var horarios = await db.StoreHours.AsNoTracking().Where(h => h.StoreId == id).ToListAsync();
            var picos = await db.ManualPeaks.AsNoTracking().Where(p => p.StoreId == id).ToListAsync();
            // Classificação dos turnos com o horário de funcionamento (abertura/fechamento de cada dia)
            var storeOp = ops.FirstOrDefault(o => o.SectorId is null)?.ToSnapshot() is { } so
                ? so with { Horarios = StoreCadastroEndpoints.DayHoursOf(horarios) }
                : null;
            return Results.Ok(new StoreDetail(Summary(s),
                s.Sectors.OrderBy(x => x.Name).Select(SectorResponse.Of).ToList(),
                s.Shifts.OrderBy(x => x.Start).Select(x => ToResponse(x, storeOp)).ToList(),
                s.Demands.OrderBy(x => x.DayOfWeek).ThenBy(x => x.Start).ToList(),
                ops.FirstOrDefault(o => o.SectorId is null) is { } store ? ToResponse(store, standard) with { ExpedienteDoHorarioDaLoja = horarios.Count > 0 } : null,
                ops.Where(o => o.SectorId is { } sid && names.ContainsKey(sid))
                    .Select(o => new SectorOperationResponse(o.SectorId!.Value, names[o.SectorId.Value], ToResponse(o, standard)))
                    .OrderBy(o => o.SectorName).ToList(),
                StoreCadastroEndpoints.Cadastro(s, horarios),
                picos.OrderBy(p => p.Data is null ? 0 : 1).ThenBy(p => p.Data).ThenBy(p => p.DiaSemana is { } d ? ((int)d + 6) % 7 : 0)
                    .ThenBy(p => p.Inicio).Select(p => StoreCadastroEndpoints.ToResponse(p, names)).ToList()));
        });

        // Prévia (somente leitura) dos turnos do mês por dia, como a geração os monta: turnos automáticos pelo horário de cada
        // dia e pelas regras vigentes (ou os personalizados). ?mes=AAAA-MM (padrão: mês atual) e ?sectorId= (exceção do setor).
        group.MapGet("/{id:guid}/turnos-automaticos", (Guid id, string? mes, Guid? sectorId, StoreDbContext db, ComplianceRulesClient rules,
            CancellationToken ct) => PreviewAsync(id, mes, sectorId, db, rules, ct));

        // ---------- Operação da loja (Gestor da loja e Admin) ----------
        group.MapPut("/{id:guid}/operacao", (Guid id, OperationRequest req, StoreDbContext db, IPublishEndpoint bus, ICurrentUser user,
                ComplianceRulesClient rules) => SaveOperationAsync(id, null, req, db, bus, user, rules))
            .RequireAuthorization(Policies.Manager).AddEndpointFilter(OwnStoreOnly);
        group.MapDelete("/{id:guid}/operacao", (Guid id, StoreDbContext db, IPublishEndpoint bus, ICurrentUser user) =>
                DeleteOperationAsync(id, null, db, bus, user))
            .RequireAuthorization(Policies.Manager).AddEndpointFilter(OwnStoreOnly);
        group.MapPut("/{id:guid}/sectors/{sectorId:guid}/operacao", (Guid id, Guid sectorId, OperationRequest req, StoreDbContext db,
                IPublishEndpoint bus, ICurrentUser user, ComplianceRulesClient rules) => SaveOperationAsync(id, sectorId, req, db, bus, user, rules))
            .RequireAuthorization(Policies.Manager).AddEndpointFilter(OwnStoreOnly);
        group.MapDelete("/{id:guid}/sectors/{sectorId:guid}/operacao", (Guid id, Guid sectorId, StoreDbContext db, IPublishEndpoint bus, ICurrentUser user) =>
                DeleteOperationAsync(id, sectorId, db, bus, user))
            .RequireAuthorization(Policies.Manager).AddEndpointFilter(OwnStoreOnly);

        // Roles por loja da intranet ("Escala5x2-Loja-{code}") com a loja de cada uma, para o painel de gestão (superAdmin)
        group.MapGet("/roles", async (RolesLojasIntranet roles, CancellationToken ct) => Results.Ok(new
        {
            modelo = roles.Modelo.Modelo,
            roles = await roles.ListarAsync(ct),
        })).RequireAuthorization(Policies.Admin);

        // "Garantir roles das lojas" (Admin; útil no deploy): pede ao AuthAPI a role de cada loja cadastrada e aguarda o
        // resultado. Idempotente; não apaga roles (loja inativa mantém a sua). 503 = AuthAPI/RabbitMQ sem resposta.
        group.MapPost("/garantir-roles", async (RolesLojasIntranet roles, ICurrentUser user, CancellationToken ct) =>
            await roles.GarantirTodasAsync(user.Display, ct) is { } r
                ? Results.Ok(r)
                : Results.Problem(title: "AuthAPI indisponível", detail: "O AuthAPI da intranet não respondeu (RabbitMQ/AuthAPI fora do ar). Tente de novo.",
                    statusCode: StatusCodes.Status503ServiceUnavailable))
            .RequireAuthorization(Policies.Admin);

        group.MapPost("/", async (CreateStoreRequest req, StoreDbContext db, IPublishEndpoint bus, ICurrentUser user, RolesLojasIntranet roles) =>
        {
            // Mesmo código normalizado do vínculo com o perfil da intranet ("04" = "4", "cd" = "CD")
            var normalized = CodigoLoja.Normalizar(req.Code);
            if (normalized is null) return ServiceDefaults.ValidationError("code", "Informe o código da loja.");
            if ((await db.Stores.AsNoTracking().Select(s => s.Code).ToListAsync()).Any(c => CodigoLoja.Normalizar(c) == normalized))
                return ServiceDefaults.ValidationError("code", "Já existe loja com este código.");
            var store = new Domain.Store
            {
                Code = req.Code.Trim(), Name = req.Name.Trim(), City = req.City.Trim(), State = req.State.Trim(),
                Format = req.Format, HasExtendedDayAgreement = req.HasExtendedDayAgreement
            };
            db.Stores.Add(store);
            await db.SaveChangesAsync();
            await bus.Publish(new StoreCreated(store.Id, store.Code, store.Name, user.Display, DateTime.UtcNow));
            // Role da loja na intranet ("Escala5x2-Loja-{code}") para o superAdmin atribuir
            await roles.PublicarAsync([store.Code], $"loja {store.Code} criada por {user.Display}");
            return Results.Created($"/api/stores/{store.Id}", Summary(store));
        }).RequireAuthorization(Policies.Admin);

        // Nome, cidade (feriados municipais), UF, formato e norma de 8h24: Admin. As lojas da rede cadastradas na
        // inicialização chegam sem cidade e como Varejo; o código não muda (vínculo com o perfil da intranet). Loja vinculada
        // à LG: nome e formato voltam ao do centro de custo na próxima sincronização de lojas.
        group.MapPut("/{id:guid}", async (Guid id, UpdateStoreRequest req, StoreDbContext db, IPublishEndpoint bus, ICurrentUser user) =>
        {
            var store = await db.Stores.FirstOrDefaultAsync(s => s.Id == id);
            if (store is null) return Results.NotFound();
            var name = req.Name?.Trim() ?? string.Empty;
            var state = req.State?.Trim().ToUpperInvariant() ?? string.Empty;
            if (name.Length == 0 || name.Length > 200) return ServiceDefaults.ValidationError("name", "Informe o nome (até 200 caracteres).");
            if (state.Length != 2 || !state.All(char.IsLetter)) return ServiceDefaults.ValidationError("state", "Informe a UF (sigla).");
            if (!Enum.IsDefined(req.Format)) return ServiceDefaults.ValidationError("format", "Formato inválido: Varejo, Atacarejo ou Atacado.");
            string Text(Domain.Store s) =>
                $"{s.Name}, {(s.City.Length > 0 ? s.City : "sem cidade")}/{s.State}, {s.Format}{(s.HasExtendedDayAgreement ? ", norma 8h24" : "")}";
            var before = Text(store);
            store.Name = name;
            store.City = req.City?.Trim() ?? string.Empty;
            store.State = state;
            store.Format = req.Format;
            store.HasExtendedDayAgreement = req.HasExtendedDayAgreement;
            var after = Text(store);
            if (after == before) return Results.Ok(Summary(store));
            await db.SaveChangesAsync();
            await bus.Publish(new StorePlanningChanged(id, $"Loja {store.Code} alterada: {after} (antes: {before})", user.Display, DateTime.UtcNow));
            return Results.Ok(Summary(store));
        }).RequireAuthorization(Policies.Admin);

        group.MapPost("/{id:guid}/sectors", async (Guid id, CreateSectorRequest req, StoreDbContext db, IPublishEndpoint bus, ICurrentUser user) =>
        {
            if (!await db.Stores.AnyAsync(s => s.Id == id)) return Results.NotFound();
            var existentes = await db.Sectors.AsNoTracking().Where(s => s.StoreId == id).ToListAsync();
            if (ValidateSectorName(req.Name, existentes) is { } invalid) return invalid;
            var sector = new Sector { StoreId = id, Name = LgSetoresRegras.NomeDoOrgao(req.Name) };
            db.Sectors.Add(sector);
            await db.SaveChangesAsync();
            await bus.Publish(new StorePlanningChanged(id, $"Setor {sector.Name} criado", user.Display, DateTime.UtcNow));
            return Results.Ok(SectorResponse.Of(sector));
        }).RequireAuthorization(Policies.Manager).AddEndpointFilter(OwnStoreOnly);

        group.MapPost("/{id:guid}/shifts", async (Guid id, CreateShiftRequest req, StoreDbContext db, IPublishEndpoint bus, ICurrentUser user,
            ComplianceRulesClient rules) =>
        {
            if (!await db.Stores.AnyAsync(s => s.Id == id)) return Results.NotFound();
            var code = req.Code?.Trim().ToUpperInvariant() ?? string.Empty;
            var (breakMinutes, invalid) = await ValidateShiftAsync(id, null, code, req.Start, req.End, req.BreakMinutes, db, rules);
            if (invalid is not null) return invalid;
            var shift = new Shift { StoreId = id, Code = code, Start = req.Start, End = req.End, BreakMinutes = breakMinutes };
            db.Shifts.Add(shift);
            await db.SaveChangesAsync();
            await bus.Publish(new StorePlanningChanged(id, $"Turno {shift.Code} criado", user.Display, DateTime.UtcNow));
            return Results.Ok(ToResponse(shift));
        }).RequireAuthorization(Policies.Manager).AddEndpointFilter(OwnStoreOnly);

        // Edição do turno do catálogo (código, horário, intervalo), com as validações da criação. Mantém o Id: as escalas
        // existentes guardam o horário em cada célula e a fotografia dos turnos, então a mudança vale para as próximas
        // gerações (e para "gerar de novo", que atualiza os turnos da fotografia). A classificação como abertura/
        // fechamento/flexível é pelo horário (OperationPlan.Classify), refeita a cada geração.
        group.MapPut("/{id:guid}/shifts/{shiftId:guid}", async (Guid id, Guid shiftId, CreateShiftRequest req, StoreDbContext db,
            IPublishEndpoint bus, ICurrentUser user, ComplianceRulesClient rules) =>
        {
            var shift = await db.Shifts.FirstOrDefaultAsync(s => s.Id == shiftId && s.StoreId == id);
            if (shift is null) return Results.NotFound();
            var code = req.Code?.Trim().ToUpperInvariant() ?? string.Empty;
            // Turno inativo: o código só precisa ser único entre os ativos ao reativar
            var (breakMinutes, invalid) = await ValidateShiftAsync(id, shift.Id, code, req.Start, req.End, req.BreakMinutes, db, rules,
                checkCode: shift.Active);
            if (invalid is not null) return invalid;
            var before = Describe(shift);
            shift.Code = code;
            shift.Start = req.Start;
            shift.End = req.End;
            shift.BreakMinutes = breakMinutes;
            var after = Describe(shift);
            if (after == before) return Results.Ok(ToResponse(shift));
            await db.SaveChangesAsync();
            await bus.Publish(new StorePlanningChanged(id, $"Turno alterado: {after} (antes: {before})", user.Display, DateTime.UtcNow));
            return Results.Ok(ToResponse(shift));
        }).RequireAuthorization(Policies.Manager).AddEndpointFilter(OwnStoreOnly);

        group.MapPost("/{id:guid}/shifts/{shiftId:guid}/deactivate", async (Guid id, Guid shiftId, StoreDbContext db, IPublishEndpoint bus, ICurrentUser user) =>
        {
            var shift = await db.Shifts.FirstOrDefaultAsync(s => s.Id == shiftId && s.StoreId == id);
            if (shift is null) return Results.NotFound();
            if (!shift.Active) return Results.NoContent();
            shift.Active = false; // turnos não são apagados: escalas antigas os referenciam.
            await db.SaveChangesAsync();
            await bus.Publish(new StorePlanningChanged(id, $"Turno {Describe(shift)} desativado", user.Display, DateTime.UtcNow));
            return Results.NoContent();
        }).RequireAuthorization(Policies.Manager).AddEndpointFilter(OwnStoreOnly);

        // Reativa o turno desativado (volta ao catálogo das próximas gerações); o código não pode repetir um turno ativo.
        group.MapPost("/{id:guid}/shifts/{shiftId:guid}/activate", async (Guid id, Guid shiftId, StoreDbContext db, IPublishEndpoint bus, ICurrentUser user) =>
        {
            var shift = await db.Shifts.FirstOrDefaultAsync(s => s.Id == shiftId && s.StoreId == id);
            if (shift is null) return Results.NotFound();
            if (shift.Active) return Results.Ok(ToResponse(shift));
            if (await db.Shifts.AnyAsync(s => s.StoreId == id && s.Active && s.Code == shift.Code))
                return ServiceDefaults.ValidationError("code", $"Já existe turno ativo com o código {shift.Code}: edite o código antes de reativar.");
            shift.Active = true;
            await db.SaveChangesAsync();
            await bus.Publish(new StorePlanningChanged(id, $"Turno {Describe(shift)} reativado", user.Display, DateTime.UtcNow));
            return Results.Ok(ToResponse(shift));
        }).RequireAuthorization(Policies.Manager).AddEndpointFilter(OwnStoreOnly);

        group.MapPost("/{id:guid}/demands", async (Guid id, CreateDemandRequest req, StoreDbContext db, IPublishEndpoint bus, ICurrentUser user) =>
        {
            var sector = await db.Sectors.AsNoTracking().FirstOrDefaultAsync(s => s.Id == req.SectorId && s.StoreId == id);
            if (sector is null) return ServiceDefaults.ValidationError("sectorId", "Setor não pertence a esta loja.");
            // Setor inativo (órgão inativo/fora do catálogo da LG): as demandas existentes ficam, mas não recebe novas
            if (!sector.Active) return ServiceDefaults.ValidationError("sectorId", $"O setor {sector.Name} está inativo na LG.");
            if (req.End <= req.Start) return ServiceDefaults.ValidationError("end", "O fim precisa ser depois do início no mesmo dia.");
            if (req.MinStaff < 0) return ServiceDefaults.ValidationError("minStaff", "Quantidade inválida.");
            var demand = new StaffDemand { StoreId = id, SectorId = req.SectorId, DayOfWeek = req.DayOfWeek, Start = req.Start, End = req.End, MinStaff = req.MinStaff };
            db.Demands.Add(demand);
            await db.SaveChangesAsync();
            await bus.Publish(new StorePlanningChanged(id, "Demanda alterada", user.Display, DateTime.UtcNow));
            return Results.Ok(demand);
        }).RequireAuthorization(Policies.Manager).AddEndpointFilter(OwnStoreOnly);

        group.MapDelete("/{id:guid}/demands/{demandId:guid}", async (Guid id, Guid demandId, StoreDbContext db) =>
        {
            var d = await db.Demands.FirstOrDefaultAsync(x => x.Id == demandId && x.StoreId == id);
            if (d is null) return Results.NotFound();
            db.Demands.Remove(d);
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireAuthorization(Policies.Manager).AddEndpointFilter(OwnStoreOnly);

        // Feriados: HolidayEndpoints (/api/holidays e GET /api/stores/{id}/holidays). O antigo POST /api/stores/{id}/holidays
        // ("feriado da loja") deixou de existir: feriado municipal vale para todas as lojas do município.

        // ---------- Interno ----------
        var internalApi = app.MapInternalGroup();

        // Usado pelos demais serviços 5x2 (LojasEscalaHttpClient) para converter o código da loja do perfil da intranet em StoreId.
        internalApi.MapGet("/stores", async (StoreDbContext db) =>
            Results.Ok(await db.Stores.AsNoTracking().OrderBy(s => s.Code)
                .Select(s => new StoreReference(s.Id, s.Code, s.Name, s.LgCentroDeCustoCodigo, s.Active)).ToListAsync()));

        internalApi.MapGet("/stores/{id:guid}/planning", async (Guid id, DateOnly from, DateOnly to, StoreDbContext db) =>
        {
            var s = await db.Stores.Include(x => x.Sectors).Include(x => x.Shifts).Include(x => x.Demands).Include(x => x.Horarios)
                .AsSplitQuery().FirstOrDefaultAsync(x => x.Id == id);
            if (s is null) return Results.NotFound();
            // Nacionais + estaduais da UF + municipais do município (ou IBGE) + os da própria loja, recorrentes expandidos
            // para o horizonte, com o funcionamento da loja em cada um (exceção da loja → do feriado → padrão de feriados
            // da loja). Na mesma data vale o mais restritivo (HolidayCalendar.Snapshot).
            var holidays = (await db.Holidays.AsNoTracking().ToListAsync()).Select(h => h.ToDefinition());
            var excecoes = await db.HolidayStoreHours.AsNoTracking().Where(x => x.StoreId == id).ToDictionaryAsync(x => x.HolidayId, x => x.ToHours());
            // Operação efetiva por setor: a exceção do setor ou a da loja, com o horário de funcionamento da loja (a da loja
            // segue o horário de cada dia; a exceção do setor mantém o expediente dela, mas não abre quando a loja fecha)
            var horarios = StoreCadastroEndpoints.DayHoursOf(s.Horarios);
            var fechados = horarios?.Where(h => h.Fechada).ToList();
            var ops = await db.Operations.AsNoTracking().Where(o => o.StoreId == id).ToListAsync();
            var storeOp = ops.FirstOrDefault(o => o.SectorId is null);
            var operacoes = s.Sectors
                .Select(x => ops.FirstOrDefault(o => o.SectorId == x.Id) is { } so
                    ? new SectorOperation(x.Id, so.ToSnapshot() with { Horarios = fechados is { Count: > 0 } ? fechados : null }, true)
                    : storeOp is not null ? new SectorOperation(x.Id, storeOp.ToSnapshot() with { Horarios = horarios }, false) : null)
                .OfType<SectorOperation>().ToList();
            // Demanda: linhas comuns (sem as dos dias em que a loja fecha) + picos manuais (pico da loja: um por setor)
            var closedDays = fechados?.Select(h => h.DiaSemana).ToHashSet() ?? [];
            var picos = await db.ManualPeaks.AsNoTracking().Where(p => p.StoreId == id).ToListAsync();
            return Results.Ok(new StorePlanningResponse(
                s.Id, s.Name, s.HasExtendedDayAgreement,
                s.Sectors.Select(x => new SectorSnapshot(x.Id, x.Name)).ToList(),
                s.Shifts.Where(x => x.Active).Select(x => new ShiftSnapshot(x.Id, x.Code, x.Start, x.End, x.BreakMinutes)).ToList(),
                s.Demands.Where(x => !closedDays.Contains(x.DayOfWeek))
                    .Select(x => new DemandSnapshot(x.SectorId, x.DayOfWeek, x.Start, x.End, x.MinStaff))
                    .Concat(StoreCadastroEndpoints.PeakRows(picos.Where(p => p.Data is null ? true : p.Data >= from && p.Data <= to), s.Sectors))
                    .ToList(),
                HolidayCalendar.Snapshot(holidays, s.Location(), from, to, s.PadraoFeriado(), excecoes),
                operacoes));
        });
    }

    private static string Hm(TimeOnly t) => t.ToString("HH:mm");

    private static OperationResponse ToResponse(StoreOperation o, decimal? standardBreak)
    {
        // Janela flexível/entradas pela duração personalizada (é o que vale ao ligar "Personalizar turnos")
        var op = o.ToSnapshot() with { TurnosAutomaticos = false };
        return new OperationResponse(Hm(o.Abertura), Hm(o.Fechamento), o.CoberturaMinima, o.MinimoAbertura, o.MinimoFechamento,
            o.IncrementoFlexivelMinutos, OperationPlan.Hm(OperationPlan.EarliestFlex(op)), OperationPlan.Hm(OperationPlan.LatestFlex(op)),
            o.DuracaoJornadaMinutos, o.IntervaloMinutos ?? (standardBreak is { } v ? (int)v : null), o.IntervaloMinutos is null,
            o.EntradaFlexivelMaisCedo is null && o.EntradaFlexivelMaisTarde is null,
            OperationPlan.FlexEntries(op).Select(OperationPlan.Hm).ToList(), o.AtualizadoPor, o.AtualizadoEm, o.RodizioAberturaFechamento,
            FolgasFixasSetorDuasPessoas: o.FolgasFixasSetorDuasPessoas, CoberturaComoMetaQuadroInsuficiente: o.CoberturaComoMetaQuadroInsuficiente,
            TurnosAutomaticos: o.TurnosAutomaticos);
    }

    /// <summary>
    /// Prévia dos turnos do mês (GET /api/stores/{id}/turnos-automaticos): monta o contexto da operação com o horário da loja,
    /// os feriados do horizonte e as regras do Compliance.Api (PlanningContext.ForOperation — a mesma conta do motor) e
    /// agrupa os dias abertos com o mesmo expediente e os mesmos turnos.
    /// </summary>
    private static async Task<IResult> PreviewAsync(Guid id, string? mes, Guid? sectorId, StoreDbContext db, ComplianceRulesClient rules,
        CancellationToken ct)
    {
        var s = await db.Stores.Include(x => x.Sectors).Include(x => x.Shifts).Include(x => x.Horarios)
            .AsSplitQuery().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return Results.NotFound();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var first = new DateOnly(today.Year, today.Month, 1);
        if (!string.IsNullOrWhiteSpace(mes) && !DateOnly.TryParseExact($"{mes.Trim()}-01", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out first))
            return ServiceDefaults.ValidationError("mes", "Informe o mês como AAAA-MM.");
        var (from, to) = PlanningContext.MonthOf(first);
        var label = $"{from:yyyy-MM}";

        // Operação efetiva (como no endpoint interno de planejamento): a exceção do setor ou a da loja com o horário de cada dia
        var ops = await db.Operations.AsNoTracking().Where(o => o.StoreId == id).ToListAsync(ct);
        var horarios = StoreCadastroEndpoints.DayHoursOf(s.Horarios);
        var fechados = horarios?.Where(h => h.Fechada).ToList();
        OperationSnapshot? op = null;
        if (sectorId is { } sid && ops.FirstOrDefault(o => o.SectorId == sid) is { } sectorOp)
            op = sectorOp.ToSnapshot() with { Horarios = fechados is { Count: > 0 } ? fechados : null };
        else if (ops.FirstOrDefault(o => o.SectorId is null) is { } storeOp)
            op = storeOp.ToSnapshot() with { Horarios = horarios };
        if (op is null) return Results.Ok(new AutoShiftPreview(label, false, "A loja não tem operação configurada.", []));

        var (horizonFrom, weeks) = PlanningContext.Horizon(from, to);
        var horizonTo = horizonFrom.AddDays(weeks * 7 - 1);
        var regras = await rules.RulesAsync(horizonFrom, horizonTo, ct);
        if (regras is null)
            return Results.Ok(new AutoShiftPreview(label, op.TurnosAutomaticos,
                "Regras de conformidade indisponíveis (Compliance.Api): não foi possível calcular os turnos agora.", []));
        var holidayRows = (await db.Holidays.AsNoTracking().ToListAsync(ct)).Select(h => h.ToDefinition());
        var excecoes = await db.HolidayStoreHours.AsNoTracking().Where(x => x.StoreId == id).ToDictionaryAsync(x => x.HolidayId, x => x.ToHours(), ct);
        var holidays = HolidayCalendar.Snapshot(holidayRows, s.Location(), horizonFrom, horizonTo, s.PadraoFeriado(), excecoes);
        var catalog = s.Shifts.Where(x => x.Active).Select(x => new ShiftSnapshot(x.Id, x.Code, x.Start, x.End, x.BreakMinutes)).ToList();
        var ctx = PlanningContext.ForOperation(op, sectorId ?? s.Sectors.Select(x => x.Id).FirstOrDefault(), catalog, regras, holidays, from, to,
            s.HasExtendedDayAgreement);

        var open = Enumerable.Range(ctx.FirstDay, ctx.PeriodDays).Where(d => !ctx.IsClosed(d) && !ctx.DayBlocked[d]).ToList();
        string Days(List<int> days) => string.Join(", ", days.GroupBy(d => ctx.Dates[d].DayOfWeek).OrderBy(g => ((int)g.Key + 6) % 7)
            .SelectMany(g => g.Count() == open.Count(d => ctx.Dates[d].DayOfWeek == g.Key) && g.Count() > 1
                ? [PlanningContext.WeekdayShort(g.Key)]
                : g.Select(d => ctx.Dates[d].ToString("dd/MM"))));
        var groups = open
            .GroupBy(d => (Window: ctx.DayHoursText(d), Shifts: string.Join(",", ctx.ShiftsOfDay(d).Select(t => t.Index))))
            .Select(g =>
            {
                var days = g.ToList();
                var d0 = days[0];
                var shifts = ctx.ShiftsOfDay(d0);
                var journey = ctx.JourneyAt(d0);
                var special = holidays.Where(h => days.Any(d => ctx.Dates[d] == h.Date) && h.Funcionamento == nameof(FuncionamentoDia.Especial))
                    .Select(h => $"{h.Name} ({h.Date:dd/MM})").ToList();
                return new AutoShiftGroup(Days(days), days.Select(d => ctx.Dates[d]).ToList(), ctx.DayHoursText(d0),
                    journey?.Paid ?? shifts.FirstOrDefault()?.PaidMinutes ?? 0, journey?.Break ?? shifts.FirstOrDefault()?.Snapshot.BreakMinutes ?? 0,
                    ctx.AutomaticShifts ? ctx.JourneyBasis(d0) : "turnos personalizados da operação", ctx.RegimeLabel(d0 / 7),
                    special.Count > 0 ? string.Join("; ", special) : null,
                    shifts.Select(t => new AutoShiftResponse(t.Kind!, t.Snapshot.Code, OperationPlan.Hm(t.StartMin), OperationPlan.Hm(t.EndMin),
                        t.Snapshot.BreakMinutes, t.PaidMinutes)).ToList());
            })
            .OrderBy(g => g.Datas[0]).ToList();
        var semTurno = groups.Where(g => g.Turnos.Count == 0).SelectMany(g => g.Datas).ToList();
        var aviso = semTurno.Count == 0 ? null
            : $"{semTurno.Count} dia(s) aberto(s) sem turno possível ({string.Join(", ", semTurno.Take(6).Select(d => d.ToString("dd/MM")))}" +
              $"{(semTurno.Count > 6 ? "..." : "")}): a geração falha com erro de configuração nesses dias.";
        return Results.Ok(new AutoShiftPreview(label, op.TurnosAutomaticos, aviso, groups));
    }

    private static bool TryTime(string? value, out TimeOnly? time)
    {
        time = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!TimeOnly.TryParseExact(value.Trim(), ["HH:mm", "H:mm", "HH:mm:ss"], System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var t)) return false;
        time = t;
        return true;
    }

    /// <summary>Cria/atualiza a operação da loja (<paramref name="sectorId"/> nulo) ou a exceção do setor, com as validações da operação.</summary>
    private static async Task<IResult> SaveOperationAsync(Guid id, Guid? sectorId, OperationRequest req, StoreDbContext db, IPublishEndpoint bus,
        ICurrentUser user, ComplianceRulesClient rules)
    {
        if (!await db.Stores.AnyAsync(s => s.Id == id)) return Results.NotFound();
        if (sectorId is { } sid && !await db.Sectors.AnyAsync(s => s.Id == sid && s.StoreId == id))
            return ServiceDefaults.ValidationError("sectorId", "Setor não pertence a esta loja.");
        if (!TryTime(req.Abertura, out var abertura) || abertura is null) return ServiceDefaults.ValidationError("abertura", "Informe a abertura (HH:mm).");
        if (!TryTime(req.Fechamento, out var fechamento) || fechamento is null) return ServiceDefaults.ValidationError("fechamento", "Informe o fechamento (HH:mm).");
        if (!TryTime(req.EntradaFlexivelMaisCedo, out var cedo)) return ServiceDefaults.ValidationError("entradaFlexivelMaisCedo", "Horário inválido (HH:mm).");
        if (!TryTime(req.EntradaFlexivelMaisTarde, out var tarde)) return ServiceDefaults.ValidationError("entradaFlexivelMaisTarde", "Horário inválido (HH:mm).");
        // Operação da loja com horário de funcionamento cadastrado: o expediente vem do horário (o mais frequente entre os
        // dias abertos; cada dia usa o seu na geração). A exceção do setor mantém o expediente informado.
        if (sectorId is null && StoreCadastroEndpoints.StandardWindow(await db.StoreHours.AsNoTracking().Where(h => h.StoreId == id).ToListAsync()) is { } janela)
            (abertura, fechamento) = (janela.Abertura, janela.Fechamento);
        var entity = await db.Operations.FirstOrDefaultAsync(o => o.StoreId == id && o.SectorId == sectorId);
        // Turnos automáticos (padrão): duração, intervalo e janela flexível não são exigidos; vazios mantêm os personalizados
        // já gravados (voltam a valer em "Personalizar turnos")
        var automatic = req.TurnosAutomaticos ?? entity?.TurnosAutomaticos ?? true;
        if (automatic)
        {
            cedo ??= entity?.EntradaFlexivelMaisCedo;
            tarde ??= entity?.EntradaFlexivelMaisTarde;
        }
        var op = new OperationSnapshot(abertura.Value, fechamento.Value, req.CoberturaMinima, req.MinimoAbertura, req.MinimoFechamento,
            req.IncrementoFlexivelMinutos, cedo, tarde,
            req.DuracaoJornadaMinutos ?? (automatic ? entity?.DuracaoJornadaMinutos : null) ?? OperationPlan.DuracaoPadraoMinutos,
            req.IntervaloMinutos ?? (automatic ? entity?.IntervaloMinutos : null),
            req.RodizioAberturaFechamento ?? entity?.RodizioAberturaFechamento ?? true,
            FolgasFixasSetorDuasPessoas: req.FolgasFixasSetorDuasPessoas ?? entity?.FolgasFixasSetorDuasPessoas ?? true,
            CoberturaComoMetaQuadroInsuficiente: req.CoberturaComoMetaQuadroInsuficiente ?? entity?.CoberturaComoMetaQuadroInsuficiente ?? true,
            TurnosAutomaticos: automatic);
        var errors = OperationPlan.Validate(op);
        if (errors.Count > 0)
            return Results.ValidationProblem(errors.GroupBy(e => e.Campo).ToDictionary(g => g.Key, g => g.Select(e => e.Mensagem).ToArray()),
                title: "Operação inválida");
        if (!automatic && op.IntervaloMinutos is { } brk && StoreEndpoints.ValidateBreak(op.DuracaoJornadaMinutos, brk) is { } breakError)
            return ServiceDefaults.ValidationError("intervaloMinutos", breakError);

        var before = entity is null ? null : Describe(entity.ToSnapshot());
        if (entity is null) db.Operations.Add(entity = new StoreOperation { StoreId = id, SectorId = sectorId });
        entity.Abertura = op.Abertura;
        entity.Fechamento = op.Fechamento;
        entity.CoberturaMinima = op.CoberturaMinima;
        entity.MinimoAbertura = op.MinimoAbertura;
        entity.MinimoFechamento = op.MinimoFechamento;
        entity.IncrementoFlexivelMinutos = op.IncrementoFlexivelMinutos;
        entity.EntradaFlexivelMaisCedo = op.EntradaFlexivelMaisCedo;
        entity.EntradaFlexivelMaisTarde = op.EntradaFlexivelMaisTarde;
        entity.DuracaoJornadaMinutos = op.DuracaoJornadaMinutos;
        entity.IntervaloMinutos = op.IntervaloMinutos;
        entity.RodizioAberturaFechamento = op.RodizioAberturaFechamento;
        entity.FolgasFixasSetorDuasPessoas = op.FolgasFixasSetorDuasPessoas;
        entity.CoberturaComoMetaQuadroInsuficiente = op.CoberturaComoMetaQuadroInsuficiente;
        entity.TurnosAutomaticos = op.TurnosAutomaticos;
        entity.AtualizadoPor = user.Display;
        entity.AtualizadoEm = DateTime.UtcNow;
        await db.SaveChangesAsync();
        var what = $"Operação {(sectorId is null ? "da loja" : "do setor")} {(before is null ? "configurada" : "alterada")}: {Describe(op)}" +
                   (before is null ? "" : $" (antes: {before})");
        await bus.Publish(new StorePlanningChanged(id, what, user.Display, DateTime.UtcNow));
        var standard = entity.IntervaloMinutos is null ? await rules.ActiveValueAsync(RuleKeys.IntervaloPadrao, DateOnly.FromDateTime(DateTime.Today)) : null;
        return Results.Ok(ToResponse(entity, standard));
    }

    private static async Task<IResult> DeleteOperationAsync(Guid id, Guid? sectorId, StoreDbContext db, IPublishEndpoint bus, ICurrentUser user)
    {
        var entity = await db.Operations.FirstOrDefaultAsync(o => o.StoreId == id && o.SectorId == sectorId);
        if (entity is null) return Results.NotFound();
        db.Operations.Remove(entity);
        await db.SaveChangesAsync();
        await bus.Publish(new StorePlanningChanged(id,
            sectorId is null ? "Operação da loja removida (volta ao modelo só de demanda)" : "Exceção de operação do setor removida (usa a da loja)",
            user.Display, DateTime.UtcNow));
        return Results.NoContent();
    }

    private static string Describe(OperationSnapshot o) =>
        $"{o.Abertura:HH\\:mm}–{o.Fechamento:HH\\:mm}, mínimos {o.CoberturaMinima}/{o.MinimoAbertura}/{o.MinimoFechamento}, " +
        (o.TurnosAutomaticos
            ? $"turnos automáticos (horário da loja e regras vigentes; intermediários de {o.IncrementoFlexivelMinutos} em {o.IncrementoFlexivelMinutos} min), "
            : $"turnos personalizados: flexível {OperationPlan.Hm(OperationPlan.EarliestFlex(o))}–{OperationPlan.Hm(OperationPlan.LatestFlex(o))} de " +
              $"{o.IncrementoFlexivelMinutos} em {o.IncrementoFlexivelMinutos} min, " +
              $"jornada {o.DuracaoJornadaMinutos} min, intervalo {(o.IntervaloMinutos is { } b ? $"{b} min" : "da regra")}, ") +
        $"rodízio de abertura/fechamento {(o.RodizioAberturaFechamento ? "ligado" : "desligado")}, " +
        $"folgas fixas em setor de 2 pessoas {(o.FolgasFixasSetorDuasPessoas ? "ligadas" : "desligadas")}, " +
        $"cobertura como meta com quadro insuficiente {(o.CoberturaComoMetaQuadroInsuficiente ? "ligada" : "desligada")}";
}

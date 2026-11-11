using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Escala5x2.BuildingBlocks.Contracts.Employees;
using Escala5x2.BuildingBlocks.Contracts.Http;
using Escala5x2.BuildingBlocks.Contracts.Planning;
using Escala5x2.BuildingBlocks.Shared.Auth;
using Escala5x2.BuildingBlocks.Shared.Web;
using Employee.Api.Data;
using Employee.Api.Domain;
using Employee.Api.Lg;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Employee.Api.Endpoints;

public sealed record ContractRequest(
    DateOnly ValidFrom, bool JourneyControlled, bool OvertimeAgreement, SundayRule SundayRule, string? LegalOpinionRef);

public sealed record CreateEmployeeRequest(
    string Registration, string Name, Guid StoreId, string JobTitle, List<Guid> SectorIds, ContractRequest Contract,
    JourneyRequest? Jornada = null);

/// <summary>
/// Edição dos dados cadastrais. <paramref name="Active"/> vazio = mantém; inativo sai da fotografia das próximas gerações.
/// A loja não muda aqui; o contrato muda por nova versão (POST /{id}/contracts) e a jornada por PUT /{id}/jornada.
/// </summary>
public sealed record UpdateEmployeeRequest(string Registration, string Name, string? JobTitle, List<Guid> SectorIds, bool? Active = null);

/// <summary>Dia da semana na disponibilidade ("HH:mm" ou vazio = dia inteiro).</summary>
public sealed record AvailabilityInput(DayOfWeek DiaSemana, bool Disponivel, string? Inicio, string? Fim);

/// <summary>
/// Perfil de jornada (Abertura | Fechamento | Flexivel), disponibilidade semanal (dias sem linha = disponível o dia
/// todo), preferências opcionais (folgas preferidas, período Cedo | Tarde) e custo/hora opcional (R$).
/// </summary>
public sealed record JourneyRequest(string PerfilJornada, List<AvailabilityInput>? Disponibilidade, JourneyPreferences? Preferencias, decimal? CustoHora);

/// <summary>Dados de jornada na resposta (horários "HH:mm").</summary>
public sealed record JourneyResponse(string PerfilJornada, string PerfilRotulo, List<AvailabilityInput> Disponibilidade,
    JourneyPreferences? Preferencias, decimal? CustoHora, string? AtualizadoPor, DateTime? AtualizadoEm);

/// <summary>Registro/edição de ausência. Note: opcional, sem dado de saúde (CID/diagnóstico são recusados).</summary>
public sealed record UnavailabilityRequest(DateOnly Start, DateOnly End, UnavailabilityType Type, string? Note);

public sealed record ContractResponse(
    Guid Id, DateOnly ValidFrom, DateOnly? ValidTo, bool JourneyControlled, bool OvertimeAgreement,
    SundayRule SundayRule, string? LegalOpinionRef, string CreatedBy);

/// <summary>
/// Ausência na resposta. Colaborador: <paramref name="Type"/> null, <paramref name="Rotulo"/> "Afastado", sem observação
/// e sem autoria. Jurídico: tipo sem observação. Admin, RH e Gestor da loja: tudo. <paramref name="PodeEditar"/>:
/// Admin, RH e Gestor da loja do colaborador (exceto férias importadas da LG, somente leitura).
/// <paramref name="Origem"/>: "LG" = período de férias importado da LG (ServicoDeFerias); null = registro manual.
/// </summary>
public sealed record UnavailabilityResponse(
    Guid Id, DateOnly Start, DateOnly End, UnavailabilityType? Type, string Rotulo, string? Note, bool PodeEditar,
    string? CreatedBy, DateTime? CreatedAt, string? UpdatedBy, DateTime? UpdatedAt, string? Origem = null);

/// <summary>
/// Colaborador. <paramref name="SetoresManuais"/>: setores alterados manualmente (a LG não os sobrescreve; quem/quando em
/// <paramref name="SetoresAlteradosPor"/>/<paramref name="SetoresAlteradosEm"/>).
/// </summary>
public sealed record EmployeeResponse(
    Guid Id, string Registration, string Name, Guid StoreId, string JobTitle, bool Active,
    List<Guid> SectorIds, ContractResponse? CurrentContract, List<ContractResponse> Contracts,
    List<UnavailabilityResponse> Unavailabilities, JourneyResponse? Jornada = null, EmployeeLgResponse? Lg = null,
    bool SetoresManuais = false, string? SetoresAlteradosPor = null, DateTime? SetoresAlteradosEm = null);

/// <summary>
/// Situação do colaborador na LG (null = cadastro só da Escala 5x2). <paramref name="Disponivel"/>: código 1 (ATIVIDADE
/// NORMAL). <paramref name="PendenciaPeriodo"/>: em férias/afastamento sem período registrado (fora da geração até o
/// operador registrar; <paramref name="TipoSugerido"/>/<paramref name="InicioSugerido"/> preenchem o modal).
/// Colaborador vê só "Afastado" (sem a descrição da LG, que pode indicar dado de saúde).
/// <paramref name="OrgaoCodigo"/>/<paramref name="OrgaoDescricao"/>: órgão responsável atual; <paramref name="SetorLgId"/>:
/// setor da loja desse órgão (o "Setor da LG"); <paramref name="SetorDivergente"/>: setores alterados manualmente e
/// diferentes do setor da LG. <paramref name="FeriasSincronizadoEm"/>: última consulta das férias na LG (null = nunca).
/// </summary>
public sealed record EmployeeLgResponse(int? SituacaoCodigo, string? SituacaoDescricao, DateOnly? DataInicioSituacao, DateOnly? DataAdmissao,
    string? CentroDeCusto, DateTime? SincronizadoEm, bool Disponivel, bool PendenciaPeriodo, UnavailabilityType? TipoSugerido,
    DateOnly? InicioSugerido, DateTime? InativadoEm, int? OrgaoCodigo = null, string? OrgaoDescricao = null, Guid? SetorLgId = null,
    bool SetorDivergente = false, DateTime? FeriasSincronizadoEm = null);

/// <summary>"Atualizar férias" de um colaborador (Admin/RH): resumo da consulta à LG e o colaborador atualizado.</summary>
public sealed record FeriasLgColaboradorResponse(LgFeriasResumo Resumo, EmployeeResponse Colaborador);

/// <summary>
/// Registro LG completo (ContratoDeTrabalhoParcial) para o "Visualizar". <paramref name="Lgpd"/>: completo (Admin/RH),
/// mascarado (Gestor: CPF ***.***.***-88, nascimento só dia/mês) ou oculto (demais: sem CPF e nascimento).
/// </summary>
public sealed record EmployeeLgDetailResponse(Guid Id, DateTime? SincronizadoEm, string Lgpd, JsonObject? Registro);

/// <summary>Ativar/inativar (botão da lixeira): inativo sai das próximas gerações, nada é apagado.</summary>
public sealed record ActiveRequest(bool Active);

public static partial class EmployeeEndpoints
{
    private const int MaxAbsenceDays = 730;

    /// <summary>LGPD: a observação não guarda dado de saúde. Recusa menção a CID/diagnóstico.</summary>
    [GeneratedRegex(@"\b(CID|CID-?10|CID-?11|diagn[oó]stic[oa]s?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HealthDataRegex();

    private static ContractResponse ToResponse(ContractVersion c) =>
        new(c.Id, c.ValidFrom, c.ValidTo, c.JourneyControlled, c.OvertimeAgreement, c.SundayRule, c.LegalOpinionRef, c.CreatedBy);

    /// <summary>
    /// Admin, RH e Gestor da loja (papel efetivo na loja: Colaborador com role de loja é Gestor só nas lojas das roles)
    /// alteram o cadastro, registram/alteram/excluem ausências e veem a observação.
    /// </summary>
    private static bool CanManageAbsences(ICurrentUser user, Guid storeId) =>
        user.RoleIn(storeId) is Roles.Admin or Roles.RH or Roles.Gestor;

    private static UnavailabilityResponse ToResponse(Unavailability u, Guid storeId, ICurrentUser user)
    {
        var full = CanManageAbsences(user, storeId);
        var seesType = full || user.Role == Roles.Juridico;
        // Férias importadas da LG: somente leitura (a sincronização mantém; alterar na LG)
        return new UnavailabilityResponse(u.Id, u.Start, u.End,
            seesType ? u.Type : null,
            seesType ? UnavailabilityKinds.Rotulo(u.Type.ToString()) : UnavailabilityKinds.RotuloGenerico,
            full ? u.Note : null, full && !u.DaLg,
            full ? u.CreatedBy : null, full ? u.CreatedAt : null, full ? u.UpdatedBy : null, full ? u.UpdatedAt : null,
            seesType ? u.Origem : null);
    }

    private static EmployeeResponse ToResponse(Domain.Employee e, ICurrentUser user)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return new EmployeeResponse(
            e.Id, e.Registration, e.Name, e.StoreId, e.JobTitle, e.Active,
            e.Sectors.Select(s => s.SectorId).ToList(),
            e.ContractAt(today) is { } current ? ToResponse(current) : null,
            e.Contracts.OrderByDescending(c => c.ValidFrom).Select(ToResponse).ToList(),
            e.Unavailabilities.OrderByDescending(u => u.Start).Select(u => ToResponse(u, e.StoreId, user)).ToList(),
            new JourneyResponse(e.PerfilJornada, JourneyProfiles.Rotulo(e.PerfilJornada),
                Availability(e).Select(a => new AvailabilityInput(a.DiaSemana, a.Disponivel, a.Inicio?.ToString("HH:mm"), a.Fim?.ToString("HH:mm"))).ToList(),
                Preferences(e), e.CustoHora, e.JornadaAtualizadaPor, e.JornadaAtualizadaEm),
            LgOf(e, user), e.SetoresManuais, e.SetoresAlteradosPor, e.SetoresAlteradosEm);
    }

    /// <summary>Setores alterados manualmente: a sincronização com a LG deixa de trocar o setor (até "voltar a seguir a LG").</summary>
    private static void MarkManualSectors(Domain.Employee e, ICurrentUser user)
    {
        e.SetoresManuais = true;
        e.SetoresAlteradosPor = user.Display;
        e.SetoresAlteradosEm = DateTime.UtcNow;
    }

    private static EmployeeLgResponse? LgOf(Domain.Employee e, ICurrentUser user)
    {
        if (e.LgSincronizadoEm is null && e.LgSituacaoCodigo is null) return null;
        var disponivel = LgRegras.Disponivel(e.LgSituacaoCodigo);
        // Descrição da LG (ex.: "AUXILIO DOENCA") pode indicar dado de saúde: Colaborador vê só "Afastado"
        var seesType = CanManageAbsences(user, e.StoreId) || user.Role == Roles.Juridico;
        var pendencia = LgRegras.PendenciaDePeriodo(e);
        return new EmployeeLgResponse(
            seesType ? e.LgSituacaoCodigo : null,
            seesType || disponivel ? e.LgSituacaoDescricao : UnavailabilityKinds.RotuloGenerico,
            e.LgDataInicioSituacao, e.LgDataAdmissao, e.LgCentroDeCusto, e.LgSincronizadoEm, disponivel, pendencia,
            pendencia && seesType ? LgRegras.TipoSugerido(e.LgSituacaoDescricao) : null,
            pendencia ? LgRegras.InicioSugerido(e) : null,
            e.LgInativadoEm, e.LgOrgaoCodigo, e.LgOrgaoDescricao, e.LgSetorId, LgRegras.SetorDivergente(e), e.LgFeriasSincronizadoEm);
    }

    /// <summary>Nível LGPD do registro LG para o papel (ver <see cref="LgRegras.AplicarLgpd"/>).</summary>
    private static string LgpdLevel(string? role) => role switch
    {
        Roles.Admin or Roles.RH => "completo",
        Roles.Gestor => "mascarado",
        _ => "oculto"
    };

    private static readonly JsonSerializerOptions JourneyJson = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static List<AvailabilityWindow> Availability(Domain.Employee e) =>
        string.IsNullOrEmpty(e.DisponibilidadeJson) ? [] : JsonSerializer.Deserialize<List<AvailabilityWindow>>(e.DisponibilidadeJson, JourneyJson) ?? [];

    private static JourneyPreferences? Preferences(Domain.Employee e) =>
        string.IsNullOrEmpty(e.PreferenciasJson) ? null : JsonSerializer.Deserialize<JourneyPreferences>(e.PreferenciasJson, JourneyJson);

    /// <summary>Dados de jornada na fotografia de planejamento (também no endpoint interno de colaboradores).</summary>
    private static EmployeeJourneySnapshot JourneyOf(Domain.Employee e) =>
        new(e.PerfilJornada, Availability(e) is { Count: > 0 } a ? a : null, Preferences(e), e.CustoHora);

    /// <summary>Valida e aplica os dados de jornada; devolve o erro de validação ou null.</summary>
    private static IResult? ApplyJourney(Domain.Employee e, JourneyRequest req)
    {
        var perfil = req.PerfilJornada?.Trim();
        var match = JourneyProfiles.All.FirstOrDefault(p => string.Equals(p, perfil, StringComparison.OrdinalIgnoreCase)
                                                             || string.Equals(JourneyProfiles.Rotulo(p), perfil, StringComparison.OrdinalIgnoreCase));
        if (match is null) return ServiceDefaults.ValidationError("perfilJornada", "Perfil de jornada inválido: use Abertura, Fechamento ou Flexivel.");
        var windows = new List<AvailabilityWindow>();
        foreach (var a in req.Disponibilidade ?? [])
        {
            if (!Enum.IsDefined(a.DiaSemana)) return ServiceDefaults.ValidationError("disponibilidade", "Dia da semana inválido.");
            if (windows.Any(w => w.DiaSemana == a.DiaSemana)) return ServiceDefaults.ValidationError("disponibilidade", "Informe cada dia da semana uma vez só.");
            TimeOnly? from = null, to = null;
            if (!string.IsNullOrWhiteSpace(a.Inicio))
            {
                if (!TimeOnly.TryParseExact(a.Inicio.Trim(), ["HH:mm", "H:mm"], null, System.Globalization.DateTimeStyles.None, out var f))
                    return ServiceDefaults.ValidationError("disponibilidade", $"Início inválido ({a.Inicio}); use HH:mm.");
                from = f;
            }
            if (!string.IsNullOrWhiteSpace(a.Fim))
            {
                if (!TimeOnly.TryParseExact(a.Fim.Trim(), ["HH:mm", "H:mm"], null, System.Globalization.DateTimeStyles.None, out var t))
                    return ServiceDefaults.ValidationError("disponibilidade", $"Fim inválido ({a.Fim}); use HH:mm.");
                to = t;
            }
            if (from is { } x && to is { } y && y != TimeOnly.MinValue && y <= x)
                return ServiceDefaults.ValidationError("disponibilidade", "Na disponibilidade, o fim precisa ser depois do início no mesmo dia.");
            // Dia disponível o dia inteiro não precisa de linha
            if (a.Disponivel && from is null && to is null) continue;
            windows.Add(new AvailabilityWindow(a.DiaSemana, a.Disponivel, a.Disponivel ? from : null, a.Disponivel ? to : null));
        }
        if (windows.Count(w => !w.Disponivel) == 7) return ServiceDefaults.ValidationError("disponibilidade", "O colaborador precisa estar disponível em ao menos um dia.");
        var prefs = req.Preferencias;
        if (prefs is not null)
        {
            if (prefs.PeriodoPreferido is { Length: > 0 } p && p is not (JourneyPreferences.Cedo or JourneyPreferences.Tarde))
                return ServiceDefaults.ValidationError("preferencias", "Período preferido: Cedo ou Tarde (ou vazio).");
            var days = prefs.FolgasPreferidas?.Distinct().ToList() ?? [];
            if (days.Any(d => !Enum.IsDefined(d)) || days.Count > 5) return ServiceDefaults.ValidationError("preferencias", "Folgas preferidas: até 5 dias da semana.");
            prefs = days.Count == 0 && string.IsNullOrEmpty(prefs.PeriodoPreferido) ? null
                : new JourneyPreferences(days.Count > 0 ? days : null, string.IsNullOrEmpty(prefs.PeriodoPreferido) ? null : prefs.PeriodoPreferido);
        }
        if (req.CustoHora is < 0 or > 10000) return ServiceDefaults.ValidationError("custoHora", "Custo/hora entre 0 e 10.000.");
        e.PerfilJornada = match;
        e.DisponibilidadeJson = windows.Count > 0 ? JsonSerializer.Serialize(windows.OrderBy(w => w.DiaSemana), JourneyJson) : null;
        e.PreferenciasJson = prefs is null ? null : JsonSerializer.Serialize(prefs, JourneyJson);
        e.CustoHora = req.CustoHora;
        return null;
    }

    private static string DescribeJourney(Domain.Employee e)
    {
        var parts = new List<string> { $"perfil {JourneyProfiles.Rotulo(e.PerfilJornada)}" };
        var a = Availability(e);
        if (a.Count > 0)
            parts.Add("disponibilidade: " + string.Join(", ", a.Select(w => $"{(int)w.DiaSemana}:{(w.Disponivel ? $"{w.Inicio?.ToString("HH:mm") ?? "00:00"}-{w.Fim?.ToString("HH:mm") ?? "24:00"}" : "indisponível")}")));
        if (Preferences(e) is { } p)
            parts.Add($"preferências: folgas {string.Join("/", p.FolgasPreferidas?.Select(d => (int)d) ?? [])}, período {p.PeriodoPreferido ?? "—"}");
        if (e.CustoHora is { } c) parts.Add($"custo/hora {c:0.00}");
        return string.Join("; ", parts);
    }

    private static IQueryable<Domain.Employee> WithDetails(this EmployeeDbContext db) =>
        db.Employees.Include(e => e.Sectors).Include(e => e.Contracts).Include(e => e.Unavailabilities).AsSplitQuery();

    /// <summary>Período, tipo e observação válidos; mesmo tipo não pode se sobrepor (tipos diferentes podem: atestado nas férias).</summary>
    private static async Task<IResult?> ValidateAbsenceAsync(UnavailabilityRequest req, Guid employeeId, Guid? ignoreId, EmployeeDbContext db)
    {
        if (req.Start == default || req.End == default) return ServiceDefaults.ValidationError("start", "Informe o início e o fim do período.");
        if (req.End < req.Start) return ServiceDefaults.ValidationError("end", "A data final é anterior à inicial.");
        if (req.End.DayNumber - req.Start.DayNumber + 1 > MaxAbsenceDays)
            return ServiceDefaults.ValidationError("end", $"O período tem no máximo {MaxAbsenceDays} dias; registre em partes.");
        if (!Enum.IsDefined(req.Type)) return ServiceDefaults.ValidationError("type", "Tipo de ausência inválido.");
        if (req.Note is { Length: > 500 }) return ServiceDefaults.ValidationError("note", "A observação tem no máximo 500 caracteres.");
        if (req.Note is { } note && HealthDataRegex().IsMatch(note))
            return ServiceDefaults.ValidationError("note", "A observação não pode conter CID nem diagnóstico (dado de saúde, LGPD). Registre só o período e o tipo.");
        var overlap = await db.Unavailabilities.AsNoTracking()
            .Where(u => u.EmployeeId == employeeId && u.Type == req.Type && u.Id != ignoreId && u.Start <= req.End && u.End >= req.Start)
            .FirstOrDefaultAsync();
        return overlap is null
            ? null
            : ServiceDefaults.ValidationError("start", overlap.DaLg
                ? $"Já existem férias importadas da LG de {overlap.Start:dd/MM/yyyy} a {overlap.End:dd/MM/yyyy} neste período (o período vem da LG)."
                : $"Já existe {UnavailabilityKinds.Rotulo(overlap.Type.ToString()).ToLowerInvariant()} de {overlap.Start:dd/MM/yyyy} a {overlap.End:dd/MM/yyyy} neste período. Edite o registro existente.");
    }

    public static void MapEmployeeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/employees").RequireAuthorization();

        group.MapGet("/", async (Guid? storeId, EmployeeDbContext db, ICurrentUser user) =>
        {
            // Admin, RH e Jurídico: a loja informada (ou todas). Gestor e Colaborador: só as lojas permitidas (perfil ∪
            // roles de loja): a informada precisa estar entre elas (403); sem loja informada, todas as permitidas; sem
            // nenhuma loja permitida, lista vazia.
            var query = db.WithDetails();
            if (user.HasGlobalScope)
            {
                if (storeId is { } s) query = query.Where(e => e.StoreId == s);
            }
            else if (storeId is { } s)
            {
                if (!user.CanAccessStore(s)) return Results.Forbid();
                query = query.Where(e => e.StoreId == s);
            }
            else
            {
                var permitidas = user.StoreIds.ToList();
                if (permitidas.Count == 0) return Results.Ok(Array.Empty<EmployeeResponse>());
                query = query.Where(e => permitidas.Contains(e.StoreId));
            }
            var list = await query.OrderBy(e => e.Name).ToListAsync();
            return Results.Ok(list.Select(e => ToResponse(e, user)));
        });

        group.MapGet("/{id:guid}", async (Guid id, EmployeeDbContext db, ICurrentUser user) =>
            await db.WithDetails().FirstOrDefaultAsync(x => x.Id == id) is { } e
                ? user.CanAccessStore(e.StoreId) ? Results.Ok(ToResponse(e, user)) : Results.Forbid()
                : Results.NotFound());

        // ---------- Integração LG ----------

        // Registro LG completo (Visualizar). LGPD no servidor: CPF/nascimento completos só Admin e RH; Gestor mascarados;
        // demais sem esses campos (e Colaborador sem a descrição da situação).
        group.MapGet("/{id:guid}/lg", async (Guid id, EmployeeDbContext db, ICurrentUser user) =>
        {
            var e = await db.Employees.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (e is null) return Results.NotFound();
            if (!user.CanAccessStore(e.StoreId)) return Results.Forbid();
            JsonObject? registro = null;
            if (!string.IsNullOrEmpty(e.LgRegistroJson) && JsonNode.Parse(e.LgRegistroJson) is JsonObject bruto)
                registro = LgRegras.AplicarLgpd(bruto, user.RoleIn(e.StoreId));
            // Papel efetivo na loja do colaborador (Colaborador com role de loja é Gestor só nas lojas das roles)
            return Results.Ok(new EmployeeLgDetailResponse(e.Id, e.LgSincronizadoEm, LgpdLevel(user.RoleIn(e.StoreId)), registro));
        });

        // Sincronização com a LG em segundo plano (Admin e RH): 202 + status; 409 já em andamento; 503 sem configuração
        group.MapPost("/sincronizar-lg", (LgSincronizacaoRunner runner, ICurrentUser user) =>
        {
            if (runner.TryIniciar(user.Display) is { } recusa)
                return runner.Status() is { Configurada: false }
                    ? Results.Problem(title: "Integração LG não configurada", detail: recusa, statusCode: StatusCodes.Status503ServiceUnavailable)
                    : Results.Problem(title: "Sincronização em andamento", detail: recusa, statusCode: StatusCodes.Status409Conflict);
            return Results.Accepted("/api/employees/sincronizar-lg/status", runner.Status());
        }).RequireAuthorization(Policies.LgSync);

        // Status e resumo (centros de custo ignorados, matrículas...): só Admin e RH
        group.MapGet("/sincronizar-lg/status", (LgSincronizacaoRunner runner) => Results.Ok(runner.Status()))
            .RequireAuthorization(Policies.LgSync);

        // "Atualizar férias" de um colaborador (Admin e RH): consulta já a LG (ServicoDeFerias) e devolve o colaborador
        group.MapPost("/{id:guid}/ferias-lg", async (Guid id, EmployeeDbContext db, LgFeriasSincronizador ferias, ICurrentUser user,
            CancellationToken ct) =>
        {
            var e = await db.Employees.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (e is null) return Results.NotFound();
            if (!user.CanAccessStore(e.StoreId)) return Results.Forbid();
            if (e.LgEmpresaCodigo is null)
                return ServiceDefaults.ValidationError("lg", "Colaborador sem vínculo com a LG: as férias são registradas manualmente.");
            if (!e.Active) return ServiceDefaults.ValidationError("active", "Colaborador inativo: as férias da LG só são consultadas para ativos.");
            var resumo = await ferias.SincronizarAsync(new LgFeriasFiltro(EmployeeIds: [id]), user.Display, null, ct);
            if (resumo.Status == FeriasLgStatus.NaoConfigurada)
                return Results.Problem(title: "Integração de férias não configurada", detail: resumo.Mensagem, statusCode: StatusCodes.Status503ServiceUnavailable);
            if (resumo.Status == FeriasLgStatus.Falha)
                return Results.Problem(title: "Férias não atualizadas", detail: resumo.Erros.FirstOrDefault() ?? resumo.Mensagem,
                    statusCode: StatusCodes.Status502BadGateway);
            db.ChangeTracker.Clear();
            var atualizado = await db.WithDetails().AsNoTracking().FirstAsync(x => x.Id == id, ct);
            return Results.Ok(new FeriasLgColaboradorResponse(resumo, ToResponse(atualizado, user)));
        }).RequireAuthorization(Policies.LgSync);

        // Inativar/reativar (lixeira da lista): Admin, RH e Gestor da loja. Nada é apagado; inativo sai das próximas gerações.
        group.MapPut("/{id:guid}/ativo", async (Guid id, ActiveRequest req, EmployeeDbContext db, IPublishEndpoint bus, ICurrentUser user) =>
        {
            var e = await db.Employees.Include(x => x.Sectors).FirstOrDefaultAsync(x => x.Id == id);
            if (e is null) return Results.NotFound();
            if (!CanManageAbsences(user, e.StoreId)) return Results.Forbid();
            if (e.Active == req.Active) return Results.NoContent();
            e.Active = req.Active;
            // Decisão manual: a sincronização da LG não reativa por conta própria
            e.LgInativadoEm = null;
            await db.SaveChangesAsync();
            await bus.Publish(new EmployeeDetailsChanged(e.Id, e.StoreId, e.Registration, e.Name, e.Active,
                e.Sectors.Select(s => s.SectorId).ToList(), e.Active ? "reativado" : "inativado", user.Display, DateTime.UtcNow));
            return Results.NoContent();
        }).RequireAuthorization(Policies.HumanResources);

        group.MapPost("/", async (CreateEmployeeRequest req, EmployeeDbContext db, IPublishEndpoint bus, ICurrentUser user) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Registration))
                return ServiceDefaults.ValidationError("name", "Nome e matrícula são obrigatórios.");
            if (!CanManageAbsences(user, req.StoreId)) return Results.Forbid();
            if (req.SectorIds.Count == 0)
                return ServiceDefaults.ValidationError("sectorIds", "Informe pelo menos um setor habilitado.");
            if (await db.Employees.AnyAsync(e => e.StoreId == req.StoreId && e.Registration == req.Registration))
                return ServiceDefaults.ValidationError("registration", "Matrícula já cadastrada nesta loja.");

            var employee = new Domain.Employee
            {
                Registration = req.Registration.Trim(),
                Name = req.Name.Trim(),
                StoreId = req.StoreId,
                JobTitle = req.JobTitle.Trim(),
                Sectors = req.SectorIds.Distinct().Select(s => new EmployeeSector { SectorId = s }).ToList()
            };
            // Setores escolhidos no cadastro manual: se a LG depois trouxer a mesma matrícula, não os troca
            MarkManualSectors(employee, user);
            employee.Contracts.Add(NewContract(req.Contract, user.Display));
            if (req.Jornada is { } jornada)
            {
                if (ApplyJourney(employee, jornada) is { } invalid) return invalid;
                employee.JornadaAtualizadaPor = user.Display;
                employee.JornadaAtualizadaEm = DateTime.UtcNow;
            }
            db.Employees.Add(employee);
            await db.SaveChangesAsync();

            await bus.Publish(new EmployeeRegistered(employee.Id, employee.StoreId, employee.Registration, employee.Name, user.Display, DateTime.UtcNow));
            return Results.Created($"/api/employees/{employee.Id}", ToResponse(employee, user));
        }).RequireAuthorization(Policies.HumanResources);

        // Dados cadastrais (nome, matrícula única na loja, cargo, setores habilitados, ativo): Admin, RH e Gestor da loja.
        // Vale para as próximas gerações (as escalas existentes guardam nome/matrícula nas células e a fotografia).
        group.MapPut("/{id:guid}", async (Guid id, UpdateEmployeeRequest req, EmployeeDbContext db, IPublishEndpoint bus, ICurrentUser user) =>
        {
            var e = await db.WithDetails().FirstOrDefaultAsync(x => x.Id == id);
            if (e is null) return Results.NotFound();
            if (!CanManageAbsences(user, e.StoreId)) return Results.Forbid();
            if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Registration))
                return ServiceDefaults.ValidationError("name", "Nome e matrícula são obrigatórios.");
            if ((req.SectorIds ?? []).Count == 0)
                return ServiceDefaults.ValidationError("sectorIds", "Informe pelo menos um setor habilitado.");
            var registration = req.Registration.Trim();
            if (await db.Employees.AnyAsync(x => x.StoreId == e.StoreId && x.Registration == registration && x.Id != id))
                return ServiceDefaults.ValidationError("registration", "Matrícula já cadastrada nesta loja.");

            var name = req.Name.Trim();
            var jobTitle = req.JobTitle?.Trim() ?? string.Empty;
            var active = req.Active ?? e.Active;
            var wanted = req.SectorIds!.Distinct().ToHashSet();
            var current = e.Sectors.Select(s => s.SectorId).ToHashSet();
            var changes = new List<string>();
            if (e.Name != name) changes.Add($"nome {e.Name} → {name}");
            if (e.Registration != registration) changes.Add($"matrícula {e.Registration} → {registration}");
            if (e.JobTitle != jobTitle) changes.Add($"cargo {(e.JobTitle.Length > 0 ? e.JobTitle : "—")} → {(jobTitle.Length > 0 ? jobTitle : "—")}");
            if (!wanted.SetEquals(current))
                changes.Add($"setores {current.Count} → {wanted.Count} (+{wanted.Count(w => !current.Contains(w))}/−{current.Count(c => !wanted.Contains(c))}), " +
                            "alterados manualmente (a LG não troca mais)");
            if (e.Active != active) changes.Add(active ? "reativado" : "inativado");
            if (changes.Count == 0) return Results.Ok(ToResponse(e, user));

            e.Name = name;
            e.Registration = registration;
            e.JobTitle = jobTitle;
            if (e.Active != active) e.LgInativadoEm = null; // decisão manual prevalece sobre a reativação automática da LG
            e.Active = active;
            // Alteração manual: a LG não troca mais o setor (até "Voltar a seguir o setor da LG")
            if (!wanted.SetEquals(current)) MarkManualSectors(e, user);
            var removed = e.Sectors.Where(s => !wanted.Contains(s.SectorId)).ToList();
            db.RemoveRange(removed);
            e.Sectors.RemoveAll(removed.Contains);
            foreach (var w in wanted.Where(w => !current.Contains(w)))
                e.Sectors.Add(new EmployeeSector { EmployeeId = id, SectorId = w });
            await db.SaveChangesAsync();
            await bus.Publish(new EmployeeDetailsChanged(e.Id, e.StoreId, e.Registration, e.Name, e.Active,
                e.Sectors.Select(s => s.SectorId).ToList(), string.Join("; ", changes), user.Display, DateTime.UtcNow));
            return Results.Ok(ToResponse(e, user));
        }).RequireAuthorization(Policies.HumanResources);

        group.MapPut("/{id:guid}/sectors", async (Guid id, List<Guid> sectorIds, EmployeeDbContext db, ICurrentUser user) =>
        {
            var e = await db.Employees.Include(x => x.Sectors).FirstOrDefaultAsync(x => x.Id == id);
            if (e is null) return Results.NotFound();
            if (!CanManageAbsences(user, e.StoreId)) return Results.Forbid();
            if (sectorIds.Count == 0) return ServiceDefaults.ValidationError("sectorIds", "Informe pelo menos um setor.");
            var wanted = sectorIds.Distinct().ToHashSet();
            if (wanted.SetEquals(e.Sectors.Select(s => s.SectorId))) return Results.NoContent();
            // Alteração manual: a LG não troca mais o setor
            MarkManualSectors(e, user);
            db.RemoveRange(e.Sectors.Where(s => !wanted.Contains(s.SectorId)).ToList());
            db.AddRange(wanted.Where(w => e.Sectors.All(s => s.SectorId != w)).ToList()
                .Select(w => new EmployeeSector { EmployeeId = id, SectorId = w }));
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireAuthorization(Policies.HumanResources);

        // "Voltar a seguir o setor da LG": limpa a marca de alteração manual e aplica já o setor do órgão responsável
        // atual (o da última sincronização). Sem setor da LG conhecido, só limpa a marca: a próxima sincronização aplica.
        group.MapPost("/{id:guid}/sectors/seguir-lg", async (Guid id, EmployeeDbContext db, IPublishEndpoint bus, ICurrentUser user) =>
        {
            var e = await db.WithDetails().FirstOrDefaultAsync(x => x.Id == id);
            if (e is null) return Results.NotFound();
            if (!CanManageAbsences(user, e.StoreId)) return Results.Forbid();
            if (e.LgSincronizadoEm is null && e.LgEmpresaCodigo is null)
                return ServiceDefaults.ValidationError("lg", "Colaborador sem vínculo com a LG: não há setor da LG para seguir.");
            var antes = e.Sectors.Select(s => s.SectorId).ToHashSet();
            e.SetoresManuais = false;
            e.SetoresAlteradosPor = user.Display;
            e.SetoresAlteradosEm = DateTime.UtcNow;
            var (_, removidos) = LgRegras.AplicarSetorLg(e, e.LgOrgaoCodigo, e.LgOrgaoDescricao, e.LgSetorId);
            db.RemoveRange(removidos);
            await db.SaveChangesAsync();
            var depois = e.Sectors.Select(s => s.SectorId).ToHashSet();
            var resumo = e.LgSetorId is null
                ? "voltou a seguir o setor da LG (órgão responsável vazio: setor aplicado na próxima sincronização)"
                : $"voltou a seguir o setor da LG ({e.LgOrgaoDescricao ?? e.LgOrgaoCodigo?.ToString()})" +
                  (antes.SetEquals(depois) ? "" : $"; setores {antes.Count} → {depois.Count}");
            await bus.Publish(new EmployeeDetailsChanged(e.Id, e.StoreId, e.Registration, e.Name, e.Active,
                depois.ToList(), resumo, user.Display, DateTime.UtcNow));
            return Results.Ok(ToResponse(e, user));
        }).RequireAuthorization(Policies.HumanResources);

        // Perfil de jornada, disponibilidade, preferências e custo/hora: Admin, RH e Gestor da loja do colaborador.
        // Vale para as próximas gerações (as escalas existentes guardam a fotografia; gerar de novo usa os dados atuais).
        group.MapPut("/{id:guid}/jornada", async (Guid id, JourneyRequest req, EmployeeDbContext db, IPublishEndpoint bus, ICurrentUser user) =>
        {
            var e = await db.WithDetails().FirstOrDefaultAsync(x => x.Id == id);
            if (e is null) return Results.NotFound();
            if (!CanManageAbsences(user, e.StoreId)) return Results.Forbid();
            var before = e.PerfilJornada;
            var beforeText = DescribeJourney(e);
            if (ApplyJourney(e, req) is { } invalid) return invalid;
            e.JornadaAtualizadaPor = user.Display;
            e.JornadaAtualizadaEm = DateTime.UtcNow;
            await db.SaveChangesAsync();
            var after = DescribeJourney(e);
            await bus.Publish(new EmployeeJourneyChanged(e.Id, e.StoreId, e.PerfilJornada, before == e.PerfilJornada ? null : before,
                beforeText == after ? after : $"{beforeText} → {after}", user.Display, DateTime.UtcNow));
            return Results.Ok(ToResponse(e, user));
        }).RequireAuthorization(Policies.HumanResources);

        // Nova versão de contrato: fecha a vigência da versão aberta no dia anterior.
        group.MapPost("/{id:guid}/contracts", async (Guid id, ContractRequest req, EmployeeDbContext db,
            IPublishEndpoint bus, ICurrentUser user) =>
        {
            var e = await db.Employees.Include(x => x.Contracts).FirstOrDefaultAsync(x => x.Id == id);
            if (e is null) return Results.NotFound();
            if (!CanManageAbsences(user, e.StoreId)) return Results.Forbid();
            var last = e.Contracts.OrderByDescending(c => c.ValidFrom).FirstOrDefault();
            if (last is not null && req.ValidFrom <= last.ValidFrom)
                return ServiceDefaults.ValidationError("validFrom", $"A nova versão precisa começar depois de {last.ValidFrom:dd/MM/yyyy}.");
            if (last is not null) last.ValidTo = req.ValidFrom.AddDays(-1);

            var contract = NewContract(req, user.Display);
            contract.EmployeeId = e.Id;
            db.Contracts.Add(contract);
            await db.SaveChangesAsync();
            await bus.Publish(new EmployeeContractChanged(e.Id, contract.Id, contract.ValidFrom, user.Display, DateTime.UtcNow));
            return Results.Ok(ToResponse(contract));
        }).RequireAuthorization(Policies.HumanResources);

        // ---------- Ausências (férias, afastamento médico...): Admin, RH e Gestor da loja do colaborador ----------

        group.MapGet("/{id:guid}/unavailabilities", async (Guid id, EmployeeDbContext db, ICurrentUser user) =>
        {
            var e = await db.Employees.Include(x => x.Unavailabilities).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (e is null) return Results.NotFound();
            if (!user.CanAccessStore(e.StoreId)) return Results.Forbid();
            return Results.Ok(e.Unavailabilities.OrderByDescending(u => u.Start).Select(u => ToResponse(u, e.StoreId, user)));
        });

        group.MapPost("/{id:guid}/unavailabilities", async (Guid id, UnavailabilityRequest req, EmployeeDbContext db,
            IPublishEndpoint bus, ICurrentUser user) =>
        {
            var employeeStore = await db.Employees.Where(x => x.Id == id).Select(x => (Guid?)x.StoreId).FirstOrDefaultAsync();
            if (employeeStore is null) return Results.NotFound();
            if (!CanManageAbsences(user, employeeStore.Value)) return Results.Forbid();
            if (await ValidateAbsenceAsync(req, id, null, db) is { } invalid) return invalid;
            var u = new Unavailability
            {
                EmployeeId = id, Start = req.Start, End = req.End, Type = req.Type,
                Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim(), CreatedBy = user.Display
            };
            db.Unavailabilities.Add(u);
            await db.SaveChangesAsync();
            await bus.Publish(new EmployeeUnavailabilityChanged(u.Id, id, employeeStore.Value, "registrada", u.Start, u.End, u.Type.ToString(),
                null, null, user.Display, DateTime.UtcNow));
            return Results.Created($"/api/employees/{id}/unavailabilities/{u.Id}", ToResponse(u, employeeStore.Value, user));
        }).RequireAuthorization(Policies.HumanResources);

        group.MapPut("/{id:guid}/unavailabilities/{unavailabilityId:guid}", async (Guid id, Guid unavailabilityId, UnavailabilityRequest req,
            EmployeeDbContext db, IPublishEndpoint bus, ICurrentUser user) =>
        {
            var employeeStore = await db.Employees.Where(x => x.Id == id).Select(x => (Guid?)x.StoreId).FirstOrDefaultAsync();
            if (employeeStore is null) return Results.NotFound();
            if (!CanManageAbsences(user, employeeStore.Value)) return Results.Forbid();
            var u = await db.Unavailabilities.FirstOrDefaultAsync(x => x.Id == unavailabilityId && x.EmployeeId == id);
            if (u is null) return Results.NotFound();
            if (u.DaLg) return FeriasDaLg();
            if (await ValidateAbsenceAsync(req, id, u.Id, db) is { } invalid) return invalid;
            var (previousStart, previousEnd) = (u.Start, u.End);
            u.Start = req.Start;
            u.End = req.End;
            u.Type = req.Type;
            u.Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim();
            u.UpdatedBy = user.Display;
            u.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await bus.Publish(new EmployeeUnavailabilityChanged(u.Id, id, employeeStore.Value, "alterada", u.Start, u.End, u.Type.ToString(),
                previousStart, previousEnd, user.Display, DateTime.UtcNow));
            return Results.Ok(ToResponse(u, employeeStore.Value, user));
        }).RequireAuthorization(Policies.HumanResources);

        group.MapDelete("/{id:guid}/unavailabilities/{unavailabilityId:guid}", async (Guid id, Guid unavailabilityId,
            EmployeeDbContext db, IPublishEndpoint bus, ICurrentUser user) =>
        {
            var employeeStore = await db.Employees.Where(x => x.Id == id).Select(x => (Guid?)x.StoreId).FirstOrDefaultAsync();
            if (employeeStore is null) return Results.NotFound();
            if (!CanManageAbsences(user, employeeStore.Value)) return Results.Forbid();
            var u = await db.Unavailabilities.FirstOrDefaultAsync(x => x.Id == unavailabilityId && x.EmployeeId == id);
            if (u is null) return Results.NotFound();
            if (u.DaLg) return FeriasDaLg();
            db.Unavailabilities.Remove(u);
            await db.SaveChangesAsync();
            await bus.Publish(new EmployeeUnavailabilityChanged(u.Id, id, employeeStore.Value, "excluida", u.Start, u.End, u.Type.ToString(),
                null, null, user.Display, DateTime.UtcNow));
            return Results.NoContent();
        }).RequireAuthorization(Policies.HumanResources);

        // ---------- Interno: usado pelo Scheduling Service para montar a fotografia ----------
        var internalApi = app.MapInternalGroup();
        internalApi.MapGet("/planning/employees", async (Guid storeId, DateOnly from, DateOnly to, EmployeeDbContext db) =>
        {
            var list = await db.WithDetails().Where(e => e.StoreId == storeId && e.Active).ToListAsync();
            var result = new List<EmployeeSnapshot>();
            foreach (var e in list)
            {
                // Férias/afastamento na LG sem período registrado: fora da geração (abordagem mais segura; o
                // Scheduling.Api avisa pela lista de /internal/planning/pending)
                if (LgRegras.PendenciaDePeriodo(e)) continue;
                // Contrato vigente no primeiro dia do horizonte ou, com admissão no meio do período, o que começa nele
                var c = PlanningContract(e, from, to);
                if (c is null) continue;
                var absences = PlanningAbsencesOf(e, from, to);
                result.Add(new EmployeeSnapshot(e.Id, e.Registration, e.Name, e.Sectors.Select(s => s.SectorId).ToList(),
                    c.Id, c.JourneyControlled, c.OvertimeAgreement, c.SundayRule,
                    absences.Select(a => a.Date).ToList(),
                    absences.Select(a => new UnavailabilitySnapshot(a.Date, a.Tipo)).ToList(),
                    // Perfil de jornada, disponibilidade semanal, preferências e custo/hora (campo "jornada")
                    JourneyOf(e),
                    // Cargo: linha "cargo – perfil" da consulta das escalas
                    string.IsNullOrWhiteSpace(e.JobTitle) ? null : e.JobTitle));
            }
            return Results.Ok(result);
        });

        // Férias da LG antes da geração (Scheduling.Api): espera no máximo TimeoutSegundos (padrão 20, até 120); depois
        // responde "EmAndamento" e a consulta continua em segundo plano. Sem RequestAborted de propósito: se o Scheduling
        // desistir, a atualização termina e grava para a próxima geração. Nunca falha (a geração segue com o que há).
        internalApi.MapPost("/planning/ferias-lg", async (FeriasLgAtualizacaoRequest req, LgFeriasAtualizador atualizador) =>
        {
            var espera = TimeSpan.FromSeconds(Math.Clamp(req.TimeoutSegundos ?? 20, 1, 120));
            var filtro = new LgFeriasFiltro(req.StoreId, req.SectorId, req.EmployeeIds is { Count: > 0 } ids ? ids.ToList() : null);
            return Results.Ok(await atualizador.AtualizarAsync(filtro, espera, CancellationToken.None));
        });

        // Ausências ATUAIS por dia (o Scheduling.Api mostra "Afastado" e reavalia escalas editáveis com elas)
        internalApi.MapGet("/planning/absences", async (Guid storeId, DateOnly from, DateOnly to, EmployeeDbContext db) =>
        {
            // Também quem tem contrato começando/terminando no período (dias sem contrato, ver PlanningAbsencesOf)
            var list = await db.Employees.AsNoTracking().Include(e => e.Unavailabilities).Include(e => e.Contracts).AsSplitQuery()
                .Where(e => e.StoreId == storeId && (e.Unavailabilities.Any(u => u.End >= from && u.Start <= to)
                                                     || e.Contracts.Any(c => (c.ValidFrom > from && c.ValidFrom <= to)
                                                                             || (c.ValidTo != null && c.ValidTo >= from && c.ValidTo < to))))
                .ToListAsync();
            return Results.Ok(list.SelectMany(e => PlanningAbsencesOf(e, from, to)).ToList());
        });

        // Colaboradores ativos em férias/afastamento na LG sem período registrado (excluídos de /planning/employees)
        internalApi.MapGet("/planning/pending", async (Guid storeId, EmployeeDbContext db) =>
        {
            var list = await db.Employees.AsNoTracking().Include(e => e.Sectors).Include(e => e.Unavailabilities).AsSplitQuery()
                .Where(e => e.StoreId == storeId && e.Active && e.LgSituacaoCodigo != null && e.LgSituacaoCodigo != LgRegras.AtividadeNormal)
                .ToListAsync();
            return Results.Ok(list.Where(LgRegras.PendenciaDePeriodo).OrderBy(e => e.Name)
                .Select(e => new PendingPeriodEntry(e.Id, e.Registration, e.Name, e.Sectors.Select(s => s.SectorId).ToList(),
                    e.LgSituacaoDescricao ?? "situação diferente de ATIVIDADE NORMAL", e.LgDataInicioSituacao))
                .ToList());
        });
    }

    /// <summary>Férias importadas da LG não se editam nem se excluem na Escala: a sincronização desfaria.</summary>
    private static IResult FeriasDaLg() => ServiceDefaults.ValidationError("origem",
        "Período de férias importado da LG: altere ou cancele na LG; a próxima sincronização (ou \"Atualizar férias\") atualiza aqui.");

    /// <summary>
    /// Um registro por dia no período, início e fim INCLUSIVE (férias da LG 16/11–15/12: 15/11 livre, 16/11..15/12
    /// ausente, 16/12 livre); dias com mais de uma ausência ficam com o tipo de maior precedência.
    /// </summary>
    public static List<AbsenceEntry> AbsencesOf(Domain.Employee e, DateOnly from, DateOnly to) =>
        e.Unavailabilities
            .Where(u => u.End >= from && u.Start <= to)
            .SelectMany(u => Enumerable.Range(0, u.End.DayNumber - u.Start.DayNumber + 1).Select(u.Start.AddDays)
                .Where(d => d >= from && d <= to)
                .Select(d => (Date: d, Tipo: u.Type.ToString())))
            .GroupBy(x => x.Date)
            .Select(g => new AbsenceEntry(e.Id, g.Key, g.Select(x => x.Tipo).OrderBy(UnavailabilityKinds.Rank).First()))
            .OrderBy(a => a.Date)
            .ToList();

    /// <summary>
    /// Contrato da fotografia: o vigente em <paramref name="from"/> (primeiro dia do horizonte) ou, sem ele, o primeiro que
    /// começa no período (admissão no meio do mês/semana). Antes, quem era admitido depois da segunda-feira do horizonte
    /// (ex.: 27/10 a 30/11 para novembro/2026) ficava fora da escala do mês inteiro.
    /// </summary>
    public static ContractVersion? PlanningContract(Domain.Employee e, DateOnly from, DateOnly to) =>
        e.ContractAt(from) ?? e.Contracts.Where(c => c.ValidFrom > from && c.ValidFrom <= to).OrderBy(c => c.ValidFrom).FirstOrDefault();

    /// <summary>
    /// Ausências para a fotografia (<see cref="AbsencesOf"/>) mais os dias do período SEM contrato vigente (antes da
    /// admissão, depois do fim do contrato) como "Indisponivel": no motor e no avaliador são ausência (não trabalha, não é
    /// folga, interrompe as sequências e reduz os dias de trabalho da semana), nunca folga forçada.
    /// </summary>
    public static List<AbsenceEntry> PlanningAbsencesOf(Domain.Employee e, DateOnly from, DateOnly to)
    {
        var list = AbsencesOf(e, from, to);
        if (e.Contracts.Count == 0) return list;
        var known = list.Select(a => a.Date).ToHashSet();
        for (var d = from; d <= to; d = d.AddDays(1))
            if (!known.Contains(d) && e.ContractAt(d) is null) list.Add(new AbsenceEntry(e.Id, d, UnavailabilityKinds.Indisponivel));
        return list.OrderBy(a => a.Date).ToList();
    }

    private static ContractVersion NewContract(ContractRequest req, string by) => new()
    {
        ValidFrom = req.ValidFrom,
        JourneyControlled = req.JourneyControlled,
        OvertimeAgreement = req.OvertimeAgreement,
        SundayRule = req.SundayRule,
        LegalOpinionRef = req.LegalOpinionRef,
        CreatedBy = by
    };
}

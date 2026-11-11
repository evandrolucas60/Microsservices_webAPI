using System.Globalization;
using Escala5x2.BuildingBlocks.Compliance;
using Escala5x2.BuildingBlocks.Contracts.Compliance;
using Escala5x2.BuildingBlocks.Contracts.Http;
using Escala5x2.BuildingBlocks.Contracts.Planning;
using Escala5x2.BuildingBlocks.Contracts.Scheduling;
using Escala5x2.BuildingBlocks.Shared.Auth;
using Escala5x2.BuildingBlocks.Shared.Web;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Scheduling.Api.Data;
using Scheduling.Api.Domain;
using Scheduling.Api.Services;

namespace Scheduling.Api.Endpoints;

/// <summary>Intervalo proposto para a célula de trabalho do colaborador no dia ("HH:mm"; 0 min = sem intervalo).</summary>
public sealed record BreakInput(Guid EmployeeId, string? BreakStart, int? BreakMinutes);

/// <param name="ConfirmarSemAmparoLegal">Grava mesmo com regra RÍGIDA violada (fica registrado como não conforme).</param>
public sealed record DayBreaksRequest(List<BreakInput>? Intervalos, bool? ConfirmarSemAmparoLegal);

public sealed record RedistributeRequest(bool? IncluirManuais);

/// <summary>Aviso de um colaborador no dia. Rígida = sem amparo legal (severidade alta); preferencial = só aviso.</summary>
public sealed record DayIssue(string Chave, string Descricao, string Severidade, bool Rigida, string Mensagem, string? BaseLegal);

/// <summary>
/// Janela do intervalo pelas regras ATIVAS da fotografia: durações permitidas (rígidas) e início mais cedo/mais
/// tarde — legal (regras rígidas) e recomendado (rígidas + preferenciais) — para a duração atual.
/// </summary>
public sealed record DayWindow(int DuracaoMinima, int DuracaoMaxima, int? DuracaoPadrao, bool Obrigatorio,
    string? InicioMaisCedo, string? InicioMaisTarde, string? InicioRecomendadoDe, string? InicioRecomendadoAte);

/// <summary>
/// Colaborador escalado no dia. Minutos "...Min" são absolutos a partir da meia-noite do dia (turno que vira o dia passa de 1440).
/// <paramref name="IntervaloSugerido"/>: com o intervalo ainda não posicionado (escalas anteriores ao recurso de almoço),
/// a posição que o motor (BreakPlanner) escolheria agora, preservando a cobertura do dia; null quando já posicionado.
/// </summary>
public sealed record DayEmployee(
    Guid EmployeeId, string Nome, string Matricula, Guid ShiftId, string ShiftCode,
    string Entrada, string Saida, int EntradaMin, int SaidaMin, bool ViraODia,
    string? IntervaloInicio, string? IntervaloFim, int? IntervaloInicioMin, int IntervaloMinutos, int IntervaloDoTurnoMinutos,
    bool IntervaloPosicionado, string IntervaloOrigem, string CelulaOrigem, int JornadaLiquidaMinutos,
    DayWindow Janela, IReadOnlyList<DayIssue> Avisos, string? IntervaloSugerido = null, int? IntervaloSugeridoMin = null,
    // Tipo do turno na operação (Abertura | Fechamento | Flexivel; null = sem operação) e perfil do colaborador
    string? ShiftTipo = null, string? PerfilJornada = null);

/// <summary>Quem não trabalha no setor no dia: Folga, Afastado (rótulo genérico para o Colaborador), OutroSetor ou Vazio.</summary>
public sealed record DayAbsent(Guid EmployeeId, string Nome, string Matricula, string Situacao, string Rotulo);

/// <summary>Faixa de cobertura: presentes (sem quem está em intervalo) × exigido pela demanda do setor.</summary>
/// <param name="Minimo">Mínimo RÍGIDO da faixa (operação: cobertura/abertura/fechamento); <paramref name="Exigido"/> = alvo (com o reforço de pico).</param>
public sealed record DaySlot(string Inicio, string Fim, int InicioMin, int Exigido, int Presentes, int EmIntervalo, bool Abaixo, int Minimo = 0, bool AbaixoDoMinimo = false);

public sealed record DayView(
    Guid ScheduleId, DateOnly Data, DateOnly? DiaAnterior, DateOnly? ProximoDia, ScheduleSummary Escala,
    bool Editavel, string? SomenteLeituraMotivo, double? ConformidadePercentual, int FaixaMinutos, int PassoMinutos,
    IReadOnlyList<DayEmployee> Escalados, IReadOnlyList<DayAbsent> ForaDaEscala, IReadOnlyList<DaySlot> Cobertura,
    IReadOnlyList<ComplianceViolationDto> Violacoes, bool TemViolacaoRigida, bool Simulacao);

/// <summary>
/// Visão do dia da escala: ajuste dos intervalos de refeição dos escalados no dia.
/// Permissões como a edição de células: Gestor da loja e Admin editam; RH/Jurídico/Colaborador só consultam.
/// Editável em Rascunho/Gerada (e Inviável/Falhou). Aprovada/Publicada: somente leitura — a aprovação registra o
/// percentual de conformidade da escala inteira, inclusive dos intervalos (que mexem na jornada e na cobertura);
/// mudar depois tiraria o valor da aprovação. Para ajustar: na aprovada, reabrir para edição (desfaz a aprovação);
/// na publicada, gerar nova versão.
/// </summary>
public static class DayEndpoints
{
    /// <summary>Passo dos campos de horário da tela (o motor posiciona de 15 em 15 min).</summary>
    public const int StepMinutes = 5;

    private static string Hm(int minutes) => $"{minutes / 60 % 24:00}:{minutes % 60:00}";

    private static bool TryDate(string data, out DateOnly date) =>
        DateOnly.TryParseExact(data, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    public static void MapDayEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/schedules/{id:guid}/dia/{data}").RequireAuthorization();

        group.MapGet("/", async (Guid id, string data, SchedulingDbContext db, ICurrentUser user, ScheduleEvaluation evaluation,
            AbsenceSync sync, CancellationToken ct) =>
        {
            var s = await db.Schedules.Include(x => x.Assignments).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (s is null) return Results.NotFound();
            if (!ScheduleEndpoints.CanAccess(user, s.StoreId)) return Results.Forbid();
            if (CheckDate(s, data) is { } bad) return bad;
            var date = DateOnly.ParseExact(data, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var absences = s.Status == ScheduleStatus.Generating ? null : await ScheduleEndpoints.RefreshAbsencesAsync(s, sync, evaluation, ct);
            if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
            var snapshot = ScheduleCells.ReadSnapshot(s);
            var result = ScheduleEvaluation.Stored(s) ?? evaluation.Evaluate(s, snapshot);
            return Results.Ok(BuildView(s, snapshot, date, result, absences, user, simulation: false));
        });

        // Simulação (não grava): mesma estrutura com os intervalos propostos, para a tela avisar em tempo real
        group.MapPost("/validar", async (Guid id, string data, DayBreaksRequest req, SchedulingDbContext db, ICurrentUser user,
            ScheduleEvaluation evaluation, CancellationToken ct) =>
        {
            var s = await db.Schedules.AsNoTracking().Include(x => x.Assignments).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (s is null) return Results.NotFound();
            if (!ScheduleEndpoints.CanManage(user, s.StoreId)) return Results.Forbid();
            if (CheckDate(s, data) is { } bad) return bad;
            var date = DateOnly.ParseExact(data, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var snapshot = ScheduleCells.ReadSnapshot(s);
            // Entidades sem rastreamento: as mudanças ficam só em memória
            if (ApplyBreaks(s, snapshot, date, req.Intervalos, CellOrigin.Manual) is { } invalid) return invalid;
            var result = evaluation.Evaluate(s, snapshot);
            return Results.Ok(BuildView(s, snapshot, date, result, null, user, simulation: true) with { ConformidadePercentual = result.Percentual });
        }).RequireAuthorization(Policies.Manager);

        group.MapPut("/intervalos", async (Guid id, string data, DayBreaksRequest req, SchedulingDbContext db, ICurrentUser user,
            ScheduleEvaluation evaluation, AbsenceSync sync, IPublishEndpoint bus, CancellationToken ct) =>
        {
            var s = await db.Schedules.Include(x => x.Assignments).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (s is null) return Results.NotFound();
            if (!ScheduleEndpoints.CanManage(user, s.StoreId)) return Results.Forbid();
            if (!s.IsEditable) return ScheduleEndpoints.NotEditable(s);
            if (CheckDate(s, data) is { } bad) return bad;
            var date = DateOnly.ParseExact(data, "yyyy-MM-dd", CultureInfo.InvariantCulture);

            var absences = await ScheduleEndpoints.RefreshAbsencesAsync(s, sync, evaluation, ct);
            var snapshot = ScheduleCells.ReadSnapshot(s);
            var before = BuildView(s, snapshot, date, evaluation.Evaluate(s, snapshot), absences, user, simulation: true);

            if (ApplyBreaks(s, snapshot, date, req.Intervalos, CellOrigin.Manual) is { } invalid) return invalid;
            var after = BuildView(s, snapshot, date, evaluation.Evaluate(s, snapshot), absences, user, simulation: true);

            // Sem amparo legal = regra RÍGIDA ativa que passa a ser violada pelo intervalo proposto (as que já existiam
            // antes, por outro motivo, não impedem o ajuste do almoço)
            var changed = req.Intervalos!.Select(i => i.EmployeeId).ToHashSet();
            var semAmparo = after.Escalados.Where(e => changed.Contains(e.EmployeeId))
                .Select(e =>
                {
                    var had = before.Escalados.FirstOrDefault(b => b.EmployeeId == e.EmployeeId)?.Avisos.Where(a => a.Rigida).Select(a => a.Chave).ToHashSet() ?? [];
                    return (Employee: e, Issues: e.Avisos.Where(a => a.Rigida && !had.Contains(a.Chave)).ToList());
                })
                .Where(x => x.Issues.Count > 0)
                .ToList();
            var confirmed = req.ConfirmarSemAmparoLegal == true;
            if (semAmparo.Count > 0 && !confirmed)
            {
                var messages = semAmparo.SelectMany(x => x.Issues.Select(i => $"{x.Employee.Nome}: {i.Mensagem} — {i.BaseLegal}.")).ToArray();
                return Results.ValidationProblem(
                    new Dictionary<string, string[]> { ["confirmarSemAmparoLegal"] = messages },
                    detail: "O horário proposto não tem amparo legal (viola regra rígida). Para gravar mesmo assim, confirme " +
                            "(confirmarSemAmparoLegal = true): a escala fica registrada como não conforme.",
                    title: "Intervalo sem amparo legal",
                    extensions: new Dictionary<string, object?>
                    {
                        ["code"] = "sem_amparo_legal",
                        ["colaboradores"] = semAmparo.Select(x => new { x.Employee.EmployeeId, x.Employee.Nome, avisos = x.Issues }).ToList()
                    });
            }

            s.CellsRevision++;
            s.UpdatedAt = DateTime.UtcNow;
            var result = evaluation.EvaluateAndStore(s, snapshot);
            await bus.Publish(new ScheduleCellsEdited(s.Id, changed.Count, semAmparo.Count > 0 ? "IntervaloSemAmparoLegal" : "IntervaloManual",
                result.Percentual, user.Display, DateTime.UtcNow), ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(BuildView(s, snapshot, date, result, absences, user, simulation: false));
        }).RequireAuthorization(Policies.Manager);

        group.MapPost("/intervalos/redistribuir", async (Guid id, string data, [FromBody] RedistributeRequest? req, SchedulingDbContext db,
            ICurrentUser user, ScheduleEvaluation evaluation, AbsenceSync sync, IPublishEndpoint bus, CancellationToken ct) =>
        {
            var s = await db.Schedules.Include(x => x.Assignments).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (s is null) return Results.NotFound();
            if (!ScheduleEndpoints.CanManage(user, s.StoreId)) return Results.Forbid();
            if (!s.IsEditable) return ScheduleEndpoints.NotEditable(s);
            if (CheckDate(s, data) is { } bad) return bad;
            var date = DateOnly.ParseExact(data, "yyyy-MM-dd", CultureInfo.InvariantCulture);

            var absences = await ScheduleEndpoints.RefreshAbsencesAsync(s, sync, evaluation, ct);
            var snapshot = ScheduleCells.ReadSnapshot(s);
            var all = req?.IncluirManuais == true;
            // Com incluirManuais, os ajustados à mão voltam ao motor (duração do turno, posição automática)
            var count = ScheduleCells.PlanBreaks(s, snapshot, a => a.Date == date && (all || a.BreakOrigem != CellOrigin.Manual), resetDuration: all);
            s.CellsRevision++;
            s.UpdatedAt = DateTime.UtcNow;
            var result = evaluation.EvaluateAndStore(s, snapshot);
            await bus.Publish(new ScheduleCellsEdited(s.Id, count, "IntervaloRedistribuido", result.Percentual, user.Display, DateTime.UtcNow), ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(BuildView(s, snapshot, date, result, absences, user, simulation: false));
        }).RequireAuthorization(Policies.Manager);
    }

    private static IResult? CheckDate(Schedule s, string data)
    {
        if (!TryDate(data, out var date)) return ServiceDefaults.ValidationError("data", "Informe a data no formato yyyy-MM-dd.");
        if (date < s.StartDate || date > s.EndDate)
            return ServiceDefaults.ValidationError("data", $"{date:dd/MM/yyyy} está fora do período da escala ({s.StartDate:dd/MM} a {s.EndDate:dd/MM}).");
        return null;
    }

    /// <summary>
    /// Valida a forma dos intervalos propostos (erros que nem a confirmação libera) e aplica nas células do dia.
    /// Intervalo fracionado não é permitido: um único intervalo por colaborador na jornada.
    /// </summary>
    private static IResult? ApplyBreaks(Schedule s, PlanningSnapshot snapshot, DateOnly date, List<BreakInput>? input, CellOrigin origin)
    {
        if (input is not { Count: > 0 }) return ServiceDefaults.ValidationError("intervalos", "Informe ao menos um intervalo.");
        if (input.GroupBy(i => i.EmployeeId).FirstOrDefault(g => g.Count() > 1) is { } dup)
        {
            var name = s.Assignments.FirstOrDefault(a => a.EmployeeId == dup.Key)?.EmployeeName ?? "colaborador";
            return ServiceDefaults.ValidationError("intervalos",
                $"Intervalo fracionado não é permitido: informe um único intervalo para {name} na jornada (CLT art. 71).");
        }

        var plan = new List<(ScheduleAssignment Cell, TimeOnly? Start, int Minutes)>();
        foreach (var i in input)
        {
            var cell = s.Assignments.FirstOrDefault(a => a.EmployeeId == i.EmployeeId && a.Date == date && !a.Folga && a.ShiftId is not null && a.Start is not null && a.End is not null);
            if (cell is null) return ServiceDefaults.ValidationError("employeeId", $"O colaborador {i.EmployeeId} não está escalado no setor em {date:dd/MM}.");
            var start = PlanningContext.ToMinutes(cell.Start!.Value);
            var end = PlanningContext.EndAbs(start, PlanningContext.ToMinutes(cell.End!.Value));
            var minutes = i.BreakMinutes ?? ScheduleCells.EffectiveBreak(cell, snapshot);
            if (minutes < 0 || minutes >= end - start)
                return ServiceDefaults.ValidationError("breakMinutes", $"Duração de intervalo inválida para {cell.EmployeeName} ({minutes} min).");
            TimeOnly? breakStart = null;
            if (minutes > 0)
            {
                if (string.IsNullOrWhiteSpace(i.BreakStart) ||
                    !TimeOnly.TryParseExact(i.BreakStart.Trim(), ["HH:mm", "H:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
                    return ServiceDefaults.ValidationError("breakStart", $"Informe o início do intervalo de {cell.EmployeeName} (HH:mm).");
                var abs = PlanningContext.BreakAbs(start, t);
                if (abs < start || abs + minutes > end)
                    return ServiceDefaults.ValidationError("breakStart",
                        $"O intervalo de {cell.EmployeeName} ({Hm(abs)}–{Hm(abs + minutes)}) precisa ficar dentro da jornada ({cell.Start:HH\\:mm}–{cell.End:HH\\:mm}).");
                breakStart = t;
            }
            plan.Add((cell, breakStart, minutes));
        }
        foreach (var (cell, start, minutes) in plan) ScheduleCells.SetBreak(cell, snapshot, start, minutes, origin);
        return null;
    }

    private static DayView BuildView(Schedule s, PlanningSnapshot snapshot, DateOnly date, ComplianceResult evaluation,
        IReadOnlyList<AbsenceEntry>? absences, ICurrentUser user, bool simulation)
    {
        var ctx = PlanningContext.Create(snapshot, s.StartDate, s.EndDate);
        var day = ctx.DayIndex(date);
        var policy = ctx.Interval(day);
        string Desc(string key) => ctx.Rules.Representative(key)?.Descricao ?? key;
        string? BaseLegalOf(string key) => ctx.Rules.Active(key, day)?.BaseLegal ?? ctx.Rules.Representative(key)?.BaseLegal
            ?? IntervalPolicy.DefaultBaseLegal.GetValueOrDefault(key);

        var employees = snapshot.Employees.ToDictionary(e => e.EmployeeId);
        var dayCells = s.Assignments.Where(a => a.Date == date).ToList();
        var work = dayCells.Where(a => !a.Folga && a.ShiftId is not null && a.Start is not null && a.End is not null).ToList();
        var dayViolations = evaluation.Violacoes.Where(v => v.Dates.Contains(date)).ToList();

        // Intervalos não posicionados (BreakStart nulo): sugestão do BreakPlanner com os demais como estão (só leitura,
        // nada é gravado; a tela mostra o bloco tracejado nessa posição para arrastar)
        var suggested = new Dictionary<Guid, int>();
        if (work.Any(a => a.BreakStart is null))
        {
            var dayDtos = work.Select(ScheduleCells.ToAssignment).ToList();
            var planned = BreakPlanner.Plan(ctx, dayDtos, a => a.BreakStart is null);
            for (var i = 0; i < work.Count; i++)
                if (work[i].BreakStart is null && planned[i].BreakStart is { } b)
                    suggested[work[i].EmployeeId] = PlanningContext.BreakAbs(PlanningContext.ToMinutes(work[i].Start!.Value), b);
        }

        var escalados = work.Select(a =>
            {
                var dto = ScheduleCells.ToAssignment(a);
                var t = ctx.Times(dto);
                var w = policy.Window(t.Start, t.EndAbs, t.BreakMinutes);
                var issues = policy.Checks(t.Start, t.EndAbs, t.BreakStart, t.BreakMinutes)
                    .Where(c => !c.Ok)
                    .Select(c => new DayIssue(c.Key, Desc(c.Key), c.Rigid ? "alta" : "baixa", c.Rigid, c.Detail, c.BaseLegal))
                    .Concat(dayViolations
                        .Where(v => v.EmployeeId == a.EmployeeId && !IntervalPolicy.AllKeys.Contains(v.Chave))
                        .Select(v => new DayIssue(v.Chave, v.RegraDescricao, v.Severidade, v.Severidade != "baixa", v.Detalhe, BaseLegalOf(v.Chave))))
                    .ToList();
                string? H(int? m) => m is { } x ? Hm(x) : null;
                var emp = employees.GetValueOrDefault(a.EmployeeId);
                var shiftBreak = snapshot.Shifts.FirstOrDefault(x => x.ShiftId == a.ShiftId)?.BreakMinutes ?? t.BreakMinutes;
                int? sugMin = suggested.TryGetValue(a.EmployeeId, out var sg) ? sg : null;
                return new DayEmployee(a.EmployeeId, emp?.Name ?? a.EmployeeName, emp?.Registration ?? a.Registration, a.ShiftId!.Value, a.ShiftCode,
                    Hm(t.Start), Hm(t.EndAbs), t.Start, t.EndAbs, t.EndAbs > PlanningContext.MinutesPerDay,
                    H(t.BreakStart), H(t.BreakEnd), t.BreakStart, t.BreakMinutes, shiftBreak,
                    t.BreakStart is not null, a.BreakOrigem.ToString(), a.Origem.ToString(), t.Worked,
                    new DayWindow(w.MinMinutes, w.MaxMinutes, w.DefaultMinutes, w.Required,
                        H(w.LegalEarliest), H(w.LegalLatest), H(w.RecommendedEarliest), H(w.RecommendedLatest)),
                    issues, H(sugMin), sugMin, ctx.ShiftById(a.ShiftId!.Value)?.Kind, ctx.OperationMode ? emp?.Jornada?.PerfilJornada : null);
            })
            .OrderBy(e => e.EntradaMin).ThenBy(e => e.Nome)
            .ToList();

        // Fora da escala do setor no dia
        var typed = ScheduleEndpoints.SeesAbsenceType(user, s.StoreId);
        var absenceMap = (absences ?? snapshot.Employees
                .SelectMany(e => AbsenceSync.Absences(e, date, date).Select(x => new AbsenceEntry(e.EmployeeId, x.Date, x.Tipo))).ToList())
            .Where(x => x.Date == date)
            .GroupBy(x => x.EmployeeId).ToDictionary(g => g.Key, g => g.Select(x => x.Tipo).OrderBy(UnavailabilityKinds.Rank).First());
        var occupied = (snapshot.Occupied ?? []).Where(o => o.Date == date).GroupBy(o => o.EmployeeId).ToDictionary(g => g.Key, g => g.First());
        var working = work.Select(a => a.EmployeeId).ToHashSet();
        var absentIsFolga = s.Status != ScheduleStatus.Draft && !s.Assignments.Any(a => a.Folga);
        var fora = snapshot.Employees.Where(e => !working.Contains(e.EmployeeId))
            .Select(e =>
            {
                if (absenceMap.TryGetValue(e.EmployeeId, out var tipo))
                    return new DayAbsent(e.EmployeeId, e.Name, e.Registration, "Afastado",
                        typed ? UnavailabilityKinds.Rotulo(tipo) : UnavailabilityKinds.RotuloGenerico);
                if (occupied.TryGetValue(e.EmployeeId, out var o))
                    return new DayAbsent(e.EmployeeId, e.Name, e.Registration, "OutroSetor", $"Em {o.SectorName} ({o.Start:HH\\:mm}–{o.End:HH\\:mm})");
                var cell = dayCells.FirstOrDefault(a => a.EmployeeId == e.EmployeeId);
                if (cell?.Folga == true || absentIsFolga) return new DayAbsent(e.EmployeeId, e.Name, e.Registration, "Folga", "Folga");
                return new DayAbsent(e.EmployeeId, e.Name, e.Registration, "Vazio", "Sem definição");
            })
            .OrderBy(x => x.Situacao).ThenBy(x => x.Nome)
            .ToList();

        // Cobertura por faixa do dia (do primeiro horário com gente ou demanda ao último, até a meia-noite)
        var size = ctx.Rules.Value(RuleKeys.CoberturaFaixa, day) is { Value: > 0 } f ? Math.Min(f.Value, PlanningContext.MinutesPerDay) : 30;
        var required = ctx.Slots.Where(x => x.Day == day && x.Sector == 0).ToDictionary(x => x.SlotStart);
        var dtos = work.Select(ScheduleCells.ToAssignment).ToList();
        var times = dtos.Select(ctx.Times).ToList();
        var starts = times.Select(t => t.Start).Concat(required.Keys).ToList();
        var ends = times.Select(t => Math.Min(t.EndAbs, PlanningContext.MinutesPerDay)).Concat(required.Values.Select(x => x.SlotStart + x.SlotMinutes)).ToList();
        var cobertura = new List<DaySlot>();
        if (starts.Count > 0)
        {
            var from = starts.Min() / size * size;
            var to = Math.Min(PlanningContext.MinutesPerDay, ends.Max());
            for (var slotStart = from; slotStart < to; slotStart += size)
            {
                var req = required.GetValueOrDefault(slotStart);
                var slot = req ?? new CoverageSlot(0, day, slotStart, size, 0, false);
                var (present, onBreak) = ComplianceEvaluator.Presence(ctx, dtos, slot);
                var need = req?.Required ?? 0;
                var min = req?.Minimum ?? 0;
                cobertura.Add(new DaySlot(Hm(slotStart), Hm(Math.Min(slotStart + size, req?.SlotEnd ?? slotStart + size)), slotStart, need, present, onBreak,
                    need > 0 && present < need, min, min > 0 && present < min));
            }
        }

        var editable = ScheduleEndpoints.CanManage(user, s.StoreId) && s.IsEditable;
        string? reason = editable ? null
            : !s.IsEditable
                ? s.SectorId is null
                    ? "Escala antiga por loja: somente leitura."
                    : s.Status == ScheduleStatus.Approved
                        ? "Escala aprovada: somente leitura. Para ajustar os intervalos, reabra a escala para edição (a aprovação será desfeita e ela precisará ser aprovada de novo)."
                        : s.Status is ScheduleStatus.Published or ScheduleStatus.Superseded
                            ? "Escala publicada: somente leitura. Para ajustar os intervalos, gere uma nova versão (a publicada vale até a nova ser publicada)."
                            : "A escala não pode ser editada no status atual."
                : "Seu perfil só consulta a escala.";

        var manual = s.Assignments.Any(a => a.Origem == CellOrigin.Manual || ScheduleCells.HasManualBreak(a));
        return new DayView(s.Id, date, date > s.StartDate ? date.AddDays(-1) : null, date < s.EndDate ? date.AddDays(1) : null,
            ScheduleEndpoints.Summary(s, manual), editable, reason, s.ConformidadePercentual ?? evaluation.Percentual, size, StepMinutes,
            escalados, fora, cobertura, dayViolations, escalados.Any(e => e.Avisos.Any(a => a.Rigida)), simulation);
    }
}

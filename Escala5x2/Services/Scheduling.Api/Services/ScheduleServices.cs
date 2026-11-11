using System.Text.Json;
using Escala5x2.BuildingBlocks.Compliance;
using Escala5x2.BuildingBlocks.Contracts.Compliance;
using Escala5x2.BuildingBlocks.Contracts.Http;
using Escala5x2.BuildingBlocks.Contracts.Planning;
using Escala5x2.BuildingBlocks.Contracts.Scheduling;
using Microsoft.EntityFrameworkCore;
using Scheduling.Api.Clients;
using Scheduling.Api.Data;
using Scheduling.Api.Domain;

namespace Scheduling.Api.Services;

/// <summary>Conteúdo de uma célula: turno (trabalho) ou folga marcada; null = célula vazia.</summary>
public sealed record CellContent(ShiftSnapshot? Shift, bool Folga);

/// <summary>Leitura da fotografia gravada e escrita das células de uma escala carregada (com Assignments).</summary>
public static class ScheduleCells
{
    public const string FolgaCode = "Folga";

    public static PlanningSnapshot ReadSnapshot(Schedule s) =>
        JsonSerializer.Deserialize<PlanningSnapshot>(s.SnapshotJson, Json.Options)
        ?? throw new InvalidOperationException("Fotografia da escala ilegível.");

    /// <summary>
    /// Semanas civis inteiras que cobrem o período da escala (segunda da 1ª semana ao domingo da última): regras e feriados
    /// são lidos para elas, porque os dias de borda do mês vizinho entram nas contas da semana.
    /// </summary>
    public static (DateOnly From, DateOnly To) Horizon(Schedule s)
    {
        var (from, weeks) = PlanningContext.Horizon(s.StartDate, s.EndDate);
        return (from, from.AddDays(weeks * 7 - 1));
    }

    /// <summary>Duração do turno em minutos (turno que vira o dia: até a saída do dia seguinte).</summary>
    public static int Duration(TimeOnly start, TimeOnly end)
    {
        var s = start.Hour * 60 + start.Minute;
        var e = end.Hour * 60 + end.Minute;
        if (e <= s) e += 24 * 60;
        return e - s;
    }

    public static int PaidMinutes(ShiftSnapshot shift) => Duration(shift.Start, shift.End) - shift.BreakMinutes;

    /// <summary>Intervalo efetivo da célula: o dela ou, sem ele, o do turno da fotografia.</summary>
    public static int EffectiveBreak(ScheduleAssignment a, PlanningSnapshot snapshot) =>
        a.BreakMinutes ?? snapshot.Shifts.FirstOrDefault(x => x.ShiftId == a.ShiftId)?.BreakMinutes
        ?? (a.Start is { } s && a.End is { } e ? Math.Max(0, Duration(s, e) - a.PaidMinutes) : 0);

    /// <summary>
    /// Define o intervalo da célula de trabalho e recalcula os minutos pagos (duração − intervalo efetivo), que
    /// alimentam jornada diária/semanal, domingo e ocupação em outros setores.
    /// </summary>
    public static void SetBreak(ScheduleAssignment a, PlanningSnapshot snapshot, TimeOnly? start, int? minutes, CellOrigin origin)
    {
        if (a.Folga || a.Start is null || a.End is null) return;
        a.BreakStart = start;
        a.BreakMinutes = minutes;
        a.BreakOrigem = origin;
        a.PaidMinutes = Duration(a.Start.Value, a.End.Value) - EffectiveBreak(a, snapshot);
    }

    /// <summary>Células de trabalho no formato do avaliador.</summary>
    public static List<AssignmentDto> WorkCells(IEnumerable<ScheduleAssignment> cells) =>
        cells.Where(a => !a.Folga && a.ShiftId is not null && a.Start is not null && a.End is not null)
            .Select(ToAssignment)
            .ToList();

    public static AssignmentDto ToAssignment(ScheduleAssignment a) =>
        new(a.EmployeeId, a.Date, a.ShiftId!.Value, a.SectorId, a.Start!.Value, a.End!.Value, a.PaidMinutes, a.BreakStart, a.BreakMinutes);

    /// <summary>Células no formato das mensagens ao Worker (com o intervalo, para a sugestão avaliar igual à API).</summary>
    public static List<CellDto> ToDtos(IEnumerable<ScheduleAssignment> cells) =>
        cells.Select(a => new CellDto(a.EmployeeId, a.Date, a.Folga ? null : a.ShiftId, a.Folga,
            a.Folga ? null : a.BreakStart, a.Folga ? null : a.BreakMinutes, !a.Folga && a.BreakOrigem == CellOrigin.Manual)).ToList();

    /// <summary>Célula com almoço ajustado à mão: fica fixa na geração "somente_vazios" (o motor não mexe).</summary>
    public static bool HasManualBreak(ScheduleAssignment a) => !a.Folga && a.ShiftId is not null && a.BreakOrigem == CellOrigin.Manual;

    /// <summary>
    /// Posiciona automaticamente os intervalos (BreakPlanner) das células de trabalho escolhidas por
    /// <paramref name="movable"/>, escalonando os almoços para preservar a cobertura de cada faixa do setor.
    /// As células posicionadas passam a BreakOrigem = Motor. Devolve quantas células foram posicionadas.
    /// </summary>
    public static int PlanBreaks(Schedule s, PlanningSnapshot snapshot, Func<ScheduleAssignment, bool> movable, bool resetDuration = false)
    {
        var rows = s.Assignments.Where(a => !a.Folga && a.ShiftId is not null && a.Start is not null && a.End is not null).ToList();
        var chosen = rows.Select(movable).ToArray();
        if (!chosen.Any(x => x)) return 0;
        if (resetDuration)
        {
            for (var i = 0; i < rows.Count; i++)
                if (chosen[i]) SetBreak(rows[i], snapshot, rows[i].BreakStart, null, rows[i].BreakOrigem);
        }
        var ctx = PlanningContext.Create(snapshot, s.StartDate, s.EndDate);
        var dtos = rows.Select(ToAssignment).ToList();
        var flags = new HashSet<AssignmentDto>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < rows.Count; i++) if (chosen[i]) flags.Add(dtos[i]);
        var planned = BreakPlanner.Plan(ctx, dtos, a => flags.Contains(a));
        var count = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            if (!chosen[i]) continue;
            SetBreak(rows[i], snapshot, planned[i].BreakStart, rows[i].BreakMinutes, CellOrigin.Motor);
            count++;
        }
        return count;
    }

    /// <summary>Define (ou limpa, com content null) a célula da pessoa no dia, alterando a linha existente quando houver.</summary>
    public static void Set(SchedulingDbContext db, Schedule s, PlanningSnapshot snapshot, Guid employeeId, DateOnly date,
        CellContent? content, CellOrigin origin)
    {
        var existing = s.Assignments.FirstOrDefault(a => a.EmployeeId == employeeId && a.Date == date);
        if (content is null || (content.Shift is null && !content.Folga))
        {
            if (existing is not null)
            {
                db.Assignments.Remove(existing);
                while (s.Assignments.Remove(existing)) { }
            }
            return;
        }

        var target = existing;
        if (target is null)
        {
            var employee = snapshot.Employees.FirstOrDefault(e => e.EmployeeId == employeeId);
            target = new ScheduleAssignment
            {
                ScheduleId = s.Id,
                EmployeeId = employeeId,
                EmployeeName = employee?.Name ?? "?",
                Registration = employee?.Registration ?? string.Empty,
                Date = date
            };
            // Add explícito: chave Guid já preenchida faria o EF tratar como linha existente.
            // (o EF já inclui a linha em s.Assignments pela chave estrangeira; não duplicar)
            db.Assignments.Add(target);
            if (!s.Assignments.Contains(target)) s.Assignments.Add(target);
        }

        var sectorId = s.SectorId ?? snapshot.Sectors.FirstOrDefault()?.SectorId ?? Guid.Empty;
        target.SectorId = sectorId;
        target.SectorName = s.SectorName ?? snapshot.Sectors.FirstOrDefault(x => x.SectorId == sectorId)?.Name ?? "?";
        target.Origem = origin;
        if (content.Shift is { } shift)
        {
            // Mesmo turno: o intervalo da célula continua; outro turno: o intervalo volta ao do turno, sem posição
            // (quem chama reposiciona com PlanBreaks)
            var sameShift = !target.Folga && target.ShiftId == shift.ShiftId;
            target.Folga = false;
            target.ShiftId = shift.ShiftId;
            target.ShiftCode = shift.Code;
            target.Start = shift.Start;
            target.End = shift.End;
            if (sameShift) SetBreak(target, snapshot, target.BreakStart, target.BreakMinutes, target.BreakOrigem);
            else SetBreak(target, snapshot, null, null, CellOrigin.Motor);
        }
        else
        {
            target.Folga = true;
            target.ShiftId = null;
            target.ShiftCode = FolgaCode;
            target.Start = null;
            target.End = null;
            target.PaidMinutes = 0;
            target.BreakStart = null;
            target.BreakMinutes = null;
            target.BreakOrigem = CellOrigin.Motor;
        }
    }
}

/// <summary>
/// Escala PRINCIPAL do setor no período (regra em <see cref="PrincipalSchedule"/>): no máximo uma por setor e dia.
/// Quem chama publica o <see cref="SchedulePrincipalChanged"/> e grava (mesma transação do outbox).
/// </summary>
public static class PrincipalSchedules
{
    /// <summary>Escalas de até 52 semanas: o início mais antigo que ainda pode cruzar o período.</summary>
    private const int MaxWeeks = 52;

    /// <summary>Status que contam como "outra escala para o mesmo período": as que têm quadro e as em geração.</summary>
    private static readonly ScheduleStatus[] Overlap = [.. Schedule.LiveStatuses, ScheduleStatus.Generating];

    public static PrincipalCandidate Candidate(Schedule s) => new(s.Id, s.SectorId, s.StartDate, s.EndDate, s.Principal);

    /// <summary>
    /// Outras escalas vivas (Rascunho, Gerada, Aprovada, Publicada) ou em geração do mesmo setor cujo período cruza
    /// [<paramref name="from"/>, <paramref name="to"/>]: a principal primeiro, depois pela prioridade do status e a mais recente.
    /// </summary>
    public static async Task<List<Schedule>> OverlappingAsync(SchedulingDbContext db, Guid storeId, Guid sectorId, DateOnly from, DateOnly to,
        Guid? excludeId, CancellationToken ct)
    {
        var earliest = from.AddDays(-7 * MaxWeeks);
        var list = await db.Schedules.AsNoTracking()
            .Where(s => s.StoreId == storeId && s.SectorId == sectorId && s.Id != excludeId && (s.Status == ScheduleStatus.Published || s.Status == ScheduleStatus.Approved || s.Status == ScheduleStatus.Generated
                             || s.Status == ScheduleStatus.Draft || s.Status == ScheduleStatus.Generating)
                        && s.StartDate <= to && s.StartDate >= earliest)
            .ToListAsync(ct);
        return list.Where(s => s.EndDate >= from)
            .OrderByDescending(s => s.Principal)
            .ThenBy(s => Array.IndexOf(Overlap, s.Status))
            .ThenByDescending(s => s.RequestedAt)
            .ToList();
    }

    /// <summary>Há outra escala do setor para o período (viva ou em geração)?</summary>
    public static async Task<bool> HasOverlapAsync(SchedulingDbContext db, Guid storeId, Guid sectorId, DateOnly from, DateOnly to,
        Guid? excludeId, CancellationToken ct)
    {
        var earliest = from.AddDays(-7 * MaxWeeks);
        var periods = await db.Schedules.AsNoTracking()
            .Where(s => s.StoreId == storeId && s.SectorId == sectorId && s.Id != excludeId && (s.Status == ScheduleStatus.Published || s.Status == ScheduleStatus.Approved || s.Status == ScheduleStatus.Generated
                             || s.Status == ScheduleStatus.Draft || s.Status == ScheduleStatus.Generating)
                        && s.StartDate <= to && s.StartDate >= earliest)
            .Select(s => new { s.StartDate, s.EndDate })
            .ToListAsync(ct);
        return periods.Any(p => p.EndDate >= from);
    }

    /// <summary>
    /// Torna <paramref name="s"/> a principal e desmarca as outras principais do mesmo setor que se sobrepõem ao período
    /// (rastreadas no contexto). Devolve as desmarcadas. Não grava.
    /// </summary>
    public static async Task<List<Guid>> MarkAsync(SchedulingDbContext db, Schedule s, string by, CancellationToken ct)
    {
        if (!s.CanBePrincipal)
            throw new InvalidOperationException($"A escala {s.Id} não pode ser a principal (status {s.Status}).");
        var end = s.EndDate;
        var earliest = s.StartDate.AddDays(-7 * MaxWeeks);
        var principals = await db.Schedules
            .Where(o => o.StoreId == s.StoreId && o.SectorId == s.SectorId && o.Principal && o.Id != s.Id
                        && o.StartDate <= end && o.StartDate >= earliest)
            .ToListAsync(ct);
        var unmark = PrincipalSchedule.ToUnmark(Candidate(s), principals.Select(Candidate)).ToHashSet();
        var now = DateTime.UtcNow;
        foreach (var o in principals.Where(o => unmark.Contains(o.Id)))
        {
            o.Principal = false;
            o.PrincipalDesde = null;
            o.PrincipalPor = null;
            o.UpdatedAt = now;
        }
        s.Principal = true;
        s.PrincipalDesde = now;
        s.PrincipalPor = by;
        s.PrincipalPedidoPor = null;
        s.UpdatedAt = now;
        return [.. unmark];
    }

    public static SchedulePrincipalChanged Changed(Schedule s, string motivo, IReadOnlyList<Guid> unmarked, string by) =>
        new(s.Id, s.StoreId, s.SectorId, s.SectorName ?? Schedule.AllSectors, s.StartDate, s.EndDate, s.Version, s.Status.ToString(),
            motivo, unmarked, by, DateTime.UtcNow);
}

/// <summary>Avalia a escala com as regras congeladas na fotografia e grava o resultado nela.</summary>
public sealed class ScheduleEvaluation(ComplianceEvaluator evaluator)
{
    public ComplianceResult Evaluate(Schedule s, PlanningSnapshot? snapshot = null)
    {
        snapshot ??= ScheduleCells.ReadSnapshot(s);
        var ctx = PlanningContext.Create(snapshot, s.StartDate, s.EndDate);
        return evaluator.Evaluate(ctx, ScheduleCells.WorkCells(s.Assignments));
    }

    /// <summary>Avalia e grava ConformidadePercentual/ConformidadeJson/AvaliadaEm (a escala precisa estar rastreada).</summary>
    public ComplianceResult EvaluateAndStore(Schedule s, PlanningSnapshot? snapshot = null)
    {
        var result = Evaluate(s, snapshot);
        s.ConformidadePercentual = result.Percentual;
        s.ConformidadeJson = JsonSerializer.Serialize(result, Json.Options);
        s.AvaliadaEm = result.AvaliadaEm;
        return result;
    }

    public static ComplianceResult? Stored(Schedule s) =>
        s.ConformidadeJson is null ? null : JsonSerializer.Deserialize<ComplianceResult>(s.ConformidadeJson, Json.Options);
}

/// <summary>
/// Ausências ATUAIS do Employee.Api (afastamento médico, férias...) aplicadas às escalas: um afastamento lançado
/// depois da geração aparece na grade e, nas escalas editáveis (Rascunho/Gerada...), entra na fotografia para o
/// avaliador (verificação "indisponibilidade"), a geração e as sugestões. Aprovadas/publicadas não mudam (a tela
/// mostra o alerta "escalado em dia de afastamento").
/// </summary>
public sealed class AbsenceSync(PlanningClients clients, SnapshotBuilder builder, ILogger<AbsenceSync> logger)
{
    /// <summary>Ausências atuais no horizonte da escala (só das pessoas da fotografia) e se a fotografia foi atualizada.</summary>
    public sealed record Result(IReadOnlyList<AbsenceEntry> Absences, bool SnapshotChanged, bool FromSnapshot);

    /// <summary>
    /// Lê as ausências atuais; na escala editável troca as da fotografia por elas e atualiza também a CONTINUIDADE (escala
    /// anterior e seguinte atuais — <see cref="SnapshotBuilder.ContinuityOfAsync"/>): o avaliador, as sugestões e a edição
    /// de células de uma escala do meio usam as vizinhas como estão agora, não como estavam quando ela foi gerada
    /// (SnapshotJson e CellsRevision — sugestões calculadas antes ficam desatualizadas). Employee.Api fora do ar: usa as
    /// ausências da fotografia, sem erro. Quem chama reavalia/grava quando <see cref="Result.SnapshotChanged"/>.
    /// </summary>
    public async Task<Result> SyncAsync(Schedule s, CancellationToken ct)
    {
        var snapshot = ScheduleCells.ReadSnapshot(s);
        IReadOnlyList<AbsenceEntry>? current = null;
        try
        {
            current = await clients.GetAbsencesAsync(s.StoreId, s.StartDate, s.EndDate, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Não foi possível ler as ausências atuais no Employee.Api para a escala {ScheduleId} ({Motivo}); usando as da fotografia",
                s.Id, $"{ex.GetType().Name}: {ex.Message}");
        }

        var people = snapshot.Employees.Select(e => e.EmployeeId).ToHashSet();
        var horizon = current?.Where(a => people.Contains(a.EmployeeId) && a.Date >= s.StartDate && a.Date <= s.EndDate)
            .OrderBy(a => a.Date).ToList() ?? FromSnapshot(snapshot, s.StartDate, s.EndDate);
        if (!s.IsEditable) return new Result(horizon, false, current is null);

        var changed = false;
        if (current is not null)
        {
            var byEmployee = horizon.ToLookup(a => a.EmployeeId);
            var employees = snapshot.Employees.Select(e =>
            {
                var now = byEmployee[e.EmployeeId].Select(a => new UnavailabilitySnapshot(a.Date, a.Tipo)).ToList();
                if (now.SequenceEqual(Absences(e, s.StartDate, s.EndDate))) return e;
                changed = true;
                return e with { UnavailableDates = now.Select(a => a.Date).ToList(), Absences = now };
            }).ToList();
            if (changed) snapshot = snapshot with { Employees = employees };
        }
        if (s.SectorId is not null)
        {
            // Escala anterior/seguinte mudou (regerada, reaberta, outra principal...): a emenda passa a valer como está agora
            var continuity = await builder.ContinuityOfAsync(s, snapshot, ct);
            if (!continuity.SameAs(snapshot))
            {
                snapshot = continuity.ApplyTo(snapshot);
                changed = true;
            }
        }
        if (changed)
        {
            s.SnapshotJson = JsonSerializer.Serialize(snapshot, Json.Options);
            s.CellsRevision++;
            s.UpdatedAt = DateTime.UtcNow;
        }
        return new Result(horizon, changed, current is null);
    }

    /// <summary>Ausências da fotografia no período (fotografias antigas, sem tipo: Indisponivel).</summary>
    public static List<UnavailabilitySnapshot> Absences(EmployeeSnapshot e, DateOnly from, DateOnly to)
    {
        var typed = (e.Absences ?? []).Where(a => a.Date >= from && a.Date <= to)
            .GroupBy(a => a.Date).ToDictionary(g => g.Key, g => g.Select(a => a.Tipo).OrderBy(UnavailabilityKinds.Rank).First());
        foreach (var d in e.UnavailableDates.Where(d => d >= from && d <= to)) typed.TryAdd(d, UnavailabilityKinds.Indisponivel);
        return typed.OrderBy(kv => kv.Key).Select(kv => new UnavailabilitySnapshot(kv.Key, kv.Value)).ToList();
    }

    private static List<AbsenceEntry> FromSnapshot(PlanningSnapshot snapshot, DateOnly from, DateOnly to) =>
        snapshot.Employees.SelectMany(e => Absences(e, from, to).Select(a => new AbsenceEntry(e.EmployeeId, a.Date, a.Tipo)))
            .OrderBy(a => a.Date).ToList();
}

/// <summary>Monta a fotografia de um setor a partir das APIs internas e das escalas deste serviço.</summary>
public sealed class SnapshotBuilder(SchedulingDbContext db, PlanningClients clients)
{
    /// <summary>Mínimo de dias de histórico/escala seguinte (a janela real vem das regras: <see cref="PlanningContext.ContinuityDaysFor"/>).</summary>
    private const int HistoryDays = PlanningContext.FollowingDays;

    /// <summary>Máximo de dias de borda de cada lado (semana civil parcial na ponta do mês: segunda a sábado ou terça a domingo).</summary>
    private const int EdgeDays = 6;

    /// <summary>Escalas vivas (não substituídas) cujas células ocupam a pessoa em outro setor.</summary>
    private static readonly ScheduleStatus[] Live = [ScheduleStatus.Published, ScheduleStatus.Approved, ScheduleStatus.Generated, ScheduleStatus.Draft];

    public sealed record Built(PlanningSnapshot Snapshot, string StoreName, string SectorName);

    public async Task<(Built? Result, string? Field, string? Error, bool NotFound)> BuildAsync(
        Guid storeId, Guid sectorId, DateOnly from, DateOnly to, Guid? excludeScheduleId, CancellationToken ct)
    {
        // Feriados e regras das semanas civis inteiras do período (os dias de borda do mês vizinho entram nas contas da semana)
        var (horizonFrom, horizonWeeks) = PlanningContext.Horizon(from, to);
        var horizonTo = horizonFrom.AddDays(horizonWeeks * 7 - 1);
        var store = await clients.GetStorePlanningAsync(storeId, horizonFrom, horizonTo, ct);
        if (store is null) return (null, null, null, true);
        var sector = store.Sectors.FirstOrDefault(s => s.SectorId == sectorId);
        if (sector is null) return (null, "sectorId", "O setor não pertence a esta loja.", false);

        // Férias da LG do setor atualizadas antes de ler os colaboradores (tempo curto; LG fora: aviso e segue com as gravadas)
        var vacationNotice = await clients.RefreshLgVacationsAsync(storeId, sectorId, null, ct);
        var employees = (await clients.GetEmployeesAsync(storeId, from, to, ct)).Where(e => e.SectorIds.Contains(sectorId)).ToList();
        // Férias/afastamento na LG sem período definido: o Employee.Api já os deixou fora; aqui só o aviso da geração
        var pending = (await clients.GetPendingAsync(storeId, ct)).Where(p => p.SectorIds.Contains(sectorId)).ToList();
        var rules = await clients.GetRulesAsync(horizonFrom, horizonTo, ct);
        if (rules.Count == 0) return (null, "rules", "Não há regra de conformidade cadastrada para o período.", false);
        if (employees.Count == 0)
            return (null, "employees", pending.Count == 0
                ? $"O setor {sector.Name} não tem colaboradores com contrato vigente."
                : $"O setor {sector.Name} não tem colaboradores disponíveis. {PendingNotice(pending)}", false);
        // Operação do setor (com turnos automáticos o catálogo de turnos é opcional: só para edição manual)
        var operation = store.Operacoes?.FirstOrDefault(o => o.SectorId == sectorId)?.Operacao;
        if (store.Shifts.Count == 0 && operation is not { TurnosAutomaticos: true })
            return (null, "shifts", "A loja não tem turnos ativos: configure a operação da loja (turnos automáticos pelo horário de funcionamento) " +
                                    "em Setores, turnos e demanda.", false);

        // Continuidade (dias seguidos, interjornada, folga isolada, domingos, max_folgas_seguidas): a melhor escala viva por
        // setor e dia (qualquer setor da loja) antes e depois do período, na janela que as regras ativas exigem — sem as
        // outras versões do mesmo mês (inclusive as antigas por semanas que começam/terminam no mês vizinho)
        var employeeIds = employees.Select(e => e.EmployeeId).ToList();
        var names = employees.DistinctBy(e => e.EmployeeId).ToDictionary(e => e.EmployeeId, e => e.Name);
        var continuity = await ContinuityAsync(storeId, from, to, names, rules, excludeScheduleId, ct);

        var occupied = await OccupiedAsync(storeId, sectorId, from, to, employeeIds, excludeScheduleId, ct);
        // Pontuação de folga dupla (meses anteriores sem duas folgas seguidas): prioridade no motor
        var folgaDupla = await FolgaDuplaAsync(storeId, from, employeeIds, excludeScheduleId, ct, to);

        // Turnos da operação do setor: automáticos (expediente de cada dia + regras vigentes) ou, com "Personalizar turnos", os do
        // catálogo classificados (abertura/fechamento/flexível) + os gerados da operação — com os do horário especial dos
        // feriados do horizonte, como o motor: o turno que ele usar existe na fotografia
        var shifts = PlanningContext.OperationShifts(operation, sectorId, store.Shifts, rules, store.Holidays, from, to,
            store.HasExtendedDayAgreement, StandardBreak(rules, from));

        List<string> notices = pending.Count == 0
            ? continuity.Notices
            : [PendingNotice(pending), .. continuity.Notices];
        if (vacationNotice is not null) notices = [vacationNotice, .. notices];

        var snapshot = new PlanningSnapshot(store.StoreId, store.StoreName, store.HasExtendedDayAgreement,
            [sector], shifts, store.Demands.Where(d => d.SectorId == sectorId).ToList(), store.Holidays,
            employees, null, continuity.History, rules, occupied, operation, continuity.Following, notices,
            continuity.HistoryRest, continuity.FollowingRest, folgaDupla);
        return (new Built(snapshot, store.StoreName, sector.Name), null, null, false);
    }

    /// <summary>"N colaboradores com férias/afastamento sem período definido foram excluídos da escala: ...".</summary>
    public static string PendingNotice(IReadOnlyList<PendingPeriodEntry> pending) =>
        $"{pending.Count} colaborador{(pending.Count == 1 ? "" : "es")} com férias/afastamento sem período definido "
        + $"{(pending.Count == 1 ? "foi excluído" : "foram excluídos")} da escala: "
        + string.Join(", ", pending.Select(p => $"{p.Name} ({p.Registration}{(p.Desde is { } d ? $", desde {d:dd/MM/yyyy}" : "")})"))
        + ". Registre o período em Colaboradores e gere de novo.";

    /// <summary>Continuidade lida das escalas vizinhas: antes (histórico) e depois (seguinte) do período, com os avisos.</summary>
    public sealed record Continuity(List<HistoryEntry> History, List<RestDayEntry> HistoryRest, List<HistoryEntry> Following,
        List<RestDayEntry> FollowingRest, List<string> Notices)
    {
        /// <summary>A fotografia já tem exatamente esta continuidade (nada a atualizar). Avisos da geração não contam.</summary>
        public bool SameAs(PlanningSnapshot s) =>
            s.History.SequenceEqual(History) && (s.HistoryRest ?? []).SequenceEqual(HistoryRest)
            && (s.Following ?? []).SequenceEqual(Following) && (s.FollowingRest ?? []).SequenceEqual(FollowingRest)
            && s.HistoryNotices is not null && s.HistoryNotices.Where(n => !GenerationNotice(n)).SequenceEqual(Notices);

        /// <summary>Troca a continuidade e os avisos dela; os avisos da geração (férias da LG, pendência de período) ficam.</summary>
        public PlanningSnapshot ApplyTo(PlanningSnapshot s) => s with
        {
            History = History, HistoryRest = HistoryRest, Following = Following, FollowingRest = FollowingRest,
            HistoryNotices = [.. (s.HistoryNotices ?? []).Where(GenerationNotice), .. Notices]
        };
    }

    /// <summary>
    /// Aviso da montagem da fotografia na geração (não da continuidade): férias da LG não atualizadas
    /// (<see cref="FeriasLgStatus.Aviso"/>) e colaboradores sem período definido (<see cref="PendingNotice"/>). Vale até a
    /// próxima geração.
    /// </summary>
    public static bool GenerationNotice(string notice) =>
        notice.StartsWith("Férias da LG", StringComparison.Ordinal) || notice.Contains("sem período definido", StringComparison.Ordinal);

    /// <summary>Avisos da fotografia sem os da geração anterior e com o aviso de férias desta geração (se houver).</summary>
    public static List<string> WithVacationNotice(IReadOnlyList<string>? notices, string? vacationNotice)
    {
        var list = (notices ?? []).Where(n => !GenerationNotice(n)).ToList();
        if (vacationNotice is not null) list.Insert(0, vacationNotice);
        return list;
    }

    /// <summary>
    /// Continuidade ATUAL do período [<paramref name="from"/>, <paramref name="to"/>] (sem a própria escala): histórico e
    /// escala seguinte na janela que as regras ativas exigem (<see cref="PlanningContext.ContinuityDaysFor"/> — ex.: domingo a
    /// cada 5 semanas lê 35 dias), as folgas registradas junto das emendas (max_folgas_seguidas) e os avisos das duas emendas.
    /// Usada ao montar a fotografia, ao gerar de novo na própria escala e ao abrir/editar uma escala editável (a escala
    /// anterior/seguinte pode ter mudado depois desta: o motor e o avaliador nunca usam a fotografia velha).
    /// </summary>
    public async Task<Continuity> ContinuityAsync(Guid storeId, DateOnly from, DateOnly to, IReadOnlyDictionary<Guid, string> employees,
        IReadOnlyList<ComplianceRuleSnapshot> rules, Guid? excludeScheduleId, CancellationToken ct)
    {
        // + dias de borda: a escala do mês começa/termina no meio da semana e o motor conta a semana civil inteira (até 6
        // dias do mês vizinho) — a janela das regras continua valendo a partir das pontas do horizonte
        var days = PlanningContext.ContinuityDaysFor(rules) + EdgeDays;
        var restWindow = PlanningContext.RestWindowFor(rules) + EdgeDays;
        var previous = await HistoryAsync(storeId, from, employees, excludeScheduleId, ct, days, restWindow, to);
        var following = await FollowingAsync(storeId, to, employees.Keys.ToList(), excludeScheduleId, ct, days, restWindow, from);
        return new Continuity(previous.History, previous.Rest ?? [], following.Worked, following.RestDays,
            [.. previous.Notices, .. following.Notices]);
    }

    /// <summary>
    /// Continuidade com a semana ANTERIOR: dias trabalhados (qualquer setor da loja) nos <paramref name="days"/> dias antes do
    /// período (padrão 21; a janela das regras ativas), da
    /// melhor escala viva por setor e dia — a PRINCIPAL do setor; sem principal no dia, Publicada &gt; Aprovada &gt;
    /// Gerada/Rascunho (a mais recente) e, por último, a que está sendo gerada de novo na própria escala (quadro de antes)
    /// —, sem a própria escala (<paramref name="excludeScheduleId"/>), sem as outras versões do mesmo mês (<paramref name="to"/>)
    /// nem as substituídas, inviáveis, com falha, sem solução ou canceladas. Devolve também os avisos (semana anterior sem escala; histórico
    /// vindo da escala principal ou de escala ainda não publicada) e as folgas de descanso dos 7 dias antes do período
    /// (max_folgas_seguidas na emenda; ausências e feriados sem autorização das fotografias não são folga).
    /// </summary>
    /// <param name="days">Dias de histórico (padrão 21; a janela das regras ativas: <see cref="PlanningContext.ContinuityDaysFor"/>).</param>
    /// <param name="restWindow">Dias antes do período com as folgas registradas (max_folgas_seguidas).</param>
    /// <param name="to">
    /// Último dia do período: as outras versões do mesmo mês (inclusive as antigas por semanas que começam no mês anterior)
    /// não são escala anterior (<see cref="ContinuityHistory.IsNeighbour"/>).
    /// </param>
    public async Task<ContinuityResult> HistoryAsync(Guid storeId, DateOnly from, IReadOnlyDictionary<Guid, string> employees,
        Guid? excludeScheduleId, CancellationToken ct, int days = HistoryDays, int restWindow = ContinuityHistory.RestDaysWindow,
        DateOnly? to = null)
    {
        days = Math.Max(days, HistoryDays);
        restWindow = Math.Clamp(restWindow, 1, days);
        var historyFrom = from.AddDays(-days);
        var earliest = historyFrom.AddDays(-7 * 52);
        var candidates = (await db.Schedules.AsNoTracking()
                .Where(s => s.StoreId == storeId && s.Id != excludeScheduleId
                            && (s.Status == ScheduleStatus.Published || s.Status == ScheduleStatus.Approved
                                || s.Status == ScheduleStatus.Generated || s.Status == ScheduleStatus.Draft
                                || s.Status == ScheduleStatus.Generating)
                            && s.StartDate < from && s.StartDate >= earliest)
                .Select(s => new { s.Id, s.SectorId, s.SectorName, s.Version, s.StartDate, s.EndDate, s.Status, s.RequestedAt, s.Principal })
                .ToListAsync(ct))
            .Where(s => s.EndDate >= historyFrom && ContinuityHistory.IsNeighbour(s.StartDate, s.EndDate, from, to ?? from))
            .Select(s => new ContinuitySchedule(s.Id, s.SectorId, s.SectorName ?? Schedule.AllSectors, s.Version, s.StartDate,
                s.EndDate, StatusRank(s.Status), s.RequestedAt, StatusLabel(s.Status),
                s.Status == ScheduleStatus.Published, s.Principal && s.SectorId != null, s.Status != ScheduleStatus.Draft))
            .ToList();
        var ids = candidates.Select(c => c.Id).ToList();
        var employeeIds = employees.Keys.ToList();
        var cells = ids.Count == 0
            ? []
            : await db.Assignments.AsNoTracking()
                .Where(a => ids.Contains(a.ScheduleId) && a.Date >= historyFrom && a.Date < from && employeeIds.Contains(a.EmployeeId))
                .Select(a => new ContinuityCell(a.ScheduleId, a.EmployeeId, a.Date, a.Start, a.End, a.Folga))
                .ToListAsync(ct);
        var notRest = await NotRestAsync(candidates, from.AddDays(-restWindow), from.AddDays(-1), ct);
        return ContinuityHistory.Previous(candidates, cells, from, days, employees, notRest, restWindow);
    }

    /// <summary>
    /// max_folgas_seguidas na emenda: dias sem trabalho que NÃO são folga de descanso nas escalas que cobrem
    /// [<paramref name="first"/>, <paramref name="last"/>] — ausência (férias, afastamento, atestado, indisponibilidade) ou
    /// feriado sem autorização registrados na fotografia de cada escala (as ausências da fotografia são atualizadas pelo
    /// Employee.Api). Fotografia ilegível: nenhum dia dela é folga conhecida (não restringe).
    /// </summary>
    private async Task<NotRestDay> NotRestAsync(IEnumerable<ContinuitySchedule> schedules, DateOnly first, DateOnly last, CancellationToken ct)
    {
        var ids = schedules.Where(s => s.Start <= last && s.End >= first).Select(s => s.Id).ToList();
        var byId = new Dictionary<Guid, (HashSet<(Guid, DateOnly)> Absent, HashSet<DateOnly> Closed)>();
        if (ids.Count > 0)
        {
            var rows = await db.Schedules.AsNoTracking().Where(s => ids.Contains(s.Id)).Select(s => new { s.Id, s.SnapshotJson }).ToListAsync(ct);
            foreach (var row in rows)
            {
                PlanningSnapshot? snap;
                try { snap = JsonSerializer.Deserialize<PlanningSnapshot>(row.SnapshotJson, Json.Options); }
                catch (JsonException) { snap = null; }
                if (snap is null) continue;
                var absent = snap.Employees
                    .SelectMany(e => e.UnavailableDates.Concat(e.Absences?.Select(a => a.Date) ?? []).Select(d => (e.EmployeeId, d)))
                    .Where(x => x.d >= first && x.d <= last).ToHashSet();
                var closed = snap.Holidays.Where(h => !h.WorkAuthorized && h.Date >= first && h.Date <= last).Select(h => h.Date).ToHashSet();
                byId[row.Id] = (absent, closed);
            }
        }
        return (scheduleId, employeeId, date) => !byId.TryGetValue(scheduleId, out var m) || m.Absent.Contains((employeeId, date)) || m.Closed.Contains(date);
    }

    /// <summary>
    /// Prioridade do status na continuidade (sem principal): 0 = Publicada, 1 = Aprovada, 2 = Gerada/Rascunho, 3 = em
    /// geração NA PRÓPRIA escala (vale o quadro de antes, só quando nenhuma outra cobre o dia — ex.: regerar várias escalas
    /// seguidas depois de um afastamento; escala nova em geração não tem células e não entra).
    /// </summary>
    private static int StatusRank(ScheduleStatus status) => status switch
    {
        ScheduleStatus.Published => 0,
        ScheduleStatus.Approved => 1,
        ScheduleStatus.Generating => 3,
        _ => 2
    };

    private static string StatusLabel(ScheduleStatus status) => status switch
    {
        ScheduleStatus.Published => "Publicada",
        ScheduleStatus.Approved => "Aprovada",
        ScheduleStatus.Generated => "Gerada",
        ScheduleStatus.Draft => "Rascunho",
        ScheduleStatus.Generating => "em geração agora; vale o quadro de antes",
        _ => status.ToString()
    };

    /// <summary>intervalo_padrao_minutos ativo no início do período (60 se ausente/bloqueada): intervalo dos turnos gerados.</summary>
    public static int StandardBreak(IReadOnlyList<ComplianceRuleSnapshot> rules, DateOnly day) =>
        (int)(rules.Where(r => r.Chave == RuleKeys.IntervaloPadrao && r.Status == RuleStatus.Ativo && r.Contem(day))
            .OrderByDescending(r => r.VigenciaInicio ?? DateOnly.MinValue).FirstOrDefault()?.Valor ?? 60);

    /// <summary>
    /// Continuidade com a semana SEGUINTE: dias trabalhados (qualquer setor da loja) nos <paramref name="days"/> dias depois
    /// do período (padrão 21; a janela das regras ativas), nas mesmas escalas da semana anterior e da visão do mês — vivas
    /// (Publicada, Aprovada, Gerada, Rascunho), sem a própria escala. Por setor e dia vale a PRINCIPAL; sem ela, a de melhor
    /// status (Publicada &gt; Aprovada &gt; Gerada/Rascunho) e a mais recente (<see cref="ContinuityHistory.FollowingWithNotices"/>).
    /// Antes só Publicadas/Aprovadas/principais entravam: uma escala seguinte apenas Gerada (não principal) que a visão do mês
    /// mostra era ignorada na emenda.
    /// </summary>
    /// <returns>Dias trabalhados, as folgas de descanso registradas junto do fim (max_folgas_seguidas) e os avisos.</returns>
    /// <param name="from">
    /// Primeiro dia do período: as outras versões do mesmo mês (inclusive as antigas por semanas que terminam no mês
    /// seguinte) não são escala seguinte (<see cref="ContinuityHistory.IsNeighbour"/>).
    /// </param>
    public async Task<(List<HistoryEntry> Worked, List<RestDayEntry> RestDays, List<string> Notices)> FollowingAsync(Guid storeId, DateOnly to,
        IReadOnlyCollection<Guid> employeeIds, Guid? excludeScheduleId, CancellationToken ct, int days = HistoryDays,
        int restWindow = ContinuityHistory.RestDaysWindow, DateOnly? from = null)
    {
        days = Math.Max(days, HistoryDays);
        restWindow = Math.Clamp(restWindow, 1, days);
        var until = to.AddDays(days);
        var schedules = (await db.Schedules.AsNoTracking()
                .Where(s => s.StoreId == storeId && s.Id != excludeScheduleId
                            && (s.Status == ScheduleStatus.Published || s.Status == ScheduleStatus.Approved
                                || s.Status == ScheduleStatus.Generated || s.Status == ScheduleStatus.Draft
                                || s.Status == ScheduleStatus.Generating)
                            && s.StartDate <= until && s.StartDate > to.AddDays(-7 * 52))
                .Select(s => new { s.Id, s.SectorId, s.SectorName, s.Version, s.StartDate, s.EndDate, s.Status, s.RequestedAt, s.Principal })
                .ToListAsync(ct))
            .Where(s => s.EndDate > to && ContinuityHistory.IsNeighbour(s.StartDate, s.EndDate, from ?? to, to))
            .Select(s => new ContinuitySchedule(s.Id, s.SectorId, s.SectorName ?? Schedule.AllSectors, s.Version, s.StartDate,
                s.EndDate, StatusRank(s.Status), s.RequestedAt, StatusLabel(s.Status),
                s.Status == ScheduleStatus.Published, s.Principal && s.SectorId != null, s.Status != ScheduleStatus.Draft))
            .ToList();
        if (schedules.Count == 0) return ([], [], []);
        var ids = schedules.Select(s => s.Id).ToList();
        var cells = await db.Assignments.AsNoTracking()
            .Where(a => ids.Contains(a.ScheduleId) && a.Date > to && a.Date <= until && employeeIds.Contains(a.EmployeeId))
            .Select(a => new ContinuityCell(a.ScheduleId, a.EmployeeId, a.Date, a.Start, a.End, a.Folga))
            .ToListAsync(ct);
        var following = ContinuityHistory.FollowingWithNotices(schedules, cells, to, days, employeeIds);
        var (first, last) = (to.AddDays(1), to.AddDays(restWindow));
        var rest = ContinuityHistory.RestDays(schedules, cells, first, last, employeeIds, await NotRestAsync(schedules, first, last, ct));
        return (following.History, rest, following.Notices);
    }

    /// <summary>
    /// Gerar de novo na própria escala: atualiza na fotografia a operação, os turnos da operação (mantendo os antigos,
    /// usados por células), o perfil/disponibilidade/preferências dos colaboradores da fotografia, a escala seguinte e o
    /// histórico da semana anterior (com os avisos das duas emendas, na janela das regras ativas).
    /// Store/Employee fora do ar: mantém o que a fotografia tem.
    /// </summary>
    public async Task<PlanningSnapshot> RefreshPlanningAsync(Schedule s, PlanningSnapshot snapshot, CancellationToken ct, ILogger logger)
    {
        var sectorId = s.SectorId!.Value;
        try
        {
            var store = await clients.GetStorePlanningAsync(s.StoreId, s.StartDate, s.EndDate, ct);
            if (store is not null)
            {
                var operation = store.Operacoes?.FirstOrDefault(o => o.SectorId == sectorId)?.Operacao;
                var current = store.Shifts.Concat(snapshot.Shifts.Where(old => store.Shifts.All(n => n.ShiftId != old.ShiftId))).ToList();
                // Turnos automáticos (horário da loja + regras da fotografia) ou personalizados (catálogo + gerados da operação)
                var shifts = PlanningContext.OperationShifts(operation, sectorId, current, snapshot.ComplianceRules ?? [], snapshot.Holidays,
                    s.StartDate, s.EndDate, snapshot.StoreHasExtendedDayAgreement, StandardBreak(snapshot.ComplianceRules ?? [], s.StartDate));
                snapshot = snapshot with
                {
                    Operacao = operation,
                    Shifts = shifts,
                    Demands = store.Demands.Where(d => d.SectorId == sectorId).ToList()
                };
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Operação/turnos atuais indisponíveis no Store.Api para a escala {ScheduleId} ({Motivo}); usando os da fotografia",
                s.Id, $"{ex.GetType().Name}: {ex.Message}");
        }
        try
        {
            var people = (await clients.GetEmployeesAsync(s.StoreId, s.StartDate, s.EndDate, ct)).ToDictionary(e => e.EmployeeId);
            snapshot = snapshot with
            {
                Employees = snapshot.Employees.Select(e => people.TryGetValue(e.EmployeeId, out var now) ? e with { Jornada = now.Jornada } : e).ToList()
            };
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Perfil de jornada atual indisponível no Employee.Api para a escala {ScheduleId} ({Motivo}); usando o da fotografia",
                s.Id, $"{ex.GetType().Name}: {ex.Message}");
        }
        // Pontuação de folga dupla com os meses anteriores como estão agora (o mês anterior pode ter sido aprovado depois)
        snapshot = snapshot with
        {
            FolgaDupla = await FolgaDuplaAsync(s.StoreId, s.StartDate, snapshot.Employees.Select(e => e.EmployeeId).Distinct().ToList(), s.Id, ct, s.EndDate)
        };
        // Semana anterior e seguinte atuais (as vizinhas podem ter sido geradas/aprovadas/publicadas ou marcadas como
        // principal depois desta), na janela das regras da fotografia (já atualizadas por quem chama)
        return (await ContinuityOfAsync(s, snapshot, ct)).ApplyTo(snapshot);
    }

    /// <summary>
    /// Pontuação de folga dupla (<see cref="FolgaDuplaHistory.Score"/>) dos colaboradores para a escala que começa em
    /// <paramref name="from"/>: os <see cref="FolgaDuplaHistory.MesesHistorico"/> meses civis anteriores, nas escalas da
    /// loja (qualquer setor: a folga é da pessoa) que valem como histórico confirmado — a PRINCIPAL do setor ou, sem ela, a
    /// Publicada/Aprovada (Gerada/Rascunho que não é principal não entra: ainda pode mudar). Ausências e feriados sem
    /// autorização das fotografias não são folga. Sem a própria escala.
    /// </summary>
    /// <param name="to">Último dia do período: as outras versões do mesmo mês não são histórico (<see cref="ContinuityHistory.IsNeighbour"/>).</param>
    public async Task<List<FolgaDuplaScore>> FolgaDuplaAsync(Guid storeId, DateOnly from, IReadOnlyCollection<Guid> employeeIds,
        Guid? excludeScheduleId, CancellationToken ct, DateOnly? to = null)
    {
        var first = FolgaDuplaHistory.MonthBefore(from, FolgaDuplaHistory.MesesHistorico).From;
        var last = FolgaDuplaHistory.MonthBefore(from, 1).To;
        var candidates = (await db.Schedules.AsNoTracking()
                .Where(s => s.StoreId == storeId && s.Id != excludeScheduleId
                            && (s.Status == ScheduleStatus.Published || s.Status == ScheduleStatus.Approved
                                || (s.Principal && s.SectorId != null && (s.Status == ScheduleStatus.Generated || s.Status == ScheduleStatus.Draft)))
                            && s.StartDate <= last && s.EndDate >= first)
                .Select(s => new { s.Id, s.SectorId, s.SectorName, s.Version, s.StartDate, s.EndDate, s.Status, s.RequestedAt, s.Principal })
                .ToListAsync(ct))
            .Where(s => ContinuityHistory.IsNeighbour(s.StartDate, s.EndDate, from, to ?? from))
            .Select(s => new ContinuitySchedule(s.Id, s.SectorId, s.SectorName ?? Schedule.AllSectors, s.Version, s.StartDate,
                s.EndDate, StatusRank(s.Status), s.RequestedAt, StatusLabel(s.Status),
                s.Status == ScheduleStatus.Published, s.Principal && s.SectorId != null, s.Status != ScheduleStatus.Draft))
            .ToList();
        if (candidates.Count == 0 || employeeIds.Count == 0) return FolgaDuplaHistory.Score([], [], from, employeeIds);
        var ids = candidates.Select(c => c.Id).ToList();
        var cells = await db.Assignments.AsNoTracking()
            .Where(a => ids.Contains(a.ScheduleId) && a.Date >= first && a.Date <= last && employeeIds.Contains(a.EmployeeId))
            .Select(a => new ContinuityCell(a.ScheduleId, a.EmployeeId, a.Date, a.Start, a.End, a.Folga))
            .ToListAsync(ct);
        // Só as escalas que têm células dessas pessoas (as fotografias das outras não precisam ser lidas)
        var used = cells.Select(c => c.ScheduleId).ToHashSet();
        var schedules = candidates.Where(c => used.Contains(c.Id)).ToList();
        var notRest = await NotRestAsync(schedules, first, last, ct);
        return FolgaDuplaHistory.Score(schedules, cells, from, employeeIds, notRest);
    }

    /// <summary>Continuidade atual da escala <paramref name="s"/> (sem ela mesma), com as regras e pessoas da fotografia.</summary>
    public Task<Continuity> ContinuityOfAsync(Schedule s, PlanningSnapshot snapshot, CancellationToken ct) =>
        ContinuityAsync(s.StoreId, s.StartDate, s.EndDate, snapshot.Employees.DistinctBy(e => e.EmployeeId).ToDictionary(e => e.EmployeeId, e => e.Name),
            LegacyRules.Of(snapshot), s.Id, ct);

    /// <summary>
    /// Dias em que as pessoas já estão escaladas em escalas vivas de OUTROS setores da loja no período.
    /// Por setor e dia vale uma escala só: a PRINCIPAL do setor (é a que vale para a continuidade e a visão do mês);
    /// sem principal no dia, Publicada &gt; Aprovada &gt; Gerada &gt; Rascunho, e a mais recente.
    /// Escalas antigas por loja (sem setor) não entram.
    /// </summary>
    public async Task<List<OccupiedEntry>> OccupiedAsync(Guid storeId, Guid sectorId, DateOnly from, DateOnly to,
        IReadOnlyCollection<Guid> employeeIds, Guid? excludeScheduleId, CancellationToken ct)
    {
        var earliest = from.AddDays(-7 * 52);
        var candidates = (await db.Schedules.AsNoTracking()
                .Where(s => s.StoreId == storeId && s.SectorId != null && s.SectorId != sectorId
                            && (s.Status == ScheduleStatus.Published || s.Status == ScheduleStatus.Approved
                                || s.Status == ScheduleStatus.Generated || s.Status == ScheduleStatus.Draft)
                            && s.StartDate <= to && s.StartDate >= earliest && s.Id != excludeScheduleId)
                .Select(s => new { s.Id, s.SectorId, s.SectorName, s.StartDate, s.EndDate, s.Status, s.RequestedAt, s.Principal })
                .ToListAsync(ct))
            .Where(s => s.EndDate >= from)
            .ToList();
        if (candidates.Count == 0) return [];

        int Rank(ScheduleStatus st) => Array.IndexOf(Live, st);
        var ids = candidates.Select(c => c.Id).ToList();
        var cells = await db.Assignments.AsNoTracking()
            .Where(a => ids.Contains(a.ScheduleId) && a.Date >= from && a.Date <= to && !a.Folga && a.ShiftId != null && employeeIds.Contains(a.EmployeeId))
            .ToListAsync(ct);

        var result = new List<OccupiedEntry>();
        foreach (var bySector in candidates.GroupBy(c => c.SectorId))
        {
            for (var date = from; date <= to; date = date.AddDays(1))
            {
                var day = date;
                var chosen = bySector.Where(c => c.StartDate <= day && c.EndDate >= day)
                    .OrderByDescending(c => c.Principal).ThenBy(c => Rank(c.Status)).ThenByDescending(c => c.RequestedAt).FirstOrDefault();
                if (chosen is null) continue;
                result.AddRange(cells.Where(a => a.ScheduleId == chosen.Id && a.Date == day)
                    .Select(a => new OccupiedEntry(a.EmployeeId, a.Date, a.Start!.Value, a.End!.Value, a.PaidMinutes,
                        a.SectorId, chosen.SectorName ?? a.SectorName, chosen.Id)));
            }
        }
        return result;
    }
}

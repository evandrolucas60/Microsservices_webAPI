using System.Text.Json;
using Escala5x2.BuildingBlocks.Contracts.Scheduling;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Scheduling.Api.Clients;
using Scheduling.Api.Data;
using Scheduling.Api.Domain;
using Scheduling.Api.Endpoints;
using Scheduling.Api.Services;

namespace Scheduling.Api.Consumers;

/// <summary>
/// Recebe o resultado do Optimization Worker. Idempotente: ignora mensagens para escalas que já saíram de "Generating"
/// (inclusive as de geração cancelada) e de outra geração da mesma escala (GenerationId).
/// </summary>
public sealed class ScheduleResultConsumers(SchedulingDbContext db, ScheduleEvaluation evaluation, ILogger<ScheduleResultConsumers> logger) :
    IConsumer<ScheduleGenerated>, IConsumer<ScheduleInfeasible>, IConsumer<ScheduleGenerationFailed>, IConsumer<CorrectionSuggested>,
    IConsumer<ScheduleNoSolutionInTime>
{
    public async Task Consume(ConsumeContext<ScheduleGenerated> context)
    {
        var msg = context.Message;
        var schedule = await LoadGeneratingAsync(msg.ScheduleId, msg.GenerationId, context.CancellationToken, includeCells: true);
        if (schedule is null) return;

        var snapshot = ScheduleCells.ReadSnapshot(schedule);
        // Setores (ou só dias) em que o motor usou a cobertura como meta por quadro insuficiente: ficam na fotografia para o
        // avaliador tratar a faixa descoberta como aviso (como o motor), também nas edições seguintes
        var target = msg.CoberturaMetaSetores is { Count: > 0 } sectors ? sectors : null;
        var targetDays = msg.CoberturaMetaDias is { Count: > 0 } days ? days : null;
        if (!SameSectors(snapshot.CoberturaMetaSetores, target) || !(snapshot.CoberturaMetaDias ?? []).SequenceEqual(targetDays ?? []))
        {
            snapshot = snapshot with { CoberturaMetaSetores = target, CoberturaMetaDias = targetDays };
            schedule.SnapshotJson = JsonSerializer.Serialize(snapshot, Json.Options);
        }
        var shifts = snapshot.Shifts.ToDictionary(s => s.ShiftId);
        var keepManual = schedule.GenerationMode == GenerationModes.SomenteVazios;
        // Turno manual em dia de afastamento/férias não é mantido (o motor também o descartou das células fixas)
        var absent = snapshot.Employees
            .SelectMany(e => e.UnavailableDates.Concat(e.Absences?.Select(a => a.Date) ?? []).Select(d => (e.EmployeeId, d)))
            .ToHashSet();
        // Células com almoço ajustado à mão também ficam (o motor não mexe em almoço manual em "somente_vazios")
        var manual = keepManual
            ? schedule.Assignments.Where(a => (a.Origem == CellOrigin.Manual || ScheduleCells.HasManualBreak(a)) && (a.Folga || !absent.Contains((a.EmployeeId, a.Date))))
                .Select(a => (a.EmployeeId, a.Date)).ToHashSet()
            : [];

        // Célula do motor com turno que a fotografia não tem: nunca some em silêncio (viraria folga na grade) — log e aviso
        var unknown = msg.Assignments.Where(a => !manual.Contains((a.EmployeeId, a.Date)) && !shifts.ContainsKey(a.ShiftId)).ToList();
        string? unknownNotice = null;
        if (unknown.Count > 0)
        {
            var lost = unknown.Select(a => a.Date).Distinct().Order().ToList();
            unknownNotice = $"Atenção: {unknown.Count} célula(s) do motor com turno fora da fotografia da escala foram descartadas " +
                            $"({string.Join(", ", lost.Take(8).Select(d => d.ToString("dd/MM")))}{(lost.Count > 8 ? "..." : "")}): gere de novo.";
            logger.LogError("Escala {ScheduleId}: {Count} célula(s) do motor com turno fora da fotografia descartadas ({Shifts})", msg.ScheduleId,
                unknown.Count, string.Join(", ", unknown.Select(a => $"{a.Start:HH\\:mm}-{a.End:HH\\:mm}").Distinct()));
        }

        // Resultado do motor nas células não manuais (em "completa" o manual também é substituído)
        var result = msg.Assignments
            .Where(a => !manual.Contains((a.EmployeeId, a.Date)) && shifts.ContainsKey(a.ShiftId))
            .GroupBy(a => (a.EmployeeId, a.Date)).ToDictionary(g => g.Key, g => g.First());
        foreach (var cell in schedule.Assignments.Where(a => !manual.Contains((a.EmployeeId, a.Date)) && !result.ContainsKey((a.EmployeeId, a.Date))).ToList())
            ScheduleCells.Set(db, schedule, snapshot, cell.EmployeeId, cell.Date, null, CellOrigin.Motor);
        var fromEngine = new HashSet<(Guid, DateOnly)>();
        foreach (var ((employeeId, date), a) in result)
        {
            ScheduleCells.Set(db, schedule, snapshot, employeeId, date, new CellContent(shifts[a.ShiftId], false), CellOrigin.Motor);
            // Intervalo decidido pelo motor (dentro do modelo, com a cobertura descontando quem está em intervalo)
            if (a.BreakStart is not null && schedule.Assignments.FirstOrDefault(x => x.EmployeeId == employeeId && x.Date == date) is { } cell)
            {
                ScheduleCells.SetBreak(cell, snapshot, a.BreakStart, null, CellOrigin.Motor);
                fromEngine.Add((employeeId, date));
            }
        }

        // Almoços sem posição do motor (dia sem faixa de cobertura, ou Worker antigo): BreakPlanner. Na "completa" os
        // manuais também voltam ao motor (duração do turno), porque ela substitui tudo
        var placed = keepManual
            ? ScheduleCells.PlanBreaks(schedule, snapshot, a => a.BreakOrigem != CellOrigin.Manual && !fromEngine.Contains((a.EmployeeId, a.Date)))
            : ScheduleCells.PlanBreaks(schedule, snapshot, a => !fromEngine.Contains((a.EmployeeId, a.Date)), resetDuration: true);

        schedule.Status = ScheduleStatus.Generated;
        schedule.StatusReason = unknownNotice is null ? msg.Notice : string.Join(" ", new[] { unknownNotice, msg.Notice }.OfType<string>());
        schedule.DiagnosticoJson = null;
        schedule.SolucoesMotorJson = msg.SolucoesMotor is { } solutions ? JsonSerializer.Serialize(solutions, Json.Options) : null;
        schedule.DeficitsJson = msg.Deficits is { Count: > 0 } d ? JsonSerializer.Serialize(d, Json.Options) : null;
        schedule.SolverStatus = msg.Resultado ?? (msg.FlexibleMode ? $"{msg.SolverStatus} (modo flexível)" : msg.SolverStatus);
        schedule.ObjectiveValue = msg.ObjectiveValue;
        schedule.SolveSeconds = msg.SolveSeconds;
        schedule.CellsRevision++;
        schedule.UpdatedAt = DateTime.UtcNow;
        var conformidade = evaluation.EvaluateAndStore(schedule, snapshot);
        // Pedido de tornar principal (na geração): só agora, com a escala entregue, a anterior deixa de ser a principal
        if (schedule.PrincipalPedidoPor is { } by)
        {
            var unmarked = await PrincipalSchedules.MarkAsync(db, schedule, by, context.CancellationToken);
            await context.Publish(PrincipalSchedules.Changed(schedule, "geracao", unmarked, by), context.CancellationToken);
            logger.LogInformation("Escala {ScheduleId} agora é a principal do setor {Sector} ({Count} desmarcada(s))",
                schedule.Id, schedule.SectorName, unmarked.Count);
        }
        await db.SaveChangesAsync(context.CancellationToken);
        logger.LogInformation("Escala {ScheduleId} gerada ({Mode}) com {Count} alocações em {Seconds:F1}s, {Placed} almoço(s) posicionado(s), conformidade {Percent}%{Flex}, {Solutions} opção(ões) avaliadas pelo motor",
            schedule.Id, schedule.GenerationMode, msg.Assignments.Count, msg.SolveSeconds, placed, conformidade.Percentual, msg.FlexibleMode ? " (modo flexível)" : "",
            msg.SolucoesMotor?.Total);
    }

    /// <summary>
    /// Inviável (provado): NENHUMA escala é entregue. As células do motor de uma geração anterior saem (não sobra escala
    /// "aparentemente válida" com status inviável); as células manuais do usuário ficam como estavam.
    /// </summary>
    public async Task Consume(ConsumeContext<ScheduleInfeasible> context)
    {
        var schedule = await LoadGeneratingAsync(context.Message.ScheduleId, context.Message.GenerationId, context.CancellationToken, includeCells: true);
        if (schedule is null) return;
        var removed = ClearEngineCells(schedule);
        // Sem escala do motor: a cobertura como meta da geração anterior deixa de valer (as células que ficam são manuais)
        var current = ScheduleCells.ReadSnapshot(schedule);
        if (current.CoberturaMetaSetores is not null || current.CoberturaMetaDias is not null)
            schedule.SnapshotJson = JsonSerializer.Serialize(current with { CoberturaMetaSetores = null, CoberturaMetaDias = null }, Json.Options);
        schedule.Status = ScheduleStatus.Infeasible;
        schedule.StatusReason = context.Message.Reason;
        schedule.SolverStatus = context.Message.Diagnostico is null ? schedule.SolverStatus : EngineResults.Inviavel;
        schedule.DeficitsJson = JsonSerializer.Serialize(context.Message.Deficits, Json.Options);
        schedule.DiagnosticoJson = context.Message.Diagnostico is { } d ? JsonSerializer.Serialize(d, Json.Options) : null;
        schedule.PrincipalPedidoPor = null; // sem escala entregue: a principal anterior continua
        schedule.UpdatedAt = DateTime.UtcNow;
        evaluation.EvaluateAndStore(schedule);
        await db.SaveChangesAsync(context.CancellationToken);
        logger.LogWarning("Escala {ScheduleId} inviável ({Removed} célula(s) do motor removidas): {Reason}", schedule.Id, removed, context.Message.Reason);
    }

    /// <summary>Sem solução no tempo: não é inviável. Nenhuma escala é entregue; dá para tentar com mais tempo.</summary>
    public async Task Consume(ConsumeContext<ScheduleNoSolutionInTime> context)
    {
        var schedule = await LoadGeneratingAsync(context.Message.ScheduleId, context.Message.GenerationId, context.CancellationToken, includeCells: true);
        if (schedule is null) return;
        var removed = ClearEngineCells(schedule);
        schedule.Status = ScheduleStatus.TimedOut;
        schedule.StatusReason = context.Message.Reason;
        schedule.SolverStatus = EngineResults.SemSolucaoNoTempo;
        schedule.SolveSeconds = context.Message.SolveSeconds;
        schedule.DeficitsJson = null;
        schedule.DiagnosticoJson = null;
        schedule.PrincipalPedidoPor = null; // sem escala entregue: a principal anterior continua
        schedule.UpdatedAt = DateTime.UtcNow;
        evaluation.EvaluateAndStore(schedule);
        await db.SaveChangesAsync(context.CancellationToken);
        logger.LogWarning("Escala {ScheduleId} sem solução em {Seconds}s ({Removed} célula(s) do motor removidas)", schedule.Id,
            context.Message.TimeLimitSeconds, removed);
    }

    private static bool SameSectors(IReadOnlyList<Guid>? a, IReadOnlyList<Guid>? b) =>
        (a ?? []).Order().SequenceEqual((b ?? []).Order());

    /// <summary>Tira as células do motor (as manuais e as com almoço ajustado à mão ficam).</summary>
    private int ClearEngineCells(Schedule schedule)
    {
        var engine = schedule.Assignments.Where(a => a.Origem == CellOrigin.Motor && !ScheduleCells.HasManualBreak(a)).ToList();
        if (engine.Count == 0) return 0;
        var snapshot = ScheduleCells.ReadSnapshot(schedule);
        foreach (var cell in engine) ScheduleCells.Set(db, schedule, snapshot, cell.EmployeeId, cell.Date, null, CellOrigin.Motor);
        schedule.CellsRevision++;
        return engine.Count;
    }

    public async Task Consume(ConsumeContext<ScheduleGenerationFailed> context)
    {
        // Falha (erro do motor ou escala descartada pelo avaliador independente): nada inválido fica na escala
        var schedule = await LoadGeneratingAsync(context.Message.ScheduleId, context.Message.GenerationId, context.CancellationToken, includeCells: true);
        if (schedule is null) return;
        if (ClearEngineCells(schedule) > 0) evaluation.EvaluateAndStore(schedule);
        schedule.Status = ScheduleStatus.Failed;
        schedule.StatusReason = context.Message.Error;
        schedule.SolverStatus = EngineResults.Falha;
        schedule.DiagnosticoJson = null;
        schedule.PrincipalPedidoPor = null; // sem escala entregue: a principal anterior continua
        schedule.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(context.CancellationToken);
        logger.LogError("Falha ao gerar a escala {ScheduleId}: {Error}", schedule.Id, context.Message.Error);
    }

    public async Task Consume(ConsumeContext<CorrectionSuggested> context)
    {
        var msg = context.Message;
        var sug = await db.Suggestions.FirstOrDefaultAsync(x => x.Id == msg.SuggestionId, context.CancellationToken);
        if (sug is null || sug.Status != SuggestionStatus.Processando)
        {
            logger.LogInformation("Resultado de sugestão ignorado ({SuggestionId}: inexistente ou já processada)", msg.SuggestionId);
            return;
        }
        var schedule = await db.Schedules.AsNoTracking().FirstOrDefaultAsync(x => x.Id == msg.ScheduleId, context.CancellationToken);
        var snapshot = schedule is null ? null : ScheduleCells.ReadSnapshot(schedule);
        var shifts = snapshot?.Shifts.ToDictionary(s => s.ShiftId) ?? [];
        var names = snapshot?.Employees.ToDictionary(e => e.EmployeeId, e => e.Name) ?? [];

        SuggestionCell Cell(Guid? shiftId, bool folga) =>
            shiftId is { } id && shifts.TryGetValue(id, out var s) ? new SuggestionCell(id, s.Code, s.Start, s.End, false)
            : folga ? new SuggestionCell(null, ScheduleCells.FolgaCode, null, null, true)
            : new SuggestionCell(null, "—", null, null, false);

        var changes = msg.Changes.Select(c => new SuggestionChange(c.EmployeeId, names.GetValueOrDefault(c.EmployeeId) ?? "?", c.Date,
            Cell(c.FromShiftId, c.FromFolga), Cell(c.ToShiftId, c.ToFolga))).ToList();
        sug.Status = msg.Status;
        sug.PercentualAtual = msg.PercentualAtual;
        sug.PercentualPrevisto = msg.PercentualPrevisto;
        sug.AlteracoesJson = JsonSerializer.Serialize(changes, Json.Options);
        sug.Mensagem = msg.Message;
        sug.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(context.CancellationToken);
        logger.LogInformation("Sugestão {SuggestionId} da escala {ScheduleId}: {Status}, {Count} alteração(ões), {Before}% → {After}%",
            sug.Id, msg.ScheduleId, msg.Status, changes.Count, msg.PercentualAtual, msg.PercentualPrevisto);
    }

    /// <summary>
    /// Escala em geração à espera DESTE resultado. Ignora: escala inexistente, que já saiu de "em geração" (resultado
    /// repetido, ou geração cancelada — cancelar-geracao já a devolveu ao estado anterior) e resultado de OUTRA geração
    /// (cancelada e pedida de novo: o id não bate). Resultado sem id (Worker antigo) ou escala sem id (pedido antigo) vale.
    /// O estado guardado para o cancelamento deixa de valer (a geração terminou).
    /// </summary>
    private async Task<Schedule?> LoadGeneratingAsync(Guid id, Guid? generationId, CancellationToken ct, bool includeCells = false)
    {
        IQueryable<Schedule> query = includeCells ? db.Schedules.Include(s => s.Assignments) : db.Schedules;
        var schedule = await query.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (schedule is null || schedule.Status != ScheduleStatus.Generating)
        {
            logger.LogInformation("Resultado ignorado para a escala {ScheduleId} (inexistente, já processada ou geração cancelada)", id);
            return null;
        }
        if (generationId is { } g && schedule.GeracaoId is { } current && g != current)
        {
            logger.LogInformation("Resultado da geração {GenerationId} ignorado para a escala {ScheduleId}: a geração em andamento é {Current} (a anterior foi cancelada)",
                g, id, current);
            return null;
        }
        schedule.GeracaoAnteriorJson = null;
        return schedule;
    }
}

using Escala5x2.BuildingBlocks.Contracts.Employees;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Scheduling.Api.Data;
using Scheduling.Api.Domain;
using Scheduling.Api.Endpoints;
using Scheduling.Api.Services;

namespace Scheduling.Api.Consumers;

/// <summary>
/// Ausência (afastamento médico, férias...) registrada, alterada ou excluída: as escalas EDITÁVEIS da loja que cobrem o
/// período e têm a pessoa passam a usar as ausências atuais (fotografia + avaliação gravada), para o percentual da lista
/// já refletir o afastamento. Aprovadas/publicadas não mudam (a tela mostra o alerta). Idempotente: sem mudança, nada grava.
/// </summary>
public sealed class EmployeeAbsenceConsumer(SchedulingDbContext db, AbsenceSync sync, ScheduleEvaluation evaluation,
    ILogger<EmployeeAbsenceConsumer> logger) : IConsumer<EmployeeUnavailabilityChanged>
{
    public async Task Consume(ConsumeContext<EmployeeUnavailabilityChanged> context)
    {
        var msg = context.Message;
        var from = msg.PreviousStart is { } ps && ps < msg.Start ? ps : msg.Start;
        var to = msg.PreviousEnd is { } pe && pe > msg.End ? pe : msg.End;
        var earliest = from.AddDays(-7 * 52);

        var candidates = (await db.Schedules.Include(s => s.Assignments)
                .Where(s => s.StoreId == msg.StoreId && s.SectorId != null && s.StartDate <= to && s.StartDate >= earliest
                            && (s.Status == ScheduleStatus.Draft || s.Status == ScheduleStatus.Generated
                                || s.Status == ScheduleStatus.Infeasible || s.Status == ScheduleStatus.Failed))
                .ToListAsync(context.CancellationToken))
            .Where(s => s.EndDate >= from && ScheduleCells.ReadSnapshot(s).Employees.Any(e => e.EmployeeId == msg.EmployeeId))
            .ToList();

        var updated = 0;
        foreach (var s in candidates)
        {
            await ScheduleEndpoints.RefreshAbsencesAsync(s, sync, evaluation, context.CancellationToken);
            if (db.Entry(s).State == EntityState.Modified) updated++;
        }
        if (updated > 0) await db.SaveChangesAsync(context.CancellationToken);
        logger.LogInformation("Ausência {Acao} de {EmployeeId} ({Start}–{End}): {Updated} de {Count} escala(s) editável(is) reavaliada(s)",
            msg.Acao, msg.EmployeeId, msg.Start, msg.End, updated, candidates.Count);
    }
}

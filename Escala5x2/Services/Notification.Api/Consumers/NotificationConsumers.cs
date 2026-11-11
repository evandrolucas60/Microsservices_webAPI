using Escala5x2.BuildingBlocks.Contracts.Scheduling;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Notification.Api.Domain;

namespace Notification.Api.Consumers;

/// <summary>
/// MVP: notificação dentro do sistema. Canais externos (e-mail, WhatsApp, push) entram aqui
/// depois, sem mudar quem publica os eventos.
/// </summary>
public sealed class NotificationConsumers(NotificationDbContext db, ILogger<NotificationConsumers> logger) :
    IConsumer<SchedulePublished>, IConsumer<ScheduleGenerated>, IConsumer<ScheduleInfeasible>, IConsumer<ScheduleViolationDetected>,
    IConsumer<ScheduleDeleted>, IConsumer<ScheduleNoSolutionInTime>, IConsumer<ScheduleGenerationFailed>
{
    /// <summary>Escala excluída: os avisos dela apontariam para uma escala que não existe mais.</summary>
    public async Task Consume(ConsumeContext<ScheduleDeleted> c)
    {
        var removed = await db.Notifications.Where(n => n.ScheduleId == c.Message.ScheduleId).ExecuteDeleteAsync(c.CancellationToken);
        logger.LogInformation("Escala {ScheduleId} excluída: {Count} aviso(s) removido(s)", c.Message.ScheduleId, removed);
    }

    public Task Consume(ConsumeContext<SchedulePublished> c) => Save(c.Message.StoreId, c.Message.ScheduleId, "published",
        "Escala publicada",
        c.Message.EndDate is { } fim
            ? $"A escala de {c.Message.StartDate:dd/MM/yyyy} a {fim:dd/MM/yyyy} (versão {c.Message.Version}) foi publicada para {c.Message.EmployeeIds.Count} pessoas."
            // Evento antigo (escala por semanas)
            : $"A escala de {c.Message.StartDate:dd/MM/yyyy} ({c.Message.Weeks} semana(s), versão {c.Message.Version}) foi publicada para {c.Message.EmployeeIds.Count} pessoas.");

    // Ótima (prova de ótimo) ou Viável (cumpre todas as regras rígidas, sem prova de ótimo)
    public Task Consume(ConsumeContext<ScheduleGenerated> c) => Save(null, c.Message.ScheduleId, "generated",
        $"Escala pronta para aprovação ({EngineResults.Rotulo(c.Message.Resultado ?? c.Message.SolverStatus)})",
        $"Gerada em {c.Message.SolveSeconds:F0}s com {c.Message.Assignments.Count} turnos" +
        (c.Message.Resultado == EngineResults.Viavel ? " — cumpre todas as regras rígidas; o motor não provou que é a melhor no tempo." : "."));

    // Inviável = provado que não existe escala; o diagnóstico fica na escala
    public Task Consume(ConsumeContext<ScheduleInfeasible> c) => Save(null, c.Message.ScheduleId, "infeasible",
        "Escala inviável (nenhuma escala gerada)", c.Message.Reason);

    public Task Consume(ConsumeContext<ScheduleNoSolutionInTime> c) => Save(null, c.Message.ScheduleId, "timeout",
        "Escala sem solução no tempo", $"{c.Message.Reason} (tempo usado: {c.Message.TimeLimitSeconds} s)");

    public Task Consume(ConsumeContext<ScheduleGenerationFailed> c) => Save(null, c.Message.ScheduleId, "failed",
        "Falha na geração da escala", c.Message.Error);

    public Task Consume(ConsumeContext<ScheduleViolationDetected> c) => Save(null, c.Message.ScheduleId, "violation",
        $"Violação {c.Message.RuleCode} detectada", c.Message.Detail);

    private async Task Save(Guid? storeId, Guid scheduleId, string kind, string title, string message)
    {
        db.Notifications.Add(new UserNotification { StoreId = storeId, ScheduleId = scheduleId, Kind = kind, Title = title, Message = message });
        await db.SaveChangesAsync();
        logger.LogInformation("Notificação {Kind} para a escala {ScheduleId}", kind, scheduleId);
    }
}

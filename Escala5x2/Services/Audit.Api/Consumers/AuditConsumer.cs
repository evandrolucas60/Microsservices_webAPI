using System.Text.Json;
using Audit.Api.Domain;
using Escala5x2.BuildingBlocks.Contracts.Compliance;
using Escala5x2.BuildingBlocks.Contracts.Employees;
using Escala5x2.BuildingBlocks.Contracts.Scheduling;
using Escala5x2.BuildingBlocks.Contracts.Stores;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Audit.Api.Consumers;

/// <summary>
/// Uma fila ("escala5x2-audit-consumer") assinando todos os eventos de negócio da Escala 5x2.
/// Usuários/papéis são da intranet (AuthAPI); o antigo evento UserCreated do Identity.Api foi removido.
/// </summary>
public sealed class AuditConsumer(AuditDbContext db, ILogger<AuditConsumer> logger) :
    IConsumer<EmployeeRegistered>, IConsumer<EmployeeContractChanged>, IConsumer<EmployeeUnavailabilityRegistered>,
    IConsumer<EmployeeUnavailabilityChanged>, IConsumer<StoreCreated>, IConsumer<StorePlanningChanged>, IConsumer<HolidayChanged>,
    IConsumer<LegalRuleVersionPublished>, IConsumer<ComplianceRuleChanged>,
    IConsumer<ScheduleRequested>, IConsumer<ScheduleGenerated>, IConsumer<ScheduleInfeasible>,
    IConsumer<ScheduleGenerationFailed>, IConsumer<ScheduleViolationDetected>,
    IConsumer<ScheduleApproved>, IConsumer<SchedulePublished>, IConsumer<ScheduleCellsEdited>, IConsumer<CorrectionSuggested>,
    IConsumer<ScheduleApprovalRevoked>, IConsumer<ScheduleDeleted>, IConsumer<ScheduleInfoEdited>, IConsumer<ScheduleNoSolutionInTime>, IConsumer<EmployeeJourneyChanged>,
    IConsumer<EmployeeDetailsChanged>, IConsumer<SchedulePrincipalChanged>, IConsumer<ScheduleGenerationCancelled>,
    IConsumer<EmployeesLgSyncCompleted>, IConsumer<EmployeeLgStatusChanged>, IConsumer<StoresLgSyncCompleted>, IConsumer<StoreLgChanged>,
    IConsumer<SectorLgChanged>, IConsumer<StoresSectorsLgSyncCompleted>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task Consume(ConsumeContext<EmployeeRegistered> c) => Save(c, c.Message.By, c.Message.OccurredAt, null);
    public Task Consume(ConsumeContext<EmployeeContractChanged> c) => Save(c, c.Message.By, c.Message.OccurredAt, null);
    public Task Consume(ConsumeContext<EmployeeUnavailabilityRegistered> c) => Save(c, c.Message.By, c.Message.OccurredAt, null);
    // Ausências registradas/alteradas/excluídas (sem a observação: o evento não a carrega, LGPD)
    public Task Consume(ConsumeContext<EmployeeUnavailabilityChanged> c) => Save(c, c.Message.By, c.Message.OccurredAt, null);
    // Feriados nacionais/estaduais/municipais criados, alterados e excluídos
    public Task Consume(ConsumeContext<HolidayChanged> c) => Save(c, c.Message.By, c.Message.OccurredAt, null);
    public Task Consume(ConsumeContext<StoreCreated> c) => Save(c, c.Message.By, c.Message.OccurredAt, null);
    public Task Consume(ConsumeContext<StorePlanningChanged> c) => Save(c, c.Message.By, c.Message.OccurredAt, null);
    public Task Consume(ConsumeContext<LegalRuleVersionPublished> c) => Save(c, c.Message.By, c.Message.OccurredAt, null);
    // Histórico do cadastro de regras de conformidade (valor anterior/novo, bloqueio e confirmação de limite legal)
    public Task Consume(ConsumeContext<ComplianceRuleChanged> c) => Save(c, c.Message.By, c.Message.OccurredAt, null);
    public Task Consume(ConsumeContext<ScheduleCellsEdited> c) => Save(c, c.Message.By, c.Message.OccurredAt, c.Message.ScheduleId);
    public Task Consume(ConsumeContext<CorrectionSuggested> c) => Save(c, "optimization-worker", c.Message.OccurredAt, c.Message.ScheduleId,
        new { c.Message.ScheduleId, c.Message.SuggestionId, c.Message.Status, c.Message.PercentualAtual, c.Message.PercentualPrevisto, ChangeCount = c.Message.Changes.Count, c.Message.Message });
    public Task Consume(ConsumeContext<ScheduleRequested> c) => Save(c, c.Message.RequestedBy, c.Message.OccurredAt, c.Message.ScheduleId);
    // Inviável com o diagnóstico (resumo, itens e sugestões)
    public Task Consume(ConsumeContext<ScheduleInfeasible> c) => Save(c, "optimization-worker", c.Message.OccurredAt, c.Message.ScheduleId);
    public Task Consume(ConsumeContext<ScheduleNoSolutionInTime> c) => Save(c, "optimization-worker", c.Message.OccurredAt, c.Message.ScheduleId);
    // Perfil de jornada, disponibilidade, preferências e custo/hora do colaborador
    public Task Consume(ConsumeContext<EmployeeJourneyChanged> c) => Save(c, c.Message.By, c.Message.OccurredAt, null);
    // Nome, matrícula, cargo, setores habilitados e ativo/inativo do colaborador
    public Task Consume(ConsumeContext<EmployeeDetailsChanged> c) => Save(c, c.Message.By, c.Message.OccurredAt, null);
    // Sincronização com a LG: resumo (contagens e centros de custo ignorados; sem CPF/dados pessoais)
    public Task Consume(ConsumeContext<EmployeesLgSyncCompleted> c) => Save(c, c.Message.By, c.Message.OccurredAt, null);
    // Colaborador movido de loja, inativado ou reativado pela LG (sem CPF)
    public Task Consume(ConsumeContext<EmployeeLgStatusChanged> c) => Save(c, c.Message.By, c.Message.OccurredAt, null);
    // Sincronização de lojas com a LG (centros de custo): resumo, centros ignorados e conflitos
    public Task Consume(ConsumeContext<StoresLgSyncCompleted> c) => Save(c, c.Message.By, c.Message.OccurredAt, null);
    // Loja criada, atualizada, inativada ou reativada pela LG (com o resumo de antes)
    public Task Consume(ConsumeContext<StoreLgChanged> c) => Save(c, c.Message.By, c.Message.OccurredAt, null);
    // Setor criado, adotado (setor manual recebeu o código do órgão), renomeado, inativado ou reativado pela LG (OrgaoResponsavel)
    public Task Consume(ConsumeContext<SectorLgChanged> c) => Save(c, c.Message.By, c.Message.OccurredAt, null);
    // Sincronização do catálogo de setores (órgãos responsáveis da LG): contagens e órgãos ignorados
    public Task Consume(ConsumeContext<StoresSectorsLgSyncCompleted> c) => Save(c, c.Message.By, c.Message.OccurredAt, null);
    public Task Consume(ConsumeContext<ScheduleGenerationFailed> c) => Save(c, "optimization-worker", c.Message.OccurredAt, c.Message.ScheduleId);
    public Task Consume(ConsumeContext<ScheduleViolationDetected> c) => Save(c, "optimization-worker", c.Message.OccurredAt, c.Message.ScheduleId);
    public Task Consume(ConsumeContext<ScheduleApproved> c) => Save(c, c.Message.ApprovedBy, c.Message.OccurredAt, c.Message.ScheduleId);
    public Task Consume(ConsumeContext<SchedulePublished> c) => Save(c, c.Message.PublishedBy, c.Message.OccurredAt, c.Message.ScheduleId);
    // Aprovação desfeita (regerar na própria escala ou reabrir para edição), com o percentual da aprovação
    public Task Consume(ConsumeContext<ScheduleApprovalRevoked> c) => Save(c, c.Message.By, c.Message.OccurredAt, c.Message.ScheduleId);
    // Escala excluída: resumo (loja, setor, período, versão, status no momento da exclusão, aprovação, se era a principal,
    // quem e com que papel); as células não existem mais
    public Task Consume(ConsumeContext<ScheduleDeleted> c) => Save(c, c.Message.By, c.Message.OccurredAt, c.Message.ScheduleId);
    // Escala principal do setor no período (quem, por quê e quais deixaram de ser principal)
    public Task Consume(ConsumeContext<SchedulePrincipalChanged> c) => Save(c, c.Message.By, c.Message.OccurredAt, c.Message.ScheduleId);
    // Geração cancelada (status anterior restaurado ou Cancelada, células preservadas, pedido de principal descartado)
    public Task Consume(ConsumeContext<ScheduleGenerationCancelled> c) => Save(c, c.Message.By, c.Message.OccurredAt, c.Message.ScheduleId);
    // Título/observação (o texto da observação não vai para a auditoria: só se mudou)
    public Task Consume(ConsumeContext<ScheduleInfoEdited> c) => Save(c, c.Message.By, c.Message.OccurredAt, c.Message.ScheduleId);
    // Edição salva mesmo com inconformidades novas ("Salvar assim mesmo"): quem aceitou, status (aprovada/publicada não
    // mudam), percentual antes/depois e as inconformidades (texto do avaliador, sem o tipo da ausência: LGPD)
    public Task Consume(ConsumeContext<ScheduleSavedWithViolations> c) => Save(c, c.Message.By, c.Message.OccurredAt, c.Message.ScheduleId);

    // As alocações completas já ficam no Scheduling; aqui guardamos o resumo.
    public Task Consume(ConsumeContext<ScheduleGenerated> c) => Save(c, "optimization-worker", c.Message.OccurredAt, c.Message.ScheduleId,
        new { c.Message.ScheduleId, AssignmentCount = c.Message.Assignments.Count, c.Message.SolverStatus, c.Message.Resultado, c.Message.SolveSeconds, c.Message.ObjectiveValue, c.Message.Notice });

    private async Task Save<T>(ConsumeContext<T> context, string? actor, DateTime occurredAt, Guid? scheduleId, object? payload = null)
        where T : class
    {
        if (context.MessageId is { } id && await db.Entries.AnyAsync(e => e.MessageId == id))
            return; // entrega repetida

        db.Entries.Add(new AuditEntry
        {
            MessageType = typeof(T).Name,
            MessageId = context.MessageId,
            CorrelationId = context.CorrelationId ?? context.ConversationId,
            ScheduleId = scheduleId,
            Actor = actor,
            OccurredAt = DateTime.SpecifyKind(occurredAt, DateTimeKind.Utc),
            Payload = JsonSerializer.Serialize(payload ?? context.Message, Json)
        });
        await db.SaveChangesAsync();
        logger.LogDebug("Auditado {Type} {MessageId}", typeof(T).Name, context.MessageId);
    }
}

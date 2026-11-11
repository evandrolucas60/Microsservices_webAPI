using Escala5x2.BuildingBlocks.Contracts.Planning;

namespace Escala5x2.BuildingBlocks.Contracts.Http;

/// <summary>
/// Resposta do endpoint interno do Store Service usado pelo Scheduling Service.
/// <paramref name="Operacoes"/>: operação EFETIVA de cada setor que tem operação (a do setor ou, sem ela, a da loja);
/// setor fora da lista = sem operação configurada (modelo anterior).
/// </summary>
public sealed record StorePlanningResponse(
    Guid StoreId,
    string StoreName,
    bool HasExtendedDayAgreement,
    IReadOnlyList<SectorSnapshot> Sectors,
    IReadOnlyList<ShiftSnapshot> Shifts,
    IReadOnlyList<DemandSnapshot> Demands,
    IReadOnlyList<HolidaySnapshot> Holidays,
    IReadOnlyList<SectorOperation>? Operacoes = null);

/// <summary>
/// Dia de ausência de um colaborador (GET /internal/planning/absences do Employee.Api): o Scheduling.Api usa para
/// mostrar "Afastado" na grade e reavaliar escalas com os registros ATUAIS. Sem observação (LGPD).
/// </summary>
public sealed record AbsenceEntry(Guid EmployeeId, DateOnly Date, string Tipo);

/// <summary>
/// Colaborador ativo em férias/afastamento na LG (situação diferente de 1 - ATIVIDADE NORMAL) SEM o período registrado
/// (GET /internal/planning/pending do Employee.Api). Fica fora de /internal/planning/employees até o operador registrar
/// o período; o Scheduling.Api avisa na geração. <paramref name="Situacao"/>: descrição da LG; <paramref name="Desde"/>:
/// DataInicioSituacaoAtual. Sem CPF nem data de nascimento.
/// </summary>
public sealed record PendingPeriodEntry(Guid EmployeeId, string Registration, string Name, IReadOnlyList<Guid> SectorIds,
    string Situacao, DateOnly? Desde);

namespace Escala5x2.BuildingBlocks.Contracts.Employees;

public sealed record EmployeeRegistered(Guid EmployeeId, Guid StoreId, string Registration, string Name, string By, DateTime OccurredAt);

public sealed record EmployeeContractChanged(Guid EmployeeId, Guid ContractVersionId, DateOnly ValidFrom, string By, DateTime OccurredAt);

/// <summary>LEGADO: evento dos registros de ausência antes da edição/exclusão. Mantido só para ler a auditoria antiga.</summary>
public sealed record EmployeeUnavailabilityRegistered(Guid EmployeeId, DateOnly Start, DateOnly End, string Type, string By, DateTime OccurredAt);

/// <summary>
/// Ausência (férias, afastamento, afastamento médico...) registrada, alterada ou excluída.
/// <paramref name="Acao"/>: registrada | alterada | excluida. Sem a observação (LGPD: pode conter dado de saúde).
/// O Scheduling.Api reavalia as escalas editáveis do período; a auditoria grava o histórico.
/// <paramref name="PreviousStart"/>/<paramref name="PreviousEnd"/>: período anterior numa alteração.
/// </summary>
public sealed record EmployeeUnavailabilityChanged(
    Guid UnavailabilityId,
    Guid EmployeeId,
    Guid StoreId,
    string Acao,
    DateOnly Start,
    DateOnly End,
    string Type,
    DateOnly? PreviousStart,
    DateOnly? PreviousEnd,
    string By,
    DateTime OccurredAt);

/// <summary>
/// Perfil de jornada, disponibilidade semanal, preferências ou custo/hora do colaborador alterados (auditoria).
/// <paramref name="Resumo"/>: o que mudou ("perfil Abertura → Flexível; disponibilidade: dom indisponível").
/// </summary>
public sealed record EmployeeJourneyChanged(Guid EmployeeId, Guid StoreId, string PerfilJornada, string? PerfilAnterior, string Resumo,
    string By, DateTime OccurredAt);

/// <summary>
/// Dados cadastrais do colaborador alterados (auditoria): nome, matrícula, cargo, setores habilitados e ativo/inativo.
/// Contrato muda por nova versão (<see cref="EmployeeContractChanged"/>). <paramref name="Resumo"/>: o que mudou
/// ("nome Ana → Ana Paula; setores 1 → 2"); <paramref name="SectorIds"/>: setores habilitados depois da alteração.
/// </summary>
public sealed record EmployeeDetailsChanged(Guid EmployeeId, Guid StoreId, string Registration, string Name, bool Active,
    IReadOnlyList<Guid> SectorIds, string Resumo, string By, DateTime OccurredAt);

/// <summary>
/// Sincronização dos colaboradores com a LG (ConsultarListaHistoricosVigenteNaDataPorDemanda) concluída ou com falha
/// (auditoria). Só contagens e listas de centros de custo/matrículas: sem CPF, data de nascimento ou outro dado pessoal.
/// <paramref name="Status"/>: Concluida | Falha. <paramref name="CentrosIgnorados"/>: "código descrição (quantidade)".
/// <paramref name="SetoresCriados"/>/<paramref name="SetoresAdotados"/>: setores dos órgãos responsáveis criados/adotados
/// no Store.Api; <paramref name="SetoresDivergentes"/>: colaboradores com setor manual diferente do da LG.
/// <paramref name="FeriasConsultadas"/>/<paramref name="FeriasFalhas"/>: férias consultadas na LG (ServicoDeFerias) logo
/// depois, com sucesso / com falha (as com falha mantêm os períodos já gravados).
/// </summary>
public sealed record EmployeesLgSyncCompleted(
    string Status,
    DateOnly DataVigente,
    int Paginas,
    int TotalLg,
    int Criados,
    int Atualizados,
    int Inalterados,
    int Ignorados,
    int Inativados,
    int Movidos,
    int Reativados,
    int SemSetor,
    int PendenciasDePeriodo,
    int Erros,
    IReadOnlyList<string> CentrosIgnorados,
    string? Mensagem,
    string By,
    DateTime StartedAt,
    DateTime OccurredAt,
    int SetoresCriados = 0,
    int SetoresAdotados = 0,
    int SetoresDivergentes = 0,
    int FeriasConsultadas = 0,
    int FeriasFalhas = 0);

/// <summary>
/// Colaborador movido de loja, inativado ou reativado pela sincronização com a LG (auditoria). <paramref name="Acao"/>:
/// movido | inativado | reativado. <paramref name="PreviousStoreId"/>: loja anterior numa mudança de loja.
/// <paramref name="Motivo"/>: ex. "centro de custo 203000 VAREJO LOJA 03 - PONTEZINHA" ou "não veio na LG em 30/10/2026".
/// Sem CPF nem data de nascimento.
/// </summary>
public sealed record EmployeeLgStatusChanged(Guid EmployeeId, Guid StoreId, Guid? PreviousStoreId, string Registration, string Name,
    string Acao, string Motivo, string By, DateTime OccurredAt);
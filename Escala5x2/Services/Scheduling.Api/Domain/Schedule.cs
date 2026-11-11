namespace Scheduling.Api.Domain;

/// <summary>
/// Draft = Rascunho (escala manual, ainda sem geração). Generated = o motor entregou (Ótima ou Viável, em
/// <see cref="Schedule.SolverStatus"/>); Infeasible = provado que não existe escala (diagnóstico em DiagnosticoJson);
/// TimedOut = "Sem solução no tempo" (o motor não concluiu: pode tentar com mais tempo); Failed = erro/modelo inválido ou
/// escala descartada pelo avaliador independente. Cancelled = a geração de uma escala NOVA foi cancelada pelo gestor/Admin
/// antes de terminar (sem quadro: pode gerar de novo na própria escala, preencher à mão ou excluir). A geração cancelada
/// numa escala que já tinha quadro (gerar na própria escala) não usa este status: a escala volta ao status anterior com as
/// células preservadas.
/// </summary>
public enum ScheduleStatus { Draft, Generating, Generated, Infeasible, Failed, Approved, Published, Superseded, TimedOut, Cancelled }

/// <summary>
/// Estado da escala antes de uma geração NA PRÓPRIA escala, guardado para o cancelamento devolvê-la como estava (as
/// células não mudam até o resultado chegar). Aprovada volta como Gerada: a aprovação foi desfeita ao pedir a geração
/// (evento ScheduleApprovalRevoked já registrado).
/// </summary>
public sealed record GenerationRollback(ScheduleStatus Status, string? StatusReason, string? DeficitsJson, string? DiagnosticoJson,
    string? SolucoesMotorJson, string? SolverStatus, long? ObjectiveValue, double? SolveSeconds, string? GenerationMode, int? TempoLimiteSegundos);

/// <summary>Quem preencheu a célula: o operador (Manual) ou o motor (geração/sugestão aplicada).</summary>
public enum CellOrigin { Motor, Manual }

public sealed class Schedule
{
    /// <summary>Nome exibido para as escalas antigas, geradas por loja (todos os setores, somente leitura).</summary>
    public const string AllSectors = "Todos os setores";

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StoreId { get; set; }
    public string StoreName { get; set; } = string.Empty;
    /// <summary>Setor da escala. Nulo = escala antiga por loja ("Todos os setores"), somente leitura.</summary>
    public Guid? SectorId { get; set; }
    public string? SectorName { get; set; }
    /// <summary>
    /// Primeiro e último dia da escala. Escalas novas são do MÊS civil (dia 1º ao último dia: 28, 29, 30 ou 31 dias); as
    /// antigas, por semanas, vão de uma segunda-feira a um domingo (a migration EscalaMensal preencheu o fim delas).
    /// </summary>
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    /// <summary>
    /// Semanas civis (segunda a domingo) que o período cobre: o horizonte do motor/avaliador, com as semanas parciais das
    /// pontas do mês contadas inteiras junto com o mês vizinho (PlanningContext.Horizon). Nas escalas antigas, o tamanho pedido.
    /// </summary>
    public int Weeks { get; set; }
    /// <summary>Dias do período (o mês: 28 a 31).</summary>
    public int Days => EndDate.DayNumber - StartDate.DayNumber + 1;
    public int Version { get; set; } = 1;
    public Guid? ParentScheduleId { get; set; }
    public ScheduleStatus Status { get; set; } = ScheduleStatus.Generating;
    public string? StatusReason { get; set; }

    /// <summary>Nome livre da escala (opcional, até <see cref="TituloMax"/> caracteres). Período e setor não mudam.</summary>
    public string? Titulo { get; set; }
    /// <summary>Observação livre do gestor (opcional, até <see cref="ObservacaoMax"/> caracteres).</summary>
    public string? Observacao { get; set; }
    public const int TituloMax = 120;
    public const int ObservacaoMax = 1000;

    /// <summary>Fotografia completa (pessoas, contratos, turnos, demanda, regras, histórico, ocupações) usada na geração e na avaliação.</summary>
    public string SnapshotJson { get; set; } = "{}";
    public string? DeficitsJson { get; set; }
    /// <summary>Diagnóstico de inviabilidade (InfeasibilityDiagnosis) da última geração Inviável.</summary>
    public string? DiagnosticoJson { get; set; }
    /// <summary>Opções de escala avaliadas pelo motor na última geração entregue (EngineSolutionsSummary, jsonb).</summary>
    public string? SolucoesMotorJson { get; set; }
    /// <summary>Tempo do motor (s) da última geração pedida ("tentar com mais tempo" aumenta).</summary>
    public int? TempoLimiteSegundos { get; set; }
    /// <summary>Resultado do motor: Otima | Viavel (Workers antigos gravavam Optimal/Feasible).</summary>
    public string? SolverStatus { get; set; }
    public long? ObjectiveValue { get; set; }
    public double? SolveSeconds { get; set; }
    /// <summary>Modo da última geração pedida (completa | somente_vazios).</summary>
    public string? GenerationMode { get; set; }
    /// <summary>Incrementa a cada mudança de células (sugestões calculadas sobre outra revisão não são aplicadas).</summary>
    public int CellsRevision { get; set; }

    /// <summary>Resultado do avaliador gravado a cada geração/edição.</summary>
    public double? ConformidadePercentual { get; set; }
    public string? ConformidadeJson { get; set; }
    public DateTime? AvaliadaEm { get; set; }

    public string RequestedBy { get; set; } = string.Empty;
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? PublishedBy { get; set; }
    public DateTime? PublishedAt { get; set; }
    public Guid? SupersededBy { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Escala PRINCIPAL do setor no período: por setor e dia, no máximo uma (marcar uma desmarca as outras do mesmo setor
    /// que se sobrepõem). A continuidade das escalas seguintes (semana anterior/seguinte) e a ocupação em outros setores
    /// usam a principal antes de qualquer outra; sem principal no dia vale a prioridade do status (Publicada &gt; Aprovada &gt;
    /// Gerada &gt; Rascunho, a mais recente).
    /// </summary>
    public bool Principal { get; set; }
    public DateTime? PrincipalDesde { get; set; }
    public string? PrincipalPor { get; set; }
    /// <summary>
    /// Pedido de tornar a escala principal quando a geração em andamento terminar com sucesso (Ótima/Viável): quem pediu.
    /// Null = sem pedido. Se a geração não entregar escala, o pedido cai e a principal anterior continua.
    /// </summary>
    public string? PrincipalPedidoPor { get; set; }

    /// <summary>
    /// Geração em andamento (ou a última pedida): vai no GenerateScheduleCommand e volta nos resultados do Worker. Resultado
    /// com outro id (geração cancelada e pedida de novo) é ignorado.
    /// </summary>
    public Guid? GeracaoId { get; set; }
    /// <summary>Quando a geração em andamento (ou a última) foi pedida: tempo decorrido na tela de "Gerando escala...".</summary>
    public DateTime? GeracaoIniciadaEm { get; set; }
    /// <summary>
    /// Estado anterior (<see cref="GenerationRollback"/>, jsonb) de uma geração na própria escala, para o cancelamento.
    /// Null = escala nova (o cancelamento a deixa Cancelada) ou nenhuma geração em andamento.
    /// </summary>
    public string? GeracaoAnteriorJson { get; set; }
    /// <summary>Última geração cancelada: quando e por quem (POST cancelar-geracao).</summary>
    public DateTime? GeracaoCanceladaEm { get; set; }
    public string? GeracaoCanceladaPor { get; set; }

    /// <summary>Status que valem como escala (têm quadro): podem ser a principal e entram na continuidade/ocupação.</summary>
    public static readonly ScheduleStatus[] LiveStatuses = [ScheduleStatus.Published, ScheduleStatus.Approved, ScheduleStatus.Generated, ScheduleStatus.Draft];

    /// <summary>Pode ser (ou continuar) a principal: escala por setor com quadro (Rascunho, Gerada, Aprovada ou Publicada).</summary>
    public bool CanBePrincipal => SectorId is not null && LiveStatuses.Contains(Status);

    /// <summary>O período da escala cruza [from, to].</summary>
    public bool Overlaps(DateOnly from, DateOnly to) => StartDate <= to && EndDate >= from;

    public List<ScheduleAssignment> Assignments { get; set; } = [];

    /// <summary>
    /// Escala em construção: Rascunho, Gerada (antes de aprovar) e as que ficaram sem solução/falharam/foram canceladas.
    /// Nelas a fotografia acompanha as ausências e a continuidade atuais, e dá para gerar, pedir sugestão e mexer nos intervalos.
    /// </summary>
    public bool IsEditable => SectorId is not null && Status is ScheduleStatus.Draft or ScheduleStatus.Generated or ScheduleStatus.Infeasible
        or ScheduleStatus.Failed or ScheduleStatus.TimedOut or ScheduleStatus.Cancelled;

    /// <summary>
    /// Células (turno/folga) editáveis à mão: as escalas em construção (<see cref="IsEditable"/>) e também as já Aprovadas e
    /// Publicadas — a edição não muda o status (a aprovada continua aprovada, a publicada continua sendo o registro oficial,
    /// com a edição auditada). Substituída (histórico), em geração e as antigas por loja não.
    /// </summary>
    public bool CanEditCells => IsEditable || (SectorId is not null && Status is ScheduleStatus.Approved or ScheduleStatus.Published);

    /// <summary>
    /// Salvar uma edição que cria inconformidade pede confirmação ("Deseja salvar assim mesmo?") e fica registrada
    /// (<see cref="ScheduleEditAck"/>): toda escala com quadro gerado/aprovado/publicado. O Rascunho (montagem à mão, ainda
    /// sem geração) salva direto.
    /// </summary>
    public bool EditRequiresConfirmation => CanEditCells && Status != ScheduleStatus.Draft;

    /// <summary>A geração em andamento pode ser cancelada (na fila ou calculando).</summary>
    public bool CanCancelGeneration => Status == ScheduleStatus.Generating;

    /// <summary>
    /// Aprovada (por setor): pode ser regerada na própria escala ou reaberta para edição — a aprovação é desfeita
    /// (volta a Gerada e precisa ser aprovada de novo). Publicada não: gera nova versão.
    /// </summary>
    public bool CanRevokeApproval => SectorId is not null && Status == ScheduleStatus.Approved;

    /// <summary>
    /// Pode ser excluída (pelo status): Rascunho, Gerada, Inviável, Falhou, Sem solução no tempo, Cancelada e — só o Admin
    /// (superAdmin da intranet) — Aprovada e Substituída. Publicada é o registro oficial e não é excluída por ninguém
    /// (publique uma nova versão: a antiga vira Substituída); em geração, cancele a geração ou aguarde terminar.
    /// </summary>
    public bool IsDeletable => Status is not (ScheduleStatus.Published or ScheduleStatus.Generating);

    /// <summary>Só o Admin exclui: aprovada (a aprovação é decisão registrada) e substituída (histórico).</summary>
    public bool DeleteRequiresAdmin => Status is ScheduleStatus.Approved or ScheduleStatus.Superseded;

    /// <summary>Desfaz a aprovação (regerar na própria escala / reabrir para edição): volta a Gerada, sem aprovador.</summary>
    public void RevokeApproval()
    {
        Status = ScheduleStatus.Generated;
        ApprovedBy = null;
        ApprovedAt = null;
        UpdatedAt = DateTime.UtcNow;
    }
}

/// <summary>
/// Uma célula da escala: trabalho num turno ou folga marcada (<see cref="Folga"/>, sem turno).
/// Editável à mão em qualquer escala por setor com quadro (<see cref="Schedule.CanEditCells"/>; na Aprovada/Publicada a edição
/// é auditada e, com inconformidade nova, confirmada). Substituída é histórico e não muda.
/// </summary>
public sealed class ScheduleAssignment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ScheduleId { get; set; }
    public Guid EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public string Registration { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
    public Guid? ShiftId { get; set; }
    public string ShiftCode { get; set; } = string.Empty;
    public Guid SectorId { get; set; }
    public string SectorName { get; set; } = string.Empty;
    public TimeOnly? Start { get; set; }
    public TimeOnly? End { get; set; }
    /// <summary>Duração − intervalo efetivo (<see cref="BreakMinutes"/> ou o do turno): o intervalo não conta na jornada.</summary>
    public int PaidMinutes { get; set; }
    public CellOrigin Origem { get; set; } = CellOrigin.Motor;
    public bool Folga { get; set; }

    /// <summary>Início do intervalo de refeição. Nulo = não posicionado (escalas anteriores a esta função).</summary>
    public TimeOnly? BreakStart { get; set; }
    /// <summary>Duração do intervalo própria da célula. Nulo = a do turno.</summary>
    public int? BreakMinutes { get; set; }
    /// <summary>Quem posicionou o intervalo: o motor (geração/redistribuição) ou o operador (visão do dia).</summary>
    public CellOrigin BreakOrigem { get; set; } = CellOrigin.Motor;
}

/// <summary>
/// Edição salva mesmo com inconformidades novas (o usuário confirmou "Salvar assim mesmo"): quem, quando, o status da
/// escala na edição e as inconformidades que a edição criou (lista de AcceptedViolationDto, jsonb). A tela da escala mostra
/// o último registro (aviso persistido); a auditoria recebe o evento ScheduleSavedWithViolations.
/// </summary>
public sealed class ScheduleEditAck
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ScheduleId { get; set; }
    public string By { get; set; } = string.Empty;
    public DateTime At { get; set; } = DateTime.UtcNow;
    public ScheduleStatus Status { get; set; }
    /// <summary>Células alteradas na edição.</summary>
    public int CellCount { get; set; }
    /// <summary>Revisão das células depois da edição.</summary>
    public int CellsRevision { get; set; }
    public double? PercentualAntes { get; set; }
    public double? PercentualDepois { get; set; }
    public string ViolacoesJson { get; set; } = "[]";
}

/// <summary>Sugestão de correção calculada pelo Worker (mudança mínima).</summary>
public sealed class ScheduleSuggestion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ScheduleId { get; set; }
    /// <summary>processando | pronta | sem_solucao</summary>
    public string Status { get; set; } = "processando";
    /// <summary>Revisão das células quando a sugestão foi pedida.</summary>
    public int CellsRevision { get; set; }
    public double? PercentualAtual { get; set; }
    public double? PercentualPrevisto { get; set; }
    /// <summary>Lista de SuggestionChange (jsonb).</summary>
    public string? AlteracoesJson { get; set; }
    public string? Mensagem { get; set; }
    public string RequestedBy { get; set; } = string.Empty;
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public DateTime? AppliedAt { get; set; }
    public string? AppliedBy { get; set; }
}

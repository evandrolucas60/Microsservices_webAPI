using Escala5x2.BuildingBlocks.Contracts.Planning;

namespace Escala5x2.BuildingBlocks.Contracts.Scheduling;

/// <summary>Modo de geração de uma escala que já tem células.</summary>
public static class GenerationModes
{
    /// <summary>Descarta tudo (inclusive o que foi inserido à mão) e gera a escala inteira.</summary>
    public const string Completa = "completa";
    /// <summary>Células manuais (trabalho e folga) viram fixas; o motor preenche só as vazias.</summary>
    public const string SomenteVazios = "somente_vazios";

    public static bool IsValid(string? mode) => mode is Completa or SomenteVazios;
}

// ---------- Comandos para o Optimization Worker ----------

/// <summary>
/// Gera a escala. Em <see cref="GenerationModes.SomenteVazios"/>, <paramref name="FixedCells"/> são mantidas;
/// se elas tornarem as regras rígidas impossíveis o resultado é INVIÁVEL com diagnóstico (não há modo flexível:
/// regra rígida nunca vira penalidade). <paramref name="TimeLimitSeconds"/>: tempo do motor (o "tentar com mais tempo"
/// manda um valor maior). <paramref name="GenerationId"/>: identifica ESTA geração da escala (cada pedido tem um novo);
/// o Worker devolve o mesmo id nos resultados e o cancelamento (<see cref="CancelScheduleGeneration"/>) o cita. Null =
/// mensagem de API antiga. <paramref name="EndDate"/>: último dia da escala (escala do mês: 28 a 31 dias a partir do dia 1º);
/// null = mensagem antiga, escala de <paramref name="Weeks"/> semanas a partir da segunda-feira <paramref name="StartDate"/>.
/// </summary>
public sealed record GenerateScheduleCommand(
    Guid ScheduleId,
    int Version,
    DateOnly StartDate,
    int Weeks,
    PlanningSnapshot Snapshot,
    int TimeLimitSeconds,
    string Mode = GenerationModes.Completa,
    IReadOnlyList<CellDto>? FixedCells = null,
    Guid? GenerationId = null,
    DateOnly? EndDate = null)
{
    /// <summary>Último dia da escala (mensagem antiga: o domingo da última semana).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public DateOnly LastDate => EndDate ?? StartDate.AddDays(Weeks * 7 - 1);
}

/// <summary>
/// Cancela a geração da escala (POST /api/schedules/{id}/cancelar-geracao). Publicado para TODAS as réplicas do
/// Optimization.Worker (cada uma tem a sua fila temporária): a que está calculando interrompe o CP-SAT; as outras guardam
/// o cancelamento e descartam o comando se ele ainda estiver na fila. <paramref name="GenerationId"/> null = qualquer
/// geração da escala em andamento. A escala já volta ao estado anterior no Scheduling.Api antes desta mensagem (o
/// resultado de uma geração cancelada é ignorado lá).
/// </summary>
public sealed record CancelScheduleGeneration(Guid ScheduleId, Guid? GenerationId, string By, DateTime OccurredAt);

/// <summary>
/// Otimização de mudança mínima para corrigir as violações da escala atual. <paramref name="EndDate"/>: como em
/// <see cref="GenerateScheduleCommand"/> (null = mensagem antiga, por semanas).
/// </summary>
public sealed record SuggestCorrectionsCommand(
    Guid ScheduleId,
    Guid SuggestionId,
    DateOnly StartDate,
    int Weeks,
    PlanningSnapshot Snapshot,
    IReadOnlyList<CellDto> Cells,
    int TimeLimitSeconds,
    DateOnly? EndDate = null)
{
    /// <summary>Último dia da escala (mensagem antiga: o domingo da última semana).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public DateOnly LastDate => EndDate ?? StartDate.AddDays(Weeks * 7 - 1);
}

// ---------- DTOs ----------

/// <summary>
/// Célula da escala: trabalha no turno <paramref name="ShiftId"/>, ou folga marcada (<paramref name="Folga"/>).
/// Intervalo de refeição: início (<paramref name="BreakStart"/>, null = não posicionado), duração própria da célula
/// (<paramref name="BreakMinutes"/>, null = a do turno) e se foi ajustado à mão (<paramref name="BreakManual"/>).
/// </summary>
public sealed record CellDto(Guid EmployeeId, DateOnly Date, Guid? ShiftId, bool Folga,
    TimeOnly? BreakStart = null, int? BreakMinutes = null, bool BreakManual = false);

/// <summary>
/// Célula de trabalho no formato do avaliador. <paramref name="PaidMinutes"/> = duração − intervalo efetivo
/// (<paramref name="BreakMinutes"/> da célula ou, se null, o do turno). <paramref name="BreakStart"/> null =
/// intervalo não posicionado (escalas antigas): a pessoa conta como presente o turno inteiro na cobertura.
/// </summary>
public sealed record AssignmentDto(
    Guid EmployeeId,
    DateOnly Date,
    Guid ShiftId,
    Guid SectorId,
    TimeOnly Start,
    TimeOnly End,
    int PaidMinutes,
    TimeOnly? BreakStart = null,
    int? BreakMinutes = null);

public sealed record DeficitDto(Guid SectorId, DateOnly Date, TimeOnly SlotStart, int Missing);

/// <summary>
/// Resultado do motor (status do CP-SAT): OPTIMAL → <see cref="Otima"/> (prova de ótimo); FEASIBLE → <see cref="Viavel"/>
/// (cumpre todas as regras rígidas, sem prova de ótimo); INFEASIBLE → <see cref="Inviavel"/> (provado: não existe escala);
/// UNKNOWN/tempo esgotado → <see cref="SemSolucaoNoTempo"/> (NÃO é inviável: tente com mais tempo); MODEL_INVALID ou erro →
/// <see cref="Falha"/>. A escala só é entregue em Ótima/Viável e depois da conferência do avaliador independente.
/// </summary>
public static class EngineResults
{
    public const string Otima = "Otima";
    public const string Viavel = "Viavel";
    public const string Inviavel = "Inviavel";
    public const string SemSolucaoNoTempo = "SemSolucaoNoTempo";
    public const string Falha = "Falha";

    public static string Rotulo(string? r) => r switch
    {
        Otima => "Ótima",
        Viavel => "Viável",
        Inviavel => "Inviável",
        SemSolucaoNoTempo => "Sem solução no tempo",
        Falha => "Falha",
        _ => r ?? "—"
    };
}

/// <summary>
/// Item do diagnóstico de inviabilidade. <paramref name="Tipo"/>: capacidade (faixa/dia sem gente suficiente, provado
/// por contagem), cobertura (falta mínima de pessoas por faixa com todas as outras regras), regra (regra que entra no
/// conflito), celulas_manuais (células fixas de um colaborador), disponibilidade, continuidade (escala anterior/seguinte).
/// </summary>
public sealed record DiagnosisItem(string Tipo, string Mensagem, IReadOnlyList<DateOnly>? Dates = null, Guid? EmployeeId = null, string? Chave = null);

/// <summary>Diagnóstico de inviabilidade: resumo em pt-BR, itens e sugestões do que mudar.</summary>
public sealed record InfeasibilityDiagnosis(string Resumo, IReadOnlyList<DiagnosisItem> Itens, IReadOnlyList<string> Sugestoes);

/// <summary>Alteração sugerida numa célula. ShiftId nulo = sem trabalho (folga marcada em <c>Folga</c> ou vazia).</summary>
public sealed record SuggestedChangeDto(
    Guid EmployeeId, DateOnly Date, Guid? FromShiftId, bool FromFolga, Guid? ToShiftId, bool ToFolga);

public static class SuggestionStatus
{
    public const string Processando = "processando";
    public const string Pronta = "pronta";
    public const string SemSolucao = "sem_solucao";
}

// ---------- Eventos ----------
/// <summary><paramref name="EndDate"/>: último dia da escala (null = evento antigo, por semanas).</summary>
public sealed record ScheduleRequested(
    Guid ScheduleId, Guid StoreId, DateOnly StartDate, int Weeks, int Version, string RequestedBy, DateTime OccurredAt,
    Guid? SectorId = null, string? Mode = null, DateOnly? EndDate = null);

/// <summary>
/// Escala gerada (Ótima ou Viável). <paramref name="Resultado"/>: <see cref="EngineResults"/> (null = Worker antigo).
/// <paramref name="FlexibleMode"/>: LEGADO (o modo flexível foi removido; só mensagens de Workers antigos trazem true).
/// <paramref name="Deficits"/>: faltas no reforço de pico (demanda acima do mínimo, preferencial).
/// As células trazem o início do intervalo decidido pelo motor (BreakStart).
/// <paramref name="SolucoesMotor"/>: opções de escala avaliadas pelo motor (null = Worker antigo).
/// <paramref name="GenerationId"/> (também nos outros resultados): a geração que produziu o resultado (o do
/// <see cref="GenerateScheduleCommand"/>); resultado de outra geração (ex.: cancelada e pedida de novo) é ignorado.
/// <paramref name="CoberturaMetaSetores"/>: setores em que o motor usou a cobertura como meta por quadro insuficiente
/// (gravado na fotografia da escala, <see cref="PlanningSnapshot.CoberturaMetaSetores"/>; null = nenhum/Worker antigo);
/// <paramref name="CoberturaMetaDias"/>: o mesmo só em alguns dias (<see cref="PlanningSnapshot.CoberturaMetaDias"/>).
/// </summary>
public sealed record ScheduleGenerated(
    Guid ScheduleId,
    IReadOnlyList<AssignmentDto> Assignments,
    long ObjectiveValue,
    double SolveSeconds,
    string SolverStatus,
    DateTime OccurredAt,
    bool FlexibleMode = false,
    string? Notice = null,
    IReadOnlyList<DeficitDto>? Deficits = null,
    string? Resultado = null,
    EngineSolutionsSummary? SolucoesMotor = null,
    Guid? GenerationId = null,
    IReadOnlyList<Guid>? CoberturaMetaSetores = null,
    IReadOnlyList<CoberturaMetaDia>? CoberturaMetaDias = null);

/// <summary>Inviável (provado). <paramref name="Diagnostico"/>: null só em mensagens de Workers antigos.</summary>
public sealed record ScheduleInfeasible(
    Guid ScheduleId, string Reason, IReadOnlyList<DeficitDto> Deficits, DateTime OccurredAt,
    InfeasibilityDiagnosis? Diagnostico = null, Guid? GenerationId = null);

/// <summary>O motor não concluiu no tempo (nem escala nem prova de inviabilidade): dá para tentar com mais tempo.</summary>
public sealed record ScheduleNoSolutionInTime(Guid ScheduleId, string Reason, int TimeLimitSeconds, double SolveSeconds, DateTime OccurredAt,
    Guid? GenerationId = null);

public sealed record ScheduleGenerationFailed(Guid ScheduleId, string Error, DateTime OccurredAt, Guid? GenerationId = null);

/// <summary>
/// Geração cancelada pelo gestor/Admin (auditoria). <paramref name="StatusAnterior"/>: status da escala antes da geração
/// (null = escala nova, sem quadro anterior); <paramref name="Status"/>: status depois do cancelamento (Cancelled na escala
/// nova; o anterior na geração na própria escala, com as células preservadas — Aprovada volta como Gerada, a aprovação já
/// tinha sido desfeita ao pedir a geração). <paramref name="PrincipalPedidoCancelado"/>: havia pedido de virar a principal
/// (caiu: a principal anterior continua). <paramref name="SegundosEmGeracao"/>: tempo desde o pedido da geração.
/// </summary>
public sealed record ScheduleGenerationCancelled(
    Guid ScheduleId, Guid StoreId, Guid? SectorId, string SectorName, DateOnly StartDate, DateOnly EndDate, int Version,
    Guid? GenerationId, string? StatusAnterior, string Status, int CelulasPreservadas, bool PrincipalPedidoCancelado,
    double? SegundosEmGeracao, string By, DateTime OccurredAt);

public sealed record ScheduleViolationDetected(Guid ScheduleId, string RuleCode, string Detail, DateTime OccurredAt);

public sealed record ScheduleApproved(Guid ScheduleId, Guid StoreId, string ApprovedBy, DateTime OccurredAt,
    double? ConformidadePercentual = null);

/// <summary><paramref name="EndDate"/>: último dia da escala (null = evento antigo, por semanas).</summary>
public sealed record SchedulePublished(
    Guid ScheduleId,
    Guid StoreId,
    DateOnly StartDate,
    int Weeks,
    int Version,
    IReadOnlyList<Guid> EmployeeIds,
    string PublishedBy,
    DateTime OccurredAt,
    DateOnly? EndDate = null);

/// <summary>
/// Aprovação desfeita: a escala aprovada foi regerada na própria escala (<paramref name="Motivo"/> = "regerar") ou
/// reaberta para edição ("reabrir"). Leva quem tinha aprovado e o percentual de conformidade da aprovação.
/// </summary>
public sealed record ScheduleApprovalRevoked(
    Guid ScheduleId, Guid StoreId, string Motivo, string? PreviousApprovedBy, DateTime? PreviousApprovedAt,
    double? ConformidadeAnterior, string By, DateTime OccurredAt);

/// <summary>
/// Escala removida (resumo para a auditoria; as células e sugestões são apagadas). <paramref name="Status"/>: status da
/// escala no momento da exclusão (Aprovada/Substituída só o Admin exclui); <paramref name="ApprovedBy"/>/<paramref name="ApprovedAt"/>:
/// a aprovação que deixou de existir; <paramref name="Principal"/>: era a escala principal do setor no período (as
/// seguintes deixam de se basear nela).
/// </summary>
public sealed record ScheduleDeleted(
    Guid ScheduleId, Guid StoreId, string StoreName, Guid? SectorId, string SectorName, DateOnly StartDate, DateOnly EndDate,
    int Weeks, int Version, string Status, double? ConformidadePercentual, int CellCount, string? Titulo, string By, DateTime OccurredAt,
    string? ByRole = null, string? ApprovedBy = null, DateTime? ApprovedAt = null, bool Principal = false);

/// <summary>
/// A escala passou a ser a PRINCIPAL do setor no período. <paramref name="Motivo"/>: "manual" (POST /principal),
/// "geracao" (pedido na geração, aplicado quando ela terminou com sucesso), "criacao" (nova escala sem outra sobreposta)
/// ou "publicacao" (nova versão publicada no lugar da principal substituída). <paramref name="Desmarcadas"/>: escalas do
/// mesmo setor que se sobrepunham e deixaram de ser principal.
/// </summary>
public sealed record SchedulePrincipalChanged(
    Guid ScheduleId, Guid StoreId, Guid? SectorId, string SectorName, DateOnly StartDate, DateOnly EndDate, int Version, string Status,
    string Motivo, IReadOnlyList<Guid> Desmarcadas, string By, DateTime OccurredAt);

/// <summary>Título/observação da escala alterados (PATCH).</summary>
public sealed record ScheduleInfoEdited(
    Guid ScheduleId, string? TituloAnterior, string? Titulo, bool ObservacaoAlterada, string By, DateTime OccurredAt);

/// <summary>
/// Células da escala editadas à mão (em qualquer status com quadro, inclusive Aprovada/Publicada) ou por sugestão aplicada.
/// <paramref name="Status"/>: status da escala na edição (null = evento antigo).
/// </summary>
public sealed record ScheduleCellsEdited(
    Guid ScheduleId, int CellCount, string Origin, double? ConformidadePercentual, string By, DateTime OccurredAt,
    string? Status = null);

/// <summary>
/// Inconformidade nova aceita ao salvar uma edição (regra, colaborador, dias e mensagem do avaliador). O texto é o do
/// avaliador: ausência aparece como "férias/afastamento/indisponibilidade", sem o tipo (LGPD).
/// </summary>
public sealed record AcceptedViolationDto(string Chave, string RegraDescricao, string Severidade, Guid? EmployeeId, string? EmployeeName,
    IReadOnlyList<DateOnly> Dates, string Detalhe);

/// <summary>
/// Edição salva mesmo com inconformidades novas ("Deseja salvar assim mesmo?" → sim): quem confirmou, quando, o status da
/// escala (a edição não muda o status: Aprovada/Publicada continuam), o percentual depois da edição e as inconformidades
/// que a edição criou. Registrado também na escala (aviso persistido).
/// </summary>
public sealed record ScheduleSavedWithViolations(
    Guid ScheduleId, Guid StoreId, Guid? SectorId, string SectorName, DateOnly StartDate, DateOnly EndDate, int Version, string Status,
    int CellCount, double? PercentualAntes, double? PercentualDepois, IReadOnlyList<AcceptedViolationDto> Violacoes, string By, DateTime OccurredAt);

/// <summary>Resultado da sugestão de correção (status: pronta | sem_solucao).</summary>
public sealed record CorrectionSuggested(
    Guid ScheduleId,
    Guid SuggestionId,
    string Status,
    double PercentualAtual,
    double PercentualPrevisto,
    IReadOnlyList<SuggestedChangeDto> Changes,
    string? Message,
    DateTime OccurredAt);

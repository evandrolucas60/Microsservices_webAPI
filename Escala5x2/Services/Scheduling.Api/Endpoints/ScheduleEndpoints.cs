using System.Text.Json;
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
using Scheduling.Api.Clients;
using Scheduling.Api.Data;
using Scheduling.Api.Domain;
using Scheduling.Api.Services;

namespace Scheduling.Api.Endpoints;

/// <param name="StartDate">Qualquer dia do mês: a escala é do MÊS civil inteiro (dia 1º ao último dia — 28, 29, 30 ou 31 dias).</param>
/// <param name="Weeks">Legado (escalas por semanas): ignorado; a duração vem do tamanho do mês.</param>
/// <param name="Principal">
/// Tornar a nova escala a PRINCIPAL do setor no período: true = sim (na geração, só quando ela terminar Ótima/Viável;
/// a principal anterior continua se a geração falhar), false = não; null = principal se não houver outra escala do
/// setor sobreposta ao período (a tela pergunta quando há — GET /api/schedules/sobreposicao).
/// </param>
/// <param name="EndDate">
/// Uso interno (nova versão de uma escala, POST /{id}/regenerate): mantém o período da escala de origem — inclusive o das
/// escalas antigas por semanas. Ignorado nos pedidos da tela.
/// </param>
public sealed record RequestScheduleRequest(Guid StoreId, Guid? SectorId, DateOnly StartDate, int Weeks = 0, int? TempoLimiteSegundos = null,
    bool? Principal = null, DateOnly? EndDate = null);

/// <summary>
/// Modo (completa | somente_vazios) e tempo do motor (s); "tentar com mais tempo" manda um tempo maior.
/// <paramref name="Principal"/>: como em <see cref="RequestScheduleRequest"/> (a escala que já é a principal continua).
/// </summary>
public sealed record GenerateRequest(string? Modo, int? TempoLimiteSegundos = null, bool? Principal = null);

/// <summary>POST /{id}/regenerate (nova versão): <paramref name="Principal"/> como em <see cref="RequestScheduleRequest"/>.</summary>
public sealed record RegenerateRequest(bool? Principal = null);

/// <summary>
/// POST /{id}/approve: <paramref name="ConfirmarDiasSemNinguem"/> = aprovar mesmo com dia aberto sem ninguém no setor quando a
/// cobertura do setor é meta (quadro insuficiente/setor de 2 pessoas). Escala sem nenhum trabalho, ou com dia aberto vazio
/// e cobertura rígida, nunca é aprovada (ComplianceEvaluator.ApprovalBlock).
/// </summary>
public sealed record ApproveRequest(bool? ConfirmarDiasSemNinguem = null);

/// <summary>
/// GET /api/schedules/sobreposicao: outras escalas do setor (vivas ou em geração) cujo período cruza o informado, a
/// principal primeiro. <paramref name="Principal"/>: a principal entre elas (null = nenhuma; vale a prioridade do status).
/// </summary>
public sealed record OverlapResponse(Guid StoreId, Guid SectorId, DateOnly Inicio, DateOnly Fim, IReadOnlyList<ScheduleSummary> Escalas,
    ScheduleSummary? Principal);

public sealed record CellInput(Guid EmployeeId, DateOnly Date, Guid? ShiftId, bool Folga);

/// <param name="Revisao">
/// Revisão das células (CellsRevision) que a tela editou: diferente da atual = a escala mudou nesse meio tempo (regerada,
/// editada por outra pessoa, sugestão aplicada...) → 409 "escala_alterada", nada é salvo. null = sem conferência (legado).
/// </param>
/// <param name="SalvarComInconformidades">
/// Confirmação de "Deseja salvar assim mesmo?": com inconformidade nova e sem ela, a edição não é salva (409
/// "inconformidades" com a lista); com ela, salva e registra quem aceitou (ScheduleEditAck + ScheduleSavedWithViolations).
/// </param>
public sealed record UpdateCellsRequest(List<CellInput>? Cells, int? Revisao = null, bool? SalvarComInconformidades = null);

/// <summary>
/// Conferência de uma edição antes de salvar (POST /{id}/cells/validar e o 409 do PUT): inconformidades que a edição CRIA
/// (as que já existiam na escala não entram), o percentual antes/depois e se salvar pede confirmação.
/// </summary>
public sealed record EditCheckResponse(Guid ScheduleId, int Revisao, ScheduleStatus Status, bool ConfirmacaoNecessaria,
    double? PercentualAtual, double? PercentualPrevisto, int TotalViolacoes, IReadOnlyList<ComplianceViolationDto> Novas);

/// <summary>Inconformidade aceita numa edição; <paramref name="Pendente"/> = ainda aparece na avaliação atual da escala.</summary>
public sealed record EditAckViolation(string Chave, string RegraDescricao, string Severidade, Guid? EmployeeId, string? EmployeeName,
    IReadOnlyList<DateOnly> Dates, string Detalhe, bool Pendente);

/// <summary>Última edição salva com inconformidades (aviso persistido na escala): quem, quando, status e a lista.</summary>
public sealed record EditAckView(Guid Id, string Por, DateTime Em, ScheduleStatus Status, int Celulas, double? PercentualAntes,
    double? PercentualDepois, IReadOnlyList<EditAckViolation> Violacoes, int Pendentes);

public sealed record ApplySuggestionRequest(List<int>? Alteracoes);

/// <summary>PATCH /schedules/{id}: campo null = não muda; texto vazio = limpa. Período e setor não são editáveis.</summary>
public sealed record UpdateScheduleRequest(string? Titulo, string? Observacao);

/// <param name="PodeExcluir">Status permite excluir (Publicada nunca; Aprovada e Substituída só Admin; em geração, aguarde).</param>
/// <param name="PodeRegerarNaPropria">Aprovada: regerar/reabrir na própria escala desfazendo a aprovação.</param>
/// <param name="ExcluirSomenteAdmin">Aprovada ou Substituída: só o Admin (superAdmin da intranet) exclui.</param>
/// <param name="Principal">Escala principal do setor no período (desde quando e quem marcou).</param>
/// <param name="PrincipalAoGerar">Em geração com o pedido de virar principal quando terminar com sucesso.</param>
public sealed record ScheduleSummary(
    Guid Id, Guid StoreId, string StoreName, Guid? SectorId, string SectorName, DateOnly StartDate, DateOnly EndDate, int Weeks, int Version,
    ScheduleStatus Status, string? StatusReason, double? ConformidadePercentual, bool TemCelulasManuais, bool Editavel, bool SomenteLeitura,
    string? GenerationMode, int CellsRevision, string RequestedBy, DateTime RequestedAt,
    string? ApprovedBy, string? PublishedBy, DateTime? PublishedAt, DateTime? AvaliadaEm,
    string? Titulo = null, string? Observacao = null, bool PodeExcluir = false, bool PodeRegerarNaPropria = false, DateTime? ApprovedAt = null,
    // Regimes (6x1/5x2) das semanas do período, na ordem em que aparecem (da avaliação gravada; null = avaliação antiga)
    IReadOnlyList<string>? Regimes = null,
    // Resultado do motor: Otima | Viavel | Inviavel | SemSolucaoNoTempo | Falha (escalas antigas: Optimal/Feasible ou null)
    string? ResultadoMotor = null, int? TempoLimiteSegundos = null,
    // Sem solução no tempo: a tela oferece "Tentar com mais tempo" (Admin/Gestor)
    bool PodeTentarMaisTempo = false,
    bool ExcluirSomenteAdmin = false,
    bool Principal = false, DateTime? PrincipalDesde = null, string? PrincipalPor = null, bool PrincipalAoGerar = false,
    // Em geração (na fila ou calculando): pode ser cancelada (POST cancelar-geracao); desde quando a geração foi pedida
    bool PodeCancelarGeracao = false, DateTime? GeracaoIniciadaEm = null,
    // Última geração cancelada (quem e quando)
    DateTime? GeracaoCanceladaEm = null, string? GeracaoCanceladaPor = null,
    // Células editáveis à mão (também Aprovada/Publicada, sem mudar o status) e se salvar com inconformidade nova pede
    // confirmação (todas menos o Rascunho)
    bool PodeEditarCelulas = false, bool EdicaoPedeConfirmacao = false,
    // Dias do período (escala do mês: 28 a 31)
    int Dias = 0);

/// <summary>Feriado do período da escala (da fotografia): sem autorização = ninguém trabalha e não é folga.</summary>
public sealed record HolidayColumn(DateOnly Date, string Name, bool WorkAuthorized, string? Abrangencia);

/// <param name="PerfilJornada">Abertura | Fechamento | Flexivel (null = fotografia antiga).</param>
/// <param name="MesesSemFolgaDupla">
/// Pontuação de folga dupla da fotografia: meses seguidos antes desta escala sem duas folgas seguidas no mês (0 = teve no mês
/// anterior ou sem histórico; null = fotografia antiga, sem pontuação). &gt; 0 = prioridade para folga dupla neste mês.
/// </param>
/// <param name="UltimaFolgaDupla">Primeiro dia da folga dupla mais recente nos meses anteriores (null = nenhuma no histórico lido).</param>
/// <param name="FolgaDuplaNoMes">Primeiro dia da folga dupla nesta escala (null = nenhuma ou não avaliado, ex.: Rascunho).</param>
/// <param name="FolgasFixas">
/// Setor de 2 pessoas com folgas fixas e intercaladas: dias da semana das folgas fixas ("ter/sex"; "" = ainda não
/// avaliado). null = não se aplica. Quando preenchido, a folga dupla não se aplica à pessoa.
/// </param>
public sealed record EmployeeColumn(Guid Id, string Name, string Registration, SundayRule SundayRule, string? PerfilJornada = null,
    int? MesesSemFolgaDupla = null, DateOnly? UltimaFolgaDupla = null, DateOnly? FolgaDuplaNoMes = null, string? FolgasFixas = null);

/// <param name="Tipo">Abertura | Fechamento | Flexivel (null = sem operação ou fora dela); <paramref name="Gerado"/> = turno criado pela operação.</param>
public sealed record ShiftColumn(Guid Id, string Code, TimeOnly Start, TimeOnly End, int BreakMinutes, int PaidMinutes, string? Tipo = null, bool Gerado = false);

public sealed record OccupiedColumn(Guid EmployeeId, DateOnly Date, Guid SectorId, string SectorName, TimeOnly Start, TimeOnly End);

/// <summary>
/// Ausência da pessoa no dia (afastamento médico, férias...), pelos registros ATUAIS do Employee.Api: a grade mostra
/// "Afastado", nunca folga. Colaborador: <paramref name="Tipo"/> null e <paramref name="Rotulo"/> "Afastado" (LGPD).
/// </summary>
public sealed record AbsenceCell(Guid EmployeeId, DateOnly Date, string? Tipo, string Rotulo);

/// <summary>Pessoa com turno na escala em dia de afastamento/ausência (alerta nas aprovadas/publicadas).</summary>
public sealed record AbsenceConflict(Guid EmployeeId, string EmployeeName, IReadOnlyList<DateOnly> Dates);

public sealed record ScheduleDetail(
    ScheduleSummary Summary, IReadOnlyList<EmployeeColumn> Employees, IReadOnlyList<ShiftColumn> Shifts,
    IReadOnlyList<ScheduleAssignment> Assignments, IReadOnlyList<DeficitDto> Deficits, IReadOnlyList<string> RuleCodes,
    string? SolverStatus, double? SolveSeconds, ComplianceResult? Conformidade, bool TemCelulasManuais,
    IReadOnlyList<OccupiedColumn> Ocupados, IReadOnlyList<AbsenceCell> Ausencias, IReadOnlyList<AbsenceConflict> EscaladosEmAfastamento,
    IReadOnlyList<HolidayColumn>? Feriados = null,
    // Regime de cada semana pela fotografia da escala (vazio = fotografia sem o modelo de regime)
    IReadOnlyList<WeekRegimeDto>? RegimePorSemana = null,
    // Diagnóstico da última geração Inviável e operação do setor na fotografia (null = sem operação)
    InfeasibilityDiagnosis? Diagnostico = null, OperationSnapshot? Operacao = null,
    // Avisos sobre a continuidade com a semana anterior (sem escala cadastrada; escala anterior ainda não publicada)
    IReadOnlyList<string>? AvisosHistorico = null,
    // Quantas opções de escala o motor avaliou na última geração (detalhe em GET /api/schedules/{id}/solucoes)
    int? SolucoesEncontradas = null,
    // Última edição salva com inconformidades aceitas (null = nenhuma)
    EditAckView? AvisoEdicao = null,
    // Setor de 2 pessoas com folgas fixas e intercaladas (null = não se aplica)
    FixedDaysOffDto? FolgasFixas = null);

/// <summary>
/// Folgas fixas e intercaladas do setor de 2 pessoas (PlanningContext.FixedDaysOff): o setor e o padrão de cada pessoa
/// observado nas células.
/// </summary>
public sealed record FixedDaysOffDto(string Setor, IReadOnlyList<FixedDaysOffPersonDto> Pessoas);

/// <param name="Dias">Dias da semana das folgas fixas ("ter/sex"); null = não avaliado (Rascunho, em geração).</param>
/// <param name="DomingosDeFolga">Domingos de folga no mês fora do padrão (rodízio da regra de domingos).</param>
public sealed record FixedDaysOffPersonDto(Guid EmployeeId, string Nome, string? Dias, int DomingosDeFolga);

/// <summary>
/// GET /api/schedules/{id}/solucoes: opções de escala avaliadas pelo motor na última geração entregue (null = escala
/// manual, gerada por Worker antigo ou sem geração entregue) e os avisos da continuidade com a semana anterior.
/// </summary>
public sealed record EngineSolutionsResponse(Guid ScheduleId, string? ResultadoMotor, EngineSolutionsSummary? Resumo, IReadOnlyList<string> AvisosHistorico);

/// <summary>Célula numa alteração sugerida: shiftCode = código do turno, "Folga" ou "—" (vazia).</summary>
public sealed record SuggestionCell(Guid? ShiftId, string ShiftCode, TimeOnly? Start, TimeOnly? End, bool Folga);

public sealed record SuggestionChange(Guid EmployeeId, string EmployeeName, DateOnly Date, SuggestionCell De, SuggestionCell Para);

public sealed record SuggestionResponse(
    Guid SugestaoId, Guid ScheduleId, string Status, double? PercentualAtual, double? PercentualPrevisto,
    IReadOnlyList<SuggestionChange> Alteracoes, string? Mensagem, bool Desatualizada, DateTime RequestedAt, DateTime? CompletedAt, DateTime? AplicadaEm);

/// <summary>
/// Escala afetada por um afastamento/ausência do colaborador no período.
/// <paramref name="Acao"/>: "gerar" (editável ou aprovada: gera na própria escala; na aprovada a aprovação é desfeita),
/// "nova_versao" (publicada: gera uma nova versão, que só substitui a publicada quando for publicada) ou null (em geração agora).
/// <paramref name="DiasAfetados"/>: dias do afastamento dentro do período da escala; <paramref name="TurnosNoAfastamento"/>:
/// quantos desses dias o colaborador está com turno na escala. <paramref name="VersaoMaisNovaId"/>: já existe versão
/// mais nova (não substituída) do mesmo setor e período.
/// </summary>
public sealed record AffectedSchedule(
    Guid Id, Guid SectorId, string SectorName, DateOnly StartDate, DateOnly EndDate, int Weeks, int Version, ScheduleStatus Status,
    double? ConformidadePercentual, bool TemCelulasManuais, bool Editavel, string? Acao, bool ColaboradorNaEscala,
    IReadOnlyList<DateOnly> DiasAfetados, int TurnosNoAfastamento, Guid? VersaoMaisNovaId);

/// <param name="PodeRegerar">O usuário pode gerar/regerar escalas desta loja (Admin ou Gestor da loja); RH só consulta.</param>
public sealed record AffectedSchedulesResponse(
    Guid EmployeeId, Guid? StoreId, DateOnly Inicio, DateOnly Fim, bool PodeRegerar, string? Motivo, IReadOnlyList<AffectedSchedule> Escalas);

public static class ScheduleEndpoints
{
    public static ScheduleSummary Summary(Schedule s, bool manual) => new(
        s.Id, s.StoreId, s.StoreName, s.SectorId, s.SectorName ?? Schedule.AllSectors, s.StartDate, s.EndDate, s.Weeks, s.Version,
        s.Status, s.StatusReason, s.ConformidadePercentual, manual, s.IsEditable, s.SectorId is null,
        s.GenerationMode, s.CellsRevision, s.RequestedBy, s.RequestedAt, s.ApprovedBy, s.PublishedBy, s.PublishedAt, s.AvaliadaEm,
        s.Titulo, s.Observacao, s.IsDeletable, s.CanRevokeApproval, s.ApprovedAt, RegimesOf(s.ConformidadeJson),
        ResultOf(s), s.TempoLimiteSegundos, s.Status == ScheduleStatus.TimedOut, s.DeleteRequiresAdmin,
        s.Principal, s.PrincipalDesde, s.PrincipalPor, s.PrincipalPedidoPor is not null && s.Status == ScheduleStatus.Generating,
        s.CanCancelGeneration, s.Status == ScheduleStatus.Generating ? s.GeracaoIniciadaEm ?? s.UpdatedAt : s.GeracaoIniciadaEm,
        s.GeracaoCanceladaEm, s.GeracaoCanceladaPor, s.CanEditCells, s.EditRequiresConfirmation, s.Days);

    /// <summary>Resultado do motor normalizado (escalas antigas: Optimal → Otima, Feasible → Viavel; inviável/falha pelo status).</summary>
    internal static string? ResultOf(Schedule s) => s.Status switch
    {
        ScheduleStatus.TimedOut => EngineResults.SemSolucaoNoTempo,
        ScheduleStatus.Infeasible => EngineResults.Inviavel,
        ScheduleStatus.Failed => EngineResults.Falha,
        ScheduleStatus.Generating or ScheduleStatus.Draft or ScheduleStatus.Cancelled => null,
        _ => s.SolverStatus switch
        {
            null => null,
            var x when x.StartsWith("Optimal") => EngineResults.Otima,
            var x when x.StartsWith("Feasible") => EngineResults.Viavel,
            var x => x
        }
    };

    /// <summary>Regimes distintos (na ordem das semanas) do regimePorSemana da avaliação gravada.</summary>
    internal static IReadOnlyList<string>? RegimesOf(string? conformidadeJson)
    {
        if (string.IsNullOrEmpty(conformidadeJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(conformidadeJson);
            if (!doc.RootElement.TryGetProperty("regimePorSemana", out var weeks) || weeks.ValueKind != JsonValueKind.Array) return null;
            var list = weeks.EnumerateArray()
                .Select(w => w.TryGetProperty("regime", out var r) ? r.GetString() : null)
                .OfType<string>().Distinct().ToList();
            return list.Count > 0 ? list : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Consulta: Admin, RH e Jurídico todas as lojas; Gestor e Colaborador só as permitidas (perfil ∪ roles de loja
    /// "Escala5x2-Loja-{code}").
    /// </summary>
    internal static bool CanAccess(ICurrentUser user, Guid storeId) => user.CanAccessStore(storeId);

    /// <summary>
    /// Criar/editar/gerar/aprovar/publicar/aplicar sugestão: Admin, ou Gestor da loja (papel efetivo na loja: o
    /// Colaborador com role de loja é Gestor só nas lojas das roles).
    /// </summary>
    internal static bool CanManage(ICurrentUser user, Guid storeId) => user.CanManageStore(storeId);

    internal static IResult NotEditable(Schedule s) => s.SectorId is null
        ? Results.Problem($"Escala antiga por loja ({Schedule.AllSectors}) é somente leitura. Crie uma escala por setor.", statusCode: 409)
        : s.Status switch
        {
            ScheduleStatus.Approved => Results.Problem(
                "Escala aprovada: as células podem ser editadas à mão; para gerar de novo ou pedir sugestão, reabra para edição ou regere na própria escala (a aprovação será desfeita e a escala precisará ser aprovada de novo).", statusCode: 409),
            ScheduleStatus.Published => Results.Problem(
                "Escala publicada: as células podem ser editadas à mão (a edição fica registrada); para gerar de novo, gere uma nova versão (a publicada vale até a nova ser publicada).", statusCode: 409),
            _ => Results.Problem($"A escala não pode ser editada no status atual ({s.Status}).", statusCode: 409)
        };

    /// <summary>Desfaz a aprovação (regerar na própria escala ou reabrir) e publica o evento com o percentual anterior.</summary>
    internal static async Task RevokeApprovalAsync(Schedule s, string motivo, ICurrentUser user, IPublishEndpoint bus, CancellationToken ct)
    {
        var (by, at, pct) = (s.ApprovedBy, s.ApprovedAt, s.ConformidadePercentual);
        s.RevokeApproval();
        await bus.Publish(new ScheduleApprovalRevoked(s.Id, s.StoreId, motivo, by, at, pct, user.Display, DateTime.UtcNow), ct);
    }

    /// <summary>
    /// Quem vê o tipo da ausência (Afastamento médico, Férias...): Admin, RH, Jurídico e Gestor da loja; o Colaborador
    /// (inclusive o Gestor por role de loja olhando a loja do perfil) vê "Afastado".
    /// </summary>
    internal static bool SeesAbsenceType(ICurrentUser? user, Guid storeId) =>
        user is null || user.RoleIn(storeId) is Roles.Admin or Roles.RH or Roles.Juridico or Roles.Gestor;

    /// <summary>
    /// Atualiza as ausências da escala com as atuais (editável: fotografia + avaliação gravada; o chamador salva)
    /// e devolve as do horizonte.
    /// </summary>
    public static async Task<IReadOnlyList<AbsenceEntry>> RefreshAbsencesAsync(Schedule s, AbsenceSync sync, ScheduleEvaluation evaluation, CancellationToken ct)
    {
        var result = await sync.SyncAsync(s, ct);
        if (result.SnapshotChanged) evaluation.EvaluateAndStore(s);
        return result.Absences;
    }

    /// <param name="absences">Ausências atuais (null = as da fotografia).</param>
    /// <param name="user">Usuário da requisição (máscara do tipo da ausência para o Colaborador).</param>
    /// <param name="ack">Última edição salva com inconformidades aceitas (aviso persistido; null = nenhuma).</param>
    public static ScheduleDetail Detail(Schedule s, ScheduleEvaluation evaluation, IReadOnlyList<AbsenceEntry>? absences = null, ICurrentUser? user = null,
        ScheduleEditAck? ack = null)
    {
        var snapshot = JsonSerializer.Deserialize<PlanningSnapshot>(s.SnapshotJson, Json.Options);
        absences ??= snapshot?.Employees
            .SelectMany(e => AbsenceSync.Absences(e, s.StartDate, s.EndDate).Select(a => new AbsenceEntry(e.EmployeeId, a.Date, a.Tipo)))
            .ToList() ?? [];
        var typed = SeesAbsenceType(user, s.StoreId);
        var ausencias = absences
            .Select(a => new AbsenceCell(a.EmployeeId, a.Date, typed ? a.Tipo : null, typed ? UnavailabilityKinds.Rotulo(a.Tipo) : UnavailabilityKinds.RotuloGenerico))
            .ToList();
        var absent = absences.Select(a => (a.EmployeeId, a.Date)).ToHashSet();
        var conflicts = s.Assignments.Where(a => !a.Folga && a.ShiftId is not null && absent.Contains((a.EmployeeId, a.Date)))
            .GroupBy(a => a.EmployeeId)
            .Select(g => new AbsenceConflict(g.Key, g.First().EmployeeName, g.Select(a => a.Date).Distinct().Order().ToList()))
            .OrderBy(c => c.EmployeeName)
            .ToList();
        List<DeficitDto> deficits = s.DeficitsJson is null
            ? []
            : JsonSerializer.Deserialize<List<DeficitDto>>(s.DeficitsJson, Json.Options) ?? [];
        // Escalas antigas (sem avaliação gravada) são avaliadas na hora, sem gravar
        var conformidade = ScheduleEvaluation.Stored(s) ?? (snapshot is null ? null : evaluation.Evaluate(s, snapshot));
        var manual = s.Assignments.Any(a => a.Origem == CellOrigin.Manual || ScheduleCells.HasManualBreak(a));
        var ruleCodes = snapshot is null
            ? []
            : snapshot.ComplianceRules is { Count: > 0 } rules
                ? rules.Where(r => r.Status == RuleStatus.Ativo).Select(r => r.Chave).Distinct().Order().ToList()
                : snapshot.Rules?.Select(r => r.Code).ToList() ?? [];
        var pontuacao = snapshot?.FolgaDupla?.GroupBy(f => f.EmployeeId).ToDictionary(g => g.Key, g => g.First());
        var folgaDupla = FolgaDuplaNoMes(s, snapshot);
        var folgasFixas = FolgasFixas(s, snapshot);
        return new ScheduleDetail(
            Summary(s, manual) with { ConformidadePercentual = s.ConformidadePercentual ?? conformidade?.Percentual },
            snapshot?.Employees.OrderBy(e => e.Name).Select(e => new EmployeeColumn(e.EmployeeId, e.Name, e.Registration, e.SundayRule,
                snapshot.Operacao is null ? null : e.Jornada?.PerfilJornada,
                pontuacao is null ? null : pontuacao.GetValueOrDefault(e.EmployeeId)?.MesesSemFolgaDupla ?? 0,
                pontuacao?.GetValueOrDefault(e.EmployeeId)?.UltimaFolgaDupla,
                folgaDupla?.GetValueOrDefault(e.EmployeeId),
                folgasFixas?.Pessoas.FirstOrDefault(p => p.EmployeeId == e.EmployeeId) is { } fixa ? fixa.Dias ?? "" : null)).ToList() ?? [],
            snapshot?.Shifts.OrderBy(x => x.Start).Select(x => new ShiftColumn(x.ShiftId, x.Code, x.Start, x.End, x.BreakMinutes, ScheduleCells.PaidMinutes(x),
                x.Tipo, x.Gerado)).ToList() ?? [],
            s.Assignments.OrderBy(a => a.Date).ThenBy(a => a.EmployeeName).ToList(),
            deficits,
            ruleCodes,
            s.SolverStatus, s.SolveSeconds, conformidade, manual,
            snapshot?.Occupied?.Select(o => new OccupiedColumn(o.EmployeeId, o.Date, o.SectorId, o.SectorName, o.Start, o.End)).ToList() ?? [],
            ausencias, conflicts,
            snapshot?.Holidays.Where(h => h.Date >= s.StartDate && h.Date <= s.EndDate).OrderBy(h => h.Date)
                .Select(h => new HolidayColumn(h.Date, h.Name, h.WorkAuthorized, h.Abrangencia)).ToList() ?? [],
            WeekRegimes(s, snapshot),
            s.DiagnosticoJson is null ? null : JsonSerializer.Deserialize<InfeasibilityDiagnosis>(s.DiagnosticoJson, Json.Options),
            snapshot?.Operacao,
            snapshot?.HistoryNotices ?? [],
            Solutions(s)?.Total,
            AckView(ack, conformidade),
            folgasFixas);
    }

    /// <summary>
    /// Folgas fixas e intercaladas (setor com exatamente 2 colaboradores disponíveis e a opção ligada na operação da
    /// fotografia): o padrão de cada pessoa observado nas células atuais (<see cref="PlanningContext.FixedDaysOffViews"/>).
    /// null = não se aplica ou fotografia ilegível; Dias null = não avaliado (Rascunho, em geração).
    /// </summary>
    private static FixedDaysOffDto? FolgasFixas(Schedule s, PlanningSnapshot? snapshot)
    {
        if (snapshot is null || s.SectorId is null || s.EndDate < s.StartDate) return null;
        try
        {
            var ctx = PlanningContext.Create(snapshot, s.StartDate, s.EndDate);
            if (ctx.FixedDaysOffPairs.Count == 0) return null;
            var evaluated = s.Status is ScheduleStatus.Generated or ScheduleStatus.Approved or ScheduleStatus.Published or ScheduleStatus.Superseded;
            var views = evaluated ? ctx.FixedDaysOffViews(ScheduleCells.WorkCells(s.Assignments)).ToDictionary(v => v.EmployeeId) : null;
            var people = ctx.FixedDaysOffPairs.SelectMany(p => p.People)
                .Select(e => views?.GetValueOrDefault(e.Snapshot.EmployeeId) is { } v
                    ? new FixedDaysOffPersonDto(v.EmployeeId, v.Nome, v.Texto, v.DomingosDeFolga)
                    : new FixedDaysOffPersonDto(e.Snapshot.EmployeeId, e.Snapshot.Name, null, 0))
                .ToList();
            return new FixedDaysOffDto(string.Join(", ", ctx.FixedDaysOffPairs.Select(p => ctx.Sectors[p.Sector].Name).Distinct()), people);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Última edição salva com inconformidades aceitas, para o aviso persistido na tela da escala.</summary>
    internal static Task<ScheduleEditAck?> LatestAckAsync(SchedulingDbContext db, Guid scheduleId, CancellationToken ct) =>
        db.EditAcks.AsNoTracking().Where(a => a.ScheduleId == scheduleId).OrderByDescending(a => a.At).FirstOrDefaultAsync(ct);

    /// <summary>Aviso da edição com as inconformidades aceitas, marcando as que ainda aparecem na avaliação atual.</summary>
    private static EditAckView? AckView(ScheduleEditAck? ack, ComplianceResult? current)
    {
        if (ack is null) return null;
        List<AcceptedViolationDto> list;
        try
        {
            list = JsonSerializer.Deserialize<List<AcceptedViolationDto>>(ack.ViolacoesJson, Json.Options) ?? [];
        }
        catch (JsonException)
        {
            list = [];
        }
        var now = (current?.Violacoes ?? []).Select(v => CellEdits.Key(v.Chave, v.EmployeeId, v.Dates)).ToHashSet();
        var items = list.Select(v => new EditAckViolation(v.Chave, v.RegraDescricao, v.Severidade, v.EmployeeId, v.EmployeeName, v.Dates, v.Detalhe,
            current is not null && now.Contains(CellEdits.Key(v.Chave, v.EmployeeId, v.Dates)))).ToList();
        return new EditAckView(ack.Id, ack.By, ack.At, ack.Status, ack.CellCount, ack.PercentualAntes, ack.PercentualDepois, items,
            items.Count(i => i.Pendente));
    }

    /// <summary>Resumo das soluções do motor gravado na escala (null = sem registro ou ilegível).</summary>
    internal static EngineSolutionsSummary? Solutions(Schedule s)
    {
        if (string.IsNullOrEmpty(s.SolucoesMotorJson)) return null;
        try
        {
            return JsonSerializer.Deserialize<EngineSolutionsSummary>(s.SolucoesMotorJson, Json.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Folga dupla de cada colaborador nas células atuais da escala (dois dias seguidos de folga de descanso dentro do
    /// período: <see cref="FolgaDuplaHistory.InPeriod"/>). null = não avaliado: Rascunho (célula vazia é "sem definição"),
    /// em geração, sem quadro, escala antiga por loja ou fotografia ilegível.
    /// </summary>
    private static Dictionary<Guid, DateOnly?>? FolgaDuplaNoMes(Schedule s, PlanningSnapshot? snapshot)
    {
        if (snapshot is null || s.SectorId is null || s.EndDate < s.StartDate
            || s.Status is not (ScheduleStatus.Generated or ScheduleStatus.Approved or ScheduleStatus.Published or ScheduleStatus.Superseded))
            return null;
        try
        {
            return FolgaDuplaHistory.InPeriod(PlanningContext.Create(snapshot, s.StartDate, s.EndDate), ScheduleCells.WorkCells(s.Assignments));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Regime por semana calculado da fotografia (escala antiga por loja ou fotografia ilegível: vazio).</summary>
    private static IReadOnlyList<WeekRegimeDto> WeekRegimes(Schedule s, PlanningSnapshot? snapshot)
    {
        if (snapshot is null || s.EndDate < s.StartDate) return [];
        try
        {
            // Semanas civis do horizonte (na escala do mês, as parciais das pontas aparecem inteiras)
            return PlanningContext.Create(snapshot, s.StartDate, s.EndDate).WeekRegimes();
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }

    public static void MapScheduleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/schedules").RequireAuthorization();

        // Lista (as 200 mais recentes). Opcional: inicio/fim = só as escalas cujo período cruza [inicio, fim] (visão do mês)
        group.MapGet("/", async (Guid? storeId, Guid? sectorId, DateOnly? inicio, DateOnly? fim, SchedulingDbContext db, ICurrentUser user) =>
        {
            if (inicio is { } i0 && fim is { } f0)
            {
                if (f0 < i0) return ServiceDefaults.ValidationError("fim", "A data final é anterior à inicial.");
                if (f0.DayNumber - i0.DayNumber > 366) return ServiceDefaults.ValidationError("fim", "Informe um período de no máximo 366 dias.");
            }
            var query = db.Schedules.AsNoTracking();
            // Gestor/Colaborador: a loja informada precisa estar entre as permitidas (403); sem loja, todas as permitidas
            if (storeId is { } sid)
            {
                if (!user.CanAccessStore(sid)) return Results.Forbid();
                query = query.Where(s => s.StoreId == sid);
            }
            else if (!user.HasGlobalScope)
            {
                var permitidas = user.StoreIds.ToList();
                query = query.Where(s => permitidas.Contains(s.StoreId));
            }
            if (sectorId is { } sec) query = query.Where(s => s.SectorId == sec);
            // Período cruza [inicio, fim] (EndDate gravado: escala do mês ou antiga por semanas)
            if (fim is { } f) query = query.Where(s => s.StartDate <= f);
            if (inicio is { } i) query = query.Where(s => s.EndDate >= i);
            var list = await query.OrderByDescending(s => s.RequestedAt).Take(200)
                .Select(s => new
                {
                    Schedule = s,
                    Manual = db.Assignments.Any(a => a.ScheduleId == s.Id && (a.Origem == CellOrigin.Manual || (!a.Folga && a.BreakOrigem == CellOrigin.Manual)))
                })
                .ToListAsync();
            return Results.Ok(list.Select(x => Summary(x.Schedule, x.Manual)));
        });

        // Escalas afetadas por um afastamento (a tela pergunta, depois de salvar a ausência, se deseja regerar):
        // não substituídas, da loja, com período que cruza o do afastamento e do(s) setor(es) do colaborador — pelo
        // cadastro ATUAL no Employee.Api (inclui escalas em que ele ainda não tem células) ou pela fotografia da escala.
        // Consulta: Admin, RH e Gestor da loja (quem registra ausências). Regerar: Admin e Gestor da loja (PodeRegerar).
        group.MapGet("/afetadas", async (Guid employeeId, DateOnly inicio, DateOnly fim, Guid? storeId, SchedulingDbContext db,
            ICurrentUser user, PlanningClients clients, AbsenceSync sync, ScheduleEvaluation evaluation, ILogger<AbsenceSync> logger,
            CancellationToken ct) =>
        {
            if (user.Role is not (Roles.Admin or Roles.RH or Roles.Gestor)) return Results.Forbid();
            if (fim < inicio) return ServiceDefaults.ValidationError("fim", "A data final é anterior à inicial.");
            if (fim.DayNumber - inicio.DayNumber > 730) return ServiceDefaults.ValidationError("fim", "Informe um período de no máximo 730 dias.");

            // Loja: a informada, a única loja do Gestor ou a da escala mais recente que tem o colaborador na fotografia
            var marker = JsonSerializer.Serialize(new { employees = new[] { new { employeeId } } });
            var store = storeId ?? (user.Role == Roles.Gestor && user.ManagedStoreIds.Count == 1 ? user.ManagedStoreIds.First() : (Guid?)null)
                ?? await db.Schedules.AsNoTracking().Where(s => EF.Functions.JsonContains(s.SnapshotJson, marker))
                    .OrderByDescending(s => s.RequestedAt).Select(s => (Guid?)s.StoreId).FirstOrDefaultAsync(ct);
            if (store is not { } sid)
                return Results.Ok(new AffectedSchedulesResponse(employeeId, null, inicio, fim, false, null, []));
            // Papel efetivo na loja (Colaborador com role de loja é Gestor só nas lojas das roles)
            if (user.RoleIn(sid) is not (Roles.Admin or Roles.RH or Roles.Gestor)) return Results.Forbid();

            // Setores atuais do colaborador (Employee.Api fora do ar: só a fotografia das escalas decide)
            var sectors = new HashSet<Guid>();
            try
            {
                var employee = (await clients.GetEmployeesAsync(sid, inicio, fim, ct)).FirstOrDefault(e => e.EmployeeId == employeeId);
                if (employee is not null) sectors.UnionWith(employee.SectorIds);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("Setores do colaborador {EmployeeId} indisponíveis no Employee.Api ({Motivo}); usando as fotografias",
                    employeeId, $"{ex.GetType().Name}: {ex.Message}");
            }

            var earliest = inicio.AddDays(-7 * 52);
            var live = (await db.Schedules
                    .Where(s => s.StoreId == sid && s.SectorId != null && s.Status != ScheduleStatus.Superseded
                                && s.StartDate <= fim && s.StartDate >= earliest)
                    .ToListAsync(ct))
                .Where(s => s.EndDate >= inicio)
                .ToList();
            var affected = live
                .Select(s => (Schedule: s, InSnapshot: ScheduleCells.ReadSnapshot(s).Employees.Any(e => e.EmployeeId == employeeId)))
                .Where(x => x.InSnapshot || sectors.Contains(x.Schedule.SectorId!.Value))
                .ToList();

            var rows = new List<AffectedSchedule>();
            foreach (var (s, inSnapshot) in affected)
            {
                await db.Entry(s).Collection(x => x.Assignments).LoadAsync(ct);
                // Escala editável com o colaborador: já passa a usar o afastamento (percentual atualizado na lista)
                if (s.IsEditable && inSnapshot) await RefreshAbsencesAsync(s, sync, evaluation, ct);
                var from = inicio > s.StartDate ? inicio : s.StartDate;
                var to = fim < s.EndDate ? fim : s.EndDate;
                var dias = Enumerable.Range(0, to.DayNumber - from.DayNumber + 1).Select(from.AddDays).ToList();
                var turnos = s.Assignments.Count(a => a.EmployeeId == employeeId && !a.Folga && a.ShiftId is not null && a.Date >= from && a.Date <= to);
                var newer = live.Where(o => o.SectorId == s.SectorId && o.StartDate == s.StartDate && o.EndDate == s.EndDate && o.Version > s.Version)
                    .OrderByDescending(o => o.Version).FirstOrDefault();
                // Aprovada também gera na própria escala (a aprovação é desfeita); publicada gera nova versão
                var acao = s.Status == ScheduleStatus.Generating ? null
                    : s.IsEditable || s.CanRevokeApproval ? "gerar"
                    : s.Status == ScheduleStatus.Published ? "nova_versao" : null;
                rows.Add(new AffectedSchedule(s.Id, s.SectorId!.Value, s.SectorName ?? Schedule.AllSectors, s.StartDate, s.EndDate, s.Weeks,
                    s.Version, s.Status, s.ConformidadePercentual, s.Assignments.Any(a => a.Origem == CellOrigin.Manual || ScheduleCells.HasManualBreak(a)),
                    s.IsEditable, acao, inSnapshot, dias, turnos, newer?.Id));
            }
            if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);

            var pode = CanManage(user, sid);
            return Results.Ok(new AffectedSchedulesResponse(employeeId, sid, inicio, fim, pode,
                pode ? null : "Seu perfil não gera escalas: peça ao gestor da loja para regerar as escalas listadas.",
                rows.OrderBy(r => r.StartDate).ThenBy(r => r.SectorName).ThenBy(r => r.Version).ToList()));
        });

        // Antes de gerar: outras escalas do setor (vivas ou em geração) que cruzam o período. Havendo, a tela pergunta se
        // a nova deve ser a principal (campo "principal" do POST /schedules, /{id}/generate e /{id}/regenerate).
        // excluirId = a própria escala (gerar na própria escala).
        group.MapGet("/sobreposicao", async (Guid storeId, Guid sectorId, DateOnly inicio, DateOnly fim, Guid? excluirId,
            SchedulingDbContext db, ICurrentUser user, CancellationToken ct) =>
        {
            if (fim < inicio) return ServiceDefaults.ValidationError("fim", "A data final é anterior à inicial.");
            if (fim.DayNumber - inicio.DayNumber > 366) return ServiceDefaults.ValidationError("fim", "Informe um período de no máximo 366 dias.");
            if (!CanAccess(user, storeId)) return Results.Forbid();
            var list = await PrincipalSchedules.OverlappingAsync(db, storeId, sectorId, inicio, fim, excluirId, ct);
            var ids = list.Select(s => s.Id).ToList();
            var manual = (await db.Assignments.AsNoTracking()
                    .Where(a => ids.Contains(a.ScheduleId) && (a.Origem == CellOrigin.Manual || (!a.Folga && a.BreakOrigem == CellOrigin.Manual)))
                    .Select(a => a.ScheduleId).Distinct().ToListAsync(ct))
                .ToHashSet();
            var escalas = list.Select(s => Summary(s, manual.Contains(s.Id))).ToList();
            return Results.Ok(new OverlapResponse(storeId, sectorId, inicio, fim, escalas,
                escalas.FirstOrDefault(s => s.Principal && s.Status != ScheduleStatus.Generating)));
        });

        // Tornar a escala a PRINCIPAL do setor no período (Gestor da loja/Admin): desmarca as outras principais do setor
        // que se sobrepõem. A continuidade das escalas geradas depois passa a se basear nela. Auditoria: SchedulePrincipalChanged.
        group.MapPost("/{id:guid}/principal", async (Guid id, SchedulingDbContext db, ICurrentUser user, IPublishEndpoint bus,
            ILogger<ScheduleRequester> logger, CancellationToken ct) =>
        {
            var s = await db.Schedules.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (s is null) return Results.NotFound();
            if (!CanManage(user, s.StoreId)) return Results.Forbid();
            if (s.SectorId is null)
                return Results.Problem($"Escala antiga por loja ({Schedule.AllSectors}) não pode ser a principal.", statusCode: 409);
            if (s.Status == ScheduleStatus.Generating)
                return Results.Problem("A escala está sendo gerada agora. Aguarde terminar para torná-la a principal.", statusCode: 409);
            if (!s.CanBePrincipal)
                return Results.Problem(
                    $"Só escalas em Rascunho, Gerada, Aprovada ou Publicada podem ser a principal (status atual: {s.Status}).", statusCode: 409);
            var manual = await db.Assignments.AnyAsync(a => a.ScheduleId == s.Id && a.Origem == CellOrigin.Manual, ct);
            if (s.Principal) return Results.Ok(Summary(s, manual));

            var unmarked = await PrincipalSchedules.MarkAsync(db, s, user.Display, ct);
            await bus.Publish(PrincipalSchedules.Changed(s, "manual", unmarked, user.Display), ct);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Escala {ScheduleId} v{Version} ({Status}) do setor {Sector} agora é a principal ({Count} desmarcada(s)) por {User}",
                s.Id, s.Version, s.Status, s.SectorName, unmarked.Count, user.Display);
            return Results.Ok(Summary(s, manual));
        }).RequireAuthorization(Policies.Manager);

        // Detalhe com as ausências ATUAIS: na escala editável um afastamento lançado depois da geração entra na
        // fotografia e na avaliação (gravadas aqui); nas aprovadas/publicadas vira o alerta escaladosEmAfastamento.
        group.MapGet("/{id:guid}", async (Guid id, SchedulingDbContext db, ICurrentUser user, ScheduleEvaluation evaluation,
            AbsenceSync sync, CancellationToken ct) =>
        {
            var s = await db.Schedules.Include(x => x.Assignments).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (s is null) return Results.NotFound();
            if (!CanAccess(user, s.StoreId)) return Results.Forbid();
            // Durante a geração a fotografia é a do pedido ao Worker: não muda
            var absences = s.Status == ScheduleStatus.Generating ? null : await RefreshAbsencesAsync(s, sync, evaluation, ct);
            if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
            return Results.Ok(Detail(s, evaluation, absences, user, await LatestAckAsync(db, s.Id, ct)));
        });

        // Opções de escala avaliadas pelo motor (para decidir pesos/critérios de exclusão) e avisos da semana anterior
        group.MapGet("/{id:guid}/solucoes", async (Guid id, SchedulingDbContext db, ICurrentUser user, CancellationToken ct) =>
        {
            var s = await db.Schedules.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (s is null) return Results.NotFound();
            if (!CanAccess(user, s.StoreId)) return Results.Forbid();
            PlanningSnapshot? snapshot = null;
            try
            {
                snapshot = JsonSerializer.Deserialize<PlanningSnapshot>(s.SnapshotJson, Json.Options);
            }
            catch (JsonException)
            {
                // fotografia ilegível: sem avisos
            }
            return Results.Ok(new EngineSolutionsResponse(s.Id, ResultOf(s), Solutions(s), snapshot?.HistoryNotices ?? []));
        });

        group.MapGet("/{id:guid}/conformidade", async (Guid id, SchedulingDbContext db, ICurrentUser user, ScheduleEvaluation evaluation,
            AbsenceSync sync, CancellationToken ct) =>
        {
            var s = await db.Schedules.Include(x => x.Assignments).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (s is null) return Results.NotFound();
            if (!CanAccess(user, s.StoreId)) return Results.Forbid();
            if (s.Status != ScheduleStatus.Generating) await RefreshAbsencesAsync(s, sync, evaluation, ct);
            if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
            return Results.Ok(ScheduleEvaluation.Stored(s) ?? evaluation.Evaluate(s));
        });

        // Opcional: troca as regras congeladas na fotografia pelas regras atuais do cadastro e reavalia
        group.MapPost("/{id:guid}/conformidade/reavaliar", async (Guid id, SchedulingDbContext db, ICurrentUser user,
            PlanningClients clients, ScheduleEvaluation evaluation, CancellationToken ct) =>
        {
            var s = await db.Schedules.Include(x => x.Assignments).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (s is null) return Results.NotFound();
            if (!CanManage(user, s.StoreId)) return Results.Forbid();
            if (!s.IsEditable) return NotEditable(s);
            var (horizonFrom, horizonTo) = ScheduleCells.Horizon(s);
            var snapshot = ScheduleCells.ReadSnapshot(s) with { ComplianceRules = await clients.GetRulesAsync(horizonFrom, horizonTo, ct), Rules = null };
            s.SnapshotJson = JsonSerializer.Serialize(snapshot, Json.Options);
            var result = evaluation.EvaluateAndStore(s, snapshot);
            s.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(result);
        }).RequireAuthorization(Policies.Manager);

        // Escala nova: sempre do mês civil de startDate (o fim explícito é só da nova versão, /{id}/regenerate)
        group.MapPost("/", (RequestScheduleRequest req, ScheduleRequester requester, ICurrentUser user, CancellationToken ct) =>
            requester.RequestAsync(req with { EndDate = null }, null, user, ct)).RequireAuthorization(Policies.Manager);

        group.MapPost("/manual", (RequestScheduleRequest req, ScheduleRequester requester, ICurrentUser user, CancellationToken ct) =>
            requester.CreateManualAsync(req with { EndDate = null }, user, ct)).RequireAuthorization(Policies.Manager);

        // Conferência da edição ANTES de salvar (nada é gravado): inconformidades que as células alteradas criariam. A tela
        // pergunta "Deseja salvar assim mesmo?" quando há alguma e a escala pede confirmação (todas menos o Rascunho).
        group.MapPost("/{id:guid}/cells/validar", async (Guid id, UpdateCellsRequest req, SchedulingDbContext db, ICurrentUser user,
            ScheduleEvaluation evaluation, AbsenceSync sync, CancellationToken ct) =>
        {
            var s = await db.Schedules.Include(x => x.Assignments).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (s is null) return Results.NotFound();
            if (!CanManage(user, s.StoreId)) return Results.Forbid();
            if (!s.CanEditCells) return NotEditable(s);
            if (CellEdits.Stale(s, req.Revisao) is { } stale) return stale;
            var cells = req.Cells ?? [];
            if (cells.Count == 0) return ServiceDefaults.ValidationError("cells", "Informe ao menos uma célula.");
            await RefreshAbsencesAsync(s, sync, evaluation, ct);
            var snapshot = ScheduleCells.ReadSnapshot(s);
            if (CellEdits.Invalid(s, snapshot, cells) is { } invalid) return invalid;
            var before = evaluation.Evaluate(s, snapshot);
            CellEdits.Apply(db, s, snapshot, cells);
            var after = evaluation.Evaluate(s, snapshot);
            // Só a conferência: as células aplicadas no contexto não são gravadas (a atualização de ausências da escala
            // editável fica para o GET/salvar)
            return Results.Ok(CellEdits.Check(s, before, after));
        }).RequireAuthorization(Policies.Manager);

        // Salvar a edição das células (turno/folga). Também na Aprovada e na Publicada: o status NÃO muda (a aprovada continua
        // aprovada, a publicada continua o registro oficial); a edição é auditada (ScheduleCellsEdited com o status). Com
        // inconformidade nova (fora do Rascunho), só salva com salvarComInconformidades = true — e então registra quem aceitou
        // (ScheduleEditAck, mostrado na escala) e publica ScheduleSavedWithViolations para a auditoria. Revisão diferente da
        // atual (escala mudou nesse meio tempo): 409 sem salvar.
        group.MapPut("/{id:guid}/cells", async (Guid id, UpdateCellsRequest req, SchedulingDbContext db, ICurrentUser user,
            ScheduleEvaluation evaluation, IPublishEndpoint bus, AbsenceSync sync, ILogger<ScheduleRequester> logger, CancellationToken ct) =>
        {
            var s = await db.Schedules.Include(x => x.Assignments).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (s is null) return Results.NotFound();
            if (!CanManage(user, s.StoreId)) return Results.Forbid();
            if (!s.CanEditCells) return NotEditable(s);
            if (CellEdits.Stale(s, req.Revisao) is { } stale) return stale;
            var cells = req.Cells ?? [];
            if (cells.Count == 0) return ServiceDefaults.ValidationError("cells", "Informe ao menos uma célula.");

            // Avalia com as ausências atuais (turno em dia de afastamento = violação "indisponibilidade")
            var absences = await RefreshAbsencesAsync(s, sync, evaluation, ct);
            var snapshot = ScheduleCells.ReadSnapshot(s);
            if (CellEdits.Invalid(s, snapshot, cells) is { } invalid) return invalid;

            var before = evaluation.Evaluate(s, snapshot);
            CellEdits.Apply(db, s, snapshot, cells);
            var after = evaluation.Evaluate(s, snapshot);
            var check = CellEdits.Check(s, before, after);
            if (check.ConfirmacaoNecessaria && req.SalvarComInconformidades != true)
            {
                // Nada é gravado: a tela mostra a lista e pergunta se deseja salvar assim mesmo
                return Results.Problem(
                    $"A alteração gera {check.Novas.Count} inconformidade(s). Confirme para salvar assim mesmo.",
                    statusCode: 409, extensions: new Dictionary<string, object?> { ["code"] = "inconformidades", ["conferencia"] = check });
            }

            // Aprovada/Publicada: edição manual autorizada (status, período e fotografia não mudam)
            if (!s.IsEditable) db.AllowCellEditAfterApproval(s.Id);
            var now = DateTime.UtcNow;
            s.CellsRevision++;
            s.UpdatedAt = now;
            var result = evaluation.EvaluateAndStore(s, snapshot);
            await bus.Publish(new ScheduleCellsEdited(s.Id, cells.Count, "Manual", result.Percentual, user.Display, now, s.Status.ToString()), ct);
            ScheduleEditAck? ack = null;
            if (check.ConfirmacaoNecessaria)
            {
                var accepted = check.Novas.Select(v => new AcceptedViolationDto(v.Chave, v.RegraDescricao, v.Severidade, v.EmployeeId, v.EmployeeName,
                    v.Dates, v.Detalhe)).ToList();
                ack = new ScheduleEditAck
                {
                    ScheduleId = s.Id, By = user.Display, At = now, Status = s.Status, CellCount = cells.Count, CellsRevision = s.CellsRevision,
                    PercentualAntes = before.Percentual, PercentualDepois = result.Percentual,
                    ViolacoesJson = JsonSerializer.Serialize(accepted, Json.Options)
                };
                db.EditAcks.Add(ack);
                await bus.Publish(new ScheduleSavedWithViolations(s.Id, s.StoreId, s.SectorId, s.SectorName ?? Schedule.AllSectors, s.StartDate, s.EndDate,
                    s.Version, s.Status.ToString(), cells.Count, before.Percentual, result.Percentual, accepted, user.Display, now), ct);
            }
            await db.SaveChangesAsync(ct);
            if (ack is not null)
                // Sem o texto das inconformidades no log (nomes de colaboradores): só as regras
                logger.LogInformation("Escala {ScheduleId} v{Version} ({Status}) salva por {User} com {Count} inconformidade(s) aceita(s): {Rules}",
                    s.Id, s.Version, s.Status, user.Display, check.Novas.Count, string.Join(", ", check.Novas.Select(v => v.Chave).Distinct()));
            return Results.Ok(Detail(s, evaluation, absences, user, ack ?? await LatestAckAsync(db, s.Id, ct)));
        }).RequireAuthorization(Policies.Manager);

        // Gera na própria escala: Rascunho/Gerada/Inviável/Falhou e também Aprovada — nesta a aprovação é desfeita
        // (ScheduleApprovalRevoked) e a escala volta a precisar de aprovação. Publicada: nova versão (/regenerate).
        group.MapPost("/{id:guid}/generate", async (Guid id, [FromBody] GenerateRequest? req, SchedulingDbContext db,
            ScheduleRequester requester, ICurrentUser user, IPublishEndpoint bus, CancellationToken ct) =>
        {
            var s = await db.Schedules.Include(x => x.Assignments).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (s is null) return Results.NotFound();
            if (!CanManage(user, s.StoreId)) return Results.Forbid();
            if (!s.IsEditable && !s.CanRevokeApproval) return NotEditable(s);
            var mode = string.IsNullOrWhiteSpace(req?.Modo) ? GenerationModes.Completa : req!.Modo!.Trim().ToLowerInvariant();
            if (!GenerationModes.IsValid(mode))
                return ServiceDefaults.ValidationError("modo", $"Modo inválido. Use \"{GenerationModes.Completa}\" ou \"{GenerationModes.SomenteVazios}\".");
            if (requester.InvalidTime(req?.TempoLimiteSegundos) is { } badTime) return badTime;
            if (s.CanRevokeApproval) await RevokeApprovalAsync(s, "regerar", user, bus, ct);
            return await requester.GenerateInPlaceAsync(s, mode, user, ct, req?.TempoLimiteSegundos, req?.Principal);
        }).RequireAuthorization(Policies.Manager);

        // Cancelar a geração em andamento (na fila ou calculando): Gestor da loja/Admin, como quem gera. A escala volta na
        // hora ao estado anterior (escala nova: Cancelada, sem quadro; gerar na própria escala: status e células de antes —
        // a aprovada volta como Gerada, a aprovação já tinha sido desfeita), o pedido de virar a principal cai e o Worker
        // interrompe o CP-SAT (ou descarta o comando ainda na fila); resultado de geração cancelada é ignorado.
        // Auditoria: ScheduleGenerationCancelled.
        group.MapPost("/{id:guid}/cancelar-geracao", (Guid id, ScheduleRequester requester, ICurrentUser user, CancellationToken ct) =>
            requester.CancelGenerationAsync(id, user, ct)).RequireAuthorization(Policies.Manager);

        // Reabrir para edição: Aprovada → Gerada (aprovação desfeita; precisa ser aprovada de novo). Células intactas.
        group.MapPost("/{id:guid}/reabrir", async (Guid id, SchedulingDbContext db, ICurrentUser user, IPublishEndpoint bus,
            ScheduleEvaluation evaluation, AbsenceSync sync, CancellationToken ct) =>
        {
            var s = await db.Schedules.Include(x => x.Assignments).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (s is null) return Results.NotFound();
            if (!CanManage(user, s.StoreId)) return Results.Forbid();
            if (!s.CanRevokeApproval)
                return s.IsEditable
                    ? Results.Problem("A escala já está aberta para edição.", statusCode: 409)
                    : NotEditable(s);
            await RevokeApprovalAsync(s, "reabrir", user, bus, ct);
            // Grava a revogação antes de mexer na fotografia (as células de uma aprovada são imutáveis)
            await db.SaveChangesAsync(ct);
            // De volta a editável: passa a usar as ausências atuais (como qualquer escala em edição)
            var absences = await RefreshAbsencesAsync(s, sync, evaluation, ct);
            if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
            return Results.Ok(Detail(s, evaluation, absences, user));
        }).RequireAuthorization(Policies.Manager);

        // Título/observação. Período e setor não mudam (para isso, gere outra escala). Substituída é histórico.
        group.MapPatch("/{id:guid}", async (Guid id, UpdateScheduleRequest req, SchedulingDbContext db, ICurrentUser user,
            IPublishEndpoint bus, CancellationToken ct) =>
        {
            var s = await db.Schedules.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (s is null) return Results.NotFound();
            if (!CanManage(user, s.StoreId)) return Results.Forbid();
            if (s.Status == ScheduleStatus.Superseded)
                return Results.Problem("Escala substituída é histórico: título e observação não mudam.", statusCode: 409);
            var titulo = req.Titulo?.Trim();
            var observacao = req.Observacao?.Trim();
            if (titulo is { Length: > Schedule.TituloMax })
                return ServiceDefaults.ValidationError("titulo", $"O título tem no máximo {Schedule.TituloMax} caracteres.");
            if (observacao is { Length: > Schedule.ObservacaoMax })
                return ServiceDefaults.ValidationError("observacao", $"A observação tem no máximo {Schedule.ObservacaoMax} caracteres.");
            if (req.Titulo is null && req.Observacao is null)
                return ServiceDefaults.ValidationError("titulo", "Informe o título e/ou a observação.");

            var tituloAnterior = s.Titulo;
            var observacaoAnterior = s.Observacao;
            if (req.Titulo is not null) s.Titulo = titulo!.Length == 0 ? null : titulo;
            if (req.Observacao is not null) s.Observacao = observacao!.Length == 0 ? null : observacao;
            if (s.Titulo == tituloAnterior && s.Observacao == observacaoAnterior)
                return Results.Ok(Summary(s, await db.Assignments.AnyAsync(a => a.ScheduleId == s.Id && a.Origem == CellOrigin.Manual, ct)));
            s.UpdatedAt = DateTime.UtcNow;
            await bus.Publish(new ScheduleInfoEdited(s.Id, tituloAnterior, s.Titulo, s.Observacao != observacaoAnterior, user.Display, DateTime.UtcNow), ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(Summary(s, await db.Assignments.AnyAsync(a => a.ScheduleId == s.Id && a.Origem == CellOrigin.Manual, ct)));
        }).RequireAuthorization(Policies.Manager);

        // Excluir: Gestor (própria loja) e Admin. Publicada não, para ninguém (registro oficial: gere nova versão e
        // publique — a antiga vira Substituída); Aprovada e Substituída só o Admin (superAdmin da intranet): os demais
        // recebem 403 com o motivo; em geração, aguarde. Remove células e sugestões; versões que apontavam para ela como
        // "pai" ficam sem pai. Auditoria recebe o resumo com o status da escala excluída (ScheduleDeleted). Excluir a
        // principal não elege outra: as escalas geradas depois voltam à prioridade do status.
        group.MapDelete("/{id:guid}", async (Guid id, SchedulingDbContext db, ICurrentUser user, IPublishEndpoint bus,
            ILogger<ScheduleRequester> logger, CancellationToken ct) =>
        {
            var s = await db.Schedules.Include(x => x.Assignments).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (s is null) return Results.NotFound();
            if (!CanManage(user, s.StoreId)) return Results.Forbid();
            if (s.Status == ScheduleStatus.Published)
                return Results.Problem(
                    "Escala publicada não pode ser excluída: é o registro oficial da equipe. Para trocá-la, gere uma nova versão e publique (a nova substitui esta).",
                    statusCode: 409);
            if (s.Status == ScheduleStatus.Generating)
                return Results.Problem("A escala está sendo gerada agora. Cancele a geração ou aguarde terminar para excluir.", statusCode: 409);
            if (s.Status == ScheduleStatus.Approved && user.Role != Roles.Admin)
                return Results.Problem("Só o superAdmin pode excluir uma escala aprovada.", statusCode: 403);
            if (s.Status == ScheduleStatus.Superseded && user.Role != Roles.Admin)
                return Results.Problem("Escala substituída é histórico: só o superAdmin pode excluí-la.", statusCode: 403);

            var children = await db.Schedules.Where(x => x.ParentScheduleId == s.Id).ToListAsync(ct);
            foreach (var child in children) child.ParentScheduleId = null;
            // Tudo na mesma transação do SaveChanges (com o evento do outbox)
            db.Suggestions.RemoveRange(await db.Suggestions.Where(x => x.ScheduleId == s.Id).ToListAsync(ct));
            var cells = s.Assignments.Count;
            db.Assignments.RemoveRange(s.Assignments);
            db.Schedules.Remove(s);
            await bus.Publish(new ScheduleDeleted(s.Id, s.StoreId, s.StoreName, s.SectorId, s.SectorName ?? Schedule.AllSectors, s.StartDate, s.EndDate,
                s.Weeks, s.Version, s.Status.ToString(), s.ConformidadePercentual, cells, s.Titulo, user.Display, DateTime.UtcNow,
                user.Role, s.ApprovedBy, s.ApprovedAt, s.Principal), ct);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Escala {ScheduleId} v{Version} ({Status}{Principal}) do setor {Sector} excluída por {User} ({Role})",
                s.Id, s.Version, s.Status, s.Principal ? ", principal" : "", s.SectorName, user.Display, user.Role);
            return Results.Ok(new { s.Id, excluida = true, status = s.Status, principal = s.Principal });
        }).RequireAuthorization(Policies.Manager);

        group.MapPost("/{id:guid}/sugestoes", async (Guid id, SchedulingDbContext db, ICurrentUser user, IPublishEndpoint bus,
            IConfiguration cfg, AbsenceSync sync, ScheduleEvaluation evaluation, CancellationToken ct) =>
        {
            var s = await db.Schedules.Include(x => x.Assignments).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (s is null) return Results.NotFound();
            if (!CanManage(user, s.StoreId)) return Results.Forbid();
            if (!s.IsEditable) return NotEditable(s);

            // O Worker corrige com as ausências atuais (tira o turno de quem está afastado)
            await RefreshAbsencesAsync(s, sync, evaluation, ct);
            var suggestion = new ScheduleSuggestion { ScheduleId = s.Id, CellsRevision = s.CellsRevision, RequestedBy = user.Display };
            db.Suggestions.Add(suggestion);
            await bus.Publish(new SuggestCorrectionsCommand(s.Id, suggestion.Id, s.StartDate, s.Weeks, ScheduleCells.ReadSnapshot(s),
                ScheduleCells.ToDtos(s.Assignments), cfg.GetValue("Scheduling:SuggestionTimeLimitSeconds", 30), s.EndDate), ct);
            await db.SaveChangesAsync(ct);
            return Results.Accepted($"/api/schedules/{s.Id}/sugestoes/{suggestion.Id}", new { sugestaoId = suggestion.Id, status = suggestion.Status });
        }).RequireAuthorization(Policies.Manager);

        group.MapGet("/{id:guid}/sugestoes/{sugestaoId:guid}", async (Guid id, Guid sugestaoId, SchedulingDbContext db, ICurrentUser user) =>
        {
            var s = await db.Schedules.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (s is null) return Results.NotFound();
            if (!CanAccess(user, s.StoreId)) return Results.Forbid();
            var sug = await db.Suggestions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sugestaoId && x.ScheduleId == id);
            return sug is null ? Results.NotFound() : Results.Ok(ToResponse(sug, s));
        });

        group.MapPost("/{id:guid}/sugestoes/{sugestaoId:guid}/aplicar", async (Guid id, Guid sugestaoId, [FromBody] ApplySuggestionRequest? req,
            SchedulingDbContext db, ICurrentUser user, ScheduleEvaluation evaluation, IPublishEndpoint bus, AbsenceSync sync, CancellationToken ct) =>
        {
            var s = await db.Schedules.Include(x => x.Assignments).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (s is null) return Results.NotFound();
            if (!CanManage(user, s.StoreId)) return Results.Forbid();
            if (!s.IsEditable) return NotEditable(s);
            var sug = await db.Suggestions.FirstOrDefaultAsync(x => x.Id == sugestaoId && x.ScheduleId == id, ct);
            if (sug is null) return Results.NotFound();
            if (sug.Status != SuggestionStatus.Pronta) return Results.Problem($"A sugestão não está pronta (status {sug.Status}).", statusCode: 409);
            if (sug.AppliedAt is not null) return Results.Problem("A sugestão já foi aplicada.", statusCode: 409);
            // Afastamento lançado depois da sugestão muda a fotografia (CellsRevision): a sugestão fica desatualizada
            var absences = await RefreshAbsencesAsync(s, sync, evaluation, ct);
            if (sug.CellsRevision != s.CellsRevision)
            {
                if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
                return Results.Problem("A escala mudou depois que a sugestão foi calculada. Peça uma nova sugestão.", statusCode: 409);
            }

            var changes = Changes(sug);
            var indices = req?.Alteracoes is { Count: > 0 } chosen ? chosen.Distinct().ToList() : Enumerable.Range(0, changes.Count).ToList();
            if (indices.Any(i => i < 0 || i >= changes.Count))
                return ServiceDefaults.ValidationError("alteracoes", $"Índices válidos: 0 a {changes.Count - 1}.");

            var snapshot = ScheduleCells.ReadSnapshot(s);
            var shifts = snapshot.Shifts.ToDictionary(x => x.ShiftId);
            foreach (var change in indices.Select(i => changes[i]))
            {
                var para = change.Para;
                var content = para.ShiftId is { } sid && shifts.TryGetValue(sid, out var shift) ? new CellContent(shift, false)
                    : para.Folga ? new CellContent(null, true) : null;
                // Célula tocada pela sugestão passa a ser do motor (inclusive as que eram manuais)
                ScheduleCells.Set(db, s, snapshot, change.EmployeeId, change.Date, content, CellOrigin.Motor);
            }
            // Almoços dos dias alterados: reposicionados pelo motor (os ajustados à mão ficam)
            var touched = indices.Select(i => changes[i].Date).ToHashSet();
            ScheduleCells.PlanBreaks(s, snapshot, a => touched.Contains(a.Date) && a.BreakOrigem != CellOrigin.Manual);
            s.CellsRevision++;
            s.UpdatedAt = DateTime.UtcNow;
            sug.AppliedAt = DateTime.UtcNow;
            sug.AppliedBy = user.Display;
            var result = evaluation.EvaluateAndStore(s, snapshot);
            await bus.Publish(new ScheduleCellsEdited(s.Id, indices.Count, "Sugestao", result.Percentual, user.Display, DateTime.UtcNow), ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(Detail(s, evaluation, absences, user));
        }).RequireAuthorization(Policies.Manager);

        // Nova versão (publicada, inviável, falha...): sobrepõe a escala de origem, então só vira a principal se pedido
        // (principal = true) — ou quando, publicada, substituir a principal.
        group.MapPost("/{id:guid}/regenerate", async (Guid id, [FromBody] RegenerateRequest? req, SchedulingDbContext db,
            ScheduleRequester requester, ICurrentUser user, CancellationToken ct) =>
        {
            var s = await db.Schedules.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (s is null) return Results.NotFound();
            if (s.SectorId is null) return NotEditable(s);
            // Mesmo período da escala de origem (do mês ou, nas antigas, por semanas)
            return await requester.RequestAsync(new RequestScheduleRequest(s.StoreId, s.SectorId, s.StartDate, s.Weeks, null, req?.Principal, s.EndDate),
                s.Id, user, ct);
        }).RequireAuthorization(Policies.Manager);

        group.MapPost("/{id:guid}/approve", async (Guid id, [FromBody] ApproveRequest? req, SchedulingDbContext db, IPublishEndpoint bus,
            ICurrentUser user, IConfiguration cfg, ScheduleEvaluation evaluation, ILogger<ScheduleRequester> logger) =>
        {
            var s = await db.Schedules.Include(x => x.Assignments).FirstOrDefaultAsync(x => x.Id == id);
            if (s is null) return Results.NotFound();
            if (!CanManage(user, s.StoreId)) return Results.Forbid();
            if (s.Status != ScheduleStatus.Generated)
                return Results.Problem($"Só escalas geradas podem ser aprovadas (status atual: {s.Status}).", statusCode: 409);
            if (cfg.GetValue<bool>("Scheduling:RequireDifferentApprover") && s.RequestedBy == user.Display)
                return Results.Problem("A aprovação precisa ser feita por outra pessoa.", statusCode: 409);
            // Escala sem nenhum trabalho ou com dia aberto sem ninguém no setor (ex.: configuração da loja/operação sem turno
            // possível): avaliação atual (não a gravada, que pode ser de antes da correção do avaliador)
            if (s.SectorId is not null)
            {
                var current = evaluation.Evaluate(s);
                if (ComplianceEvaluator.ApprovalBlock(current, ScheduleCells.WorkCells(s.Assignments).Count, req?.ConfirmarDiasSemNinguem == true) is { } block)
                {
                    logger.LogWarning("Aprovação da escala {ScheduleId} bloqueada: {Reason}", s.Id, block);
                    return Results.Problem(block, statusCode: 409,
                        extensions: new Dictionary<string, object?> { ["code"] = "aprovacao_bloqueada" });
                }
            }

            s.Status = ScheduleStatus.Approved;
            s.ApprovedBy = user.Display;
            s.ApprovedAt = DateTime.UtcNow;
            s.UpdatedAt = DateTime.UtcNow;
            await bus.Publish(new ScheduleApproved(s.Id, s.StoreId, user.Display, DateTime.UtcNow, s.ConformidadePercentual));
            await db.SaveChangesAsync();
            return Results.Ok(Summary(s, await db.Assignments.AnyAsync(a => a.ScheduleId == s.Id && a.Origem == CellOrigin.Manual)));
        }).RequireAuthorization(Policies.Manager);

        group.MapPost("/{id:guid}/publish", async (Guid id, SchedulingDbContext db, IPublishEndpoint bus, ICurrentUser user) =>
        {
            var s = await db.Schedules.Include(x => x.Assignments).FirstOrDefaultAsync(x => x.Id == id);
            if (s is null) return Results.NotFound();
            if (!CanManage(user, s.StoreId)) return Results.Forbid();
            if (s.Status != ScheduleStatus.Approved)
                return Results.Problem($"Só escalas aprovadas podem ser publicadas (status atual: {s.Status}).", statusCode: 409);

            // A publicação substitui escalas publicadas do mesmo setor (ou, nas antigas, da loja toda) que se sobrepõem ao período.
            var end = s.EndDate;
            var overlapping = await db.Schedules
                .Where(o => o.StoreId == s.StoreId && o.SectorId == s.SectorId && o.Status == ScheduleStatus.Published && o.Id != s.Id)
                .ToListAsync();
            var substituidaPrincipal = false;
            foreach (var o in overlapping.Where(x => x.StartDate <= end && x.EndDate >= s.StartDate))
            {
                o.Status = ScheduleStatus.Superseded;
                o.SupersededBy = s.Id;
                o.UpdatedAt = DateTime.UtcNow;
                // Substituída não vale mais como escala: a principal passa para a nova versão publicada
                substituidaPrincipal |= o.Principal;
                o.Principal = false;
                o.PrincipalDesde = null;
                o.PrincipalPor = null;
            }

            s.Status = ScheduleStatus.Published;
            s.PublishedBy = user.Display;
            s.PublishedAt = DateTime.UtcNow;
            s.UpdatedAt = DateTime.UtcNow;
            if (substituidaPrincipal && !s.Principal && s.SectorId is not null)
            {
                var unmarked = await PrincipalSchedules.MarkAsync(db, s, user.Display, default);
                await bus.Publish(PrincipalSchedules.Changed(s, "publicacao", unmarked, user.Display));
            }
            await bus.Publish(new SchedulePublished(s.Id, s.StoreId, s.StartDate, s.Weeks, s.Version,
                s.Assignments.Where(a => !a.Folga).Select(a => a.EmployeeId).Distinct().ToList(), user.Display, DateTime.UtcNow, s.EndDate));
            await db.SaveChangesAsync();
            return Results.Ok(Summary(s, s.Assignments.Any(a => a.Origem == CellOrigin.Manual)));
        }).RequireAuthorization(Policies.Manager);
    }

    private static List<SuggestionChange> Changes(ScheduleSuggestion sug) =>
        sug.AlteracoesJson is null ? [] : JsonSerializer.Deserialize<List<SuggestionChange>>(sug.AlteracoesJson, Json.Options) ?? [];

    private static SuggestionResponse ToResponse(ScheduleSuggestion sug, Schedule s) => new(
        sug.Id, sug.ScheduleId, sug.Status, sug.PercentualAtual, sug.PercentualPrevisto, Changes(sug), sug.Mensagem,
        sug.CellsRevision != s.CellsRevision, sug.RequestedAt, sug.CompletedAt, sug.AppliedAt);
}

/// <summary>Edição manual de células (PUT /cells e a conferência POST /cells/validar).</summary>
internal static class CellEdits
{
    /// <summary>A tela editou outra revisão das células (escala regerada/editada/sugestão aplicada nesse meio tempo): 409.</summary>
    public static IResult? Stale(Schedule s, int? revisao) => revisao is { } r && r != s.CellsRevision
        ? Results.Problem("A escala foi alterada depois que você abriu a tela (nova geração, outra edição ou sugestão aplicada). " +
                          "Recarregue a escala e refaça as alterações.", statusCode: 409,
            extensions: new Dictionary<string, object?> { ["code"] = "escala_alterada", ["revisao"] = s.CellsRevision })
        : null;

    /// <summary>Célula de pessoa fora da fotografia, fora do período, com turno desconhecido ou com turno e folga.</summary>
    public static IResult? Invalid(Schedule s, PlanningSnapshot snapshot, IReadOnlyList<CellInput> cells)
    {
        var employees = snapshot.Employees.Select(e => e.EmployeeId).ToHashSet();
        var shifts = snapshot.Shifts.Select(x => x.ShiftId).ToHashSet();
        foreach (var c in cells)
        {
            if (!employees.Contains(c.EmployeeId)) return ServiceDefaults.ValidationError("employeeId", $"A pessoa {c.EmployeeId} não está no setor desta escala.");
            if (c.Date < s.StartDate || c.Date > s.EndDate) return ServiceDefaults.ValidationError("date", $"{c.Date:dd/MM/yyyy} está fora do período da escala.");
            if (c.ShiftId is { } sid && !shifts.Contains(sid)) return ServiceDefaults.ValidationError("shiftId", "Turno não pertence à fotografia da escala.");
            if (c.ShiftId is not null && c.Folga) return ServiceDefaults.ValidationError("folga", "Informe turno ou folga, não os dois.");
        }
        return null;
    }

    /// <summary>
    /// Aplica as células (repetições no mesmo pedido: vale a última) como manuais e reposiciona os almoços dos dias mexidos
    /// que não foram ajustados à mão (a cobertura do dia mudou). Não grava.
    /// </summary>
    public static void Apply(SchedulingDbContext db, Schedule s, PlanningSnapshot snapshot, IReadOnlyList<CellInput> cells)
    {
        var shifts = snapshot.Shifts.GroupBy(x => x.ShiftId).ToDictionary(g => g.Key, g => g.First());
        foreach (var c in cells.GroupBy(c => (c.EmployeeId, c.Date)).Select(g => g.Last()))
        {
            var content = c.ShiftId is { } sid ? new CellContent(shifts[sid], false) : c.Folga ? new CellContent(null, true) : null;
            ScheduleCells.Set(db, s, snapshot, c.EmployeeId, c.Date, content, CellOrigin.Manual);
        }
        var touched = cells.Select(c => c.Date).ToHashSet();
        ScheduleCells.PlanBreaks(s, snapshot, a => touched.Contains(a.Date) && a.BreakOrigem != CellOrigin.Manual);
    }

    /// <summary>Identidade de uma inconformidade para comparar avaliações: regra, colaborador e dias.</summary>
    public static string Key(string chave, Guid? employeeId, IEnumerable<DateOnly> dates) =>
        $"{chave}|{employeeId:N}|{string.Join(",", dates.Order().Select(d => d.DayNumber))}";

    /// <summary>
    /// Inconformidades que a edição CRIA: as da avaliação depois que não existiam antes (mesma regra, colaborador e dias;
    /// repetidas contam uma a uma). As que a edição resolveu ou que já existiam não entram.
    /// </summary>
    public static List<ComplianceViolationDto> NewViolations(ComplianceResult before, ComplianceResult after)
    {
        var existing = before.Violacoes.GroupBy(v => Key(v.Chave, v.EmployeeId, v.Dates)).ToDictionary(g => g.Key, g => g.Count());
        var result = new List<ComplianceViolationDto>();
        foreach (var v in after.Violacoes)
        {
            var key = Key(v.Chave, v.EmployeeId, v.Dates);
            if (existing.TryGetValue(key, out var n) && n > 0) existing[key] = n - 1;
            else result.Add(v);
        }
        return result;
    }

    public static EditCheckResponse Check(Schedule s, ComplianceResult before, ComplianceResult after)
    {
        var novas = NewViolations(before, after);
        return new EditCheckResponse(s.Id, s.CellsRevision, s.Status, s.EditRequiresConfirmation && novas.Count > 0,
            before.Percentual, after.Percentual, after.TotalViolacoes, novas);
    }
}

/// <summary>Monta a fotografia do setor e envia os comandos ao Worker via outbox.</summary>
public sealed class ScheduleRequester(
    SchedulingDbContext db, SnapshotBuilder builder, ScheduleEvaluation evaluation, IPublishEndpoint bus, IConfiguration cfg,
    ICurrentUser currentUser, AbsenceSync absences, PlanningClients clients, ILogger<ScheduleRequester> logger)
{
    // Admin, ou Gestor da loja (papel efetivo por loja, ver ScheduleEndpoints.CanManage)
    private bool CanManage(Guid storeId) => currentUser.CanManageStore(storeId);

    private int MaxTime => cfg.GetValue("Scheduling:MaxSolverTimeLimitSeconds", 600);

    /// <summary>Tempo do motor pedido fora de 1..Scheduling:MaxSolverTimeLimitSeconds.</summary>
    public IResult? InvalidTime(int? seconds) => seconds is { } t && (t < 1 || t > MaxTime)
        ? ServiceDefaults.ValidationError("tempoLimiteSegundos", $"Informe de 1 a {MaxTime} segundos.")
        : null;

    /// <summary>Tempo do motor: o pedido, senão o da última geração da escala, senão Scheduling:SolverTimeLimitSeconds.</summary>
    private int TimeLimit(int? requested, Schedule? s = null) =>
        Math.Clamp(requested ?? s?.TempoLimiteSegundos ?? cfg.GetValue("Scheduling:SolverTimeLimitSeconds", 60), 1, MaxTime);

    /// <summary>
    /// Período da escala nova: o MÊS civil de startDate (1º ao último dia, 28 a 31 dias). Nova versão (regenerate): o
    /// período da escala de origem (<see cref="RequestScheduleRequest.EndDate"/>), inclusive o das antigas por semanas.
    /// </summary>
    internal static (DateOnly From, DateOnly To) PeriodOf(RequestScheduleRequest req) =>
        req.EndDate is { } end ? (req.StartDate, end) : PlanningContext.MonthOf(req.StartDate);

    private IResult? ValidatePeriod(RequestScheduleRequest req)
    {
        if (InvalidTime(req.TempoLimiteSegundos) is { } badTime) return badTime;
        if (req.SectorId is null || req.SectorId == Guid.Empty)
            return ServiceDefaults.ValidationError("sectorId", "Informe o setor: as escalas são geradas por setor.");
        if (req.StartDate == default)
            return ServiceDefaults.ValidationError("startDate", "Informe o mês da escala.");
        var (from, to) = PeriodOf(req);
        if (to < from || to.DayNumber - from.DayNumber > 366)
            return ServiceDefaults.ValidationError("startDate", "Período da escala inválido.");
        if (!CanManage(req.StoreId)) return Results.Forbid();
        return null;
    }

    private async Task<(Schedule? Schedule, IResult? Error)> NewScheduleAsync(RequestScheduleRequest req, Guid? parentId, ScheduleStatus status, CancellationToken ct)
    {
        if (ValidatePeriod(req) is { } invalid) return (null, invalid);
        var sectorId = req.SectorId!.Value;
        // Escala do mês: do dia 1º ao último dia (fevereiro 28/29, meses de 30 e 31 dias)
        var (from, to) = PeriodOf(req);
        var (built, field, error, notFound) = await builder.BuildAsync(req.StoreId, sectorId, from, to, null, ct);
        if (notFound) return (null, Results.NotFound());
        if (built is null) return (null, ServiceDefaults.ValidationError(field!, error!));

        var version = 1 + (await db.Schedules
            .Where(s => s.StoreId == req.StoreId && s.SectorId == sectorId && s.StartDate == from && s.EndDate == to)
            .MaxAsync(s => (int?)s.Version, ct) ?? 0);

        var schedule = new Schedule
        {
            StoreId = req.StoreId,
            StoreName = built.StoreName,
            SectorId = sectorId,
            SectorName = built.SectorName,
            StartDate = from,
            EndDate = to,
            // Semanas civis (segunda a domingo) que o mês cobre: horizonte do motor
            Weeks = PlanningContext.Horizon(from, to).Weeks,
            Version = version,
            Status = status,
            ParentScheduleId = parentId,
            RequestedBy = currentUser.Display,
            SnapshotJson = JsonSerializer.Serialize(built.Snapshot, Json.Options)
        };
        db.Schedules.Add(schedule);
        return (schedule, null);
    }

    /// <summary>
    /// Vira (ou pede para virar) a principal: pedido explícito; sem pedido, quando não há outra escala do setor (viva ou em
    /// geração) que cruze o período.
    /// </summary>
    private async Task<bool> WantsPrincipalAsync(Guid storeId, Guid sectorId, DateOnly from, DateOnly to, Guid? excludeId, bool? requested,
        CancellationToken ct) =>
        requested ?? !await PrincipalSchedules.HasOverlapAsync(db, storeId, sectorId, from, to, excludeId, ct);

    /// <summary>POST /api/schedules (e regenerate): nova escala (nova versão) gerada do zero pelo motor.</summary>
    public async Task<IResult> RequestAsync(RequestScheduleRequest req, Guid? parentId, ICurrentUser user, CancellationToken ct)
    {
        var (schedule, error) = await NewScheduleAsync(req, parentId, ScheduleStatus.Generating, ct);
        if (schedule is null) return error!;
        // Principal só quando a geração terminar com sucesso (ScheduleResultConsumers)
        if (await WantsPrincipalAsync(schedule.StoreId, schedule.SectorId!.Value, schedule.StartDate, schedule.EndDate, schedule.Id, req.Principal, ct))
            schedule.PrincipalPedidoPor = user.Display;
        schedule.GenerationMode = GenerationModes.Completa;
        schedule.TempoLimiteSegundos = TimeLimit(req.TempoLimiteSegundos);
        // Escala nova: sem estado anterior (cancelar a deixa Cancelada)
        schedule.GeracaoId = Guid.NewGuid();
        schedule.GeracaoIniciadaEm = DateTime.UtcNow;
        schedule.GeracaoAnteriorJson = null;
        var snapshot = ScheduleCells.ReadSnapshot(schedule);

        // Com o bus outbox, as mensagens só saem depois do SaveChanges (mesma transação).
        await bus.Publish(new ScheduleRequested(schedule.Id, schedule.StoreId, schedule.StartDate, schedule.Weeks, schedule.Version, user.Display,
            DateTime.UtcNow, schedule.SectorId, GenerationModes.Completa, schedule.EndDate), ct);
        await bus.Publish(new GenerateScheduleCommand(schedule.Id, schedule.Version, schedule.StartDate, schedule.Weeks, snapshot,
            schedule.TempoLimiteSegundos.Value, GenerationModes.Completa, [], schedule.GeracaoId, schedule.EndDate), ct);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Escala {ScheduleId} v{Version} solicitada para a loja {StoreId}, setor {Sector}, de {From} a {To} com {Employees} pessoas",
            schedule.Id, schedule.Version, schedule.StoreId, schedule.SectorName, schedule.StartDate, schedule.EndDate, snapshot.Employees.Count);
        LogHistoryNotices(schedule.Id, snapshot);
        return Results.Accepted($"/api/schedules/{schedule.Id}", new
        {
            schedule.Id, schedule.Version, schedule.Status, avisos = snapshot.HistoryNotices ?? [],
            principalAoGerar = schedule.PrincipalPedidoPor is not null
        });
    }

    /// <summary>
    /// POST /api/schedules/manual: escala vazia em Rascunho com a fotografia, para preencher à mão. Principal na hora
    /// quando pedido ou quando não há outra escala do setor no período.
    /// </summary>
    public async Task<IResult> CreateManualAsync(RequestScheduleRequest req, ICurrentUser user, CancellationToken ct)
    {
        var (schedule, error) = await NewScheduleAsync(req, null, ScheduleStatus.Draft, ct);
        if (schedule is null) return error!;
        evaluation.EvaluateAndStore(schedule);
        if (await WantsPrincipalAsync(schedule.StoreId, schedule.SectorId!.Value, schedule.StartDate, schedule.EndDate, schedule.Id, req.Principal, ct))
        {
            var unmarked = await PrincipalSchedules.MarkAsync(db, schedule, user.Display, ct);
            await bus.Publish(PrincipalSchedules.Changed(schedule, "criacao", unmarked, user.Display), ct);
        }
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Escala manual {ScheduleId} v{Version} criada (Rascunho) para o setor {Sector} por {User}",
            schedule.Id, schedule.Version, schedule.SectorName, user.Display);
        return Results.Created($"/api/schedules/{schedule.Id}", ScheduleEndpoints.Detail(schedule, evaluation));
    }

    /// <summary>
    /// POST /api/schedules/{id}/generate: gera na própria escala (Rascunho/Gerada/Inviável/Falhou; a Aprovada chega
    /// aqui com a aprovação já desfeita). "completa" substitui tudo; "somente_vazios" mantém as células manuais (fixas).
    /// Atualiza na fotografia as ausências, as ocupações em outros setores e as regras de conformidade (gerar de novo
    /// usa o cadastro de regras vigente — ex.: folgas_maximas_semana numa escala criada antes dela).
    /// </summary>
    /// <param name="principal">Pedido de tornar a escala a principal ao terminar com sucesso (null = só se não houver outra no período).</param>
    public async Task<IResult> GenerateInPlaceAsync(Schedule s, string mode, ICurrentUser user, CancellationToken ct, int? timeLimit = null,
        bool? principal = null)
    {
        // A que já é a principal continua; as outras pedem para virar (aplicado só se a geração entregar escala)
        s.PrincipalPedidoPor = !s.Principal && await WantsPrincipalAsync(s.StoreId, s.SectorId!.Value, s.StartDate, s.EndDate, s.Id, principal, ct)
            ? user.Display
            : null;
        // Férias da LG das pessoas da escala atualizadas antes (tempo curto; LG fora: aviso e segue com as gravadas)
        var vacationNotice = await clients.RefreshLgVacationsAsync(s.StoreId, s.SectorId,
            ScheduleCells.ReadSnapshot(s).Employees.Select(e => e.EmployeeId).Distinct().ToList(), ct);
        // Afastamentos lançados depois da criação da escala: o motor não escala ninguém nesses dias
        await absences.SyncAsync(s, ct);
        var snapshot = ScheduleCells.ReadSnapshot(s);
        try
        {
            // Semanas civis inteiras: os dias de borda do mês vizinho entram nas contas da semana
            var (horizonFrom, horizonTo) = ScheduleCells.Horizon(s);
            var rules = await clients.GetRulesAsync(horizonFrom, horizonTo, ct);
            if (rules.Count > 0) snapshot = snapshot with { ComplianceRules = rules, Rules = null };
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Regras atuais indisponíveis no Compliance.Api para a escala {ScheduleId} ({Motivo}); gerando com as da fotografia",
                s.Id, $"{ex.GetType().Name}: {ex.Message}");
        }
        var occupied = await builder.OccupiedAsync(s.StoreId, s.SectorId!.Value, s.StartDate, s.EndDate,
            snapshot.Employees.Select(e => e.EmployeeId).ToList(), s.Id, ct);
        snapshot = snapshot with { Occupied = occupied };
        // Operação, turnos da operação, perfil/disponibilidade dos colaboradores e escala seguinte atuais
        snapshot = await builder.RefreshPlanningAsync(s, snapshot, ct, logger);
        // Aviso desta geração (férias da LG não atualizadas); os da geração anterior saem
        snapshot = snapshot with { HistoryNotices = SnapshotBuilder.WithVacationNotice(snapshot.HistoryNotices, vacationNotice) };
        s.SnapshotJson = JsonSerializer.Serialize(snapshot, Json.Options);

        // Almoço ajustado à mão também fixa a célula (o motor não mexe em almoço manual em "somente_vazios")
        var fixedCells = mode == GenerationModes.SomenteVazios
            ? ScheduleCells.ToDtos(s.Assignments.Where(a => a.Origem == CellOrigin.Manual || ScheduleCells.HasManualBreak(a)))
            : [];
        // Estado de antes, para o cancelamento devolver a escala como estava (as células só mudam com o resultado)
        s.GeracaoAnteriorJson = JsonSerializer.Serialize(new GenerationRollback(s.Status, s.StatusReason, s.DeficitsJson, s.DiagnosticoJson,
            s.SolucoesMotorJson, s.SolverStatus, s.ObjectiveValue, s.SolveSeconds, s.GenerationMode, s.TempoLimiteSegundos), Json.Options);
        s.GeracaoId = Guid.NewGuid();
        s.GeracaoIniciadaEm = DateTime.UtcNow;
        s.TempoLimiteSegundos = TimeLimit(timeLimit, s);
        s.Status = ScheduleStatus.Generating;
        s.GenerationMode = mode;
        s.StatusReason = null;
        s.DeficitsJson = null;
        s.DiagnosticoJson = null;
        s.SolucoesMotorJson = null;
        s.UpdatedAt = DateTime.UtcNow;

        await bus.Publish(new ScheduleRequested(s.Id, s.StoreId, s.StartDate, s.Weeks, s.Version, user.Display, DateTime.UtcNow, s.SectorId, mode,
            s.EndDate), ct);
        await bus.Publish(new GenerateScheduleCommand(s.Id, s.Version, s.StartDate, s.Weeks, snapshot,
            s.TempoLimiteSegundos.Value, mode, fixedCells, s.GeracaoId, s.EndDate), ct);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Geração {Mode} pedida na escala {ScheduleId} com {Fixed} célula(s) manual(is) fixa(s)", mode, s.Id, fixedCells.Count);
        LogHistoryNotices(s.Id, snapshot);
        return Results.Accepted($"/api/schedules/{s.Id}", new
        {
            s.Id, s.Version, s.Status, modo = mode, avisos = snapshot.HistoryNotices ?? [], principalAoGerar = s.PrincipalPedidoPor is not null
        });
    }

    /// <summary>
    /// POST /api/schedules/{id}/cancelar-geracao. Numa transação com a linha da escala travada (um resultado do Worker que
    /// chega ao mesmo tempo espera, ou falha e é reprocessado já sem "em geração", e é ignorado):
    /// <list type="bullet">
    /// <item>escala nova (sem <see cref="Schedule.GeracaoAnteriorJson"/>): status Cancelled, sem quadro (pode gerar de novo
    /// na própria escala, preencher à mão ou excluir);</item>
    /// <item>gerar na própria escala: volta o status e o resultado anterior (motivo, faltas, diagnóstico, opções do motor,
    /// modo e tempo) com as células intactas; Aprovada volta como Gerada (a aprovação foi desfeita ao pedir). A fotografia
    /// fica a atualizada no pedido (ausências, regras, continuidade): a escala é reavaliada com ela.</item>
    /// </list>
    /// O pedido de virar a principal cai (a principal anterior continua). Publica <see cref="CancelScheduleGeneration"/> para
    /// o Worker parar o CP-SAT/descartar o comando e <see cref="ScheduleGenerationCancelled"/> para a auditoria.
    /// </summary>
    public async Task<IResult> CancelGenerationAsync(Guid id, ICurrentUser user, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM schedules WHERE \"Id\" = {id} FOR UPDATE", ct);
        var s = await db.Schedules.Include(x => x.Assignments).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return Results.NotFound();
        if (!CanManage(s.StoreId)) return Results.Forbid();
        if (!s.CanCancelGeneration)
            return Results.Problem(s.Status == ScheduleStatus.Cancelled
                ? "A geração desta escala já foi cancelada."
                : $"A escala não está em geração (status atual: {s.Status}): a geração já terminou.", statusCode: 409);

        var now = DateTime.UtcNow;
        GenerationRollback? previous = null;
        if (!string.IsNullOrEmpty(s.GeracaoAnteriorJson))
        {
            try { previous = JsonSerializer.Deserialize<GenerationRollback>(s.GeracaoAnteriorJson, Json.Options); }
            catch (JsonException) { previous = null; }
        }
        var started = s.GeracaoIniciadaEm;
        var principalRequested = s.PrincipalPedidoPor is not null;
        if (previous is not null)
        {
            s.Status = previous.Status is ScheduleStatus.Approved or ScheduleStatus.Published or ScheduleStatus.Superseded or ScheduleStatus.Generating
                ? ScheduleStatus.Generated
                : previous.Status;
            s.StatusReason = previous.StatusReason;
            s.DeficitsJson = previous.DeficitsJson;
            s.DiagnosticoJson = previous.DiagnosticoJson;
            s.SolucoesMotorJson = previous.SolucoesMotorJson;
            s.SolverStatus = previous.SolverStatus;
            s.ObjectiveValue = previous.ObjectiveValue;
            s.SolveSeconds = previous.SolveSeconds;
            s.GenerationMode = previous.GenerationMode;
            s.TempoLimiteSegundos = previous.TempoLimiteSegundos;
        }
        else if (s.Assignments.Count > 0)
        {
            // Geração na própria escala pedida antes desta versão (sem o estado anterior guardado): as células ficam
            s.Status = s.Assignments.Any(a => a.Origem == CellOrigin.Motor) ? ScheduleStatus.Generated : ScheduleStatus.Draft;
            s.StatusReason = $"Geração cancelada por {user.Display}.";
        }
        else
        {
            s.Status = ScheduleStatus.Cancelled;
            s.StatusReason = $"Geração cancelada por {user.Display} antes de terminar: a escala não tem quadro. Gere de novo, preencha à mão ou exclua.";
            s.SolverStatus = null;
        }
        s.PrincipalPedidoPor = null; // sem escala entregue: a principal anterior continua
        s.GeracaoAnteriorJson = null;
        s.GeracaoCanceladaEm = now;
        s.GeracaoCanceladaPor = user.Display;
        s.CellsRevision++;
        s.UpdatedAt = now;
        // Fotografia atualizada no pedido (ausências, regras, continuidade): a avaliação gravada passa a ser com ela
        evaluation.EvaluateAndStore(s);

        await bus.Publish(new CancelScheduleGeneration(s.Id, s.GeracaoId, user.Display, now), ct);
        await bus.Publish(new ScheduleGenerationCancelled(s.Id, s.StoreId, s.SectorId, s.SectorName ?? Schedule.AllSectors, s.StartDate, s.EndDate,
            s.Version, s.GeracaoId, previous?.Status.ToString(), s.Status.ToString(), s.Assignments.Count, principalRequested,
            started is { } t ? Math.Round((now - t).TotalSeconds, 1) : null, user.Display, now), ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        logger.LogInformation("Geração {GenerationId} da escala {ScheduleId} v{Version} ({Sector}) cancelada por {User}: {Before} → {Status}, {Cells} célula(s) preservada(s)",
            s.GeracaoId, s.Id, s.Version, s.SectorName, user.Display, previous?.Status.ToString() ?? "nova", s.Status, s.Assignments.Count);
        var manual = s.Assignments.Any(a => a.Origem == CellOrigin.Manual || ScheduleCells.HasManualBreak(a));
        return Results.Ok(ScheduleEndpoints.Summary(s, manual));
    }

    /// <summary>Avisos da continuidade com a semana anterior (sem escala; escala anterior ainda não publicada).</summary>
    private void LogHistoryNotices(Guid scheduleId, PlanningSnapshot snapshot)
    {
        foreach (var notice in snapshot.HistoryNotices ?? [])
            logger.LogWarning("Escala {ScheduleId}: {Aviso}", scheduleId, notice);
    }
}

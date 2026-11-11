using System.Diagnostics;
using Escala5x2.BuildingBlocks.Compliance;
using Escala5x2.BuildingBlocks.Contracts.Compliance;
using Escala5x2.BuildingBlocks.Contracts.Planning;
using Escala5x2.BuildingBlocks.Contracts.Scheduling;
using Google.OrTools.Sat;
using Microsoft.Extensions.Options;

namespace Optimization.Worker.Engine;

public abstract record SolveResult(PlanningContext? Context, IReadOnlyList<string> Warnings)
{
    /// <param name="Status"><see cref="EngineResults.Otima"/> (prova de ótimo) ou <see cref="EngineResults.Viavel"/>.</param>
    /// <param name="Deficits">Faltas no reforço de pico (demanda acima do mínimo, preferencial) ou na cobertura preferencial.</param>
    /// <param name="Notice">Aviso para quem pediu a escala (vai para StatusReason).</param>
    /// <param name="Solutions">Opções de escala avaliadas pelo motor (registro das soluções do CP-SAT).</param>
    public sealed record Success(PlanningContext Ctx, IReadOnlyList<AssignmentDto> Assignments, long Objective, double Seconds, string Status,
        IReadOnlyList<string> W, IReadOnlyList<DeficitDto> Deficits, string? Notice, EngineSolutionsSummary? Solutions = null)
        : SolveResult(Ctx, W);

    /// <summary>Provado que não existe escala (contagem antes do motor ou INFEASIBLE do CP-SAT), com diagnóstico.</summary>
    public sealed record Infeasible(PlanningContext? Ctx, string Reason, IReadOnlyList<DeficitDto> Deficits, IReadOnlyList<string> W,
        InfeasibilityDiagnosis? Diagnosis = null, double Seconds = 0)
        : SolveResult(Ctx, W);

    /// <summary>O CP-SAT não achou escala nem provou inviabilidade no tempo (UNKNOWN): tentar com mais tempo.</summary>
    public sealed record TimedOut(PlanningContext Ctx, string Reason, int TimeLimitSeconds, double Seconds, IReadOnlyList<string> W)
        : SolveResult(Ctx, W);
}

public sealed record SuggestionOutcome(
    string Status, double PercentualAtual, double PercentualPrevisto, IReadOnlyList<SuggestedChangeDto> Changes, string? Message);

/// <summary>
/// Geração em uma etapa: todas as regras rígidas e os mínimos de cobertura como restrições duras (exceto no setor de 2
/// pessoas, no setor com quadro insuficiente — provado por contagem ou pelo CP-SAT, com a opção
/// CoberturaComoMetaQuadroInsuficiente — e nos dias de borda, em que a cobertura é meta: PlanningContext.HardCoverage); a função objetivo
/// pondera reforço de pico, excedente, equilíbrio, mudança de horário, preferências e custos. Status:
/// OPTIMAL → Ótima; FEASIBLE → Viável; INFEASIBLE → Inviável (com diagnóstico); UNKNOWN → Sem solução no tempo;
/// MODEL_INVALID → exceção (Falha). Não existe modo flexível.
/// </summary>
public sealed class ScheduleSolver(IOptions<SolverOptions> options, ComplianceEvaluator evaluator, ILogger<ScheduleSolver> logger)
{
    /// <param name="seconds">Tempo do motor (padrão: o do comando). Os testes usam frações de segundo.</param>
    /// <param name="ct">
    /// Cancelamento da geração (POST cancelar-geracao ou parada do Worker): interrompe o CP-SAT em andamento
    /// (<see cref="CpSolver.StopSearch"/>) e lança <see cref="OperationCanceledException"/> — nenhum resultado é entregue.
    /// </param>
    public SolveResult Solve(GenerateScheduleCommand cmd, double? seconds = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var opt = options.Value;
        var fixedCells = cmd.Mode == GenerationModes.SomenteVazios ? cmd.FixedCells ?? [] : [];
        // Escala do mês: o horizonte são as semanas civis inteiras; os dias de borda vêm da escala vizinha
        // A cobertura como meta por quadro insuficiente é decidida de novo a cada geração (a gravada na fotografia é do
        // resultado anterior, para o avaliador)
        var ctx = PlanningContext.Create(cmd.Snapshot with { CoberturaMetaSetores = null, CoberturaMetaDias = null }, cmd.StartDate, cmd.LastDate, fixedCells);
        var total = seconds ?? Math.Max(1, cmd.TimeLimitSeconds);
        var watch = Stopwatch.StartNew();
        // Picos manuais que pedem mais gente do que o setor pode ter na faixa (aviso; o pico é preferencial)
        ctx.Warnings.AddRange(InfeasibilityDiagnostics.ManualPeakNotices(ctx).Where(n => !ctx.Warnings.Contains(n)));
        // Cobertura como meta quando o quadro não cobre o mínimo (opção da operação + chave do Solver)
        var targetAllowed = opt.CoverageTargetUnderstaffedSectors && ctx.CoverageTargetEnabled;

        logger.LogInformation(
            "Modelo: {Employees} pessoas, {Days} dias, {Shifts} turnos, {Sectors} setor(es), {Slots} faixas de cobertura, {Fixed} célula(s) fixa(s), operação {Operation}, regras ativas {Rules}",
            ctx.Employees.Length, ctx.Days, ctx.Shifts.Length, ctx.Sectors.Length, ctx.Slots.Count, ctx.Fixed.Count, ctx.OperationMode ? "sim" : "não",
            string.Join(",", ctx.Rules.Keys.Where(k => Enumerable.Range(0, ctx.Days).Any(d => ctx.Rules.Active(k, d) is not null)).Order()));

        // 0. Configuração (horário da loja × operação × regras): dia aberto sem nenhum turno que caiba, faixa que nenhum turno
        // cobre, setor sem cobertura. Falha sempre, com a cobertura como meta ou não — antes a escala saía com todos de folga
        // o mês inteiro (ou todos no mesmo turno) e "100% conforme"
        var configuration = InfeasibilityDiagnostics.Configuration(ctx);
        if (configuration.Count > 0)
        {
            var diagnosis = InfeasibilityDiagnostics.Compose(ctx, configuration, [], proven: true, fromCapacity: true);
            logger.LogWarning("Erro de configuração: {Reason}", diagnosis.Resumo);
            return new SolveResult.Infeasible(ctx, diagnosis.Resumo, [], ctx.Warnings, diagnosis, watch.Elapsed.TotalSeconds);
        }

        // 1. Provas por contagem (rápidas e compreensíveis): faixa/dia/semana sem gente suficiente (inclusive os turnos que a
        // cobertura dos intervalos pede). Com a cobertura como meta permitida, os setores provados viram meta e a geração segue
        var capacity = InfeasibilityDiagnostics.Capacity(ctx);
        if (capacity.Items.Count > 0 && targetAllowed && (capacity.Sectors.Count > 0 || capacity.Days.Count > 0))
        {
            // Falta no setor (semana, perfil, domingos): meta no setor inteiro; falta só em alguns dias (ausências, folgas
            // manuais): meta só nesses dias, o resto do mês continua rígido
            foreach (var s in capacity.Sectors) ctx.UseCoverageTarget(s);
            foreach (var (s, d) in capacity.Days.Where(x => !capacity.Sectors.Contains(x.Sector))) ctx.UseCoverageTargetDay(s, d);
            logger.LogInformation("Quadro insuficiente (provado por contagem): cobertura como meta no(s) setor(es) {Sectors} e em {Days} dia(s). {Reason}",
                string.Join(", ", capacity.Sectors.Select(s => ctx.Sectors[s].Name)), ctx.CoverageTargetDays.Count, capacity.Items[0].Mensagem);
            capacity = InfeasibilityDiagnostics.Capacity(ctx);
        }
        if (capacity.Items.Count > 0)
        {
            var diagnosis = InfeasibilityDiagnostics.Compose(ctx, capacity.Items, [], proven: true, fromCapacity: true);
            logger.LogWarning("Inviável por contagem: {Reason}", diagnosis.Resumo);
            return new SolveResult.Infeasible(ctx, diagnosis.Resumo, capacity.Deficits, ctx.Warnings, diagnosis, watch.Elapsed.TotalSeconds);
        }

        // 2. Geração. Com o rodízio de abertura/fechamento ligado, primeiro com o perfil como restrição dura (o rodízio só
        // entra quando o perfil não basta): se a contagem já prova que o perfil não basta, vai direto ao rodízio; senão,
        // sem nenhuma escala na metade do tempo (ou provado inviável), gera com o rodízio no tempo que resta.
        // Setor com cobertura como meta (2 pessoas, quadro insuficiente): o perfil como restrição dura nunca fica inviável por
        // falta de cobertura — vai direto ao rodízio (cada turno fora do perfil é penalizado; o motor só sai do perfil para
        // cobrir mais tempo)
        ct.ThrowIfCancellationRequested();
        var anyTarget = Enumerable.Range(0, ctx.Sectors.Length).Any(ctx.SoftCoverage);
        var strictFirst = ctx.Rotation && !anyTarget && InfeasibilityDiagnostics.Capacity(ctx, rotation: false).Items.Count == 0;
        var (model, solver, recorder, status) = Generate(ctx, opt, total, strictFirst ? false : null, strictFirst ? total * 0.5 : null, ct);
        if (strictFirst && status is not (CpSolverStatus.Optimal or CpSolverStatus.Feasible))
        {
            logger.LogInformation("Perfil como restrição dura: {Status} em {Seconds:F1}s — gerando com o rodízio de abertura/fechamento",
                status, solver.WallTime());
            recorder.Dispose();
            (model, solver, recorder, status) = Generate(ctx, opt, Math.Max(total * 0.25, total - watch.Elapsed.TotalSeconds), null, null, ct);
        }
        else if (ctx.Rotation)
        {
            logger.LogInformation(strictFirst
                ? "Rodízio de abertura/fechamento ligado, mas não foi preciso: escala só com turnos do perfil"
                : "Rodízio de abertura/fechamento: a contagem mostra que o perfil não basta; gerando com o rodízio");
        }
        // Setor de 2 pessoas: provado que não há escala com as folgas fixas e intercaladas → gera com folgas variáveis (o
        // padrão não é regra legal; o diagnóstico de inviabilidade, se houver, é sem ele)
        string? fixedFallback = null;
        if (status == CpSolverStatus.Infeasible && model.FixedDaysOffApplied)
        {
            logger.LogInformation("Folgas fixas e intercaladas (setor de 2 pessoas): inviável em {Seconds:F1}s — gerando com folgas variáveis", solver.WallTime());
            recorder.Dispose();
            (model, solver, recorder, status) = Generate(ctx, opt, Math.Max(total * 0.25, total - watch.Elapsed.TotalSeconds), null, null, ct, fixedDaysOff: false);
            fixedFallback = FixedDaysOffFallbackNotice(ctx);
        }
        // Quadro insuficiente que a contagem não vê (perfis, interjornada, regra de domingos, ausências, células manuais): com
        // a cobertura rígida o CP-SAT PROVOU que não há escala → gera com a cobertura como meta nos setores envolvidos; as
        // regras legais continuam rígidas. Sem solução no tempo (UNKNOWN) não troca: só a pré-resolução de um mês leva ~10 s
        // e a primeira escala com a cobertura rígida às vezes só aparece no fim do tempo — trocar antes deixaria como meta um
        // setor que o quadro cobre (o resultado continua "sem solução no tempo": gere de novo com mais tempo)
        if (targetAllowed && status == CpSolverStatus.Infeasible && ctx.Slots.Any(s => s.Minimum > 0 && ctx.HardCoverage(s)))
        {
            var elapsed = watch.Elapsed.TotalSeconds;
            recorder.Dispose();
            var sectors = CoverageFallbackSectors(ctx, opt, Math.Max(1, Math.Min(opt.DiagnosisSeconds * 0.4, total * 0.2)), ct);
            foreach (var s in sectors) ctx.UseCoverageTarget(s);
            logger.LogInformation("Cobertura rígida: inviável em {Seconds:F1}s — gerando com a cobertura como meta no(s) setor(es) {Sectors}",
                elapsed, string.Join(", ", sectors.Select(s => ctx.Sectors[s].Name)));
            // Pelo menos metade do tempo: a escala com a meta sempre existe, só precisa de tempo para sair boa
            (model, solver, recorder, status) = Generate(ctx, opt, Math.Max(total * 0.5, total - watch.Elapsed.TotalSeconds), null, null, ct);
        }
        using var _ = recorder;
        logger.LogInformation("Geração: {Status} em {Seconds:F1}s, objetivo {Objective}", status, solver.WallTime(),
            status is CpSolverStatus.Optimal or CpSolverStatus.Feasible ? solver.ObjectiveValue : double.NaN);

        switch (status)
        {
            case CpSolverStatus.Optimal:
            case CpSolverStatus.Feasible:
            {
                var result = status == CpSolverStatus.Optimal ? EngineResults.Otima : EngineResults.Viavel;
                var solutions = recorder.Summary(solver, result, solver.WallTime());
                LogSolutions(solutions);
                // Faltas e células só dos dias do período (os dias de borda sem escala vizinha só orientam o motor)
                var deficits = ReadDeficits(ctx, model, solver).Where(d => d.Date >= ctx.PeriodStart && d.Date <= ctx.PeriodEnd).ToList();
                var assignments = ReadAssignments(ctx, model, solver);
                var notices = RotationNotices(ctx, assignments);
                foreach (var n in notices) logger.LogInformation("Rodízio: {Notice}", n);
                var fixedPeople = model.FixedPatterns.Keys.Select(e => ctx.Employees[e].Snapshot.EmployeeId).ToHashSet();
                var fixedNotices = model.FixedDaysOffApplied
                    ? FixedDaysOffNotices(ctx, model.FixedPatternValues(v => solver.Value(v)), assignments)
                    : new List<string>();
                if (fixedFallback is not null) fixedNotices.Add(fixedFallback);
                // Cobertura como meta (setor de 2 pessoas, quadro insuficiente): faixas descobertas agregadas por causa e horário
                var uncovered = model.TargetDeficits.Select(x => (x.Slot, Missing: (int)solver.Value(x.Deficit))).ToList();
                fixedNotices.AddRange(InfeasibilityDiagnostics.CoverageTargetNotices(ctx, uncovered.Select(x => (x.Slot, x.Missing)), assignments));
                // Setor que virou meta mas saiu todo coberto (ex.: a cobertura rígida só demorava): volta a ser rígido no
                // avaliador e no registro da escala
                foreach (var s in ctx.CoverageTargetSectors.Where(s => !uncovered.Any(x => x.Missing > 0 && x.Slot.Sector == s && ctx.InPeriod(x.Slot.Day))).ToList())
                    ctx.RemoveCoverageTarget(s);
                foreach (var (s, d) in ctx.CoverageTargetDays.Where(x => !uncovered.Any(u => u.Missing > 0 && u.Slot.Sector == x.Sector && u.Slot.Day == x.Day)).ToList())
                    ctx.RemoveCoverageTargetDay(s, d);
                foreach (var n in fixedNotices)
                {
                    notices.Add(n);
                    logger.LogInformation("{Notice}", n);
                }
                if (DoubleDayOffNotice(ctx, assignments, fixedPeople) is { } dupla)
                {
                    notices.Add(dupla);
                    logger.LogInformation("{Notice}", dupla);
                }
                if (deficits.Count > 0)
                {
                    notices.Add($"{(ctx.OperationMode ? "Reforço de pico" : "Cobertura preferencial")}: faltam {deficits.Sum(d => d.Missing)} posição(ões) de faixa acima do mínimo (veja o avaliador).");
                    // Pico manual não atendido: quanto pede e quanto a escala atende (ou o máximo possível)
                    notices.AddRange(InfeasibilityDiagnostics.ManualPeakNotices(ctx, deficits));
                }
                return new SolveResult.Success(ctx, assignments, (long)solver.ObjectiveValue, watch.Elapsed.TotalSeconds,
                    result, Merge(ctx, model.Warnings), deficits, notices.Count > 0 ? string.Join(" ", notices) : null, solutions);
            }
            case CpSolverStatus.Infeasible:
            {
                var (diagnosis, deficits) = Diagnose(ctx, opt, ct);
                logger.LogWarning("Inviável: {Reason}", diagnosis.Resumo);
                return new SolveResult.Infeasible(ctx, diagnosis.Resumo, deficits, Merge(ctx, model.Warnings), diagnosis, watch.Elapsed.TotalSeconds);
            }
            case CpSolverStatus.Unknown:
                logger.LogInformation("Motor: {Count} soluções encontradas, status {Status}", recorder.Count, EngineResults.SemSolucaoNoTempo);
                return new SolveResult.TimedOut(ctx,
                    $"O motor não concluiu em {total:0.#} s: não encontrou escala que cumpra todas as regras nem provou que ela não existe. " +
                    "Isso não quer dizer que seja inviável — tente de novo com mais tempo.",
                    (int)Math.Ceiling(total), watch.Elapsed.TotalSeconds, Merge(ctx, model.Warnings));
            default:
                throw new InvalidOperationException($"O modelo do motor é inválido ({status}): {model.Model.Validate()}");
        }
    }

    /// <summary>
    /// Monta e resolve o modelo da geração com o registro das soluções (só observa; o cancelamento para a busca).
    /// <paramref name="rotation"/>: false = perfil como restrição dura (null = o da operação). <paramref name="giveUpAfter"/>:
    /// sem nenhuma solução nesse tempo (s), a busca é interrompida (status UNKNOWN).
    /// </summary>
    private static (ScheduleModel Model, CpSolver Solver, SolutionRecorder Recorder, CpSolverStatus Status) Generate(
        PlanningContext ctx, SolverOptions opt, double seconds, bool? rotation, double? giveUpAfter, CancellationToken ct, bool? fixedDaysOff = null)
    {
        var model = new ScheduleModel(ctx, SolveStage.Generation, opt, rotation: rotation, fixedDaysOff: fixedDaysOff);
        var solver = NewSolver(seconds, opt.Workers);
        var recorder = new SolutionRecorder(model, opt.MaxLoggedSolutions);
        using var timer = giveUpAfter is { } g
            ? new Timer(_ =>
            {
                if (recorder.Count > 0) return;
                try { solver.StopSearch(); }
                catch { /* o Solve já terminou */ }
            }, null, TimeSpan.FromSeconds(Math.Max(0.01, g)), Timeout.InfiniteTimeSpan)
            : null;
        try
        {
            return (model, solver, recorder, Run(solver, model.Model, recorder, ct));
        }
        catch
        {
            recorder.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Log estruturado das opções de escala avaliadas: uma linha de resumo e uma por solução guardada (componentes do
    /// objetivo e indicadores), para decidir pesos/critérios de exclusão.
    /// </summary>
    private void LogSolutions(EngineSolutionsSummary s)
    {
        logger.LogInformation(
            "Motor: {Count} soluções encontradas ({Kept} guardadas, limite {Limit}), status {Status}, objetivo final {Objective}, melhor limite {Bound}, gap {Gap:P2}, {Seconds:F1}s",
            s.Total, s.Guardadas, s.Limite, EngineResults.Rotulo(s.Status), s.ObjetivoFinal, s.MelhorLimite, s.Gap, s.Segundos);
        foreach (var x in s.Solucoes)
        {
            logger.LogInformation(
                "Motor: solução {Number}{Chosen} em {Seconds:F2}s, objetivo {Objective}, limite {Bound}, gap {Gap:P2} | {Components} | {Indicators}",
                x.Numero, x.Escolhida ? " (escolhida)" : "", x.Segundos, x.Objetivo, x.MelhorLimite, x.Gap,
                string.Join(", ", x.Componentes.Where(c => c.Value != 0).Select(c => $"{c.Key}={c.Value}")),
                string.Join(", ", x.Indicadores.Select(i => $"{i.Key} {i.Value.Min}-{i.Value.Max}")));
        }
    }

    /// <summary>
    /// Diagnóstico de um INFEASIBLE do CP-SAT: (a) cobertura relaxada — com todas as outras regras, quantas pessoas faltam
    /// em que faixas; (b) núcleo de restrições — grupos (regras, cobertura por dia, células manuais, continuidade) que
    /// juntos não admitem escala, reduzido quando pequeno.
    /// </summary>
    private (InfeasibilityDiagnosis Diagnosis, List<DeficitDto> Deficits) Diagnose(PlanningContext ctx, SolverOptions opt, CancellationToken ct)
    {
        var budget = Math.Max(2, opt.DiagnosisSeconds);
        var items = new List<DiagnosisItem>();
        var deficits = new List<DeficitDto>();
        bool? coverageProven = null;

        // (a) Mínimo de cobertura medido (falta) com todas as outras regras rígidas (só os mínimos rígidos: dia de borda e
        // setor de 2 pessoas têm cobertura como meta e não entram)
        if (ctx.Slots.Any(s => s.Minimum > 0 && ctx.HardCoverage(s)))
        {
            var relaxed = new ScheduleModel(ctx, SolveStage.CoverageRelaxed, opt);
            var rs = NewSolver(budget * 0.4, opt.Workers);
            var st = Run(rs, relaxed.Model, null, ct);
            logger.LogInformation("Diagnóstico (cobertura relaxada): {Status} em {Seconds:F1}s", st, rs.WallTime());
            if (st is CpSolverStatus.Optimal or CpSolverStatus.Feasible)
            {
                deficits = ReadDeficits(ctx, relaxed, rs);
                coverageProven = st == CpSolverStatus.Optimal;
                items.AddRange(InfeasibilityDiagnostics.CoverageItems(ctx, deficits, relaxed.Deficits.Select(x => x.Slot).ToList()));
            }
        }

        // (b) Núcleo de restrições por hipóteses
        try
        {
            // Com a falta por faixa já medida, do núcleo ficam as regras/células/continuidade que disputam a gente
            var core = CoreGroups(ctx, opt, budget * 0.6, ct);
            items.AddRange(core.Where(i => i.Tipo != "cobertura" || deficits.Count == 0));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Núcleo de restrições não calculado");
        }

        return (InfeasibilityDiagnostics.Compose(ctx, items, deficits, proven: coverageProven ?? true, fromCapacity: false), deficits);
    }

    /// <summary>
    /// Setores que passam à cobertura como meta quando a geração com a cobertura rígida falha: os que têm mínimo rígido no
    /// período. Com mais de um, só os que a cobertura relaxada (todas as outras regras rígidas) aponta com falta — os que o
    /// quadro cobre continuam rígidos; sem resposta no tempo, todos. Com a operação (escala por setor) é o setor da escala.
    /// </summary>
    private List<int> CoverageFallbackSectors(PlanningContext ctx, SolverOptions opt, double seconds, CancellationToken ct)
    {
        var candidates = ctx.Slots.Where(s => s.Minimum > 0 && ctx.HardCoverage(s)).Select(s => s.Sector).Distinct().Order().ToList();
        if (candidates.Count <= 1) return candidates;
        var relaxed = new ScheduleModel(ctx, SolveStage.CoverageRelaxed, opt);
        var rs = NewSolver(seconds, opt.Workers);
        var st = Run(rs, relaxed.Model, null, ct);
        if (st is not (CpSolverStatus.Optimal or CpSolverStatus.Feasible)) return candidates;
        var shortSectors = relaxed.Deficits.Where(d => rs.Value(d.Deficit) > 0).Select(d => d.Slot.Sector).Distinct().Order().ToList();
        return shortSectors.Count > 0 ? shortSectors : candidates;
    }

    private List<DiagnosisItem> CoreGroups(PlanningContext ctx, SolverOptions opt, double seconds, CancellationToken ct)
    {
        // Sem as folgas fixas do setor de 2 pessoas: o núcleo aponta regras legais, cobertura, células e continuidade
        var model = new ScheduleModel(ctx, SolveStage.Generation, opt, assumptions: true, feasibilityOnly: true, fixedDaysOff: false);
        if (model.Groups.Count == 0) return [];
        var keyOf = model.Groups.ToDictionary(g => g.Value.Literal.GetIndex(), g => g.Key);
        var byIndex = model.Groups.Values.ToDictionary(g => g.Literal.GetIndex());
        model.Model.AddAssumptions(model.Groups.Values.Select(g => (ILiteral)g.Literal));
        var solver = NewSolver(seconds / 2, opt.Workers);
        var st = Run(solver, model.Model, null, ct);
        var core = st == CpSolverStatus.Infeasible ? solver.SufficientAssumptionsForInfeasibility().Where(byIndex.ContainsKey).ToList() : [];
        logger.LogInformation("Diagnóstico (núcleo): {Status}, {Count} grupo(s) de {Total}", st, core.Count, model.Groups.Count);
        if (core.Count == 0) return [];

        // Reduz o núcleo: tira um grupo por vez; se continua inviável sem ele, ele não é necessário
        if (core.Count <= opt.DiagnosisMaxCoreToMinimize)
        {
            var each = Math.Max(1, seconds / 2 / core.Count);
            foreach (var g in core.ToList())
            {
                var rest = core.Where(x => x != g).ToList();
                if (rest.Count == 0) break;
                model.Model.ClearAssumptions();
                model.Model.AddAssumptions(rest.Select(x => (ILiteral)byIndex[x].Literal));
                var test = NewSolver(each, opt.Workers);
                if (Run(test, model.Model, null, ct) == CpSolverStatus.Infeasible) core = rest;
            }
        }
        var items = core.Select(i => byIndex[i].Item).ToList();

        // Regra no núcleo (grupo "regra:chave", sem pessoa): aponta quem e quando, com a regra em grupos por pessoa e dias
        var keys = core.Select(i => keyOf[i]).ToList();
        var rules = keys.Where(k => k.StartsWith(RulePrefix) && !k[RulePrefix.Length..].Contains(':')).Select(k => k[RulePrefix.Length..]).ToHashSet();
        if (rules.Count == 0) return items;
        try
        {
            var pinpoint = Pinpoint(ctx, opt, keys, rules, Math.Max(1, seconds / 2), ct);
            if (pinpoint.Count == 0) return items;
            // Logo depois do item da regra (o resumo mostra os primeiros itens)
            var at = items.FindLastIndex(i => i.Tipo == "regra" && i.Chave is not null && rules.Contains(i.Chave)) + 1;
            items.InsertRange(at <= 0 ? items.Count : at, pinpoint);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Refinamento do núcleo (pessoa e dias) não calculado");
        }
        return items;
    }

    private const string RulePrefix = "regra:";

    /// <summary>Até quantos trechos (pessoa e dias) o refinamento lista; acima disso, "e mais N".</summary>
    internal const int MaxPinpointItems = 5;

    /// <summary>
    /// Refinamento do núcleo: com as regras <paramref name="rules"/> em grupos por pessoa e dias (ScheduleModel, refine) e os
    /// demais grupos do núcleo mantidos, quais trechos (ex.: "Ana: folgas em 07/11, 08/11 e 09/11 (...)") tornam o problema
    /// impossível — reduzido por remoção quando pequeno. Até <see cref="MaxPinpointItems"/> itens, na ordem dos dias.
    /// </summary>
    private List<DiagnosisItem> Pinpoint(PlanningContext ctx, SolverOptions opt, IReadOnlyCollection<string> core, IReadOnlySet<string> rules,
        double seconds, CancellationToken ct)
    {
        var model = new ScheduleModel(ctx, SolveStage.Generation, opt, assumptions: true, feasibilityOnly: true, fixedDaysOff: false, refine: rules);
        bool Refined(string key) => rules.Any(r => key.StartsWith($"{RulePrefix}{r}:"));
        var refined = model.Groups.Keys.Where(Refined).ToList();
        if (refined.Count == 0) return [];
        // Os outros grupos do núcleo (células manuais, continuidade...) e o que sobrou da regra sem pessoa continuam valendo
        var kept = core.Where(model.Groups.ContainsKey).ToList();
        var byIndex = kept.Concat(refined).ToDictionary(k => model.Groups[k].Literal.GetIndex(), k => k);

        CpSolverStatus Solve(IEnumerable<string> assumed, double time, out CpSolver solver)
        {
            model.Model.ClearAssumptions();
            model.Model.AddAssumptions(assumed.Select(k => (ILiteral)model.Groups[k].Literal));
            solver = NewSolver(time, opt.Workers);
            return Run(solver, model.Model, null, ct);
        }

        // Mesmo tamanho do modelo do núcleo, com menos grupos ativos (pelo menos 5 s: com a máquina ocupada a prova demora)
        var status = Solve(kept.Concat(refined), Math.Max(5, seconds), out var first);
        if (status != CpSolverStatus.Infeasible)
        {
            logger.LogInformation("Diagnóstico (refinamento por pessoa e dias): {Status} em {Seconds:F1}s — sem trechos apontados", status, first.WallTime());
            return [];
        }
        var culprits = first.SufficientAssumptionsForInfeasibility().Where(byIndex.ContainsKey).Select(i => byIndex[i]).Where(Refined).ToList();
        // Reduz: tira um trecho por vez; se continua inviável sem ele, ele não é necessário
        if (culprits.Count is > 1 and <= 24)
        {
            var each = Math.Max(0.5, seconds / 2 / culprits.Count);
            foreach (var g in culprits.ToList())
            {
                var rest = culprits.Where(x => x != g).ToList();
                if (Solve(kept.Concat(rest), each, out _) == CpSolverStatus.Infeasible) culprits = rest;
            }
        }
        logger.LogInformation("Diagnóstico (refinamento por pessoa e dias): {Count} trecho(s) de {Total}", culprits.Count, refined.Count);
        var items = culprits.Select(k => model.Groups[k].Item)
            .OrderBy(i => i.Dates?.FirstOrDefault() ?? DateOnly.MaxValue).ThenBy(i => i.Mensagem).ToList();
        if (items.Count <= MaxPinpointItems) return items;
        var shown = items.Take(MaxPinpointItems).ToList();
        shown.Add(new DiagnosisItem("regra", $"... e mais {items.Count - MaxPinpointItems} trecho(s) em conflito com a mesma regra.",
            items.Skip(MaxPinpointItems).SelectMany(i => i.Dates ?? []).Distinct().Order().ToList(), Chave: items[0].Chave));
        return shown;
    }

    /// <summary>
    /// Sugestão de correção por mudança mínima: mantém a escala, libera só as células das pessoas/dias envolvidos
    /// nas violações (± 2 dias) e, para cobertura, quem está disponível no dia (± 1 dia); minimiza violações e
    /// número de células alteradas (etapa de correção: as regras viram penalidade só para PROPOR alterações).
    /// </summary>
    public SuggestionOutcome Suggest(SuggestCorrectionsCommand cmd)
    {
        var opt = options.Value;
        var full = PlanningContext.Create(cmd.Snapshot, cmd.StartDate, cmd.LastDate);
        var shiftIndex = full.Shifts.ToDictionary(s => s.Snapshot.ShiftId, s => s.Index);
        var sectorId = full.Sectors.Length > 0 ? full.Sectors[0].SectorId : Guid.Empty;
        var employeeIndex = full.Employees.ToDictionary(e => e.Snapshot.EmployeeId, e => e.Index);

        // Situação atual: turno (índice) por célula de trabalho; folgas manuais
        var current = new Dictionary<(int E, int D), int>();
        var currentCells = new Dictionary<(int E, int D), CellDto>();
        var folgas = new HashSet<(int E, int D)>();
        foreach (var c in cmd.Cells)
        {
            if (!employeeIndex.TryGetValue(c.EmployeeId, out var e)) continue;
            var d = full.DayIndex(c.Date);
            if (!full.InPeriod(d)) continue;
            if (c.ShiftId is { } sid && shiftIndex.TryGetValue(sid, out var t))
            {
                current[(e, d)] = t;
                currentCells[(e, d)] = c;
            }
            else if (c.Folga) folgas.Add((e, d));
        }
        // Avalia com os intervalos atuais (mesmo resultado gravado na escala)
        var before = evaluator.Evaluate(full, ToAssignments(full, current, sectorId, currentCells));
        if (before.TotalViolacoes == 0)
            return new SuggestionOutcome(SuggestionStatus.SemSolucao, before.Percentual, before.Percentual, [], "A escala já está 100% conforme.");

        // Células que podem mudar (só dias do período; os de borda são da escala vizinha)
        var mutable = new HashSet<(int E, int D)>();
        void Open(int e, int from, int to)
        {
            for (var d = Math.Max(full.FirstDay, from); d <= Math.Min(full.LastDay, to); d++) mutable.Add((e, d));
        }
        var coverageKeys = new HashSet<string> { RuleKeys.CoberturaFaixa, RuleKeys.CoberturaMinima, RuleKeys.MinimoAbertura, RuleKeys.MinimoFechamento, RuleKeys.ReforcoDemanda };
        foreach (var v in before.Violacoes)
        {
            var days = v.Dates.Select(full.DayIndex).ToList();
            if (v.EmployeeId is { } id && employeeIndex.TryGetValue(id, out var e))
            {
                if (days.Count == 0) Open(e, full.FirstDay, full.LastDay);
                foreach (var d in days) Open(e, d - 2, d + 2);
            }
            else if (coverageKeys.Contains(v.Chave))
            {
                foreach (var d in days.Where(full.InPeriod))
                foreach (var emp in full.Employees.Where(x => full.IsAvailable(x, d) && !x.Busy.ContainsKey(d)))
                    Open(emp.Index, d - 1, d + 1);
            }
        }

        var fixedCells = new List<CellDto>();
        var baseline = new Dictionary<(int E, int D), int?>();
        foreach (var emp in full.Employees)
        {
            for (var d = full.FirstDay; d <= full.LastDay; d++)
            {
                var has = current.TryGetValue((emp.Index, d), out var t);
                if (mutable.Contains((emp.Index, d)))
                {
                    baseline[(emp.Index, d)] = has ? t : null;
                    continue;
                }
                fixedCells.Add(has
                    ? new CellDto(emp.Snapshot.EmployeeId, full.Dates[d], full.Shifts[t].Snapshot.ShiftId, false)
                    : new CellDto(emp.Snapshot.EmployeeId, full.Dates[d], null, true));
            }
        }

        var ctx = PlanningContext.Create(cmd.Snapshot, cmd.StartDate, cmd.LastDate, fixedCells);
        var model = new ScheduleModel(ctx, SolveStage.Correction, opt, baseline: baseline);
        foreach (var ((e, d), t) in baseline)
        {
            // Dica: começar da escala atual
            foreach (var shift in ctx.Shifts)
            foreach (var s in ctx.Employees[e].Sectors)
                if (model.X.TryGetValue((e, d, shift.Index, s), out var x)) model.Model.AddHint((IntVar)x, t == shift.Index ? 1L : 0L);
        }
        var solver = NewSolver(Math.Max(5, cmd.TimeLimitSeconds), opt.Workers);
        var status = solver.Solve(model.Model);
        logger.LogInformation("Sugestão {SuggestionId}: {Status} em {Seconds:F1}s, {Mutable} célula(s) livres de {Total}",
            cmd.SuggestionId, status, solver.WallTime(), mutable.Count, full.Employees.Length * full.PeriodDays);
        if (status is not (CpSolverStatus.Optimal or CpSolverStatus.Feasible))
            return new SuggestionOutcome(SuggestionStatus.SemSolucao, before.Percentual, before.Percentual, [],
                $"O motor não encontrou correção no tempo limite (status {status}).");

        var proposed = new Dictionary<(int E, int D), int>();
        foreach (var ((e, d, t, _), x) in model.X)
            if (ctx.InPeriod(d) && solver.BooleanValue(x)) proposed[(e, d)] = t;

        var changes = new List<SuggestedChangeDto>();
        foreach (var (e, d) in baseline.Keys.OrderBy(k => k.D).ThenBy(k => full.Employees[k.E].Snapshot.Name))
        {
            var had = current.TryGetValue((e, d), out var from);
            var has = proposed.TryGetValue((e, d), out var to);
            if (had == has && (!had || from == to)) continue;
            changes.Add(new SuggestedChangeDto(full.Employees[e].Snapshot.EmployeeId, full.Dates[d],
                had ? full.Shifts[from].Snapshot.ShiftId : null, !had && folgas.Contains((e, d)),
                has ? full.Shifts[to].Snapshot.ShiftId : null, !has));
        }

        // Previsão como a API aplicaria: almoços dos dias alterados reposicionados (os manuais mantidos)
        var touched = changes.Select(c => c.Date).ToHashSet();
        var manualBreaks = currentCells.Values.Where(c => c.BreakManual).Select(c => (c.EmployeeId, c.Date)).ToHashSet();
        var proposedCells = ToAssignments(full, proposed, sectorId, currentCells);
        var planned = BreakPlanner.Plan(full, proposedCells,
            a => touched.Contains(a.Date) && !(manualBreaks.Contains((a.EmployeeId, a.Date)) && a.BreakStart is not null));
        var after = evaluator.Evaluate(full, planned);

        if (changes.Count == 0 || after.Percentual <= before.Percentual)
            return new SuggestionOutcome(SuggestionStatus.SemSolucao, before.Percentual, before.Percentual, [],
                "O motor não encontrou alterações que melhorem a conformidade sem mexer no resto da escala.");
        return new SuggestionOutcome(SuggestionStatus.Pronta, before.Percentual, after.Percentual, changes, null);
    }

    /// <summary>Células de trabalho; com <paramref name="breaks"/>, a célula que mantém o turno mantém o intervalo dela.</summary>
    private static List<AssignmentDto> ToAssignments(PlanningContext ctx, Dictionary<(int E, int D), int> cells, Guid sectorId,
        IReadOnlyDictionary<(int E, int D), CellDto>? breaks = null) =>
        cells.Select(kv =>
        {
            var shift = ctx.Shifts[kv.Value];
            var cell = breaks?.GetValueOrDefault(kv.Key);
            var same = cell is not null && cell.ShiftId == shift.Snapshot.ShiftId;
            var brk = same ? cell!.BreakMinutes : null;
            var paid = shift.EndMin - shift.StartMin - (brk ?? shift.Snapshot.BreakMinutes);
            return new AssignmentDto(ctx.Employees[kv.Key.E].Snapshot.EmployeeId, ctx.Dates[kv.Key.D], shift.Snapshot.ShiftId,
                sectorId, shift.Snapshot.Start, shift.Snapshot.End, paid, same ? cell!.BreakStart : null, brk);
        }).OrderBy(a => a.Date).ToList();

    /// <summary>
    /// Células escolhidas pelo motor, com o início do intervalo decidido no modelo (null = sem faixa no dia). Só os dias do
    /// período: o que o motor decidiu nos dias de borda (semana parcial do mês vizinho, sem escala lá) não é gravado.
    /// </summary>
    private static List<AssignmentDto> ReadAssignments(PlanningContext ctx, ScheduleModel model, CpSolver solver) =>
        model.X
            .Where(kv => ctx.InPeriod(kv.Key.Item2) && solver.BooleanValue(kv.Value))
            .Select(kv =>
            {
                var (e, d, t, s) = kv.Key;
                var shift = ctx.Shifts[t];
                var breakStart = model.BreakStartOf(v => solver.Value(v), e, d, s);
                return new AssignmentDto(ctx.Employees[e].Snapshot.EmployeeId, ctx.Dates[d], shift.Snapshot.ShiftId,
                    ctx.Sectors[s].SectorId, shift.Snapshot.Start, shift.Snapshot.End, shift.PaidMinutes,
                    breakStart is { } b ? TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(b % PlanningContext.MinutesPerDay)) : null);
            })
            .OrderBy(a => a.Date).ToList();

    /// <summary>
    /// Avisos do rodízio de abertura/fechamento (só com o rodízio ligado; células manuais não entram): "Abertura em rodízio:
    /// 4 turno(s) fora do perfil distribuídos entre Ana (2), Bruno (1), Carla (1)." — idem para fechamento e para horários
    /// intermediários feitos por quem é Abertura/Fechamento.
    /// </summary>
    internal static List<string> RotationNotices(PlanningContext ctx, IReadOnlyList<AssignmentDto> assignments)
    {
        var notices = new List<string>();
        if (!ctx.Rotation) return notices;
        var people = ctx.Employees.ToDictionary(e => e.Snapshot.EmployeeId);
        var outside = assignments
            .Select(a => (A: a, E: people.GetValueOrDefault(a.EmployeeId), T: ctx.ShiftById(a.ShiftId)))
            .Where(x => x.E is not null && x.T is not null && ctx.OutOfProfile(x.E, x.T) && !ctx.Fixed.ContainsKey((x.E.Index, ctx.DayIndex(x.A.Date))))
            .ToList();
        foreach (var kind in JourneyProfiles.All)
        {
            var list = outside.Where(x => x.T!.Kind == kind).ToList();
            if (list.Count == 0) continue;
            var who = list.GroupBy(x => x.E!.Snapshot.Name).OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
                .Select(g => $"{g.Key} ({g.Count()})").ToList();
            var names = string.Join(", ", who.Take(8)) + (who.Count > 8 ? $" e mais {who.Count - 8}" : "");
            notices.Add(kind == JourneyProfiles.Flexivel
                ? $"Horários intermediários fora do perfil: {list.Count} turno(s) feitos por quem é Abertura/Fechamento, para cobrir o mínimo — {names}."
                : $"{JourneyProfiles.Rotulo(kind)} em rodízio: {list.Count} turno(s) fora do perfil distribuídos entre {names}.");
        }
        return notices;
    }

    /// <summary>
    /// Aviso da prioridade de folga dupla (só com alguém priorizado na fotografia): "Folga dupla: 3 de 5 com prioridade
    /// atendidos (Ana, Bruno, Carla); pendentes: Diego (2 meses sem)." Quem tem mais meses sem vem primeiro. Não diz por que
    /// alguém ficou pendente (pode ser ausência: dado que não vai para o aviso — LGPD). Quem tem folgas fixas e intercaladas
    /// (setor de 2 pessoas, <paramref name="fixedDaysOff"/>) não entra: a folga dupla não se aplica a ele.
    /// </summary>
    internal static string? DoubleDayOffNotice(PlanningContext ctx, IReadOnlyList<AssignmentDto> assignments, IReadOnlySet<Guid>? fixedDaysOff = null)
    {
        var prioritized = ctx.Employees
            .Where(e => e.Sectors.Length > 0 && fixedDaysOff?.Contains(e.Snapshot.EmployeeId) != true)
            .Select(e => (E: e, N: FolgaDuplaHistory.MonthsWithout(ctx.Snapshot, e.Snapshot.EmployeeId)))
            .Where(x => x.N > 0)
            .OrderByDescending(x => x.N).ThenBy(x => x.E.Snapshot.Name)
            .ToList();
        if (prioritized.Count == 0) return null;
        var got = FolgaDuplaHistory.InPeriod(ctx, assignments);
        var served = prioritized.Where(x => got.GetValueOrDefault(x.E.Snapshot.EmployeeId) is not null).ToList();
        var pending = prioritized.Where(x => got.GetValueOrDefault(x.E.Snapshot.EmployeeId) is null).ToList();
        static string Names(IEnumerable<string> names)
        {
            var list = names.ToList();
            return string.Join(", ", list.Take(8)) + (list.Count > 8 ? $" e mais {list.Count - 8}" : "");
        }
        var text = $"Folga dupla: {served.Count} de {prioritized.Count} com prioridade atendido(s)";
        if (served.Count > 0) text += $" ({Names(served.Select(x => x.E.Snapshot.Name))})";
        if (pending.Count > 0) text += $"; pendentes: {Names(pending.Select(x => $"{x.E.Snapshot.Name} ({FolgaDuplaHistory.MonthsText(x.N)} sem)"))}";
        return text + ".";
    }

    /// <summary>
    /// Aviso das folgas fixas e intercaladas, por setor de 2 pessoas: "Setor Padaria tem 2 colaboradores: folgas fixas e
    /// intercaladas aplicadas (Ana: ter/sex, Bruno: qua/sáb); no domingo de folga (rodízio da regra de domingos) trabalha na
    /// folga fixa de sex (Ana) e sáb (Bruno)." O padrão vem do modelo; o domingo, das células.
    /// </summary>
    internal static List<string> FixedDaysOffNotices(PlanningContext ctx, IReadOnlyDictionary<int, (List<DayOfWeek> Days, DayOfWeek? Swap)> patterns,
        IReadOnlyList<AssignmentDto> assignments)
    {
        var notices = new List<string>();
        var views = ctx.FixedDaysOffViews(assignments).ToDictionary(v => v.EmployeeId);
        foreach (var pair in ctx.FixedDaysOffPairs)
        {
            var people = pair.People.Where(e => patterns.ContainsKey(e.Index)).ToList();
            if (people.Count == 0) continue;
            var text = $"Setor {ctx.Sectors[pair.Sector].Name} tem 2 colaboradores: folgas fixas e intercaladas aplicadas (" +
                       string.Join(", ", people.Select(e => $"{e.Snapshot.Name}: {PlanningContext.WeekdaysText(patterns[e.Index].Days)}")) + ")";
            var swaps = people
                .Where(e => patterns[e.Index].Swap is not null && views.GetValueOrDefault(e.Snapshot.EmployeeId)?.DomingosDeFolga > 0)
                .Select(e => $"{PlanningContext.WeekdayShort(patterns[e.Index].Swap!.Value)} ({e.Snapshot.Name})").ToList();
            if (swaps.Count > 0)
                text += $"; no domingo de folga (rodízio da regra de domingos) trabalha{(swaps.Count > 1 ? "m" : "")} na folga fixa de {string.Join(" e ", swaps)}";
            notices.Add(text + ".");
        }
        return notices;
    }

    /// <summary>Aviso quando o padrão fixo deixou a escala inviável e a geração seguiu com folgas variáveis.</summary>
    internal static string FixedDaysOffFallbackNotice(PlanningContext ctx) =>
        $"{string.Join(", ", ctx.FixedDaysOffPairs.Select(p => $"Setor {ctx.Sectors[p.Sector].Name} ({p.A.Snapshot.Name} e {p.B.Snapshot.Name})"))}: " +
        "folgas fixas e intercaladas não aplicadas — com elas não há escala que cumpra as regras rígidas e " +
        "as células manuais; a escala foi gerada com folgas variáveis.";

    private static List<DeficitDto> ReadDeficits(PlanningContext ctx, ScheduleModel model, CpSolver solver) =>
        model.Deficits
            .Select(d => (d.Slot, Missing: solver.Value(d.Deficit)))
            .Where(d => d.Missing > 0)
            .Select(d => new DeficitDto(ctx.Sectors[d.Slot.Sector].SectorId, ctx.Dates[d.Slot.Day],
                TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(d.Slot.SlotStart)), (int)d.Missing))
            .ToList();

    private static List<string> Merge(PlanningContext ctx, IEnumerable<string> warnings) => ctx.Warnings.Concat(warnings).Distinct().ToList();

    /// <summary>
    /// Resolve o modelo com o cancelamento ligado: o token cancelado chama <see cref="CpSolver.StopSearch"/> (o CP-SAT
    /// para na hora, inclusive na pré-resolução) e, depois que o Solve volta, lança <see cref="OperationCanceledException"/>
    /// — o status de uma busca interrompida não vale como resultado.
    /// </summary>
    internal static CpSolverStatus Run(CpSolver solver, CpModel model, SolutionCallback? callback, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        CpSolverStatus status;
        using (ct.Register(static s =>
               {
                   try { ((CpSolver)s!).StopSearch(); }
                   catch { /* o Solve já terminou */ }
               }, solver))
        {
            status = callback is null ? solver.Solve(model) : solver.Solve(model, callback);
        }
        ct.ThrowIfCancellationRequested();
        return status;
    }

    private static CpSolver NewSolver(double seconds, int workers) => new()
    {
        StringParameters = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"max_time_in_seconds:{Math.Max(0.01, seconds):0.###} num_workers:{workers} random_seed:42")
    };
}

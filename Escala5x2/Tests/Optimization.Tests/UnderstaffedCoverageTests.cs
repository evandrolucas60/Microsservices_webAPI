using Escala5x2.BuildingBlocks.Compliance;
using Escala5x2.BuildingBlocks.Contracts.Compliance;
using Escala5x2.BuildingBlocks.Contracts.Planning;
using Escala5x2.BuildingBlocks.Contracts.Scheduling;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Optimization.Worker.Engine;
using Xunit;
using Xunit.Abstractions;

namespace Optimization.Tests;

/// <summary>
/// Caso relatado (novembro/2026): setor com 3 ou 4 colaboradores, loja aberta das 07:00 às 22:00. Sem os intervalos bastam
/// 2 turnos por dia (14 na semana), mas com 1h de intervalo o intervalo de quem abre cai fora da sobreposição com quem fecha:
/// são 3 turnos por dia (21 na semana) e 3 pessoas no 5x2 fazem 15 (4 fazem 20). Com a opção
/// CoberturaComoMetaQuadroInsuficiente (padrão) a cobertura vira meta (a escala sai, regras legais rígidas, intervalos
/// revezados e aviso das faixas descobertas); desligada, a inviabilidade é provada por contagem com mensagem direta. Setor
/// que o quadro cobre (5 pessoas) continua com a cobertura rígida.
/// </summary>
[Collection(nameof(UnderstaffedCoverageTests))]
public sealed class UnderstaffedCoverageTests(ITestOutputHelper output)
{
    private static readonly ComplianceEvaluator Evaluator = new();
    private static readonly Guid Sector = Guid.NewGuid();
    private static readonly DateOnly November = new(2026, 11, 1);

    private static ScheduleSolver Solver() =>
        new(Options.Create(new SolverOptions { Workers = 8, DiagnosisSeconds = 6 }), Evaluator, NullLogger<ScheduleSolver>.Instance);

    /// <summary>Operação 07:00–22:00 (mínimo 1 em cada faixa, na abertura e no fechamento), jornada 8h20 com 1h de intervalo; 5x2 estrito.</summary>
    private static OperationSnapshot Op(bool target = true) =>
        new(new TimeOnly(7, 0), new TimeOnly(22, 0), 1, 1, 1, 15, null, null, 500, 60, CoberturaComoMetaQuadroInsuficiente: target);

    private static PlanningSnapshot Snap(OperationSnapshot op, params string[] profiles)
    {
        var people = profiles.Select((p, i) => new EmployeeSnapshot(Guid.NewGuid(), $"AC{i:000}", $"Pessoa{i + 1}", [Sector], Guid.NewGuid(),
            true, false, SundayRule.OneInThreeWeeks, [], null, new EmployeeJourneySnapshot(p))).ToList();
        return new PlanningSnapshot(Guid.NewGuid(), "Teste", false, [new SectorSnapshot(Sector, "Açougue")], [], [], [], people, null, [],
            ScheduleSolverTests.StrictRules(), null, op);
    }

    private static GenerateScheduleCommand MonthCommand(PlanningSnapshot snapshot, string mode = GenerationModes.Completa, IReadOnlyList<CellDto>? cells = null)
    {
        var (from, to) = PlanningContext.MonthOf(November);
        return new(Guid.NewGuid(), 1, from, PlanningContext.Horizon(from, to).Weeks, snapshot, 30, mode, cells ?? [], Guid.NewGuid(), to);
    }

    private SolveResult.Success Success(SolveResult result)
    {
        if (result is SolveResult.Infeasible bad)
        {
            output.WriteLine($"inviável: {bad.Reason}");
            foreach (var i in bad.Diagnosis?.Itens ?? []) output.WriteLine($"  {i.Tipo}: {i.Mensagem}");
        }
        if (result is SolveResult.TimedOut slow) output.WriteLine($"sem solução: {slow.Reason}");
        var ok = Assert.IsType<SolveResult.Success>(result);
        output.WriteLine($"{ok.Status} em {ok.Seconds:F1}s");
        output.WriteLine(ok.Notice ?? "(sem aviso)");
        foreach (var e in ok.Ctx.Employees)
        {
            var cells = Enumerable.Range(0, ok.Ctx.PeriodDays).Select(i => ok.Ctx.PeriodStart.AddDays(i)).Select(d =>
            {
                var a = ok.Assignments.FirstOrDefault(x => x.EmployeeId == e.Snapshot.EmployeeId && x.Date == d);
                return (d.DayOfWeek == DayOfWeek.Monday ? "| " : "") + (a is null ? "-----" : a.Start.ToString("HH:mm"));
            });
            output.WriteLine($"{e.Snapshot.Name,-8}{string.Join(" ", cells)}");
        }
        return ok;
    }

    /// <summary>Pares de pessoas do setor em intervalo ao mesmo tempo no mesmo dia.</summary>
    private static int SimultaneousBreaks(PlanningContext ctx, IReadOnlyList<AssignmentDto> cells) =>
        cells.GroupBy(a => a.Date).Sum(day =>
        {
            var times = day.Select(ctx.Times).Where(t => t.BreakStart is not null).ToList();
            var n = 0;
            for (var i = 0; i < times.Count; i++)
                for (var j = i + 1; j < times.Count; j++)
                    if (times[i].OnBreak(times[j].BreakStart!.Value, times[j].BreakEnd!.Value)) n++;
            return n;
        });

    /// <summary>A escala sai com a cobertura como meta, as regras legais cumpridas e o aviso com a conta e as faixas descobertas.</summary>
    private void AssertTargetCoverage(SolveResult.Success ok, int people, int capacity)
    {
        var ctx = ok.Ctx;
        Assert.Contains(ok.Status, new[] { EngineResults.Otima, EngineResults.Viavel });
        Assert.All(ok.Assignments, a => Assert.InRange(a.Date, ctx.PeriodStart, ctx.PeriodEnd));
        Assert.True(ctx.SoftCoverage(0));
        Assert.False(ctx.TwoPersonCoverage(0));
        Assert.Equal([Sector], ctx.CoverageTargetSectorIds());

        // Regras legais rígidas cumpridas; cobertura descoberta só como aviso (baixa) com a nota "cobertura como meta"
        var result = Evaluator.Evaluate(ctx, ok.Assignments);
        foreach (var v in result.Violacoes.Where(v => v.Severidade != "baixa")) output.WriteLine($"{v.Chave}: {v.Detalhe}");
        Assert.Equal(0, ComplianceEvaluator.RigidViolations(result));
        var coverage = result.Violacoes.Where(v => v.Chave is RuleKeys.CoberturaMinima or RuleKeys.MinimoAbertura or RuleKeys.MinimoFechamento).ToList();
        Assert.NotEmpty(coverage);
        Assert.All(coverage, v => Assert.Equal("baixa", v.Severidade));
        Assert.All(coverage, v => Assert.Contains("quadro insuficiente: cobertura como meta", v.Detalhe));

        // O avaliador do Scheduling.Api (fotografia gravada com os setores do resultado) chega ao mesmo
        var stored = PlanningContext.Create(ctx.Snapshot with { CoberturaMetaSetores = ctx.CoverageTargetSectorIds() }, ctx.PeriodStart, ctx.PeriodEnd);
        Assert.Equal(0, ComplianceEvaluator.RigidViolations(Evaluator.Evaluate(stored, ok.Assignments)));
        // ... e sem o registro a cobertura seria rígida (por isso ele é gravado)
        Assert.True(ComplianceEvaluator.RigidViolations(Evaluator.Evaluate(PlanningContext.Create(ctx.Snapshot, ctx.PeriodStart, ctx.PeriodEnd), ok.Assignments)) > 0);

        // Aviso direto: a conta com os intervalos e as faixas descobertas por causa
        Assert.Contains($"Setor Açougue tem {people} colaboradores: para cobrir 07:00–22:00 todos os dias com os intervalos são necessários 21 turnos/semana e o setor faz {capacity}", ok.Notice);
        Assert.Contains("Faixas descobertas: ", ok.Notice);
        Assert.Contains("no mês", ok.Notice);
        Assert.Contains("dimensionamento", ok.Notice);
        Assert.Contains(ok.Solutions!.Componentes, c => c.Chave == EngineComponents.FaltaCoberturaMeta);

        // Quase todo dia do mês tem alguém e quase todo o expediente fica coberto (o motor pode parar antes do ótimo)
        Assert.True(ok.Assignments.Select(a => a.Date).Distinct().Count() >= 28, "Mais de 2 dias sem ninguém no setor");
        var slots = ctx.Slots.Where(s => ctx.InPeriod(s.Day)).ToList();
        var byDay = ok.Assignments.GroupBy(a => ctx.DayIndex(a.Date)).ToDictionary(g => g.Key, g => g.ToList());
        var covered = slots.Count(s => ComplianceEvaluator.Presence(ctx, byDay.GetValueOrDefault(s.Day) ?? [], s).Present >= s.Minimum);
        output.WriteLine($"faixas cobertas: {covered} de {slots.Count}");
        Assert.True(covered >= slots.Count * 0.85, $"Só {covered} de {slots.Count} faixas cobertas");

        // Intervalos revezados: o setor quase nunca fica vazio porque dois estavam em intervalo ao mesmo tempo (com um
        // terceiro no setor, dois intervalos juntos não descobrem nada)
        var simultaneous = SimultaneousBreaks(ctx, ok.Assignments);
        var emptied = slots.Count(s => ComplianceEvaluator.Presence(ctx, byDay.GetValueOrDefault(s.Day) ?? [], s) is { Present: 0, OnBreak: >= 2 });
        output.WriteLine($"intervalos simultâneos: {simultaneous}; faixas vazias por intervalos juntos: {emptied}");
        Assert.True(emptied <= 2, $"{emptied} faixa(s) vazia(s) com dois ou mais em intervalo ao mesmo tempo");
    }

    [Fact]
    public void Three_people_with_opening_closing_and_flexible_generate_with_coverage_as_target()
    {
        var snap = Snap(Op(), JourneyProfiles.Abertura, JourneyProfiles.Fechamento, JourneyProfiles.Flexivel);
        var ctx = PlanningContext.Create(snap, November, new DateOnly(2026, 11, 30));
        // A contagem com os intervalos prova que o mínimo não fecha (sem eles fecharia: 14 ≤ 15)
        var week = InfeasibilityDiagnostics.WeeklyShifts(ctx, ctx.Slots.Where(s => ctx.InPeriod(s.Day))).First(w => w.Dates.Count == 7);
        Assert.Equal((14, 21, 15), (week.Need, week.NeedBreaks, week.Cap));

        AssertTargetCoverage(Success(Solver().Solve(MonthCommand(snap), 30)), 3, 15);
    }

    [Fact]
    public void Four_flexible_people_generate_with_coverage_as_target()
    {
        var snap = Snap(Op(), JourneyProfiles.Flexivel, JourneyProfiles.Flexivel, JourneyProfiles.Flexivel, JourneyProfiles.Flexivel);
        AssertTargetCoverage(Success(Solver().Solve(MonthCommand(snap), 30)), 4, 20);
    }

    [Fact]
    public void Five_people_keep_hard_coverage()
    {
        var snap = Snap(Op(), JourneyProfiles.Abertura, JourneyProfiles.Abertura, JourneyProfiles.Fechamento, JourneyProfiles.Fechamento,
            JourneyProfiles.Flexivel);
        var ctx = PlanningContext.Create(snap, November, new DateOnly(2026, 11, 30));
        Assert.Empty(InfeasibilityDiagnostics.Capacity(ctx).Items);

        var ok = Success(Solver().Solve(MonthCommand(snap), 30));
        Assert.False(ok.Ctx.SoftCoverage(0));
        Assert.Null(ok.Ctx.CoverageTargetSectorIds());
        Assert.All(ok.Ctx.Slots.Where(s => ok.Ctx.InPeriod(s.Day)), s => Assert.True(ok.Ctx.HardCoverage(s)));
        var result = Evaluator.Evaluate(ok.Ctx, ok.Assignments);
        foreach (var v in result.Violacoes.Where(v => v.Severidade != "baixa")) output.WriteLine($"{v.Chave}: {v.Detalhe}");
        Assert.Equal(0, ComplianceEvaluator.RigidViolations(result));
        Assert.DoesNotContain(result.Violacoes, v => v.Chave is RuleKeys.CoberturaMinima or RuleKeys.MinimoAbertura or RuleKeys.MinimoFechamento);
        Assert.DoesNotContain("cobertura mínima do setor é meta", ok.Notice ?? "");
    }

    [Fact]
    public void Option_off_proves_infeasibility_by_shift_count_mentioning_breaks()
    {
        var snap = Snap(Op(target: false), JourneyProfiles.Abertura, JourneyProfiles.Fechamento, JourneyProfiles.Flexivel);
        var bad = Assert.IsType<SolveResult.Infeasible>(Solver().Solve(MonthCommand(snap), 10));
        output.WriteLine(bad.Reason);
        Assert.Contains("provado por contagem", bad.Reason);
        Assert.Contains("Setor Açougue tem 3 colaboradores: para cobrir 07:00–22:00 com os intervalos são necessários 21 turnos/semana (3/dia) e o setor faz no máximo 15", bad.Reason);
        Assert.Contains("Sem contar os intervalos seriam 14", bad.Reason);
        Assert.False(bad.Ctx!.SoftCoverage(0));
    }

    [Fact]
    public void Day_with_fewer_people_than_shifts_is_proven_by_count()
    {
        // 5 pessoas (o quadro cobre o mês), mas em 10/11 três têm folga manual: 2 pessoas não cobrem 07:00–22:00 com os
        // intervalos (3 turnos). A conta por dia prova; com a opção desligada, mensagem direta
        var snap = Snap(Op(target: false), JourneyProfiles.Abertura, JourneyProfiles.Fechamento, JourneyProfiles.Flexivel,
            JourneyProfiles.Flexivel, JourneyProfiles.Flexivel);
        var day = new DateOnly(2026, 11, 10);
        var cells = snap.Employees.Skip(2).Select(e => new CellDto(e.EmployeeId, day, null, true)).ToList();
        var bad = Assert.IsType<SolveResult.Infeasible>(Solver().Solve(MonthCommand(snap, GenerationModes.SomenteVazios, cells), 10));
        output.WriteLine(bad.Reason);
        Assert.Contains("provado por contagem", bad.Reason);
        Assert.Contains("Setor Açougue, em 10/11 (ter): só 2 colaborador(es) podem trabalhar no dia", bad.Reason);
        Assert.Contains("com os intervalos pede 3 turnos (sem eles seriam 2)", bad.Reason);

        // Com a opção ligada: gera com a cobertura como meta SÓ em 10/11 (o resto do mês continua rígido) e as folgas
        // manuais ficam
        var ok = Success(Solver().Solve(MonthCommand(snap with { Operacao = Op() }, GenerationModes.SomenteVazios, cells), 30));
        var ctx = ok.Ctx;
        Assert.False(ctx.SoftCoverage(0));
        Assert.Null(ctx.CoverageTargetSectorIds());
        Assert.Equal([new CoberturaMetaDia(Sector, day)], ctx.CoverageTargetDayList());
        Assert.All(ctx.Slots.Where(s => ctx.InPeriod(s.Day)), s => Assert.Equal(ctx.Dates[s.Day] != day, ctx.HardCoverage(s)));
        Assert.Contains("Setor Açougue: em 10/11 (ter) há menos gente do que turnos", ok.Notice);
        var result = Evaluator.Evaluate(ctx, ok.Assignments);
        foreach (var v in result.Violacoes.Where(v => v.Severidade != "baixa")) output.WriteLine($"{v.Chave}: {v.Detalhe}");
        Assert.Equal(0, ComplianceEvaluator.RigidViolations(result));
        // Só 10/11 tem faixa descoberta (aviso), e o avaliador da fotografia gravada chega ao mesmo
        Assert.All(result.Violacoes.Where(v => v.Chave is RuleKeys.CoberturaMinima or RuleKeys.MinimoAbertura or RuleKeys.MinimoFechamento),
            v => Assert.Equal([day], v.Dates));
        var stored = PlanningContext.Create(ctx.Snapshot with { CoberturaMetaDias = ctx.CoverageTargetDayList() }, ctx.PeriodStart, ctx.PeriodEnd, cells);
        Assert.Equal(0, ComplianceEvaluator.RigidViolations(Evaluator.Evaluate(stored, ok.Assignments)));
        Assert.DoesNotContain(ok.Assignments, a => a.Date == day && cells.Any(c => c.EmployeeId == a.EmployeeId));
    }

    [Fact]
    public void Infeasibility_the_count_does_not_see_falls_back_to_coverage_as_target()
    {
        // Expediente 07:00–15:20 (2 turnos/dia com os intervalos = 14 na semana; 3 pessoas fazem 15) e as 3 com o domingo
        // quinzenal (1 domingo de folga a cada 2): 4 turnos em 2 domingos para 3 disponíveis. Com uma célula manual a conta
        // de domingos não roda (as células mudam a conta): o CP-SAT prova que não há escala e a geração refaz com a meta
        var op = Op() with { Fechamento = new TimeOnly(15, 20) };
        var snap = Snap(op, JourneyProfiles.Flexivel, JourneyProfiles.Flexivel, JourneyProfiles.Flexivel);
        snap = snap with { Employees = snap.Employees.Select(e => e with { SundayRule = SundayRule.Biweekly }).ToList() };
        var opening = PlanningContext.Create(snap, November, new DateOnly(2026, 11, 30)).Shifts
            .First(t => t.Kind == JourneyProfiles.Abertura && t.StartMin == 7 * 60);
        var cells = new List<CellDto> { new(snap.Employees[0].EmployeeId, new DateOnly(2026, 11, 4), opening.Snapshot.ShiftId, false) };
        var ctx = PlanningContext.Create(snap, November, new DateOnly(2026, 11, 30), cells);
        Assert.Empty(InfeasibilityDiagnostics.Capacity(ctx).Items);

        var ok = Success(Solver().Solve(MonthCommand(snap, GenerationModes.SomenteVazios, cells), 30));
        Assert.True(ok.Ctx.SoftCoverage(0));
        Assert.Equal([Sector], ok.Ctx.CoverageTargetSectorIds());
        Assert.Contains("Setor Açougue tem 3 colaboradores: com o quadro, os perfis de jornada e as regras legais", ok.Notice);
        Assert.Contains("Faixas descobertas: ", ok.Notice);
        var result = Evaluator.Evaluate(ok.Ctx, ok.Assignments);
        foreach (var v in result.Violacoes.Where(v => v.Severidade != "baixa")) output.WriteLine($"{v.Chave}: {v.Detalhe}");
        // Regra de domingos (quinzenal) e demais regras legais rígidas cumpridas; a célula manual fica
        Assert.Equal(0, ComplianceEvaluator.RigidViolations(result));
        Assert.Contains(ok.Assignments, a => a.EmployeeId == cells[0].EmployeeId && a.Date == cells[0].Date && a.ShiftId == opening.Snapshot.ShiftId);
        // Só os domingos ficam descobertos
        var uncovered = ok.Ctx.Slots.Where(s => ok.Ctx.InPeriod(s.Day))
            .Where(s => ComplianceEvaluator.Presence(ok.Ctx, ok.Assignments.Where(a => a.Date == ok.Ctx.Dates[s.Day]), s).Present < s.Minimum)
            .Select(s => ok.Ctx.Dates[s.Day]).Distinct().ToList();
        output.WriteLine($"dias com faixa descoberta: {string.Join(", ", uncovered.Select(d => d.ToString("dd/MM ddd")))}");
        Assert.NotEmpty(uncovered);
        Assert.All(uncovered, d => Assert.Equal(DayOfWeek.Sunday, d.DayOfWeek));
    }
}

/// <summary>Mês inteiro com 30 min de faixa: roda sem outros testes do CP-SAT em paralelo (a qualidade depende do tempo).</summary>
[CollectionDefinition(nameof(UnderstaffedCoverageTests), DisableParallelization = true)]
public sealed class UnderstaffedCoverageTestsCollection;

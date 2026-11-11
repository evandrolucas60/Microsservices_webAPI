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
/// Caso relatado (novembro/2026): setor com 2 colaboradores, loja aberta das 07:00 às 22:00 todos os dias. São 2 turnos por
/// dia (14 na semana) e 2 pessoas no 5x2 fazem 10: o mínimo de cobertura não fecha. No setor de 2 pessoas a cobertura é META
/// (a escala sai, com folgas fixas e intercaladas, regras legais rígidas e aviso das faixas descobertas); com a opção
/// desligada, a inviabilidade é provada por contagem com mensagem direta. Dias de borda (semana parcial do mês vizinho)
/// nunca exigem cobertura desta escala.
/// </summary>
[Collection(nameof(TwoPersonCoverageTests))]
public sealed class TwoPersonCoverageTests(ITestOutputHelper output)
{
    private static readonly ComplianceEvaluator Evaluator = new();
    private static readonly DateTime Now = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Sector = Guid.NewGuid();
    private static readonly DateOnly November = new(2026, 11, 1);

    private static ScheduleSolver Solver() =>
        new(Options.Create(new SolverOptions { Workers = 8, DiagnosisSeconds = 6 }), Evaluator, NullLogger<ScheduleSolver>.Instance);

    /// <summary>Operação 07:00–22:00 (mínimo 1 em cada faixa, na abertura e no fechamento), jornada 8h20 com 1h de intervalo; 5x2 estrito.</summary>
    private static OperationSnapshot Op(bool fixedDaysOff = true, TimeOnly? close = null, bool target = true) =>
        new(new TimeOnly(7, 0), close ?? new TimeOnly(22, 0), 1, 1, 1, 15, null, null, 500, 60, FolgasFixasSetorDuasPessoas: fixedDaysOff,
            CoberturaComoMetaQuadroInsuficiente: target);

    private static PlanningSnapshot Snap(OperationSnapshot op, params string[] names)
    {
        if (names.Length == 0) names = ["Ana", "Bruno"];
        var people = names.Select((n, i) => new EmployeeSnapshot(Guid.NewGuid(), $"DP{i:000}", n, [Sector], Guid.NewGuid(),
            true, false, SundayRule.OneInThreeWeeks, [])).ToList();
        return new PlanningSnapshot(Guid.NewGuid(), "Teste", false, [new SectorSnapshot(Sector, "Padaria")], [], [], [], people, null, [],
            ScheduleSolverTests.StrictRules(), null, op);
    }

    private static GenerateScheduleCommand MonthCommand(DateOnly anyDay, PlanningSnapshot snapshot)
    {
        var (from, to) = PlanningContext.MonthOf(anyDay);
        return new(Guid.NewGuid(), 1, from, PlanningContext.Horizon(from, to).Weeks, snapshot, 30, GenerationModes.Completa, [], Guid.NewGuid(), to);
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
        return ok;
    }

    private void Print(SolveResult.Success ok)
    {
        foreach (var e in ok.Ctx.Employees)
        {
            var cells = Enumerable.Range(0, ok.Ctx.PeriodDays).Select(i => ok.Ctx.PeriodStart.AddDays(i)).Select(d =>
            {
                var a = ok.Assignments.FirstOrDefault(x => x.EmployeeId == e.Snapshot.EmployeeId && x.Date == d);
                return (d.DayOfWeek == DayOfWeek.Monday ? "| " : "") + (a is null ? "-----" : a.Start.ToString("HH:mm"));
            });
            output.WriteLine($"{e.Snapshot.Name,-6}{string.Join(" ", cells)}");
        }
    }

    /// <summary>A escala do mês sai (Ótima/Viável) com folgas fixas e intercaladas, regras legais cumpridas e o aviso das faixas descobertas.</summary>
    private void AssertTargetCoverage(SolveResult.Success ok)
    {
        Print(ok);
        var ctx = ok.Ctx;
        Assert.Contains(ok.Status, new[] { EngineResults.Otima, EngineResults.Viavel });
        Assert.All(ok.Assignments, a => Assert.InRange(a.Date, ctx.PeriodStart, ctx.PeriodEnd));
        Assert.True(ctx.SoftCoverage(0));
        Assert.DoesNotContain(ctx.Slots, s => ctx.HardCoverage(s));

        // Folgas fixas e intercaladas: 2 dias da semana por pessoa, sem dia em comum; alguém trabalha todo dia
        Assert.Contains("folgas fixas e intercaladas aplicadas", ok.Notice);
        var views = ctx.FixedDaysOffViews(ok.Assignments);
        Assert.Equal(2, views.Count);
        // (o domingo é rodízio da regra de domingos: pode aparecer no observado de quem folga em mais da metade deles)
        var weekdays = views.Select(v => v.Dias.Where(x => x != DayOfWeek.Sunday).ToList()).ToList();
        Assert.All(weekdays, w => Assert.Equal(2, w.Count));
        Assert.Empty(weekdays[0].Intersect(weekdays[1]));
        for (var d = ctx.PeriodStart; d <= ctx.PeriodEnd; d = d.AddDays(1))
        {
            var day = d;
            Assert.Contains(ok.Assignments, a => a.Date == day);
        }

        // Regras legais rígidas cumpridas; cobertura descoberta só como aviso (baixa), nunca rígida
        var result = Evaluator.Evaluate(ctx, ok.Assignments);
        foreach (var v in result.Violacoes.Where(v => v.Severidade != "baixa")) output.WriteLine($"{v.Chave}: {v.Detalhe}");
        Assert.Equal(0, ComplianceEvaluator.RigidViolations(result));
        var coverage = result.Violacoes.Where(v => v.Chave is RuleKeys.CoberturaMinima or RuleKeys.MinimoAbertura or RuleKeys.MinimoFechamento).ToList();
        Assert.NotEmpty(coverage);
        Assert.All(coverage, v => Assert.Equal("baixa", v.Severidade));
        Assert.All(result.Violacoes.SelectMany(v => v.Dates), d => Assert.InRange(d, ctx.PeriodStart, ctx.PeriodEnd));
        Assert.All(ok.Deficits, d => Assert.InRange(d.Date, ctx.PeriodStart, ctx.PeriodEnd));

        // Nos dias com os dois, abertura e fechamento cobertos (um abre e o outro fecha); com um só, ele faz a sua ponta
        var opening = ctx.Slots.Where(s => ctx.InPeriod(s.Day) && s.MinimumKey == RuleKeys.MinimoAbertura).ToDictionary(s => ctx.Dates[s.Day]);
        var closing = ctx.Slots.Where(s => ctx.InPeriod(s.Day) && s.MinimumKey == RuleKeys.MinimoFechamento).ToDictionary(s => ctx.Dates[s.Day]);
        // (o motor pode parar antes do ótimo: tolera 2 dias com o intervalo de quem está sozinho na ponta do dia)
        var both = 0;
        var neither = new List<DateOnly>();
        foreach (var (date, slot) in opening)
        {
            var cells = ok.Assignments.Where(a => a.Date == date).ToList();
            var opened = ComplianceEvaluator.Presence(ctx, cells, slot).Present >= 1;
            var closed = ComplianceEvaluator.Presence(ctx, cells, closing[date]).Present >= 1;
            if (!opened && !closed) neither.Add(date);
            if (cells.Count == 2 && opened && closed) both++;
        }
        Assert.True(neither.Count <= 2, $"Nem abertura nem fechamento em {string.Join(", ", neither.Select(d => d.ToString("dd/MM")))}");
        // Folgas fixas: 3 dias com os dois por semana (as semanas com domingo de folga trocam um dia)
        Assert.True(both >= 10, $"Só {both} dia(s) com abertura e fechamento cobertos pelos dois");

        // Aviso direto e agregado: a conta de turnos e as faixas descobertas por dia da semana
        Assert.Contains("Setor Padaria tem 2 colaboradores: não é possível cobrir 07:00–22:00 todos os dias (são 14 turnos por semana e 2 pessoas fazem 10)", ok.Notice);
        Assert.Contains("Faixas descobertas: ", ok.Notice);
        Assert.Contains("dimensionamento", ok.Notice);
        Assert.DoesNotContain("/10", ok.Notice);
        Assert.DoesNotContain("/12", ok.Notice);
        Assert.Contains(ok.Solutions!.Componentes, c => c.Chave == EngineComponents.FaltaCoberturaMeta);
    }

    [Fact]
    public void Two_person_sector_open_07_to_22_without_previous_month_generates_with_coverage_as_target()
    {
        var snap = Snap(Op());
        var ctx = PlanningContext.Create(snap, November, new DateOnly(2026, 11, 30));
        // Sem escala de outubro: 26 a 31/10 (e 01 a 06/12) são dias de borda indefinidos — têm faixa, mas só como meta
        Assert.Contains(ctx.Slots, s => s.Day == 0);
        Assert.False(ctx.HardCoverage(ctx.Slots.First(s => s.Day == 0)));

        AssertTargetCoverage(Success(Solver().Solve(MonthCommand(November, snap), 30)));
    }

    [Fact]
    public void Two_person_sector_open_07_to_22_with_october_schedule_generates_with_coverage_as_target()
    {
        var snap = Snap(Op());
        var names = snap.Employees.ToDictionary(e => e.EmployeeId, e => e.Name);
        var rules = LegacyRules.Of(snap);

        // Outubro gerado antes (escala principal do setor): 26 a 31/10 vêm dela (trabalho/folga registrados)
        var october = Success(Solver().Solve(MonthCommand(new DateOnly(2026, 10, 1), snap), 25));
        Assert.All(october.Assignments, a => Assert.Equal(10, a.Date.Month));
        var schedule = new ContinuitySchedule(Guid.NewGuid(), Sector, "Padaria", 1, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), 0, Now,
            "Publicada", true, Principal: true);
        var cells = october.Assignments.Select(a => new ContinuityCell(schedule.Id, a.EmployeeId, a.Date, a.Start, a.End, false)).ToList();
        var previous = ContinuityHistory.Previous([schedule], cells, November, PlanningContext.ContinuityDaysFor(rules) + 6, names,
            restWindow: PlanningContext.RestWindowFor(rules) + 6);
        var monthSnap = snap with { History = previous.History, HistoryRest = previous.Rest, HistoryNotices = previous.Notices };

        var ctx = PlanningContext.Create(monthSnap, November, new DateOnly(2026, 11, 30));
        Assert.All(ctx.Employees, e => Assert.DoesNotContain(e.EdgeUnknown, d => d < ctx.FirstDay));
        Assert.DoesNotContain(ctx.Slots, s => s.Day < ctx.FirstDay); // a cobertura de 26 a 31/10 é de outubro

        var ok = Success(Solver().Solve(MonthCommand(November, monthSnap), 30));
        AssertTargetCoverage(ok);

        // Emenda: outubro + novembro avaliados como uma linha do tempo, sem quebrar as regras legais
        var timeline = Evaluator.Evaluate(PlanningContext.Create(snap, new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 30)),
            october.Assignments.Concat(ok.Assignments).ToList());
        foreach (var v in timeline.Violacoes.Where(v => v.Severidade != "baixa")) output.WriteLine($"linha do tempo: {v.Chave}: {v.Detalhe}");
        Assert.Equal(0, ComplianceEvaluator.RigidViolations(timeline));
    }

    [Fact]
    public void Option_off_proves_infeasibility_by_shift_count_with_direct_message()
    {
        // As duas opções desligadas (com a cobertura como meta por quadro insuficiente ligada, o setor geraria com meta)
        var snap = Snap(Op(fixedDaysOff: false, target: false));
        var result = Solver().Solve(MonthCommand(November, snap), 10);
        var bad = Assert.IsType<SolveResult.Infeasible>(result);
        output.WriteLine(bad.Reason);
        Assert.Contains("provado por contagem", bad.Reason);
        Assert.Contains("Setor Padaria tem 2 colaboradores: no máximo 10 turnos/semana para 14 necessários (07:00–22:00 com 2 turno(s)/dia", bad.Reason);
        Assert.Contains("Aumente o quadro ou reduza o expediente/cobertura.", bad.Reason);
        // Só semanas do mês (a semana parcial com 01/11 fecha: 2 turnos para 2 pessoas)
        Assert.All(bad.Diagnosis!.Itens.SelectMany(i => i.Dates ?? []), d => Assert.Equal(11, d.Month));
    }

    [Fact]
    public void Border_days_without_neighbour_never_make_the_month_infeasible()
    {
        // 3 pessoas (setor comum: cobertura rígida no mês), expediente 07:00–15:20 (1 turno/dia). Todos de férias de 26 a
        // 28/10 (dias de borda sem escala de outubro): ninguém cobre esses dias — antes a faixa de borda era mínimo rígido
        // e a escala de novembro saía inviável por um dia que ela nem grava
        var snap = Snap(Op(close: new TimeOnly(15, 20)), "Ana", "Bruno", "Carla");
        var away = new[] { new DateOnly(2026, 10, 26), new DateOnly(2026, 10, 27), new DateOnly(2026, 10, 28) };
        snap = snap with { Employees = snap.Employees.Select(e => e with { UnavailableDates = away }).ToList() };

        var ctx = PlanningContext.Create(snap, November, new DateOnly(2026, 11, 30));
        Assert.Empty(ctx.FixedDaysOffPairs);
        Assert.Contains(ctx.Slots, s => s.Day == 0);
        Assert.All(ctx.Slots, s => Assert.Equal(ctx.InPeriod(s.Day), ctx.HardCoverage(s)));
        Assert.Empty(InfeasibilityDiagnostics.Capacity(ctx).Items);

        var ok = Success(Solver().Solve(MonthCommand(November, snap), 20));
        Assert.All(ok.Assignments, a => Assert.Equal(11, a.Date.Month));
        var result = Evaluator.Evaluate(ok.Ctx, ok.Assignments);
        foreach (var v in result.Violacoes.Where(v => v.Severidade != "baixa")) output.WriteLine($"{v.Chave}: {v.Detalhe}");
        Assert.Equal(0, ComplianceEvaluator.RigidViolations(result));
        // Todo dia do mês coberto (mínimo rígido no período)
        Assert.Equal(30, ok.Assignments.Select(a => a.Date).Distinct().Count());
    }
}

/// <summary>Mês inteiro com 30 min de faixa: roda sem outros testes do CP-SAT em paralelo (a qualidade depende do tempo).</summary>
[CollectionDefinition(nameof(TwoPersonCoverageTests), DisableParallelization = true)]
public sealed class TwoPersonCoverageTestsCollection;

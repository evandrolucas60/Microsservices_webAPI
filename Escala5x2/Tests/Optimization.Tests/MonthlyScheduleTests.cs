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
/// Escala por MÊS civil: o período vai do dia 1º ao último dia (28, 29, 30 ou 31 dias) e o horizonte do motor/avaliador são
/// as semanas civis inteiras que o cobrem. Os dias de borda (semana parcial do mês vizinho) entram nas contas da semana
/// (folgas exatas do 5x2, piso, carga, folgas seguidas) com o que a escala vizinha registrou — sem contar duas vezes — e
/// não são gravados; sem escala vizinha, o avaliador aceita qualquer valor possível neles.
/// </summary>
public sealed class MonthlyScheduleTests(ITestOutputHelper output)
{
    private static readonly ComplianceEvaluator Evaluator = new();
    private static readonly DateTime Now = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

    private static ScheduleSolver Solver() =>
        new(Options.Create(new SolverOptions { Workers = 4 }), Evaluator, NullLogger<ScheduleSolver>.Instance);

    /// <summary>Comando como o Scheduling.Api manda para a escala do mês: semanas civis do horizonte e o último dia.</summary>
    private static GenerateScheduleCommand MonthCommand(DateOnly anyDay, PlanningSnapshot snapshot, int seconds = 20)
    {
        var (from, to) = PlanningContext.MonthOf(anyDay);
        return new(Guid.NewGuid(), 1, from, PlanningContext.Horizon(from, to).Weeks, snapshot, seconds, GenerationModes.Completa, [],
            Guid.NewGuid(), to);
    }

    /// <summary>9 pessoas no setor (sobra quadro), regras padrão do cadastro (5x2 em 2027: 2 folgas, máx. 5 dias e 2 folgas seguidas).</summary>
    private static PlanningSnapshot Snap()
    {
        var baseSnap = ScheduleSolverTests.Snapshot(rules: ScheduleSolverTests.DefaultRules());
        var sector = baseSnap.Sectors[0].SectorId;
        var people = Enumerable.Range(0, 9).Select(i => new EmployeeSnapshot(Guid.NewGuid(), $"M{i:000}", $"Pessoa {i + 1}", [sector], Guid.NewGuid(),
            true, false, SundayRule.OneInThreeWeeks, [])).ToList();
        return baseSnap with { Employees = people };
    }

    private SolveResult.Success Success(SolveResult result)
    {
        if (result is SolveResult.Infeasible bad)
        {
            output.WriteLine($"inviável: {bad.Reason}");
            foreach (var i in bad.Diagnosis?.Itens ?? []) output.WriteLine($"  {i.Tipo}: {i.Mensagem}");
        }
        if (result is SolveResult.TimedOut slow) output.WriteLine($"sem solução: {slow.Reason}");
        return Assert.IsType<SolveResult.Success>(result);
    }

    /// <summary>Regras de semana/janela que a emenda entre dois meses não pode quebrar.</summary>
    private static readonly string[] SeamKeys =
    [
        RuleKeys.DiasConsecutivosMax5x2, RuleKeys.DiasConsecutivosMax6x1, RuleKeys.InterjornadaMinima, RuleKeys.FolgaIsoladaHorasLivres,
        RuleKeys.DomingoFolgaJanela, RuleKeys.MaxFolgasSeguidas, RuleKeys.FolgasSemana5x2, RuleKeys.FolgasSemana6x1,
        RuleKeys.FolgasMinimasSemana, RuleKeys.CargaHorariaSemanal
    ];

    [Theory]
    [InlineData(2027, 2, 28, 0, 0)]  // fevereiro comum: 01/02 é segunda e 28/02 domingo (sem borda)
    [InlineData(2028, 2, 29, 1, 5)]  // fevereiro bissexto: terça 01/02 a terça 29/02
    [InlineData(2027, 4, 30, 3, 2)]  // 30 dias: quinta 01/04 a sexta 30/04
    [InlineData(2027, 1, 31, 4, 0)]  // 31 dias: sexta 01/01 a domingo 31/01
    [InlineData(2026, 11, 30, 6, 6)] // começa no domingo e termina na segunda: 6 dias de borda de cada lado
    public void Month_period_has_the_size_of_the_month_and_the_horizon_is_whole_civil_weeks(int y, int m, int days, int before, int after)
    {
        var (from, to) = PlanningContext.MonthOf(new DateOnly(y, m, 15));
        Assert.Equal(new DateOnly(y, m, 1), from);
        Assert.Equal(days, to.DayNumber - from.DayNumber + 1);

        var ctx = PlanningContext.Create(Snap(), from, to);
        Assert.Equal(days, ctx.PeriodDays);
        Assert.Equal(DayOfWeek.Monday, ctx.Dates[0].DayOfWeek);
        Assert.Equal(DayOfWeek.Sunday, ctx.Dates[^1].DayOfWeek);
        Assert.Equal(before, ctx.FirstDay);
        Assert.Equal(after, ctx.Days - 1 - ctx.LastDay);
        Assert.Equal(from, ctx.Dates[ctx.FirstDay]);
        Assert.Equal(to, ctx.Dates[ctx.LastDay]);
        // Sem escala vizinha os dias de borda ficam indefinidos e têm faixa (só para o motor planejar a semana inteira)
        Assert.All(ctx.Employees, e => Assert.Equal(before + after, e.EdgeUnknown.Count));
        Assert.Equal(before + after, ctx.Slots.Where(s => !ctx.InPeriod(s.Day)).Select(s => s.Day).Distinct().Count());

        // Com a escala vizinha registrada no dia de borda (trabalho de alguém), a cobertura do dia é dela: sem faixa
        if (before > 0)
        {
            var snap = Snap();
            var known = PlanningContext.Create(snap with
            {
                History = [new HistoryEntry(snap.Employees[0].EmployeeId, from.AddDays(-1), new TimeOnly(7, 0), new TimeOnly(15, 20))]
            }, from, to);
            Assert.DoesNotContain(known.Slots, s => s.Day == known.FirstDay - 1);
            Assert.True(known.Employees[0].Busy.ContainsKey(known.FirstDay - 1));
        }

        // Escala por semanas (as antigas): período = horizonte, sem borda
        var weekly = PlanningContext.Create(Snap(), ctx.Start, ctx.Weeks);
        Assert.Equal(0, weekly.FirstDay);
        Assert.Equal(weekly.Days - 1, weekly.LastDay);
    }

    [Theory]
    [InlineData(2027, 2)]  // 28 dias
    [InlineData(2028, 2)]  // 29 dias (bissexto), começa na terça
    [InlineData(2027, 4)]  // 30 dias, começa na quinta
    [InlineData(2027, 1)]  // 31 dias, começa na sexta
    public void Generated_month_has_cells_only_inside_the_month_and_is_compliant(int y, int m)
    {
        var cmd = MonthCommand(new DateOnly(y, m, 1), Snap());
        var ok = Success(Solver().Solve(cmd, seconds: 20));

        Assert.All(ok.Assignments, a => Assert.InRange(a.Date, cmd.StartDate, cmd.LastDate));
        // Todos os dias do mês têm gente (cobertura): o quadro tem exatamente o tamanho do mês
        Assert.Equal(cmd.LastDate.DayNumber - cmd.StartDate.DayNumber + 1, ok.Assignments.Select(a => a.Date).Distinct().Count());
        var result = Evaluator.Evaluate(ok.Ctx, ok.Assignments);
        foreach (var v in result.Violacoes) output.WriteLine($"{v.Chave}: {v.Detalhe}");
        Assert.Equal(0, ComplianceEvaluator.RigidViolations(result));
        // Violações e horas apontam só dias do mês; a semana parcial informa os dias do período
        Assert.All(result.Violacoes.SelectMany(v => v.Dates), d => Assert.InRange(d, cmd.StartDate, cmd.LastDate));
        Assert.All(result.HorasSemanais!, h => Assert.InRange(h.DiasNoPeriodo!.Value, 1, 7));
        Assert.Equal(cmd.LastDate.DayNumber - cmd.StartDate.DayNumber + 1,
            result.HorasSemanais!.Where(h => h.EmployeeId == ok.Ctx.Employees[0].Snapshot.EmployeeId).Sum(h => h.DiasNoPeriodo!.Value));
    }

    [Fact]
    public void Next_month_counts_the_shared_civil_week_together_with_the_previous_month()
    {
        // Março/2027 (segunda 01 a quarta 31) gerado sem vizinhos; depois abril (quinta 01 a sexta 30) com a continuidade de
        // março, montada como o Scheduling.Api (ContinuityHistory). A semana 29/03–04/04 é dividida entre os dois meses: as 2
        // folgas do 5x2, o máximo de 5 dias seguidos e de 2 folgas seguidas valem na semana inteira, sem contar duas vezes.
        var snap = Snap();
        var march = Success(Solver().Solve(MonthCommand(new DateOnly(2027, 3, 1), snap), seconds: 20));
        Assert.All(march.Assignments, a => Assert.Equal(3, a.Date.Month));

        var marchSchedule = new ContinuitySchedule(Guid.NewGuid(), snap.Sectors[0].SectorId, snap.Sectors[0].Name, 1, new DateOnly(2027, 3, 1),
            new DateOnly(2027, 3, 31), 2, Now, "Gerada", Published: false, Principal: true);
        var cells = march.Assignments.Select(a => new ContinuityCell(marchSchedule.Id, a.EmployeeId, a.Date, a.Start, a.End, false)).ToList();
        var rules = LegacyRules.Of(snap);
        var aprilStart = new DateOnly(2027, 4, 1);
        // Mais os dias de borda (até 6), como o Scheduling.Api
        var previous = ContinuityHistory.Previous([marchSchedule], cells, aprilStart, PlanningContext.ContinuityDaysFor(rules) + 6,
            snap.Employees.ToDictionary(e => e.EmployeeId, e => e.Name), restWindow: PlanningContext.RestWindowFor(rules) + 6);
        var aprilSnap = snap with { History = previous.History, HistoryRest = previous.Rest, HistoryNotices = previous.Notices };

        var aprilCmd = MonthCommand(aprilStart, aprilSnap);
        var april = Success(Solver().Solve(aprilCmd, seconds: 20));
        Assert.All(april.Assignments, a => Assert.Equal(4, a.Date.Month));
        // Dias de borda de abril (29 a 31/03) vieram de março: trabalho = ocupado, folga = folga registrada; nada indefinido
        Assert.All(april.Ctx.Employees, e => Assert.DoesNotContain(e.EdgeUnknown, d => d < april.Ctx.FirstDay));
        var own = Evaluator.Evaluate(april.Ctx, april.Assignments);
        foreach (var v in own.Violacoes) output.WriteLine($"abril: {v.Chave}: {v.Detalhe}");
        Assert.Equal(0, ComplianceEvaluator.RigidViolations(own));

        // Semana dividida: cada pessoa tem exatamente 2 folgas de 29/03 a 04/04 (somando os dois meses)
        var all = march.Assignments.Concat(april.Assignments).ToList();
        var week = Enumerable.Range(0, 7).Select(i => new DateOnly(2027, 3, 29).AddDays(i)).ToList();
        foreach (var e in snap.Employees)
            Assert.Equal(2, week.Count(d => !all.Any(a => a.EmployeeId == e.EmployeeId && a.Date == d)));

        // Linha do tempo março + abril avaliada como uma escala só: nenhuma regra de semana/janela quebra na emenda
        var timeline = Evaluator.Evaluate(PlanningContext.Create(snap, new DateOnly(2027, 3, 1), new DateOnly(2027, 4, 30)), all);
        foreach (var v in timeline.Violacoes.Where(v => SeamKeys.Contains(v.Chave))) output.WriteLine($"linha do tempo: {v.Chave}: {v.Detalhe}");
        Assert.DoesNotContain(timeline.Violacoes, v => SeamKeys.Contains(v.Chave));
    }

    [Fact]
    public void Partial_week_without_neighbour_accepts_any_possible_value_and_with_neighbour_counts_it()
    {
        // Janeiro/2027 começa na sexta: a 1ª semana civil é 28/12/2026 a 03/01/2027 (5x2 em 2027: exatamente 2 folgas)
        var snap = Snap();
        var (from, to) = PlanningContext.MonthOf(new DateOnly(2027, 1, 1));
        var person = snap.Employees[0];
        var ctx = PlanningContext.Create(snap, from, to);
        var t = ctx.Shifts.First(s => s.Snapshot.Code == "M");
        AssignmentDto Work(DateOnly d) => new(person.EmployeeId, d, t.Snapshot.ShiftId, ctx.Sectors[0].SectorId, t.Snapshot.Start, t.Snapshot.End, t.PaidMinutes);
        string[] weekKeys = [RuleKeys.FolgasSemana5x2, RuleKeys.FolgasMinimasSemana];
        bool FirstWeekViolated(ComplianceResult r) =>
            r.Violacoes.Any(v => v.EmployeeId == person.EmployeeId && weekKeys.Contains(v.Chave) && v.Dates.All(d => d <= new DateOnly(2027, 1, 3)));

        // Sem escala de dezembro: trabalhar sex/sáb/dom não viola (as 2 folgas ainda cabem de 28 a 31/12)
        var worksWeekend = new[] { 1, 2, 3 }.Select(i => Work(new DateOnly(2027, 1, i))).ToList();
        Assert.False(FirstWeekViolated(Evaluator.Evaluate(ctx, worksWeekend)));
        // 3 folgas já no mês (sex, sáb e dom vazios) passam das 2 da semana: viola mesmo sem dezembro
        var offWeekend = Evaluator.Evaluate(ctx, [Work(new DateOnly(2027, 1, 4))]);
        Assert.True(FirstWeekViolated(offWeekend));

        // Com dezembro registrado (trabalhou de 28 a 31/12): a semana tem 0 folga se trabalhar sex/sáb/dom → viola, citando a emenda
        var history = Enumerable.Range(28, 4).Select(d => new HistoryEntry(person.EmployeeId, new DateOnly(2026, 12, d), new TimeOnly(7, 0), new TimeOnly(15, 20))).ToList();
        var withDecember = PlanningContext.Create(snap with { History = history }, from, to);
        Assert.True(withDecember.Employees[0].Busy.ContainsKey(0));
        var seam = Evaluator.Evaluate(withDecember, worksWeekend);
        var v = Assert.Single(seam.Violacoes, x => x.EmployeeId == person.EmployeeId && x.Chave == RuleKeys.FolgasSemana5x2
                                                     && x.Dates.All(d => d <= new DateOnly(2027, 1, 3)));
        Assert.Contains("emenda", v.Detalhe);
        // As datas apontadas são só as do mês (as de dezembro não estão na grade de janeiro)
        Assert.All(v.Dates, d => Assert.Equal(1, d.Month));
    }
}

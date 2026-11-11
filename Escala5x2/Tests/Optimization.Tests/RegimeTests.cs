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
/// Regime da semana derivado da carga semanal: semana com carga_horaria_semanal ≥ carga_minima_regime_6x1_horas (44h)
/// é 6x1 (exatamente 1 folga, até 6 dias seguidos); as demais são 5x2 (exatamente 2 folgas, até 5 dias seguidos).
/// Feriado sem autorização e afastamento não são folga e reduzem os dias de trabalho nos dois regimes.
/// </summary>
public sealed class RegimeTests(ITestOutputHelper output)
{
    private static readonly ComplianceEvaluator Evaluator = new();

    /// <summary>9 pessoas para a demanda de ~5 por dia: sobra quadro (sem número exato o motor daria folgas extras).</summary>
    private static readonly List<EmployeeSnapshot> Nine = Enumerable.Range(0, 9).Select(i => new EmployeeSnapshot(Guid.NewGuid(), $"S{i:000}",
        $"Pessoa {i + 1}", [Guid.Empty], Guid.NewGuid(), i != 8, false, i < 2 ? SundayRule.Biweekly : SundayRule.OneInThreeWeeks, [])).ToList();

    private static ScheduleSolver Solver() => new(Options.Create(new SolverOptions { Workers = 4 }), Evaluator, NullLogger<ScheduleSolver>.Instance);

    private static List<ComplianceRuleSnapshot> Rules(params string[] blocked) =>
        ScheduleSolverTests.DefaultRules().Select(r => blocked.Contains(r.Chave) ? r with { Status = RuleStatus.Bloqueado } : r).ToList();

    private static GenerateScheduleCommand Command(DateOnly start, int weeks, PlanningSnapshot snapshot) =>
        new(Guid.NewGuid(), 1, start, weeks, snapshot, 20, GenerationModes.Completa, null);

    /// <summary>Setor das pessoas = o da fotografia de teste.</summary>
    private static PlanningSnapshot Snap(List<EmployeeSnapshot> people, IReadOnlyList<ComplianceRuleSnapshot>? rules = null,
        IReadOnlyList<HolidaySnapshot>? holidays = null)
    {
        var baseSnap = ScheduleSolverTests.Snapshot(holidays: holidays, rules: rules ?? Rules(), employees: people);
        var sector = baseSnap.Sectors[0].SectorId;
        return baseSnap with { Employees = people.Select(e => e with { SectorIds = [sector] }).ToList() };
    }

    private static List<EmployeeSnapshot> WithLeave(List<EmployeeSnapshot> people, int index, params DateOnly[] days) =>
        people.Select((e, i) => i != index ? e : e with
        {
            UnavailableDates = days,
            Absences = days.Select(d => new UnavailabilitySnapshot(d, UnavailabilityKinds.AtestadoMedico)).ToList()
        }).ToList();

    /// <summary>(dias trabalhados, folgas) por pessoa e semana; folga = dia sem trabalho que não é feriado sem autorização nem ausência.</summary>
    private Dictionary<(Guid E, int W), (int Worked, int Off)> Count(PlanningContext ctx, IReadOnlyList<AssignmentDto> cells, IEnumerable<DateOnly> holidays)
    {
        var hol = holidays.ToHashSet();
        var result = new Dictionary<(Guid, int), (int, int)>();
        foreach (var e in ctx.Employees)
        {
            var parts = new List<string>();
            for (var w = 0; w < ctx.Weeks; w++)
            {
                var days = Enumerable.Range(w * 7, 7).Select(d => ctx.Dates[d]).ToList();
                var worked = days.Count(d => cells.Any(a => a.EmployeeId == e.Snapshot.EmployeeId && a.Date == d));
                var blocked = days.Count(d => (hol.Contains(d) || e.Unavailable.Contains(d)) && !cells.Any(a => a.EmployeeId == e.Snapshot.EmployeeId && a.Date == d));
                result[(e.Snapshot.EmployeeId, w)] = (worked, 7 - worked - blocked);
                parts.Add($"sem{w + 1}: {worked} dias, {7 - worked - blocked} folga(s)");
            }
            output.WriteLine($"{e.Snapshot.Name}: {string.Join(" | ", parts)}");
        }
        return result;
    }

    /// <summary>Maior sequência de dias trabalhados que termina em cada dia do horizonte, por pessoa.</summary>
    private static Dictionary<(Guid E, int D), int> Runs(PlanningContext ctx, IReadOnlyList<AssignmentDto> cells)
    {
        var worked = cells.Select(a => (a.EmployeeId, a.Date)).ToHashSet();
        var runs = new Dictionary<(Guid, int), int>();
        foreach (var e in ctx.Employees)
        {
            var run = 0;
            for (var d = 0; d < ctx.Days; d++)
            {
                run = worked.Contains((e.Snapshot.EmployeeId, ctx.Dates[d])) ? run + 1 : 0;
                runs[(e.Snapshot.EmployeeId, d)] = run;
            }
        }
        return runs;
    }

    // ---------------- 44h → 6x1 ----------------

    [Fact]
    public void Week_of_44_hours_is_6x1_with_exactly_one_day_off_and_44_hours()
    {
        var start = new DateOnly(2026, 11, 23);
        var ok = Assert.IsType<SolveResult.Success>(Solver().Solve(Command(start, 2, Snap(Nine))));

        Assert.All(Count(ok.Ctx, ok.Assignments, []).Values, c => Assert.Equal((6, 1), c));
        Assert.All(Runs(ok.Ctx, ok.Assignments).Values, r => Assert.True(r <= 6, $"{r} dias seguidos"));

        var evaluation = Evaluator.Evaluate(ok.Ctx, ok.Assignments);
        Assert.Equal(100, evaluation.Percentual);
        Assert.Contains(evaluation.PorRegra, r => r.Chave == RuleKeys.FolgasSemana6x1 && r.Verificacoes == 9 * 2 && r.Violacoes == 0);
        Assert.Contains(evaluation.PorRegra, r => r.Chave == RuleKeys.FolgasSemana5x2 && r.Verificacoes == 0);
        Assert.NotNull(evaluation.RegimePorSemana);
        Assert.All(evaluation.RegimePorSemana!, w =>
        {
            Assert.Equal("6x1", w.Regime);
            Assert.Equal(44m, w.CargaHoras);
            Assert.Equal(1, w.Folgas);
            Assert.Equal(6, w.DiasConsecutivosMax);
            Assert.Equal(44m, w.CargaMinima6x1Horas);
        });
        Assert.Equal([start, start.AddDays(7)], evaluation.RegimePorSemana!.Select(w => w.InicioSemana));

        // Horas informativas: 6 turnos de 7h20 líquidas = 44h
        var controlled = evaluation.HorasSemanais!.Where(h => h.ControleJornada).ToList();
        Assert.All(controlled, h =>
        {
            Assert.Equal(6 * 440, h.MinutosTrabalhados);
            Assert.Equal(44 * 60, h.CargaSemanalMinutos);
            Assert.Equal("6x1", h.Regime);
        });
    }

    [Fact]
    public void Seven_days_in_a_row_across_two_6x1_weeks_is_a_violation()
    {
        var start = new DateOnly(2026, 11, 23);
        var ok = Assert.IsType<SolveResult.Success>(Solver().Solve(Command(start, 2, Snap(Nine))));
        var person = ok.Ctx.Employees[2].Snapshot;

        // Folga na segunda da 1ª semana e no domingo da 2ª: 1 folga por semana, mas 12 dias seguidos
        var mine = Enumerable.Range(1, 12).Select(i => new AssignmentDto(person.EmployeeId, start.AddDays(i), ok.Ctx.Shifts[0].Snapshot.ShiftId,
            ok.Ctx.Sectors[0].SectorId, ok.Ctx.Shifts[0].Snapshot.Start, ok.Ctx.Shifts[0].Snapshot.End, ok.Ctx.Shifts[0].PaidMinutes));
        var cells = ok.Assignments.Where(a => a.EmployeeId != person.EmployeeId).Concat(mine).ToList();
        var evaluation = Evaluator.Evaluate(ok.Ctx, cells);

        var seq = evaluation.Violacoes.Where(v => v.Chave == RuleKeys.DiasConsecutivosMax6x1 && v.EmployeeId == person.EmployeeId).ToList();
        Assert.Equal(6, seq.Count); // do 7º ao 12º dia
        Assert.Contains("chega ao 7º dia seguido de trabalho em 30/11 (máximo 6 no regime 6x1)", seq[0].Detalhe);
        Assert.DoesNotContain(evaluation.Violacoes, v => v.Chave == RuleKeys.FolgasSemana6x1 && v.EmployeeId == person.EmployeeId);
        output.WriteLine(seq[0].Detalhe);
    }

    [Fact]
    public void Evaluator_message_cites_the_regime()
    {
        var start = new DateOnly(2026, 11, 23);
        var ok = Assert.IsType<SolveResult.Success>(Solver().Solve(Command(start, 1, Snap(Nine))));

        // O operador troca um turno por folga (pessoa que não folga no domingo): 2 folgas numa semana 6x1
        var sunday = start.AddDays(6);
        var removed = ok.Assignments.First(a => a.Date != sunday && ok.Assignments.Any(b => b.EmployeeId == a.EmployeeId && b.Date == sunday));
        var evaluation = Evaluator.Evaluate(ok.Ctx, ok.Assignments.Where(a => a != removed).ToList());

        var v = Assert.Single(evaluation.Violacoes, x => x.Chave == RuleKeys.FolgasSemana6x1);
        Assert.Equal(removed.EmployeeId, v.EmployeeId);
        Assert.Equal("alta", v.Severidade);
        Assert.Contains("tem 2 folgas na semana de 23/11 (regime 6x1 — 44h); exatamente 1", v.Detalhe);
        output.WriteLine(v.Detalhe);
    }

    [Fact]
    public void Three_weeks_of_6x1_with_seven_people_use_the_sunday_extra_day_off_only_when_needed()
    {
        // Quadro do exemplo (7 pessoas). Com 1 folga por semana e no máximo 6 dias seguidos a folga só recua na semana;
        // o rodízio de domingos (1 a cada 3 semanas) obriga a voltar ao domingo: a semana da folga de domingo pode ter 2.
        var start = new DateOnly(2026, 11, 2);
        var ok = Assert.IsType<SolveResult.Success>(Solver().Solve(Command(start, 3, ScheduleSolverTests.Snapshot())));

        var counts = Count(ok.Ctx, ok.Assignments, []);
        var sundaysOff = ok.Ctx.Employees.SelectMany(e => Enumerable.Range(0, 3).Where(w =>
            !ok.Assignments.Any(a => a.EmployeeId == e.Snapshot.EmployeeId && a.Date == start.AddDays(w * 7 + 6))).Select(w => (e.Snapshot.EmployeeId, w))).ToHashSet();
        Assert.All(counts, kv => Assert.True(kv.Value == (6, 1) || (kv.Value == (5, 2) && sundaysOff.Contains(kv.Key)), $"{kv.Value} na semana {kv.Key.W + 1}"));
        Assert.All(Runs(ok.Ctx, ok.Assignments).Values, r => Assert.True(r <= 6));
        var extras = counts.Values.Count(c => c.Off == 2);
        output.WriteLine($"semanas com folga extra de domingo: {extras} de {counts.Count}");
        Assert.True(extras < counts.Count / 2);
        var evaluation = Evaluator.Evaluate(ok.Ctx, ok.Assignments);
        Assert.Equal(100, evaluation.Percentual);
        Assert.All(evaluation.RegimePorSemana!, w => Assert.True(w.FolgaExtraDomingo));

        // Sem a folga extra (regra bloqueada): número exato sempre → inviável com o rodízio de domingos, qualquer que seja o quadro
        var strict = Solver().Solve(Command(start, 3, Snap(Nine, Rules(RuleKeys.FolgaExtraDomingo6x1))));
        Assert.IsType<SolveResult.Infeasible>(strict);
    }

    // ---------------- 42h / 40h → 5x2 ----------------

    [Theory]
    [InlineData(2027, 1, 4, 42)]
    [InlineData(2028, 1, 3, 40)]
    public void Weeks_of_42_or_40_hours_are_5x2_with_exactly_two_days_off(int y, int m, int d, int hours)
    {
        var start = new DateOnly(y, m, d);
        var ok = Assert.IsType<SolveResult.Success>(Solver().Solve(Command(start, 2, Snap(Nine))));

        Assert.All(Count(ok.Ctx, ok.Assignments, []).Values, c => Assert.Equal((5, 2), c));
        Assert.All(Runs(ok.Ctx, ok.Assignments).Values, r => Assert.True(r <= 5, $"{r} dias seguidos"));
        var evaluation = Evaluator.Evaluate(ok.Ctx, ok.Assignments);
        Assert.Equal(100, evaluation.Percentual);
        Assert.All(evaluation.RegimePorSemana!, w =>
        {
            Assert.Equal("5x2", w.Regime);
            Assert.Equal(hours, w.CargaHoras);
            Assert.Equal(2, w.Folgas);
            Assert.Equal(5, w.DiasConsecutivosMax);
        });
        Assert.Contains(evaluation.PorRegra, r => r.Chave == RuleKeys.FolgasSemana5x2 && r.Verificacoes == 9 * 2 && r.Violacoes == 0);
    }

    // ---------------- Virada de 16/12/2026 ----------------

    [Fact]
    public void Schedule_crossing_16_12_2026_switches_from_6x1_to_5x2_in_the_transition_week()
    {
        // 07/12 (44h → 6x1), 14/12 (semana de virada: 44h e 42h → a mais restritiva, 42h → 5x2), 21/12 (42h, Natal → 5x2)
        var start = new DateOnly(2026, 12, 7);
        var christmas = new DateOnly(2026, 12, 25);
        var ok = Assert.IsType<SolveResult.Success>(Solver().Solve(Command(start, 3,
            Snap(Nine, holidays: [new HolidaySnapshot(christmas, "Natal", false)]))));

        var regimes = ok.Ctx.WeekRegimes();
        Assert.Equal(["6x1", "5x2", "5x2"], regimes.Select(r => r.Regime));
        Assert.Equal([44m, 42m, 42m], regimes.Select(r => r.CargaHoras));
        Assert.Equal([1, 2, 2], regimes.Select(r => r.Folgas));
        Assert.Equal([6, 5, 5], regimes.Select(r => r.DiasConsecutivosMax));
        Assert.Equal(2, ok.Ctx.MinRestDays(1)!.Value.Value); // piso do DSR passa a 2 em 16/12

        var counts = Count(ok.Ctx, ok.Assignments, [christmas]);
        Assert.All(counts.Where(kv => kv.Key.W == 0).Select(kv => kv.Value), c => Assert.Equal((6, 1), c));
        Assert.All(counts.Where(kv => kv.Key.W == 1).Select(kv => kv.Value), c => Assert.Equal((5, 2), c));
        Assert.All(counts.Where(kv => kv.Key.W == 2).Select(kv => kv.Value), c => Assert.Equal((4, 2), c));
        // A sequência que entra na semana 5x2 já conta os dias da semana 6x1 anterior
        Assert.All(Runs(ok.Ctx, ok.Assignments), kv => Assert.True(kv.Value <= (kv.Key.D < 7 ? 6 : 5), $"{kv.Value} dias seguidos no dia {kv.Key.D}"));
        Assert.Equal(100, Evaluator.Evaluate(ok.Ctx, ok.Assignments).Percentual);
    }

    [Fact]
    public void History_from_a_6x1_week_limits_the_first_5x2_days()
    {
        // Horizonte começa em 14/12 (5x2); a pessoa trabalhou de 08 a 13/12 (6 dias, permitido no 6x1): não pode
        // trabalhar em 14/12 (seria o 7º dia, e o máximo da semana 5x2 é 5)
        var start = new DateOnly(2026, 12, 14);
        var snap = Snap(Nine);
        var person = snap.Employees[3];
        var history = Enumerable.Range(1, 6).Select(i => new HistoryEntry(person.EmployeeId, start.AddDays(-i), new TimeOnly(7, 0), new TimeOnly(15, 20))).ToList();
        var ok = Assert.IsType<SolveResult.Success>(Solver().Solve(Command(start, 1, snap with { History = history })));

        Assert.DoesNotContain(ok.Assignments, a => a.EmployeeId == person.EmployeeId && a.Date == start);
        Assert.Equal(100, Evaluator.Evaluate(ok.Ctx, ok.Assignments).Percentual);
    }

    // ---------------- Feriado e afastamento nos dois regimes ----------------

    [Fact]
    public void Holiday_and_leave_reduce_work_days_without_becoming_days_off_in_6x1()
    {
        var start = new DateOnly(2026, 11, 16);
        var holiday = new DateOnly(2026, 11, 20); // Consciência Negra, sem autorização
        var leave = new[] { start.AddDays(7 + 1), start.AddDays(7 + 2) };
        var people = WithLeave(Nine, 0, leave);
        var ok = Assert.IsType<SolveResult.Success>(Solver().Solve(Command(start, 2,
            Snap(people, holidays: [new HolidaySnapshot(holiday, "Consciência Negra", false)]))));

        Assert.DoesNotContain(ok.Assignments, a => a.Date == holiday);
        var counts = Count(ok.Ctx, ok.Assignments, [holiday]);
        Assert.All(counts.Where(kv => kv.Key.W == 0).Select(kv => kv.Value), c => Assert.Equal((5, 1), c));
        Assert.Equal((4, 1), counts[(people[0].EmployeeId, 1)]);
        Assert.All(counts.Where(kv => kv.Key.W == 1 && kv.Key.E != people[0].EmployeeId).Select(kv => kv.Value), c => Assert.Equal((6, 1), c));
        Assert.Equal(100, Evaluator.Evaluate(ok.Ctx, ok.Assignments).Percentual);
    }

    [Fact]
    public void Holiday_and_leave_reduce_work_days_without_becoming_days_off_in_5x2()
    {
        var start = new DateOnly(2026, 12, 21);
        var christmas = new DateOnly(2026, 12, 25);
        var leave = new[] { start.AddDays(7 + 1), start.AddDays(7 + 2) };
        var people = WithLeave(Nine, 0, leave);
        var ok = Assert.IsType<SolveResult.Success>(Solver().Solve(Command(start, 2,
            Snap(people, holidays: [new HolidaySnapshot(christmas, "Natal", false)]))));

        var counts = Count(ok.Ctx, ok.Assignments, [christmas]);
        Assert.All(counts.Where(kv => kv.Key.W == 0).Select(kv => kv.Value), c => Assert.Equal((4, 2), c));
        Assert.Equal((3, 2), counts[(people[0].EmployeeId, 1)]);
        Assert.All(counts.Where(kv => kv.Key.W == 1 && kv.Key.E != people[0].EmployeeId).Select(kv => kv.Value), c => Assert.Equal((5, 2), c));
        var evaluation = Evaluator.Evaluate(ok.Ctx, ok.Assignments);
        Assert.Equal(100, evaluation.Percentual);
        var row = evaluation.HorasSemanais!.Single(h => h.EmployeeId == people[0].EmployeeId && h.Semana == start.AddDays(7));
        Assert.Equal((3, 2, 2, "5x2"), (row.DiasTrabalhados, row.Folgas, row.Ausencias, row.Regime));
    }

    [Fact]
    public void Seven_people_cannot_cover_a_holiday_week_in_5x2()
    {
        // Quadro do exemplo (7 pessoas, ~5 por dia): na semana do Natal (5x2) são 4 dias × 7 = 28 turnos para 30 exigidos.
        var snapshot = ScheduleSolverTests.Snapshot(holidays: [new HolidaySnapshot(new DateOnly(2026, 12, 25), "Natal", false)]);
        var bad = Assert.IsType<SolveResult.Infeasible>(Solver().Solve(Command(new DateOnly(2026, 12, 21), 1, snapshot)));
        output.WriteLine(bad.Reason);
        // Provado pela conta de turnos da semana (com a cobertura dos intervalos: sem falta por faixa a apontar) ou pelo motor
        Assert.True(bad.Deficits.Count > 0 || bad.Reason.Contains("provado por contagem"), bad.Reason);
    }

    // ---------------- Regras bloqueadas (comportamento documentado) ----------------

    [Fact]
    public void Blocked_threshold_makes_every_week_5x2()
    {
        var start = new DateOnly(2026, 11, 23);
        var ok = Assert.IsType<SolveResult.Success>(Solver().Solve(Command(start, 1, Snap(Nine, Rules(RuleKeys.CargaMinimaRegime6x1)))));

        var week = Assert.Single(ok.Ctx.WeekRegimes());
        Assert.Equal(("5x2", 44m, (decimal?)null, 2), (week.Regime, week.CargaHoras, week.CargaMinima6x1Horas, week.Folgas));
        Assert.All(Count(ok.Ctx, ok.Assignments, []).Values, c => Assert.Equal((5, 2), c));
        var evaluation = Evaluator.Evaluate(ok.Ctx, ok.Assignments);
        Assert.True(Assert.Single(evaluation.PorRegra, r => r.Chave == RuleKeys.CargaMinimaRegime6x1).Bloqueada);
    }

    [Fact]
    public void Blocked_weekly_load_makes_the_week_5x2()
    {
        var start = new DateOnly(2026, 11, 23);
        var ctx = PlanningContext.Create(Snap(Nine, Rules(RuleKeys.CargaHorariaSemanal)), start, 1);
        var week = Assert.Single(ctx.WeekRegimes());
        Assert.Equal(("5x2", (decimal?)null), (week.Regime, week.CargaHoras));
    }

    [Fact]
    public void Blocked_regime_days_off_rule_keeps_only_the_legal_floor()
    {
        var start = new DateOnly(2026, 11, 23);
        var ok = Assert.IsType<SolveResult.Success>(Solver().Solve(Command(start, 1, Snap(Nine, Rules(RuleKeys.FolgasSemana6x1)))));

        // Só o piso (folgas_minimas_semana = 1): com sobra de quadro o motor dá folgas extras; ninguém fica sem folga
        var counts = Count(ok.Ctx, ok.Assignments, []);
        Assert.All(counts.Values, c => Assert.True(c.Off >= 1 && c.Worked <= 6));
        Assert.Contains(counts.Values, c => c.Off > 1);
        Assert.Null(Assert.Single(ok.Ctx.WeekRegimes()).Folgas);
        var evaluation = Evaluator.Evaluate(ok.Ctx, ok.Assignments);
        var rule = Assert.Single(evaluation.PorRegra, r => r.Chave == RuleKeys.FolgasSemana6x1);
        Assert.True(rule.Bloqueada);
        Assert.Equal(0, rule.Verificacoes);
        Assert.Equal(100, evaluation.Percentual);
    }

    [Fact]
    public void Blocked_consecutive_days_rule_removes_the_limit_in_that_regime()
    {
        var start = new DateOnly(2026, 11, 23);
        var ctx = PlanningContext.Create(Snap(Nine, Rules(RuleKeys.DiasConsecutivosMax6x1)), start, 2);
        Assert.Null(ctx.MaxConsecutive(3));
        Assert.Null(Assert.IsType<WeekRegimeDto>(ctx.WeekRegimes()[0]).DiasConsecutivosMax);

        var person = ctx.Employees[0].Snapshot;
        var shift = ctx.Shifts[0];
        var cells = Enumerable.Range(1, 12).Select(i => new AssignmentDto(person.EmployeeId, start.AddDays(i), shift.Snapshot.ShiftId,
            ctx.Sectors[0].SectorId, shift.Snapshot.Start, shift.Snapshot.End, shift.PaidMinutes)).ToList();
        var evaluation = Evaluator.Evaluate(ctx, cells);
        Assert.DoesNotContain(evaluation.Violacoes, v => v.Chave.StartsWith("dias_consecutivos"));
        Assert.True(Assert.Single(evaluation.PorRegra, r => r.Chave == RuleKeys.DiasConsecutivosMax6x1).Bloqueada);
    }

    // ---------------- Fotografia antiga (5x2 estrito, sem o modelo de regime) ----------------

    [Fact]
    public void Old_strict_snapshot_keeps_the_previous_model()
    {
        var start = new DateOnly(2026, 11, 23);
        var ok = Assert.IsType<SolveResult.Success>(Solver().Solve(Command(start, 1, Snap(Nine, ScheduleSolverTests.StrictRules()))));

        Assert.False(ok.Ctx.UsesRegimes);
        Assert.All(Count(ok.Ctx, ok.Assignments, []).Values, c => Assert.Equal((5, 2), c));
        var evaluation = Evaluator.Evaluate(ok.Ctx, ok.Assignments);
        Assert.Null(evaluation.RegimePorSemana);
        Assert.Contains(evaluation.PorRegra, r => r.Chave == RuleKeys.FolgasMaximasSemana && r.Verificacoes == 9 && r.Violacoes == 0);
        Assert.Equal(100, evaluation.Percentual);
    }
}

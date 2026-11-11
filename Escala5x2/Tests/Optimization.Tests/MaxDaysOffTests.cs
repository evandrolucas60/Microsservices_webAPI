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
/// max_folgas_seguidas (no máximo 2 folgas seguidas, inclusive na virada de semana e na emenda com a escala
/// anterior/seguinte) e a preferência por folgas intercaladas (WeightConsecutiveDaysOffPenalty).
/// </summary>
public sealed class MaxDaysOffTests(ITestOutputHelper output)
{
    private static readonly ComplianceEvaluator Evaluator = new();
    private static readonly DateTime Now = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
    /// <summary>Segunda-feira de uma semana 5x2 (42h).</summary>
    private static readonly DateOnly Jan2027 = new(2027, 1, 4);

    private static ScheduleSolver Solver(int? pairWeight = null) =>
        new(Options.Create(new SolverOptions { Workers = 4, WeightConsecutiveDaysOffPenalty = pairWeight ?? new SolverOptions().WeightConsecutiveDaysOffPenalty }),
            Evaluator, NullLogger<ScheduleSolver>.Instance);

    private static GenerateScheduleCommand Command(DateOnly start, int weeks, PlanningSnapshot snapshot,
        string mode = GenerationModes.Completa, IReadOnlyList<CellDto>? fixedCells = null) =>
        new(Guid.NewGuid(), 1, start, weeks, snapshot, 20, mode, fixedCells);

    private static List<ComplianceRuleSnapshot> Rules(params string[] blocked) =>
        ScheduleSolverTests.DefaultRules().Select(r => blocked.Contains(r.Chave) ? r with { Status = RuleStatus.Bloqueado } : r).ToList();

    /// <summary>9 pessoas no setor (sobra quadro: as restrições de uma pessoa não tornam a escala inviável).</summary>
    private static PlanningSnapshot Snap(IReadOnlyList<ComplianceRuleSnapshot>? rules = null, IReadOnlyList<HolidaySnapshot>? holidays = null)
    {
        var baseSnap = ScheduleSolverTests.Snapshot(rules: rules ?? Rules(), holidays: holidays);
        var sector = baseSnap.Sectors[0].SectorId;
        var people = Enumerable.Range(0, 9).Select(i => new EmployeeSnapshot(Guid.NewGuid(), $"F{i:000}", $"Pessoa {i + 1}", [sector], Guid.NewGuid(),
            true, false, SundayRule.OneInThreeWeeks, [])).ToList();
        return baseSnap with { Employees = people };
    }

    /// <summary>Folga de descanso no dia (índice; fora do horizonte: folga registrada na escala anterior/seguinte).</summary>
    private static bool Off(PlanningContext ctx, IReadOnlyList<AssignmentDto> cells, EmployeeInfo e, int d)
    {
        var date = ctx.Start.AddDays(d);
        if (d < 0) return e.HistoryRest.Contains(date);
        if (d >= ctx.Days) return e.FollowingRest.Contains(date);
        return !cells.Any(a => a.EmployeeId == e.Snapshot.EmployeeId && a.Date == date) && !ctx.NonWorkable(e, d);
    }

    /// <summary>Maior sequência de folgas de cada pessoa (com 7 dias antes e depois do horizonte).</summary>
    private static Dictionary<Guid, int> LongestRuns(PlanningContext ctx, IReadOnlyList<AssignmentDto> cells)
    {
        var result = new Dictionary<Guid, int>();
        foreach (var e in ctx.Employees)
        {
            int best = 0, run = 0;
            for (var d = -7; d < ctx.Days + 7; d++)
            {
                run = Off(ctx, cells, e, d) ? run + 1 : 0;
                best = Math.Max(best, run);
            }
            result[e.Snapshot.EmployeeId] = best;
        }
        return result;
    }

    /// <summary>Pares de folgas em dias seguidos dentro do horizonte (todas as pessoas).</summary>
    private static int Pairs(PlanningContext ctx, IReadOnlyList<AssignmentDto> cells) =>
        ctx.Employees.Sum(e => Enumerable.Range(0, ctx.Days - 1).Count(d => Off(ctx, cells, e, d) && Off(ctx, cells, e, d + 1)));

    private static AssignmentDto Work(PlanningContext ctx, Guid employee, DateOnly date)
    {
        var t = ctx.Shifts[0];
        return new AssignmentDto(employee, date, t.Snapshot.ShiftId, ctx.Sectors[0].SectorId, t.Snapshot.Start, t.Snapshot.End, t.PaidMinutes);
    }

    // ---------------- Geração ----------------

    [Theory]
    [InlineData(2027, 1, 4, 3)]   // 5x2 (42h)
    [InlineData(2026, 11, 2, 3)]  // 6x1 (44h) com folga extra de domingo
    public void Generation_never_gives_three_days_off_in_a_row(int y, int m, int d, int weeks)
    {
        var start = new DateOnly(y, m, d);
        var ok = Assert.IsType<SolveResult.Success>(Solver().Solve(Command(start, weeks, Snap())));

        var runs = LongestRuns(ok.Ctx, ok.Assignments);
        output.WriteLine($"maior sequência de folgas: {runs.Values.Max()}");
        Assert.All(runs.Values, r => Assert.True(r <= 2, $"{r} folgas seguidas"));
        var evaluation = Evaluator.Evaluate(ok.Ctx, ok.Assignments);
        Assert.Equal(100, evaluation.Percentual);
        var rule = Assert.Single(evaluation.PorRegra, r => r.Chave == RuleKeys.MaxFolgasSeguidas);
        Assert.True(rule.Verificacoes > 0);
        Assert.Equal(0, rule.Violacoes);
        Assert.Equal("Rigida", rule.Aplicacao);
    }

    [Fact]
    public void Saturday_and_sunday_off_in_the_previous_week_make_monday_a_work_day()
    {
        // Semana anterior GERADA: as pessoas 0–2 trabalharam de segunda a sexta e folgaram sábado e domingo (célula vazia
        // na escala gerada = folga); a pessoa 3 trabalhou de terça a sábado e folgou só o domingo.
        var start = Jan2027;
        var snap = Snap();
        var ids = snap.Employees.Select(e => e.EmployeeId).ToList();
        var previous = new ContinuitySchedule(Guid.NewGuid(), snap.Sectors[0].SectorId, "Caixa", 1, start.AddDays(-7), start.AddDays(-1), 2, Now, "Gerada", false);
        var cells = ids.SelectMany((p, n) => Enumerable.Range(0, 7)
                .Where(i => n == 3 ? i is >= 1 and <= 5 : n < 3 ? i <= 4 : i is not (2 or 5))
                .Select(i => new ContinuityCell(previous.Id, p, previous.Start.AddDays(i), new TimeOnly(7, 0), new TimeOnly(15, 20), false)))
            .ToList();
        var continuity = ContinuityHistory.Previous([previous], cells, start, 21, snap.Employees.ToDictionary(e => e.EmployeeId, e => e.Name));
        Assert.Equal([start.AddDays(-2), start.AddDays(-1)], continuity.Rest!.Where(r => r.EmployeeId == ids[0]).Select(r => r.Date));
        Assert.Equal([start.AddDays(-7), start.AddDays(-1)], continuity.Rest!.Where(r => r.EmployeeId == ids[3]).Select(r => r.Date));

        var withHistory = snap with { History = continuity.History, HistoryRest = continuity.Rest };
        var ok = Assert.IsType<SolveResult.Success>(Solver().Solve(Command(start, 1, withHistory)));

        foreach (var p in ids.Take(3))
            Assert.Contains(ok.Assignments, a => a.EmployeeId == p && a.Date == start);
        // Domingo de folga antes: segunda e terça não podem ser as duas de folga
        Assert.Contains(ok.Assignments, a => a.EmployeeId == ids[3] && (a.Date == start || a.Date == start.AddDays(1)));
        Assert.All(LongestRuns(ok.Ctx, ok.Assignments).Values, r => Assert.True(r <= 2));
        Assert.Equal(100, Evaluator.Evaluate(ok.Ctx, ok.Assignments).Percentual);
    }

    [Fact]
    public void Only_days_the_previous_schedule_really_covers_count_as_days_off()
    {
        var start = Jan2027;
        var snap = Snap();
        var ids = snap.Employees.Select(e => e.EmployeeId).ToList();
        var names = snap.Employees.ToDictionary(e => e.EmployeeId, e => e.Name);
        // Pessoa 0 trabalhou de segunda a sexta e a pessoa 2 a semana toda; a pessoa 1 não aparece na escala anterior
        // (não coberta: os dias dela não são folga conhecida)
        var generated = new ContinuitySchedule(Guid.NewGuid(), snap.Sectors[0].SectorId, "Caixa", 1, start.AddDays(-7), start.AddDays(-1), 2, Now, "Gerada", false);
        var work = Enumerable.Range(0, 5).Select(i => (ids[0], i)).Concat(Enumerable.Range(0, 7).Select(i => (ids[2], i)))
            .Select(x => new ContinuityCell(generated.Id, x.Item1, generated.Start.AddDays(x.i), new TimeOnly(7, 0), new TimeOnly(15, 20), false)).ToList();

        var fromGenerated = ContinuityHistory.Previous([generated], work, start, 21, names);
        Assert.Equal(2, fromGenerated.Rest!.Count(r => r.EmployeeId == ids[0]));
        Assert.DoesNotContain(fromGenerated.Rest!, r => r.EmployeeId == ids[1]);

        // Rascunho: célula vazia é "sem definição"; só a folga marcada conta
        var draft = generated with { Id = Guid.NewGuid(), StatusLabel = "Rascunho", EmptyIsRest = false };
        var draftCells = work.Select(c => c with { ScheduleId = draft.Id })
            .Append(new ContinuityCell(draft.Id, ids[0], start.AddDays(-1), null, null, true)).ToList();
        var fromDraft = ContinuityHistory.Previous([draft], draftCells, start, 21, names);
        Assert.Equal([start.AddDays(-1)], fromDraft.Rest!.Where(r => r.EmployeeId == ids[0]).Select(r => r.Date));

        // Ausência/feriado sem autorização na fotografia da escala anterior: não é folga (interrompe a contagem)
        var withLeave = ContinuityHistory.Previous([generated], work, start, 21, names,
            (scheduleId, employee, date) => scheduleId == generated.Id && employee == ids[0] && date == start.AddDays(-1));
        Assert.Equal([start.AddDays(-2)], withLeave.Rest!.Where(r => r.EmployeeId == ids[0]).Select(r => r.Date));
    }

    // ---------------- Avaliador ----------------

    [Fact]
    public void Evaluator_points_three_days_off_in_a_row_inside_the_horizon_and_at_both_seams()
    {
        var start = Jan2027;
        var snap = Snap();
        var ids = snap.Employees.Select(e => e.EmployeeId).ToList();
        // Pessoa 0: sábado e domingo da semana anterior de folga; pessoa 1: segunda e terça da semana seguinte de folga
        var ctx = PlanningContext.Create(snap with
        {
            History = Enumerable.Range(3, 5).Select(i => new HistoryEntry(ids[0], start.AddDays(-i), new TimeOnly(7, 0), new TimeOnly(15, 20))).ToList(),
            HistoryRest = [new RestDayEntry(ids[0], start.AddDays(-2)), new RestDayEntry(ids[0], start.AddDays(-1))],
            FollowingRest = [new RestDayEntry(ids[1], start.AddDays(7)), new RestDayEntry(ids[1], start.AddDays(8))]
        }, start, 1);

        // 0: folga na segunda (sáb, dom, seg); 1: folga no domingo (dom, seg, ter da seguinte); 2: folgas qua, qui, sex
        var cells = new List<AssignmentDto>();
        cells.AddRange(Enumerable.Range(1, 6).Select(i => Work(ctx, ids[0], start.AddDays(i))));
        cells.AddRange(Enumerable.Range(0, 6).Select(i => Work(ctx, ids[1], start.AddDays(i))));
        cells.AddRange(new[] { 0, 1, 5, 6 }.Select(i => Work(ctx, ids[2], start.AddDays(i))));

        var result = Evaluator.Evaluate(ctx, cells);
        var v = result.Violacoes.Where(x => x.Chave == RuleKeys.MaxFolgasSeguidas).ToDictionary(x => x.EmployeeId!.Value);
        foreach (var x in v.Values) output.WriteLine(x.Detalhe);

        Assert.Equal("alta", v[ids[0]].Severidade);
        Assert.Contains("3 folgas seguidas de 02/01 a 04/01 (máximo 2), contando as folgas registradas — continuidade com a escala anterior (emenda em 04/01)",
            v[ids[0]].Detalhe);
        Assert.Contains("continuidade com a escala seguinte (emenda em 10/01)", v[ids[1]].Detalhe);
        Assert.Equal([start], v[ids[0]].Dates);
        Assert.Contains("3 folgas seguidas de 10/01 a 12/01 (máximo 2)", v[ids[1]].Detalhe);
        Assert.Contains("3 folgas seguidas de 06/01 a 08/01 (máximo 2).", v[ids[2]].Detalhe);
        Assert.Equal([start.AddDays(2), start.AddDays(3), start.AddDays(4)], v[ids[2]].Dates);
        // Sem escala anterior/seguinte (pessoas 3 a 8, sem trabalho na semana): uma sequência de 7 folgas cada
        Assert.Equal(9, v.Count);
    }

    [Fact]
    public void Holiday_and_leave_interrupt_the_count()
    {
        var start = Jan2027;
        var holiday = start.AddDays(3); // quinta sem autorização
        var snap = Snap(holidays: [new HolidaySnapshot(holiday, "Feriado municipal", false)]);
        var ids = snap.Employees.Select(e => e.EmployeeId).ToList();
        var leave = start.AddDays(1);
        snap = snap with
        {
            Employees = snap.Employees.Select((e, i) => i != 1 ? e : e with
            {
                UnavailableDates = [leave],
                Absences = [new UnavailabilitySnapshot(leave, UnavailabilityKinds.AtestadoMedico)]
            }).ToList()
        };
        var ctx = PlanningContext.Create(snap, start, 1);
        // 0: folga qua, (feriado qui), folga sex; 1: folga seg, (atestado ter), folga qua
        var cells = new List<AssignmentDto>();
        cells.AddRange(new[] { 0, 1, 5, 6 }.Select(i => Work(ctx, ids[0], start.AddDays(i))));
        cells.AddRange(new[] { 4, 5, 6 }.Select(i => Work(ctx, ids[1], start.AddDays(i))));

        var result = Evaluator.Evaluate(ctx, cells);
        Assert.DoesNotContain(result.Violacoes, x => x.Chave == RuleKeys.MaxFolgasSeguidas && (x.EmployeeId == ids[0] || x.EmployeeId == ids[1]));
    }

    // ---------------- Regra bloqueada e conflitos ----------------

    [Fact]
    public void Blocked_rule_does_not_restrict_the_engine_nor_the_evaluator()
    {
        var start = Jan2027;
        var person = Guid.Empty;
        PlanningSnapshot WithSeam(PlanningSnapshot s)
        {
            person = s.Employees[0].EmployeeId;
            return s with
            {
                History = Enumerable.Range(3, 5).Select(i => new HistoryEntry(person, start.AddDays(-i), new TimeOnly(7, 0), new TimeOnly(15, 20))).ToList(),
                HistoryRest = [new RestDayEntry(person, start.AddDays(-2)), new RestDayEntry(person, start.AddDays(-1))]
            };
        }
        // Folga manual na segunda depois de sábado e domingo de folga: 3 seguidas
        var active = WithSeam(Snap());
        CellDto[] manual = [new(person, start, null, true)];
        var bad = Assert.IsType<SolveResult.Infeasible>(Solver().Solve(Command(start, 1, active, GenerationModes.SomenteVazios, manual)));
        Assert.NotNull(bad.Diagnosis);
        Assert.Contains(bad.Diagnosis!.Itens, i => i.Chave == RuleKeys.MaxFolgasSeguidas && i.Tipo == "continuidade");
        output.WriteLine(bad.Diagnosis.Resumo);

        var blocked = WithSeam(Snap(Rules(RuleKeys.MaxFolgasSeguidas)));
        manual = [new(person, start, null, true)];
        var ok = Assert.IsType<SolveResult.Success>(Solver().Solve(Command(start, 1, blocked, GenerationModes.SomenteVazios, manual)));
        Assert.DoesNotContain(ok.Assignments, a => a.EmployeeId == person && a.Date == start);
        var evaluation = Evaluator.Evaluate(ok.Ctx, ok.Assignments);
        var rule = Assert.Single(evaluation.PorRegra, r => r.Chave == RuleKeys.MaxFolgasSeguidas);
        Assert.True(rule.Bloqueada);
        Assert.Equal(0, rule.Verificacoes);
        Assert.Equal(100, evaluation.Percentual);
    }

    [Fact]
    public void Availability_that_forces_days_off_next_to_registered_days_off_is_neutral()
    {
        // Indisponível sábado e domingo (disponibilidade semanal: na SEMANA são as 2 folgas do 5x2) e a escala seguinte já
        // tem folga na segunda. Antes: 3 folgas seguidas "forçadas" e a escala inviável. Agora o dia que a pessoa não pode
        // trabalhar é neutro em max_folgas_seguidas (PlanningContext.DaysOff): não conta como folga nem interrompe
        var start = Jan2027;
        var snap = Snap();
        var person = snap.Employees[0].EmployeeId;
        var unavailable = new[] { DayOfWeek.Saturday, DayOfWeek.Sunday }.Select(d => new AvailabilityWindow(d, false)).ToList();
        snap = snap with
        {
            Employees = snap.Employees.Select(e => e.EmployeeId != person ? e : e with
            {
                Jornada = new EmployeeJourneySnapshot(JourneyProfiles.Flexivel, unavailable)
            }).ToList(),
            FollowingRest = [new RestDayEntry(person, start.AddDays(7))]
        };

        var ok = Assert.IsType<SolveResult.Success>(Solver().Solve(Command(start, 1, snap)));
        var evaluation = Evaluator.Evaluate(ok.Ctx, ok.Assignments);
        foreach (var v in evaluation.Violacoes) output.WriteLine($"{v.Chave}: {v.Detalhe}");
        Assert.DoesNotContain(evaluation.Violacoes, v => v.Chave == RuleKeys.MaxFolgasSeguidas);
        Assert.Equal(0, ComplianceEvaluator.RigidViolations(evaluation));
        // Sábado e domingo são as folgas da semana: de segunda a sexta trabalhados
        Assert.All(Enumerable.Range(0, 5), i => Assert.Contains(ok.Assignments, a => a.EmployeeId == person && a.Date == start.AddDays(i)));

        // Sem a folga registrada depois (ou com a regra bloqueada) a mesma disponibilidade também é viável
        Assert.IsType<SolveResult.Success>(Solver().Solve(Command(start, 1, snap with { FollowingRest = null })));
        Assert.IsType<SolveResult.Success>(Solver().Solve(Command(start, 1, snap with { ComplianceRules = Rules(RuleKeys.MaxFolgasSeguidas) })));
    }

    // ---------------- Preferência por folgas intercaladas ----------------

    [Fact]
    public void Interleaved_days_off_are_preferred_when_possible()
    {
        var start = Jan2027;
        var snap = Snap();
        var interleaved = Assert.IsType<SolveResult.Success>(Solver().Solve(Command(start, 2, snap)));
        // Peso negativo = bônus ao par (comportamento antigo)
        var paired = Assert.IsType<SolveResult.Success>(Solver(-300).Solve(Command(start, 2, snap)));

        var few = Pairs(interleaved.Ctx, interleaved.Assignments);
        var many = Pairs(paired.Ctx, paired.Assignments);
        output.WriteLine($"pares de folgas seguidas: penalidade {few} × bônus {many}");
        Assert.True(few < many, $"{few} pares com a penalidade e {many} com o bônus");
        Assert.Equal(100, Evaluator.Evaluate(interleaved.Ctx, interleaved.Assignments).Percentual);
        Assert.Equal(100, Evaluator.Evaluate(paired.Ctx, paired.Assignments).Percentual);
    }

    [Fact]
    public void Required_pair_of_folgas_consecutivas_5x2_stays_compatible()
    {
        // folgas_consecutivas_5x2 ativa: as 2 folgas da semana 5x2 juntas (par = 2 ≤ máximo); a penalidade do par não
        // impede a regra, só evita pares a mais (ex.: domingo + segunda da semana seguinte)
        var start = Jan2027;
        var rules = Rules().Append(ScheduleSolverTests.Rule(27, "regime", RuleKeys.FolgasConsecutivas5x2, 1, "booleano")).ToList();
        var ok = Assert.IsType<SolveResult.Success>(Solver().Solve(Command(start, 2, Snap(rules))));

        Assert.All(LongestRuns(ok.Ctx, ok.Assignments).Values, r => Assert.Equal(2, r));
        var evaluation = Evaluator.Evaluate(ok.Ctx, ok.Assignments);
        Assert.Equal(100, evaluation.Percentual);
        Assert.Contains(evaluation.PorRegra, r => r.Chave == RuleKeys.FolgasConsecutivas5x2 && r.Verificacoes == 9 * 2 && r.Violacoes == 0);
    }
}

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
/// Setor com 2 pessoas: folgas FIXAS (os mesmos dias da semana toda semana) e INTERCALADAS (as dos dois nunca coincidem;
/// as de cada um não ficam em dias seguidos); o domingo de folga da regra de domingos entra em rodízio no lugar do dia de
/// troca; sem folga dupla; o padrão segue o do mês anterior; 3+ pessoas e a opção desligada mantêm as folgas variáveis.
/// </summary>
public sealed class FixedDaysOffTests(ITestOutputHelper output)
{
    private static readonly ComplianceEvaluator Evaluator = new();
    private static readonly DateTime Now = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Sector = Guid.NewGuid();
    private static readonly ShiftSnapshot Morning = new(Guid.NewGuid(), "M", new TimeOnly(7, 0), new TimeOnly(15, 20), 60);

    private static ScheduleSolver Solver(bool enabled = true) =>
        new(Options.Create(new SolverOptions { Workers = 4, FixedDaysOffTwoPersonSectors = enabled }), Evaluator, NullLogger<ScheduleSolver>.Instance);

    /// <summary>Padaria: 1 pessoa das 8h às 14h todos os dias (mínimo rígido), um turno só, regras padrão (5x2 em 2027).</summary>
    private static PlanningSnapshot Snap(params string[] names)
    {
        if (names.Length == 0) names = ["Ana", "Bruno"];
        var demands = Enum.GetValues<DayOfWeek>().Select(d => new DemandSnapshot(Sector, d, new TimeOnly(8, 0), new TimeOnly(14, 0), 1)).ToList();
        var people = names.Select((n, i) => new EmployeeSnapshot(Guid.NewGuid(), $"FF{i:000}", n, [Sector], Guid.NewGuid(),
            true, false, SundayRule.OneInThreeWeeks, [])).ToList();
        return new PlanningSnapshot(Guid.NewGuid(), "Teste", false, [new SectorSnapshot(Sector, "Padaria")], [Morning], demands, [],
            people, null, [], ScheduleSolverTests.DefaultRules());
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
        output.WriteLine(ok.Notice ?? "(sem aviso)");
        return ok;
    }

    private void AssertCompliant(SolveResult.Success ok)
    {
        var result = Evaluator.Evaluate(ok.Ctx, ok.Assignments);
        foreach (var v in result.Violacoes) output.WriteLine($"{v.Chave}: {v.Detalhe}");
        Assert.Equal(0, ComplianceEvaluator.RigidViolations(result));
        Assert.DoesNotContain(result.Violacoes, v => v.Chave == ComplianceEvaluator.FolgasFixas);
    }

    /// <summary>Folgas de descanso de cada pessoa no período (dias sem célula, fora ausência/feriado sem autorização).</summary>
    private static Dictionary<Guid, HashSet<DateOnly>> Rests(SolveResult.Success ok)
    {
        var worked = ok.Assignments.Select(a => (a.EmployeeId, a.Date)).ToHashSet();
        return ok.Ctx.Employees.ToDictionary(e => e.Snapshot.EmployeeId, e =>
        {
            var set = new HashSet<DateOnly>();
            for (var d = ok.Ctx.PeriodStart; d <= ok.Ctx.PeriodEnd; d = d.AddDays(1))
                if (!worked.Contains((e.Snapshot.EmployeeId, d)) && !ok.Ctx.NonWorkable(e, ok.Ctx.DayIndex(d))) set.Add(d);
            return set;
        });
    }

    private void Print(SolveResult.Success ok)
    {
        var rests = Rests(ok);
        foreach (var e in ok.Ctx.Employees)
        {
            var line = string.Concat(Enumerable.Range(0, ok.Ctx.PeriodDays).Select(i => ok.Ctx.PeriodStart.AddDays(i))
                .Select(d => (d.DayOfWeek == DayOfWeek.Monday ? " " : "") + (rests[e.Snapshot.EmployeeId].Contains(d) ? "F" : "T")));
            output.WriteLine($"{e.Snapshot.Name,-6}{line}");
        }
    }

    /// <summary>
    /// Padrão fixo e intercalado em todo o período: 2 dias da semana por pessoa, sem segunda, sem dias vizinhos, sem dia em
    /// comum; nas semanas inteiras do período, folga de segunda a sábado só nos dias do padrão e, com o domingo trabalhado,
    /// exatamente neles; alguém trabalha todo dia; nenhuma folga seguida.
    /// </summary>
    private Dictionary<Guid, IReadOnlyList<DayOfWeek>> AssertFixedAndInterleaved(SolveResult.Success ok, DateOnly? irregularWeek = null)
    {
        Print(ok);
        var ctx = ok.Ctx;
        var views = ctx.FixedDaysOffViews(ok.Assignments);
        Assert.Equal(2, views.Count);
        foreach (var v in views)
        {
            Assert.Equal(2, v.Dias.Count);
            Assert.DoesNotContain(DayOfWeek.Monday, v.Dias);
            var idx = v.Dias.Select(PlanningContext.WeekdayIndex).Order().ToList();
            Assert.NotEqual(1, idx[1] - idx[0]);
        }
        Assert.Empty(views[0].Dias.Intersect(views[1].Dias));

        var rests = Rests(ok);
        foreach (var v in views)
        {
            var mine = rests[v.EmployeeId];
            for (var monday = ctx.Start; monday < ctx.Start.AddDays(ctx.Days); monday = monday.AddDays(7))
            {
                if (monday < ctx.PeriodStart || monday.AddDays(6) > ctx.PeriodEnd || monday == irregularWeek) continue;
                var weekdays = Enumerable.Range(0, 6).Select(monday.AddDays).Where(mine.Contains).Select(d => d.DayOfWeek).ToHashSet();
                Assert.Subset(v.Dias.ToHashSet(), weekdays);
                if (!mine.Contains(monday.AddDays(6))) Assert.Equal(v.Dias.ToHashSet(), weekdays);
                else Assert.Single(weekdays); // domingo de folga: troca um dia do padrão
            }
            bool InIrregular(DateOnly d) => irregularWeek is { } m && d >= m && d <= m.AddDays(6);
            Assert.DoesNotContain(mine, d => mine.Contains(d.AddDays(1)) && !InIrregular(d) && !InIrregular(d.AddDays(1)));
        }
        for (var d = ctx.PeriodStart; d <= ctx.PeriodEnd; d = d.AddDays(1))
            Assert.False(rests.Values.All(r => r.Contains(d)), $"Os dois de folga em {d:dd/MM}");

        Assert.Contains("Setor Padaria tem 2 colaboradores: folgas fixas e intercaladas aplicadas (", ok.Notice);
        foreach (var v in views) Assert.Contains($"{v.Nome}: {v.Texto}", ok.Notice);
        Assert.Contains(ok.Solutions!.Componentes, c => c.Chave == EngineComponents.FolgasFixas);
        AssertCompliant(ok);
        return views.ToDictionary(v => v.EmployeeId, v => v.Dias);
    }

    [Fact]
    public void Two_person_sector_gets_the_same_weekdays_off_every_week_never_together_nor_in_a_row()
    {
        var snap = Snap();
        var ok = Success(Solver().Solve(MonthCommand(new DateOnly(2027, 2, 1), snap)));
        AssertFixedAndInterleaved(ok);

        // Regra de domingos rígida (1 a cada 3 semanas): cada um folga em domingo, nunca os dois no mesmo
        var rests = Rests(ok);
        var sundays = Enumerable.Range(0, 28).Select(i => new DateOnly(2027, 2, 1).AddDays(i)).Where(d => d.DayOfWeek == DayOfWeek.Sunday).ToList();
        foreach (var r in rests.Values) Assert.Contains(sundays, r.Contains);
        Assert.Contains("no domingo de folga (rodízio da regra de domingos)", ok.Notice);
    }

    [Fact]
    public void Two_person_sector_has_no_double_day_off_priority_nor_notice()
    {
        var snap = Snap();
        var scores = snap.Employees.Select(e => new FolgaDuplaScore(e.EmployeeId, 3, null, 3)).ToList();
        var ok = Success(Solver().Solve(MonthCommand(new DateOnly(2027, 2, 1), snap with { FolgaDupla = scores })));
        AssertFixedAndInterleaved(ok);
        Assert.DoesNotContain("Folga dupla", ok.Notice);
        Assert.DoesNotContain(ok.Solutions!.Componentes, c => c.Chave == EngineComponents.FolgaDuplaPrioridade);
        Assert.All(FolgaDuplaHistory.InPeriod(ok.Ctx, ok.Assignments).Values, Assert.Null);
    }

    [Fact]
    public void Next_months_keep_the_pattern_of_the_previous_month()
    {
        var snap = Snap();
        var names = snap.Employees.ToDictionary(e => e.EmployeeId, e => e.Name);
        var rules = LegacyRules.Of(snap);
        var schedules = new List<ContinuitySchedule>();
        var cells = new List<ContinuityCell>();
        Dictionary<Guid, IReadOnlyList<DayOfWeek>>? first = null;

        // Fevereiro (semanas inteiras), março (começa na segunda) e abril (começa na quinta: semana parcial com março)
        foreach (var month in new[] { new DateOnly(2027, 2, 1), new DateOnly(2027, 3, 1), new DateOnly(2027, 4, 1) })
        {
            var previous = ContinuityHistory.Previous(schedules, cells, month, PlanningContext.ContinuityDaysFor(rules) + 6, names,
                restWindow: PlanningContext.RestWindowFor(rules) + 6);
            var monthSnap = snap with { History = previous.History, HistoryRest = previous.Rest, HistoryNotices = previous.Notices };
            var ok = Success(Solver().Solve(MonthCommand(month, monthSnap)));
            var pattern = AssertFixedAndInterleaved(ok);
            first ??= pattern;
            foreach (var (id, days) in pattern) Assert.Equal(first[id].ToHashSet(), days.ToHashSet());

            var (from, to) = PlanningContext.MonthOf(month);
            var schedule = new ContinuitySchedule(Guid.NewGuid(), Sector, "Padaria", 1, from, to, 0, Now, "Publicada", true, Principal: true);
            schedules.Add(schedule);
            cells.AddRange(ok.Assignments.Select(a => new ContinuityCell(schedule.Id, a.EmployeeId, a.Date, a.Start, a.End, false)));
        }
    }

    [Fact]
    public void Partial_absence_keeps_the_pattern_and_the_rules()
    {
        // Bruno afastado na terça e na quarta da 2ª semana de fevereiro: na semana dele o padrão vira penalidade (Ana cobre
        // os dois dias) e nas outras semanas os dois seguem o padrão; as regras legais continuam cumpridas
        var snap = Snap();
        var bruno = snap.Employees.Single(e => e.Name == "Bruno");
        var week = new DateOnly(2027, 2, 8);
        snap = snap with
        {
            Employees = snap.Employees.Select(e => e == bruno ? e with { UnavailableDates = [week.AddDays(1), week.AddDays(2)] } : e).ToList()
        };
        Assert.Single(PlanningContext.Create(snap, new DateOnly(2027, 2, 1), new DateOnly(2027, 2, 28)).FixedDaysOffPairs);

        var ok = Success(Solver().Solve(MonthCommand(new DateOnly(2027, 2, 1), snap)));
        Assert.DoesNotContain("não aplicadas", ok.Notice);
        AssertFixedAndInterleaved(ok, week);
        var ana = snap.Employees.Single(e => e.Name == "Ana").EmployeeId;
        Assert.Contains(ok.Assignments, a => a.EmployeeId == ana && a.Date == week.AddDays(1));
        Assert.Contains(ok.Assignments, a => a.EmployeeId == ana && a.Date == week.AddDays(2));
    }

    [Fact]
    public void Sector_with_three_people_or_one_available_and_option_off_keep_variable_days_off()
    {
        var feb = new DateOnly(2027, 2, 1);
        // 3 pessoas: não se aplica
        var three = Snap("Ana", "Bruno", "Carla");
        Assert.Empty(PlanningContext.Create(three, feb, new DateOnly(2027, 2, 28)).FixedDaysOffPairs);
        var ok3 = Success(Solver().Solve(MonthCommand(feb, three)));
        Assert.DoesNotContain("folgas fixas", ok3.Notice ?? "");
        Assert.DoesNotContain(ok3.Solutions!.Componentes, c => c.Chave == EngineComponents.FolgasFixas);
        Assert.Empty(ok3.Ctx.FixedDaysOffViews(ok3.Assignments));

        // 3 pessoas com uma de férias o mês inteiro: 2 disponíveis — aplica
        var carla = three.Employees.Single(e => e.Name == "Carla");
        var month = Enumerable.Range(0, 28).Select(i => feb.AddDays(i)).ToList();
        var away = three with { Employees = three.Employees.Select(e => e == carla ? e with { UnavailableDates = month } : e).ToList() };
        var pair = Assert.Single(PlanningContext.Create(away, feb, new DateOnly(2027, 2, 28)).FixedDaysOffPairs);
        Assert.DoesNotContain(pair.People, e => e.Snapshot.Name == "Carla");

        // 1 pessoa: não se aplica
        Assert.Empty(PlanningContext.Create(Snap("Ana"), feb, new DateOnly(2027, 2, 28)).FixedDaysOffPairs);

        // Opção desligada na operação: não se aplica
        var op = new OperationSnapshot(new TimeOnly(7, 0), new TimeOnly(15, 20), 1, 1, 1, 15, null, null, 500, 60,
            FolgasFixasSetorDuasPessoas: false);
        Assert.Empty(PlanningContext.Create(Snap() with { Operacao = op }, feb, new DateOnly(2027, 2, 28)).FixedDaysOffPairs);
        Assert.Single(PlanningContext.Create(Snap() with { Operacao = op with { FolgasFixasSetorDuasPessoas = true } }, feb, new DateOnly(2027, 2, 28)).FixedDaysOffPairs);

        // Desligada no motor (appsettings): folgas variáveis, sem aviso nem componente; as regras continuam
        var off = Success(Solver(enabled: false).Solve(MonthCommand(feb, Snap())));
        Assert.DoesNotContain("folgas fixas", off.Notice ?? "");
        Assert.DoesNotContain(off.Solutions!.Componentes, c => c.Chave == EngineComponents.FolgasFixas);
        Assert.Equal(0, ComplianceEvaluator.RigidViolations(Evaluator.Evaluate(off.Ctx, off.Assignments)));
    }

    [Fact]
    public void Evaluator_flags_coinciding_days_off_only_as_informative()
    {
        var snap = Snap();
        var ok = Success(Solver().Solve(MonthCommand(new DateOnly(2027, 2, 1), snap)));
        var before = Evaluator.Evaluate(ok.Ctx, ok.Assignments);
        Assert.Equal(0, before.PorRegra.Single(r => r.Chave == ComplianceEvaluator.FolgasFixas).Violacoes);

        // Edição manual: tira os dois de um dia (folgas coincidem) — informativa (baixa), não rígida
        var day = ok.Assignments.Select(a => a.Date).Distinct().Order().First(d => ok.Assignments.Count(a => a.Date == d) == 1);
        var edited = ok.Assignments.Where(a => a.Date != day).ToList();
        var after = Evaluator.Evaluate(ok.Ctx, edited);
        var v = Assert.Single(after.Violacoes, x => x.Chave == ComplianceEvaluator.FolgasFixas);
        Assert.Equal("baixa", v.Severidade);
        Assert.Equal([day], v.Dates);
    }
}

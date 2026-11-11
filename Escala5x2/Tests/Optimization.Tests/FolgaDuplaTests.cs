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
/// Pontuação de FOLGA DUPLA (duas folgas seguidas dentro do mês): quem ficou sem folga dupla nos meses anteriores tem
/// prioridade no mês seguinte (WeightDoubleDayOffPriority × meses sem), até todos do setor terem a sua; a virada do mês
/// não conta; ausência não conta nem tira a vez; a prioridade nunca derruba o mínimo de cobertura nem regra rígida.
/// </summary>
public sealed class FolgaDuplaTests(ITestOutputHelper output)
{
    private static readonly ComplianceEvaluator Evaluator = new();
    private static readonly DateTime Now = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Sector = Guid.NewGuid();
    private static readonly ShiftSnapshot Morning = new(Guid.NewGuid(), "M", new TimeOnly(7, 0), new TimeOnly(15, 20), 60);
    private static readonly ShiftSnapshot Evening = new(Guid.NewGuid(), "T", new TimeOnly(13, 40), new TimeOnly(22, 0), 60);
    private static readonly string[] Names = ["Ana", "Bruno", "Carla", "Diego", "Elisa"];

    private static ScheduleSolver Solver(int? weight = null) =>
        new(Options.Create(new SolverOptions
        {
            Workers = 4,
            WeightDoubleDayOffPriority = weight ?? new SolverOptions().WeightDoubleDayOffPriority
        }), Evaluator, NullLogger<ScheduleSolver>.Instance);

    /// <summary>Setor de 5 pessoas: 1 pessoa de manhã e 1 à tarde todos os dias (mínimo rígido), regras padrão (5x2 em 2027).</summary>
    private static PlanningSnapshot Snap()
    {
        var demands = Enum.GetValues<DayOfWeek>().SelectMany(d => new[]
        {
            new DemandSnapshot(Sector, d, new TimeOnly(8, 0), new TimeOnly(13, 30), 1),
            new DemandSnapshot(Sector, d, new TimeOnly(15, 30), new TimeOnly(21, 30), 1)
        }).ToList();
        var people = Names.Select((n, i) => new EmployeeSnapshot(Guid.NewGuid(), $"FD{i:000}", n, [Sector], Guid.NewGuid(),
            true, false, SundayRule.OneInThreeWeeks, [])).ToList();
        return new PlanningSnapshot(Guid.NewGuid(), "Teste", false, [new SectorSnapshot(Sector, "Caixa")], [Morning, Evening], demands, [],
            people, null, [], ScheduleSolverTests.DefaultRules());
    }

    private static GenerateScheduleCommand MonthCommand(DateOnly anyDay, PlanningSnapshot snapshot,
        string mode = GenerationModes.Completa, IReadOnlyList<CellDto>? fixedCells = null)
    {
        var (from, to) = PlanningContext.MonthOf(anyDay);
        return new(Guid.NewGuid(), 1, from, PlanningContext.Horizon(from, to).Weeks, snapshot, 15, mode, fixedCells ?? [], Guid.NewGuid(), to);
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

    private static Guid Id(PlanningSnapshot s, string name) => s.Employees.Single(e => e.Name == name).EmployeeId;

    private static List<FolgaDuplaScore> Scores(PlanningSnapshot s, params (string Name, int Months)[] prioritized) =>
        s.Employees.Select(e => new FolgaDuplaScore(e.EmployeeId, prioritized.FirstOrDefault(p => p.Name == e.Name).Months, null, 1)).ToList();

    /// <summary>A escala cumpre todas as regras rígidas e o mínimo de cobertura.</summary>
    private void AssertCompliant(SolveResult.Success ok)
    {
        var result = Evaluator.Evaluate(ok.Ctx, ok.Assignments);
        foreach (var v in result.Violacoes) output.WriteLine($"{v.Chave}: {v.Detalhe}");
        Assert.Equal(0, ComplianceEvaluator.RigidViolations(result));
        Assert.DoesNotContain(result.Violacoes, v => v.Chave is RuleKeys.CoberturaFaixa or RuleKeys.CoberturaMinima);
    }

    // ---------------- Pontuação (histórico) ----------------

    [Fact]
    public void Score_counts_months_without_a_pair_skipping_absences_and_ignoring_the_month_boundary()
    {
        var snap = Snap();
        var ids = Names.ToDictionary(n => n, n => Id(snap, n));
        var jan = new ContinuitySchedule(Guid.NewGuid(), Sector, "Caixa", 1, new DateOnly(2027, 1, 1), new DateOnly(2027, 1, 31), 0, Now, "Publicada", true, Principal: true);
        var feb = new ContinuitySchedule(Guid.NewGuid(), Sector, "Caixa", 1, new DateOnly(2027, 2, 1), new DateOnly(2027, 2, 28), 0, Now, "Publicada", true, Principal: true);

        // Folgas intercaladas (quarta e domingo) ou folga dupla (sábado e domingo); o resto trabalhado
        static bool Interleaved(DateOnly d) => d.DayOfWeek is DayOfWeek.Wednesday or DayOfWeek.Sunday;
        static bool Paired(DateOnly d) => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        var cells = new List<ContinuityCell>();
        void Month(ContinuitySchedule s, Guid e, Func<DateOnly, bool> off)
        {
            for (var d = s.Start; d <= s.End; d = d.AddDays(1))
                if (!off(d)) cells.Add(new ContinuityCell(s.Id, e, d, new TimeOnly(7, 0), new TimeOnly(15, 20), false));
        }
        Month(jan, ids["Ana"], Interleaved); Month(feb, ids["Ana"], Paired);           // teve em fevereiro
        Month(jan, ids["Bruno"], Interleaved); Month(feb, ids["Bruno"], Interleaved);  // 2 meses sem
        Month(jan, ids["Carla"], Interleaved);                                         // fevereiro de férias (neutro)
        cells.Add(new ContinuityCell(feb.Id, ids["Carla"], new DateOnly(2027, 2, 1), null, null, true));
        // Elisa: 31/01 (domingo) e 01/02 (segunda) de folga — a folga dupla cruza a virada do mês e não conta
        Month(jan, ids["Elisa"], Interleaved);
        Month(feb, ids["Elisa"], d => Interleaved(d) || d == new DateOnly(2027, 2, 1));
        // Diego: sem nenhuma escala (colaborador novo)
        NotRestDay vacation = (scheduleId, employee, date) => scheduleId == feb.Id && employee == ids["Carla"];

        var score = FolgaDuplaHistory.Score([jan, feb], cells, new DateOnly(2027, 3, 1), ids.Values, vacation).ToDictionary(s => s.EmployeeId);

        Assert.Equal(0, score[ids["Ana"]].MesesSemFolgaDupla);
        Assert.Equal(new DateOnly(2027, 2, 27), score[ids["Ana"]].UltimaFolgaDupla);
        Assert.Equal(2, score[ids["Bruno"]].MesesSemFolgaDupla);
        Assert.Null(score[ids["Bruno"]].UltimaFolgaDupla);
        Assert.Equal(1, score[ids["Carla"]].MesesSemFolgaDupla);
        Assert.Equal(1, score[ids["Carla"]].MesesAvaliados);
        Assert.Equal(0, score[ids["Diego"]].MesesSemFolgaDupla);
        Assert.Equal(0, score[ids["Diego"]].MesesAvaliados);
        Assert.Equal(2, score[ids["Elisa"]].MesesSemFolgaDupla);
    }

    // ---------------- Motor ----------------

    [Fact]
    public void Employee_without_a_pair_last_month_gets_one_in_preference_over_who_had_it()
    {
        var snap = Snap();
        var ana = Id(snap, "Ana");
        var bruno = Id(snap, "Bruno");

        // Ana ficou 2 meses sem folga dupla (Bruno teve no mês anterior): a prioridade é dela
        var first = Success(Solver().Solve(MonthCommand(new DateOnly(2027, 2, 1), snap with { FolgaDupla = Scores(snap, ("Ana", 2)) })));
        var got = FolgaDuplaHistory.InPeriod(first.Ctx, first.Assignments);
        output.WriteLine(first.Notice);
        Assert.NotNull(got[ana]);
        Assert.Contains("Folga dupla: 1 de 1 com prioridade atendido(s) (Ana)", first.Notice);
        Assert.Contains(first.Solutions!.Componentes, c => c.Chave == EngineComponents.FolgaDuplaPrioridade);
        Assert.Equal(0, first.Solutions.Solucoes.Single(s => s.Escolhida).Componentes[EngineComponents.FolgaDuplaPrioridade]);
        AssertCompliant(first);

        // Invertendo (Bruno sem, Ana teve): a prioridade passa para o Bruno
        var swapped = Success(Solver().Solve(MonthCommand(new DateOnly(2027, 2, 1), snap with { FolgaDupla = Scores(snap, ("Bruno", 2)) })));
        Assert.NotNull(FolgaDuplaHistory.InPeriod(swapped.Ctx, swapped.Assignments)[bruno]);
        Assert.Contains("(Bruno)", swapped.Notice);
        AssertCompliant(swapped);
    }

    [Fact]
    public void Over_three_months_everyone_in_the_sector_gets_a_pair()
    {
        var snap = Snap();
        var names = snap.Employees.ToDictionary(e => e.EmployeeId, e => e.Name);
        var rules = LegacyRules.Of(snap);
        var schedules = new List<ContinuitySchedule>();
        var cells = new List<ContinuityCell>();
        var withPair = new HashSet<Guid>();

        foreach (var month in new[] { new DateOnly(2027, 2, 1), new DateOnly(2027, 3, 1), new DateOnly(2027, 4, 1) })
        {
            // Como o Scheduling.Api: pontuação dos meses anteriores e continuidade com o mês anterior
            var scores = FolgaDuplaHistory.Score(schedules, cells, month, snap.Employees.Select(e => e.EmployeeId));
            var previous = ContinuityHistory.Previous(schedules, cells, month, PlanningContext.ContinuityDaysFor(rules) + 6, names,
                restWindow: PlanningContext.RestWindowFor(rules) + 6);
            var monthSnap = snap with { FolgaDupla = scores, History = previous.History, HistoryRest = previous.Rest, HistoryNotices = previous.Notices };
            var ok = Success(Solver().Solve(MonthCommand(month, monthSnap)));
            AssertCompliant(ok);

            var got = FolgaDuplaHistory.InPeriod(ok.Ctx, ok.Assignments);
            output.WriteLine($"{month:MM/yyyy}: prioridade {string.Join(", ", scores.Where(s => s.MesesSemFolgaDupla > 0).Select(s => $"{names[s.EmployeeId]} ({s.MesesSemFolgaDupla})"))}" +
                             $" · folga dupla {string.Join(", ", got.Where(g => g.Value is not null).Select(g => names[g.Key]))} · {ok.Notice}");
            // Todo priorizado recebe a sua folga dupla no mês
            foreach (var s in scores.Where(s => s.MesesSemFolgaDupla > 0)) Assert.NotNull(got[s.EmployeeId]);
            withPair.UnionWith(got.Where(g => g.Value is not null).Select(g => g.Key));

            var (from, to) = PlanningContext.MonthOf(month);
            var schedule = new ContinuitySchedule(Guid.NewGuid(), Sector, "Caixa", 1, from, to, 0, Now, "Publicada", true, Principal: true);
            schedules.Add(schedule);
            cells.AddRange(ok.Assignments.Select(a => new ContinuityCell(schedule.Id, a.EmployeeId, a.Date, a.Start, a.End, false)));
        }

        Assert.Equal(snap.Employees.Select(e => e.EmployeeId).ToHashSet(), withPair);
    }

    [Fact]
    public void Priority_never_sacrifices_coverage_nor_rigid_rules_and_impossible_pair_stays_pending()
    {
        // Pesos: no teto, a prioridade fica acima das folgas intercaladas e abaixo do turno fora do perfil e do reforço de pico
        var o = new SolverOptions();
        Assert.True(o.WeightDoubleDayOffPriority > o.WeightConsecutiveDaysOffPenalty);
        Assert.True((long)o.WeightDoubleDayOffPriority * o.DoubleDayOffPriorityMaxMonths < o.WeightOutOfProfileShift);
        Assert.True((long)o.WeightDoubleDayOffPriority * o.DoubleDayOffPriorityMaxMonths < o.WeightPeakDeficit);

        var snap = Snap();
        // Todos com prioridade máxima: o mínimo de cobertura (1 de manhã e 1 à tarde) continua atendido todos os dias
        var all = Success(Solver().Solve(MonthCommand(new DateOnly(2027, 2, 1), snap with { FolgaDupla = Scores(snap, Names.Select(n => (n, 6)).ToArray()) })));
        AssertCompliant(all);
        output.WriteLine(all.Notice);

        // Ana com folgas manuais intercaladas o mês inteiro (quarta e domingo): a folga dupla é impossível — a escala sai
        // assim mesmo (a prioridade é preferência) e o aviso a lista como pendente
        var ana = Id(snap, "Ana");
        var feb = PlanningContext.MonthOf(new DateOnly(2027, 2, 1));
        var manual = new List<CellDto>();
        for (var d = feb.From; d <= feb.To; d = d.AddDays(1))
            manual.Add(d.DayOfWeek is DayOfWeek.Wednesday or DayOfWeek.Sunday ? new CellDto(ana, d, null, true) : new CellDto(ana, d, Morning.ShiftId, false));
        var pending = Success(Solver().Solve(MonthCommand(feb.From, snap with { FolgaDupla = Scores(snap, ("Ana", 2), ("Bruno", 1)) },
            GenerationModes.SomenteVazios, manual)));
        output.WriteLine(pending.Notice);
        AssertCompliant(pending);
        Assert.Null(FolgaDuplaHistory.InPeriod(pending.Ctx, pending.Assignments)[ana]);
        Assert.Contains("Folga dupla: 1 de 2 com prioridade atendido(s) (Bruno); pendentes: Ana (2 meses sem).", pending.Notice);
    }

    [Fact]
    public void Old_snapshot_or_zero_weight_has_no_priority_nor_notice()
    {
        var snap = Snap();
        var old = Success(Solver().Solve(MonthCommand(new DateOnly(2027, 2, 1), snap)));
        Assert.DoesNotContain("Folga dupla", old.Notice ?? "");
        Assert.DoesNotContain(old.Solutions!.Componentes, c => c.Chave == EngineComponents.FolgaDuplaPrioridade);

        var off = Success(Solver(weight: 0).Solve(MonthCommand(new DateOnly(2027, 2, 1), snap with { FolgaDupla = Scores(snap, ("Ana", 2)) })));
        Assert.DoesNotContain(off.Solutions!.Componentes, c => c.Chave == EngineComponents.FolgaDuplaPrioridade);
        AssertCompliant(off);
    }
}

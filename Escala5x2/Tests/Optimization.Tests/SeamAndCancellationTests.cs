using System.Diagnostics;
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
/// Escala de um mês intermediário gerada de novo entre duas escalas principais já existentes (anterior e seguinte): todas
/// as regras de janela valem nas duas emendas (dias seguidos, interjornada, folga isolada, domingos, max_folgas_seguidas,
/// folgas/carga da semana civil); a janela da continuidade cresce com as regras; o diagnóstico de inviável e o avaliador
/// citam a emenda; e o cancelamento da geração interrompe o CP-SAT.
/// </summary>
public sealed class SeamAndCancellationTests(ITestOutputHelper output)
{
    private static readonly ComplianceEvaluator Evaluator = new();
    private static readonly DateTime Now = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

    private static ScheduleSolver Solver() =>
        new(Options.Create(new SolverOptions { Workers = 4 }), Evaluator, NullLogger<ScheduleSolver>.Instance);

    private static GenerateScheduleCommand Command(DateOnly start, int weeks, PlanningSnapshot snapshot, int seconds = 20,
        string mode = GenerationModes.Completa, IReadOnlyList<CellDto>? fixedCells = null) =>
        new(Guid.NewGuid(), 1, start, weeks, snapshot, seconds, mode, fixedCells, Guid.NewGuid());

    /// <summary>Regras padrão do cadastro (folgas_consecutivas_5x2 fica bloqueada, como a migration a cria).</summary>
    private static List<ComplianceRuleSnapshot> Rules(params ComplianceRuleSnapshot[] extra) => [.. ScheduleSolverTests.DefaultRules(), .. extra];

    /// <summary>9 pessoas no setor (sobra quadro: as emendas não tornam a escala inviável).</summary>
    private static PlanningSnapshot Snap(IReadOnlyList<ComplianceRuleSnapshot>? rules = null)
    {
        var baseSnap = ScheduleSolverTests.Snapshot(rules: rules ?? Rules());
        var sector = baseSnap.Sectors[0].SectorId;
        var people = Enumerable.Range(0, 9).Select(i => new EmployeeSnapshot(Guid.NewGuid(), $"S{i:000}", $"Pessoa {i + 1}", [sector], Guid.NewGuid(),
            true, false, SundayRule.OneInThreeWeeks, [])).ToList();
        return baseSnap with { Employees = people };
    }

    /// <summary>Escala principal Gerada do setor (como o Scheduling.Api monta a candidata da continuidade).</summary>
    private static ContinuitySchedule Principal(PlanningSnapshot snap, DateOnly start, int weeks) =>
        new(Guid.NewGuid(), snap.Sectors[0].SectorId, snap.Sectors[0].Name, 1, start, start.AddDays(weeks * 7 - 1), 2, Now, "Gerada",
            Published: false, Principal: true);

    /// <summary>Células de trabalho gravadas da escala (o motor não grava folgas: célula vazia na Gerada = folga).</summary>
    private static IEnumerable<ContinuityCell> Cells(ContinuitySchedule s, IEnumerable<AssignmentDto> work) =>
        work.Select(a => new ContinuityCell(s.Id, a.EmployeeId, a.Date, a.Start, a.End, false));

    /// <summary>
    /// Fotografia com a continuidade como o Scheduling.Api monta (SnapshotBuilder.ContinuityAsync): histórico e escala
    /// seguinte na janela das regras ativas, folgas registradas junto das emendas e os avisos das duas emendas.
    /// </summary>
    private static (PlanningSnapshot Snapshot, ContinuityResult Previous, ContinuityResult Following) WithContinuity(PlanningSnapshot snap,
        IReadOnlyList<ContinuitySchedule> schedules, IReadOnlyList<ContinuityCell> cells, DateOnly start, int weeks)
    {
        var rules = LegacyRules.Of(snap);
        var days = PlanningContext.ContinuityDaysFor(rules);
        var restWindow = PlanningContext.RestWindowFor(rules);
        var ids = snap.Employees.Select(e => e.EmployeeId).ToList();
        var end = start.AddDays(weeks * 7 - 1);
        var previous = ContinuityHistory.Previous(schedules, cells, start, days, snap.Employees.ToDictionary(e => e.EmployeeId, e => e.Name),
            restWindow: restWindow);
        var following = ContinuityHistory.FollowingWithNotices(schedules, cells, end, days, ids);
        var followingRest = ContinuityHistory.RestDays(schedules, cells, end.AddDays(1), end.AddDays(restWindow), ids);
        return (snap with
        {
            History = previous.History, HistoryRest = previous.Rest, Following = following.History, FollowingRest = followingRest,
            HistoryNotices = [.. previous.Notices, .. following.Notices]
        }, previous, following);
    }

    /// <summary>Resultado com sucesso (senão mostra o diagnóstico/motivo antes de falhar).</summary>
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

    /// <summary>Chaves das regras de janela/semana verificadas nas emendas.</summary>
    private static readonly string[] SeamKeys =
    [
        RuleKeys.DiasConsecutivosMax5x2, RuleKeys.DiasConsecutivosMax6x1, RuleKeys.InterjornadaMinima, RuleKeys.FolgaIsoladaHorasLivres,
        RuleKeys.DomingoFolgaJanela, RuleKeys.DomingoQuinzenalJanela, RuleKeys.MaxFolgasSeguidas, RuleKeys.FolgasSemana5x2,
        RuleKeys.FolgasSemana6x1, RuleKeys.FolgasMinimasSemana, RuleKeys.FolgasConsecutivas5x2, RuleKeys.CargaHorariaSemanal
    ];

    [Fact]
    public void Middle_month_regenerated_between_two_principal_schedules_respects_every_rule_at_both_seams()
    {
        var snap = Snap();
        // Escala anterior (04/01–24/01/2027), do meio (25/01–07/02) e seguinte (08/02–28/02) — 5x2 (42h), máx. 5 dias
        // seguidos, máx. 2 folgas seguidas, domingo a cada 3 semanas, interjornada 11 h, folga isolada 35 h — principais
        // (variáveis oct/nov/dec = anterior/meio/seguinte)
        var octStart = new DateOnly(2027, 1, 4);
        var novStart = new DateOnly(2027, 1, 25);
        var decStart = new DateOnly(2027, 2, 8);
        var octSched = Principal(snap, octStart, 3);
        var decSched = Principal(snap, decStart, 3);
        // As três escalas existentes são coerentes entre si (aqui: recortes de uma geração de 8 semanas; gerar mês a mês
        // sem olhar o seguinte pode deixar a emenda sem saída, o que não é o caso testado)
        var full = Success(Solver().Solve(Command(octStart, 8, snap), seconds: 30));
        var oct = full.Assignments.Where(a => a.Date < novStart).ToList();
        var dec = full.Assignments.Where(a => a.Date >= decStart).ToList();
        var octCells = Cells(octSched, oct).ToList();

        // A escala do meio gerada DE NOVO depois de um afastamento (01 a 03/02), com a anterior e a seguinte já lá: a
        // continuidade é montada como no Scheduling.Api (sem a própria escala, janela das regras ativas, as duas emendas)
        var absent = snap.Employees[4];
        var leave = Enumerable.Range(0, 3).Select(i => new UnavailabilitySnapshot(new DateOnly(2027, 2, 1 + i), UnavailabilityKinds.Afastamento)).ToList();
        var withLeave = snap with
        {
            Employees = snap.Employees.Select(e => e.EmployeeId == absent.EmployeeId
                ? e with { UnavailableDates = leave.Select(l => l.Date).ToList(), Absences = leave }
                : e).ToList()
        };
        var (novSnap, previous, following) = WithContinuity(withLeave, [octSched, decSched], [.. octCells, .. Cells(decSched, dec)], novStart, 2);
        Assert.NotEmpty(previous.History);
        Assert.NotEmpty(following.History);
        Assert.NotEmpty(novSnap.FollowingRest!);
        Assert.Contains(previous.Notices, n => n.Contains("escala principal") && n.Contains("semana anterior"));
        Assert.Contains(following.Notices, n => n.Contains("escala principal") && n.Contains("semana seguinte"));

        var nov = Success(Solver().Solve(Command(novStart, 2, novSnap), seconds: 30));
        var own = Evaluator.Evaluate(nov.Ctx, nov.Assignments);
        foreach (var v in own.Violacoes) output.WriteLine($"novembro: {v.Chave}: {v.Detalhe}");
        Assert.Equal(0, ComplianceEvaluator.RigidViolations(own));
        Assert.DoesNotContain(own.Violacoes, v => v.Detalhe.Contains("continuidade"));

        // A linha do tempo inteira (anterior + meio + seguinte, 8 semanas) avaliada como uma escala só: nenhuma regra de
        // janela/semana é violada — em particular nas emendas (24/01 | 25/01 e 07/02 | 08/02)
        Assert.DoesNotContain(nov.Assignments, a => a.EmployeeId == absent.EmployeeId && leave.Any(l => l.Date == a.Date));
        var whole = PlanningContext.Create(withLeave, octStart, 8);
        var all = oct.Concat(nov.Assignments).Concat(dec).ToList();
        var timeline = Evaluator.Evaluate(whole, all);
        foreach (var v in timeline.Violacoes.Where(v => SeamKeys.Contains(v.Chave))) output.WriteLine($"linha do tempo: {v.Chave}: {v.Detalhe}");
        Assert.DoesNotContain(timeline.Violacoes, v => SeamKeys.Contains(v.Chave));
        Assert.Equal(0, ComplianceEvaluator.RigidViolations(timeline));
    }

    [Fact]
    public void Civil_week_is_never_split_by_the_seam_and_the_seam_week_counts_only_whole_weeks()
    {
        // A escala sempre começa na segunda e tem semanas inteiras: a emenda cai entre domingo e segunda, então a semana
        // civil nunca é cortada (as regras da semana — regime, folgas exatas, carga — são sempre da escala inteira)
        var snap = Snap();
        Assert.Throws<InvalidOperationException>(() => PlanningContext.Create(snap, new DateOnly(2026, 11, 3), 1));
        var start = new DateOnly(2026, 11, 2);
        var ctx = PlanningContext.Create(snap, start, 4);
        Assert.Equal(DayOfWeek.Monday, ctx.Dates[0].DayOfWeek);
        Assert.Equal(DayOfWeek.Sunday, ctx.Dates[^1].DayOfWeek);
        Assert.All(Enumerable.Range(0, ctx.Weeks), w => Assert.Equal(DayOfWeek.Monday, ctx.Regime(w)!.Start.DayOfWeek));

        // Histórico da semana anterior (trabalho de seg a sáb) não entra na conta da primeira semana: ela é inteira desta escala
        var person = snap.Employees[0].EmployeeId;
        var history = Enumerable.Range(2, 6).Select(i => new HistoryEntry(person, start.AddDays(-i), new TimeOnly(7, 0), new TimeOnly(15, 20))).ToList();
        var withHistory = PlanningContext.Create(snap with { History = history }, start, 1);
        var t = withHistory.Shifts[0];
        var work = Enumerable.Range(1, 6).Select(i => new AssignmentDto(person, start.AddDays(i), t.Snapshot.ShiftId, withHistory.Sectors[0].SectorId,
            t.Snapshot.Start, t.Snapshot.End, t.PaidMinutes)).ToList();
        var result = Evaluator.Evaluate(withHistory, work);
        var row = Assert.Single(result.HorasSemanais!, h => h.EmployeeId == person);
        Assert.Equal(6, row.DiasTrabalhados);
        Assert.Equal(6 * t.PaidMinutes, row.MinutosTrabalhados);
    }

    [Fact]
    public void Continuity_window_grows_with_the_sunday_rule_and_the_fourth_sunday_back_is_respected()
    {
        // Domingo a cada 5 semanas: a continuidade precisa de 35 dias (antes eram 21 fixos e o 4º domingo para trás se perdia)
        var rules = ScheduleSolverTests.DefaultRules()
            .Select(r => r.Chave == RuleKeys.DomingoFolgaJanela ? r with { Valor = 5 } : r).ToList();
        Assert.Equal(35, PlanningContext.ContinuityDaysFor(rules));
        Assert.Equal(PlanningContext.FollowingDays, PlanningContext.ContinuityDaysFor(ScheduleSolverTests.DefaultRules()));
        Assert.Equal(ContinuityHistory.RestDaysWindow, PlanningContext.RestWindowFor([ScheduleSolverTests.Rule(1, "descanso", RuleKeys.MaxFolgasSeguidas, 2, "dias")]));
        Assert.Equal(11, PlanningContext.RestWindowFor([ScheduleSolverTests.Rule(1, "descanso", RuleKeys.MaxFolgasSeguidas, 10, "dias")]));

        var snap = Snap(rules);
        var start = new DateOnly(2026, 11, 2);
        var person = snap.Employees[0].EmployeeId;
        var names = snap.Employees.ToDictionary(e => e.EmployeeId, e => e.Name);
        // Escala anterior (5 semanas) com a pessoa trabalhando nos 4 últimos domingos (01/11, 25/10, 18/10, 11/10)
        var previousSched = Principal(snap, start.AddDays(-35), 5);
        var sundays = new[] { 1, 8, 15, 22 }.Select(i => start.AddDays(-i)).ToList();
        var cells = sundays.Select(d => new ContinuityCell(previousSched.Id, person, d, new TimeOnly(7, 0), new TimeOnly(15, 20), false)).ToList();

        var short21 = ContinuityHistory.Previous([previousSched], cells, start, 21, names);
        Assert.DoesNotContain(short21.History, h => h.Date == start.AddDays(-22));
        var days = PlanningContext.ContinuityDaysFor(rules);
        var continuity = ContinuityHistory.Previous([previousSched], cells, start, days, names);
        Assert.Contains(continuity.History, h => h.Date == start.AddDays(-22));

        var ok = Assert.IsType<SolveResult.Success>(Solver().Solve(Command(start, 1, snap with { History = continuity.History }), seconds: 10));
        Assert.Equal(35, ok.Ctx.ContinuityDays);
        // 5º domingo seguido seria 08/11: a pessoa folga
        Assert.DoesNotContain(ok.Assignments, a => a.EmployeeId == person && a.Date == start.AddDays(6));
        Assert.DoesNotContain(Evaluator.Evaluate(ok.Ctx, ok.Assignments).Violacoes, v => v.Chave == RuleKeys.DomingoFolgaJanela);
    }

    [Fact]
    public void Infeasible_at_the_seam_cites_the_previous_schedule_and_the_evaluator_names_the_seam()
    {
        // 5x2 (jan/2027, máx. 5 dias seguidos): a pessoa trabalhou de quarta a domingo na escala anterior e tem célula manual
        // na segunda — seria o 6º dia seguido
        var start = new DateOnly(2027, 1, 4);
        var snap = Snap();
        var person = snap.Employees[0];
        var history = Enumerable.Range(1, 5).Select(i => new HistoryEntry(person.EmployeeId, start.AddDays(-i), new TimeOnly(7, 0), new TimeOnly(15, 20))).ToList();
        var withHistory = snap with { History = history };
        var morning = snap.Shifts.Single(s => s.Code == "M");
        CellDto[] manual = [new(person.EmployeeId, start, morning.ShiftId, false)];

        var bad = Assert.IsType<SolveResult.Infeasible>(Solver().Solve(Command(start, 1, withHistory, 10, GenerationModes.SomenteVazios, manual)));

        var diagnosis = Assert.IsType<InfeasibilityDiagnosis>(bad.Diagnosis);
        foreach (var i in diagnosis.Itens) output.WriteLine($"{i.Tipo}: {i.Mensagem}");
        var item = Assert.Single(diagnosis.Itens, i => i.Tipo == "continuidade");
        Assert.Contains("emenda com a escala anterior", item.Mensagem);
        Assert.Contains("04/01", item.Mensagem);
        Assert.Equal(RuleKeys.DiasConsecutivosMax5x2, item.Chave);
        Assert.Contains(diagnosis.Sugestoes, s => s.Contains("emenda") && s.Contains("escala anterior"));

        // Avaliador: a violação da emenda cita a escala anterior
        var ctx = PlanningContext.Create(withHistory, start, 1);
        var t = ctx.ShiftById(morning.ShiftId)!;
        var result = Evaluator.Evaluate(ctx, [new AssignmentDto(person.EmployeeId, start, morning.ShiftId, ctx.Sectors[0].SectorId, morning.Start, morning.End, t.PaidMinutes)]);
        var v = Assert.Single(result.Violacoes, x => x.Chave == RuleKeys.DiasConsecutivosMax5x2 && x.EmployeeId == person.EmployeeId);
        Assert.Contains("continuidade com a escala anterior (emenda em 04/01)", v.Detalhe);
    }

    // ---------------- Cancelamento da geração ----------------

    [Fact]
    public void Cancelled_before_starting_never_builds_the_model()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Solver().Solve(Command(new DateOnly(2026, 11, 2), 1, Snap()), ct: cts.Token));
    }

    [Fact]
    public void Cancelling_a_running_generation_stops_the_cp_sat_quickly()
    {
        // 6 semanas com 120 s de limite: sem o cancelamento o CP-SAT usaria o tempo todo buscando o ótimo
        var cmd = Command(new DateOnly(2026, 11, 2), 6, Snap(), seconds: 120);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
        var watch = Stopwatch.StartNew();

        Assert.ThrowsAny<OperationCanceledException>(() => Solver().Solve(cmd, ct: cts.Token));

        output.WriteLine($"parou em {watch.Elapsed.TotalSeconds:F1}s");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"o CP-SAT não parou: {watch.Elapsed.TotalSeconds:F1}s");
    }

    [Fact]
    public void Registry_discards_a_queued_generation_and_stops_only_the_matching_running_one()
    {
        var registry = new GenerationRegistry();
        var queued = Guid.NewGuid();
        var generation = Guid.NewGuid();

        // Cancelada enquanto ainda estava na fila: o Worker descarta ao começar
        Assert.False(registry.Cancel(queued, generation));
        Assert.True(registry.IsCancelled(queued, generation));
        Assert.False(registry.IsCancelled(queued, Guid.NewGuid()));
        using (var late = registry.Start(queued, generation, CancellationToken.None))
            Assert.True(late.Token.IsCancellationRequested);

        // Calculando: cancelamento de OUTRA geração da escala não para; o da geração certa para
        var running = Guid.NewGuid();
        var current = Guid.NewGuid();
        using var lease = registry.Start(running, current, CancellationToken.None);
        Assert.Equal(1, registry.RunningCount);
        Assert.False(registry.Cancel(running, Guid.NewGuid()));
        Assert.False(lease.Token.IsCancellationRequested);
        Assert.True(registry.Cancel(running, current));
        Assert.True(lease.Token.IsCancellationRequested);
        lease.Dispose();
        Assert.Equal(0, registry.RunningCount);

        // Sem id (mensagem antiga): cancela qualquer geração da escala
        var any = Guid.NewGuid();
        using var other = registry.Start(any, Guid.NewGuid(), CancellationToken.None);
        Assert.True(registry.Cancel(any, null));
        Assert.True(other.Token.IsCancellationRequested);
    }
}

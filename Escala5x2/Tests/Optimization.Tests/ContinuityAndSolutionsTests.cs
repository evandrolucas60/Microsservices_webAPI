using Escala5x2.BuildingBlocks.Compliance;
using Escala5x2.BuildingBlocks.Contracts.Compliance;
using Escala5x2.BuildingBlocks.Contracts.Planning;
using Escala5x2.BuildingBlocks.Contracts.Scheduling;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Optimization.Worker.Engine;
using Xunit;

namespace Optimization.Tests;

/// <summary>
/// Continuidade com a semana anterior vinda de escala ainda NÃO publicada (Gerada/Aprovada), escolhida por setor e dia
/// (Publicada &gt; Aprovada &gt; Gerada/Rascunho), e o registro das opções de escala avaliadas pelo motor.
/// </summary>
public sealed class ContinuityAndSolutionsTests
{
    private static readonly ComplianceEvaluator Evaluator = new();
    private static readonly DateTime Now = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);

    private static ScheduleSolver Solver(int maxLogged = 50) =>
        new(Options.Create(new SolverOptions { Workers = 4, MaxLoggedSolutions = maxLogged }), Evaluator, NullLogger<ScheduleSolver>.Instance);

    private static GenerateScheduleCommand Command(DateOnly start, int weeks, PlanningSnapshot snapshot) =>
        new(Guid.NewGuid(), 1, start, weeks, snapshot, 20, GenerationModes.Completa, null);

    /// <summary>Fotografia de teste com 9 pessoas no setor (sobra quadro: a restrição de uma pessoa não torna inviável).</summary>
    private static PlanningSnapshot Snap()
    {
        var baseSnap = ScheduleSolverTests.Snapshot();
        var sector = baseSnap.Sectors[0].SectorId;
        var people = Enumerable.Range(0, 9).Select(i => new EmployeeSnapshot(Guid.NewGuid(), $"H{i:000}", $"Pessoa {i + 1}", [sector], Guid.NewGuid(),
            true, false, SundayRule.OneInThreeWeeks, [])).ToList();
        return baseSnap with { Employees = people };
    }

    private static Dictionary<Guid, string> Names(PlanningSnapshot snap) => snap.Employees.ToDictionary(e => e.EmployeeId, e => e.Name);

    private static ContinuitySchedule Sched(Guid? sector, DateOnly start, int rank, string label, DateTime requestedAt, int version = 1) =>
        new(Guid.NewGuid(), sector, "Outro setor", version, start, start.AddDays(6), rank, requestedAt, label, rank == 0);

    /// <summary>Células da semana: trabalho nos dias indicados (07:00–15:20) e folga nos demais, para todas as pessoas.</summary>
    private static List<ContinuityCell> Week(ContinuitySchedule s, IEnumerable<Guid> people, Func<Guid, int, bool> works,
        TimeOnly? start = null, TimeOnly? end = null) =>
        people.SelectMany(p => Enumerable.Range(0, 7).Select(i => works(p, i)
            ? new ContinuityCell(s.Id, p, s.Start.AddDays(i), start ?? new TimeOnly(7, 0), end ?? new TimeOnly(15, 20), false)
            : new ContinuityCell(s.Id, p, s.Start.AddDays(i), null, null, true))).ToList();

    // ---------------- Item 1: semana anterior de escala não publicada ----------------

    [Fact]
    public void Consecutive_days_from_a_generated_previous_week_are_respected()
    {
        // Semana anterior (07–13/12) só GERADA, noutro setor: a pessoa trabalhou de quarta a domingo (5 dias). A escala
        // de 14/12 (5x2, máx. 5 seguidos) não pode escalá-la na segunda (seria o 6º dia).
        var start = new DateOnly(2026, 12, 14);
        var snap = Snap();
        var person = snap.Employees[3].EmployeeId;
        var previous = Sched(Guid.NewGuid(), start.AddDays(-7), 2, "Gerada", Now);
        var cells = Week(previous, snap.Employees.Select(e => e.EmployeeId), (p, i) => p == person ? i >= 2 : i is 0 or 1 or 3 or 4 or 5);

        var continuity = ContinuityHistory.Previous([previous], cells, start, 21, Names(snap));
        Assert.Equal(5, continuity.History.Count(h => h.EmployeeId == person));
        Assert.Contains(continuity.Notices, n => n.Contains("ainda não publicada"));

        var withHistory = snap with { History = continuity.History, HistoryNotices = continuity.Notices };
        var ok = Assert.IsType<SolveResult.Success>(Solver().Solve(Command(start, 1, withHistory)));

        Assert.DoesNotContain(ok.Assignments, a => a.EmployeeId == person && a.Date == start);
        Assert.Contains(ok.Warnings, w => w.Contains("ainda não publicada"));
        // Avaliador pós-geração com o mesmo histórico: sem violação na emenda
        Assert.Equal(100, Evaluator.Evaluate(ok.Ctx, ok.Assignments).Percentual);
    }

    [Fact]
    public void Inter_shift_rest_from_an_approved_previous_week_is_respected()
    {
        // Domingo 01/11 a pessoa saiu às 23:00 (escala Aprovada, não publicada): na segunda 02/11 não pode entrar às
        // 07:00 (8 h < 11 h de interjornada); o turno das 13:40 pode.
        var start = new DateOnly(2026, 11, 2);
        var snap = Snap();
        var person = snap.Employees[0].EmployeeId;
        var previous = Sched(snap.Sectors[0].SectorId, start.AddDays(-7), 1, "Aprovada", Now);
        var cells = Week(previous, [person], (_, i) => i == 6, new TimeOnly(15, 0), new TimeOnly(23, 0));

        var continuity = ContinuityHistory.Previous([previous], cells, start, 21, Names(snap));
        var ok = Assert.IsType<SolveResult.Success>(Solver().Solve(Command(start, 1, snap with { History = continuity.History })));

        var morning = snap.Shifts.Single(s => s.Code == "M").ShiftId;
        Assert.DoesNotContain(ok.Assignments, a => a.EmployeeId == person && a.Date == start && a.ShiftId == morning);
        Assert.Equal(100, Evaluator.Evaluate(ok.Ctx, ok.Assignments).Percentual);
    }

    [Fact]
    public void Evaluator_points_the_violation_at_the_seam_with_the_previous_week()
    {
        var start = new DateOnly(2026, 12, 14);
        var snap = Snap();
        var person = snap.Employees[1];
        var morning = snap.Shifts.Single(s => s.Code == "M");
        // 5 dias trabalhados antes (07:00–15:20) e uma saída às 23:00 no domingo
        List<HistoryEntry> history =
        [
            .. Enumerable.Range(2, 4).Select(i => new HistoryEntry(person.EmployeeId, start.AddDays(-i), new TimeOnly(7, 0), new TimeOnly(15, 20))),
            new HistoryEntry(person.EmployeeId, start.AddDays(-1), new TimeOnly(15, 0), new TimeOnly(23, 0))
        ];
        var ctx = PlanningContext.Create(snap with { History = history }, start, 1);
        AssignmentDto[] cells = [new(person.EmployeeId, start, morning.ShiftId, snap.Sectors[0].SectorId, morning.Start, morning.End, 440)];

        var result = Evaluator.Evaluate(ctx, cells);

        Assert.Contains(result.Violacoes, v => v.EmployeeId == person.EmployeeId && v.Chave == RuleKeys.DiasConsecutivosMax5x2);
        Assert.Contains(result.Violacoes, v => v.EmployeeId == person.EmployeeId && v.Chave == RuleKeys.InterjornadaMinima);
    }

    [Fact]
    public void Per_sector_and_day_the_published_wins_and_an_empty_draft_does_not_hide_the_generated()
    {
        var start = new DateOnly(2026, 12, 14);
        var snap = Snap();
        var people = snap.Employees.Select(e => e.EmployeeId).ToList();
        var sectorA = Guid.NewGuid();
        var sectorB = Guid.NewGuid();
        // Setor A: publicada (folga de todos no domingo) e uma versão gerada MAIS NOVA (trabalho no domingo): vale a publicada
        var published = Sched(sectorA, start.AddDays(-7), 0, "Publicada", Now.AddDays(-5));
        var newer = Sched(sectorA, start.AddDays(-7), 2, "Gerada", Now, version: 2);
        // Setor B: gerada com a pessoa 9 e um rascunho vazio mais novo (não esconde a gerada)
        var generatedB = Sched(sectorB, start.AddDays(-7), 2, "Gerada", Now.AddDays(-1));
        var emptyDraft = Sched(sectorB, start.AddDays(-7), 2, "Rascunho", Now);
        var cells = Week(published, people.Take(8), (_, i) => i < 5)
            .Concat(Week(newer, people.Take(8), (_, _) => true))
            .Concat(Week(generatedB, [people[8]], (_, i) => i >= 2))
            .ToList();

        var continuity = ContinuityHistory.Previous([published, newer, generatedB, emptyDraft], cells, start, 21, Names(snap));

        Assert.DoesNotContain(continuity.History, h => h.EmployeeId != people[8] && h.Date == start.AddDays(-1));
        Assert.Equal(5 * 8, continuity.History.Count(h => h.EmployeeId != people[8]));
        Assert.Equal(5, continuity.History.Count(h => h.EmployeeId == people[8]));
        var notice = Assert.Single(continuity.Notices);
        Assert.Contains("v1", notice);
        Assert.Contains("Gerada", notice);
    }

    [Fact]
    public void Missing_previous_week_is_warned()
    {
        var start = new DateOnly(2026, 12, 14);
        var snap = Snap();
        var none = ContinuityHistory.Previous([], [], start, 21, Names(snap));
        Assert.Empty(none.History);
        Assert.Contains("não tem escala cadastrada na loja", Assert.Single(none.Notices));

        // Só 2 pessoas na escala publicada da semana anterior: aviso com as outras 7
        var published = Sched(Guid.NewGuid(), start.AddDays(-7), 0, "Publicada", Now);
        var some = ContinuityHistory.Previous([published], Week(published, snap.Employees.Take(2).Select(e => e.EmployeeId), (_, i) => i < 5),
            start, 21, Names(snap));
        Assert.Contains("para 7 colaborador(es)", Assert.Single(some.Notices));
    }

    // ---------------- Escala principal ----------------

    [Fact]
    public void Principal_wins_over_the_published_in_the_previous_week_and_is_cited_in_the_notice()
    {
        var start = new DateOnly(2026, 12, 14);
        var snap = Snap();
        var people = snap.Employees.Select(e => e.EmployeeId).ToList();
        var sector = Guid.NewGuid();
        // Mesmo setor e semana: a publicada (trabalho todos os dias) e uma gerada MAIS ANTIGA marcada como principal
        // (trabalho de segunda a sexta): vale a principal, mesmo com status menor e pedida antes
        var published = Sched(sector, start.AddDays(-7), 0, "Publicada", Now);
        var principal = Sched(sector, start.AddDays(-7), 2, "Gerada", Now.AddDays(-3), version: 2) with { Principal = true };
        var cells = Week(published, people, (_, _) => true).Concat(Week(principal, people, (_, i) => i < 5)).ToList();

        var continuity = ContinuityHistory.Previous([published, principal], cells, start, 21, Names(snap));

        Assert.Equal(5 * people.Count, continuity.History.Count);
        Assert.DoesNotContain(continuity.History, h => h.Date >= start.AddDays(-2));
        var notice = Assert.Single(continuity.Notices);
        Assert.Contains("escala principal", notice);
        Assert.Contains("v2", notice);
        Assert.Contains("ainda não publicada", notice);

        // Sem a marcação, volta a valer a publicada (prioridade do status)
        var fallback = ContinuityHistory.Previous([published, principal with { Principal = false }], cells, start, 21, Names(snap));
        Assert.Equal(7 * people.Count, fallback.History.Count);
        Assert.Empty(fallback.Notices);
    }

    [Fact]
    public void Published_principal_is_cited_without_the_unpublished_warning()
    {
        var start = new DateOnly(2026, 12, 14);
        var snap = Snap();
        var principal = Sched(Guid.NewGuid(), start.AddDays(-7), 0, "Publicada", Now) with { Principal = true };
        var cells = Week(principal, snap.Employees.Select(e => e.EmployeeId), (_, i) => i < 5);

        var notice = Assert.Single(ContinuityHistory.Previous([principal], cells, start, 21, Names(snap)).Notices);

        Assert.Contains("escala principal", notice);
        Assert.DoesNotContain("ainda não publicada", notice);
    }

    [Fact]
    public void Principal_without_cells_on_the_day_does_not_hide_the_other_schedule()
    {
        // Principal só com a primeira metade da semana preenchida: nos dias sem célula vale a publicada
        var start = new DateOnly(2026, 12, 14);
        var snap = Snap();
        var people = snap.Employees.Select(e => e.EmployeeId).ToList();
        var sector = Guid.NewGuid();
        var published = Sched(sector, start.AddDays(-7), 0, "Publicada", Now);
        var principal = Sched(sector, start.AddDays(-7), 2, "Rascunho", Now, version: 2) with { Principal = true };
        var cells = Week(published, people, (_, _) => true)
            .Concat(Week(principal, people, (_, _) => false).Where(c => c.Date < start.AddDays(-4)))
            .ToList();

        var continuity = ContinuityHistory.Previous([published, principal], cells, start, 21, Names(snap));

        // Seg–qua: folga da principal; qui–dom: trabalho da publicada
        Assert.Equal(4 * people.Count, continuity.History.Count);
        Assert.All(continuity.History, h => Assert.True(h.Date >= start.AddDays(-4)));
    }

    [Fact]
    public void Following_week_uses_the_principal_before_the_published()
    {
        var end = new DateOnly(2026, 12, 13);
        var snap = Snap();
        var people = snap.Employees.Select(e => e.EmployeeId).ToList();
        var sector = Guid.NewGuid();
        var published = Sched(sector, end.AddDays(1), 0, "Publicada", Now);
        var principal = Sched(sector, end.AddDays(1), 1, "Aprovada", Now.AddDays(-1), version: 2) with { Principal = true };
        var cells = Week(published, people, (_, _) => true).Concat(Week(principal, people, (_, i) => i >= 2)).ToList();

        var following = ContinuityHistory.Following([published, principal], cells, end, PlanningContext.FollowingDays, people);

        Assert.Equal(5 * people.Count, following.Count);
        Assert.DoesNotContain(following, h => h.Date < end.AddDays(3));
        var fallback = ContinuityHistory.Following([published, principal with { Principal = false }], cells, end, PlanningContext.FollowingDays, people);
        Assert.Equal(7 * people.Count, fallback.Count);
    }

    [Fact]
    public void Marking_a_principal_unmarks_only_the_overlapping_principals_of_the_same_sector()
    {
        var sectorA = Guid.NewGuid();
        var sectorB = Guid.NewGuid();
        var monday = new DateOnly(2026, 11, 2);
        PrincipalCandidate Cand(Guid? sector, int startWeek, int weeks, bool principal) =>
            new(Guid.NewGuid(), sector, monday.AddDays(7 * startWeek), monday.AddDays(7 * (startWeek + weeks) - 1), principal);

        var oldA = Cand(sectorA, 0, 3, true);          // 02/11–22/11, principal: sobrepõe a nova → desmarcada
        var laterA = Cand(sectorA, 4, 2, true);        // 30/11–13/12, principal: não sobrepõe → continua
        var draftA = Cand(sectorA, 1, 1, false);       // sobrepõe, mas não era principal
        var otherSector = Cand(sectorB, 0, 4, true);   // outro setor no mesmo período → continua
        var legacy = Cand(null, 0, 4, false);          // escala antiga por loja: nunca é principal
        var all = new[] { oldA, laterA, draftA, otherSector, legacy };
        Assert.True(PrincipalSchedule.IsConsistent(all));

        var nova = Cand(sectorA, 2, 2, false);         // 16/11–29/11
        Assert.Equal(new[] { oldA.Id }, PrincipalSchedule.ToUnmark(nova, all));

        var (result, unmarked) = PrincipalSchedule.Mark(nova, all);
        Assert.Equal(new[] { oldA.Id }, unmarked);
        Assert.True(PrincipalSchedule.IsConsistent(result));
        var byId = result.ToDictionary(s => s.Id);
        Assert.True(byId[nova.Id].Principal);
        Assert.False(byId[oldA.Id].Principal);
        Assert.True(byId[laterA.Id].Principal);
        Assert.False(byId[draftA.Id].Principal);
        Assert.True(byId[otherSector.Id].Principal);

        // Duas principais do mesmo setor no mesmo dia violam a regra; marcar de novo a antiga desmarca a nova
        Assert.False(PrincipalSchedule.IsConsistent([.. result, nova with { Id = Guid.NewGuid(), Principal = true }]));
        var (back, again) = PrincipalSchedule.Mark(oldA, result);
        Assert.Equal(new[] { nova.Id }, again);
        Assert.True(PrincipalSchedule.IsConsistent(back));
        Assert.Throws<InvalidOperationException>(() => PrincipalSchedule.Mark(legacy, all));
    }

    // ---------------- Item 2: registro das soluções do motor ----------------

    [Fact]
    public void Solution_log_counts_solutions_and_the_last_one_is_the_final_objective()
    {
        var ok = Assert.IsType<SolveResult.Success>(Solver().Solve(Command(new DateOnly(2026, 11, 2), 1, ScheduleSolverTests.Snapshot())));

        var log = Assert.IsType<EngineSolutionsSummary>(ok.Solutions);
        Assert.True(log.Total >= 1);
        Assert.Equal(log.Solucoes.Count, log.Guardadas);
        Assert.True(log.Guardadas <= log.Total);
        Assert.Equal(ok.Status, log.Status);
        var last = log.Solucoes[^1];
        Assert.True(last.Escolhida);
        Assert.Single(log.Solucoes, s => s.Escolhida);
        Assert.Equal(ok.Objective, last.Objetivo);
        Assert.Equal(ok.Objective, log.ObjetivoFinal);
        // O detalhamento fecha com o objetivo: Σ componentes − deslocamento (constante das preferenciais)
        Assert.Equal(ok.Objective, last.Componentes.Values.Sum() - log.Deslocamento);
        Assert.Contains(EngineComponents.Custo, last.Componentes.Keys);
        Assert.Contains("domingos", last.Indicadores.Keys);
        Assert.All(log.Solucoes, s => Assert.Equal(s.Objetivo, s.Componentes.Values.Sum() - log.Deslocamento));
        // Objetivo nunca piora de uma solução para a seguinte
        Assert.True(log.Solucoes.Zip(log.Solucoes.Skip(1)).All(p => p.Second.Objetivo <= p.First.Objetivo && p.Second.Numero > p.First.Numero));
    }

    [Fact]
    public void Solution_log_keeps_at_most_the_configured_number_and_does_not_change_the_choice()
    {
        var cmd = Command(new DateOnly(2026, 11, 2), 1, ScheduleSolverTests.Snapshot());
        var full = Assert.IsType<SolveResult.Success>(Solver().Solve(cmd));
        var one = Assert.IsType<SolveResult.Success>(Solver(maxLogged: 1).Solve(cmd));

        var log = one.Solutions!;
        Assert.Equal(1, log.Guardadas);
        Assert.True(log.Total >= 1);
        Assert.Equal(one.Objective, log.Solucoes[0].Objetivo);
        Assert.True(log.Solucoes[0].Escolhida);
        // Mesmo modelo e semente: o registro não altera a solução escolhida (com prova de ótimo)
        if (full.Status == EngineResults.Otima && one.Status == EngineResults.Otima) Assert.Equal(full.Objective, one.Objective);
    }
}

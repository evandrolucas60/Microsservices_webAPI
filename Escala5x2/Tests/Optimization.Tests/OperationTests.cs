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
/// Operação da loja (07h–22h, mínimos 2/2/2, entradas flexíveis de 15 em 15 min), perfil de jornada, disponibilidade,
/// intervalo dentro do modelo, continuidade com a semana seguinte, status e diagnóstico de inviabilidade.
/// </summary>
public sealed class OperationTests(ITestOutputHelper output)
{
    private static readonly Guid Sector = Guid.NewGuid();
    private static readonly ShiftSnapshot Morning = new(Guid.NewGuid(), "M", new TimeOnly(7, 0), new TimeOnly(15, 20), 60);
    private static readonly ShiftSnapshot Evening = new(Guid.NewGuid(), "T", new TimeOnly(13, 40), new TimeOnly(22, 0), 60);
    private static readonly ComplianceEvaluator Evaluator = new();
    /// <summary>Semana de 42h (5x2) depois de 16/12/2026.</summary>
    private static readonly DateOnly Jan2027 = new(2027, 1, 4);

    /// <summary>Rodízio de abertura/fechamento DESLIGADO: estes testes verificam o perfil como restrição dura (ver RotationTests).</summary>
    private static OperationSnapshot Op(int minimum = 2) =>
        new(new TimeOnly(7, 0), new TimeOnly(22, 0), minimum, minimum, minimum, 15, null, null, 500, 60, RodizioAberturaFechamento: false);

    /// <summary>Regras padrão + regras de posição do intervalo como no cadastro (jornada contínua ≤ 6h rígida).</summary>
    private static List<ComplianceRuleSnapshot> Rules(params ComplianceRuleSnapshot[] extra)
    {
        var list = ScheduleSolverTests.DefaultRules();
        list.Add(ScheduleSolverTests.Rule(101, "intervalo", RuleKeys.IntervaloObrigatorioAcima, 360, "minutos"));
        list.Add(ScheduleSolverTests.Rule(102, "intervalo", RuleKeys.IntervaloJornada4a6h, 15, "minutos"));
        list.Add(ScheduleSolverTests.Rule(103, "intervalo", RuleKeys.IntervaloInicioMinimoAposEntrada, 60, "minutos", aplicacao: RuleApplication.Preferencial));
        list.Add(ScheduleSolverTests.Rule(104, "intervalo", RuleKeys.IntervaloFimMinimoAntesSaida, 60, "minutos", aplicacao: RuleApplication.Preferencial));
        list.Add(ScheduleSolverTests.Rule(105, "intervalo", RuleKeys.JornadaContinuaMaxima, 360, "minutos"));
        list.AddRange(extra);
        return list;
    }

    private static EmployeeSnapshot Person(string name, string profile, IReadOnlyList<AvailabilityWindow>? availability = null,
        IReadOnlyList<DateOnly>? absent = null, JourneyPreferences? prefs = null) =>
        new(Guid.NewGuid(), name[..Math.Min(6, name.Length)], name, [Sector], Guid.NewGuid(), true, false, SundayRule.OneInThreeWeeks,
            absent ?? [], absent?.Select(d => new UnavailabilitySnapshot(d, UnavailabilityKinds.Ferias)).ToList(),
            new EmployeeJourneySnapshot(profile, availability, prefs));

    /// <summary>4 de abertura, 4 de fechamento e 3 flexíveis.</summary>
    private static List<EmployeeSnapshot> Team(int opening = 4, int closing = 4, int flexible = 3) =>
    [
        .. Enumerable.Range(1, opening).Select(i => Person($"Abre{i}", JourneyProfiles.Abertura)),
        .. Enumerable.Range(1, closing).Select(i => Person($"Fecha{i}", JourneyProfiles.Fechamento)),
        .. Enumerable.Range(1, flexible).Select(i => Person($"Flex{i}", JourneyProfiles.Flexivel)),
    ];

    /// <summary>Reforço nos picos 10–13h e 15–19h (3 pessoas) todos os dias.</summary>
    private static PlanningSnapshot Snap(List<EmployeeSnapshot> people, OperationSnapshot? op = null, List<ComplianceRuleSnapshot>? rules = null,
        IReadOnlyList<HistoryEntry>? following = null, bool withPeaks = true)
    {
        var demands = withPeaks
            ? Enum.GetValues<DayOfWeek>().SelectMany(d => new[]
            {
                new DemandSnapshot(Sector, d, new TimeOnly(10, 0), new TimeOnly(13, 0), 3),
                new DemandSnapshot(Sector, d, new TimeOnly(15, 0), new TimeOnly(19, 0), 3)
            }).ToList()
            : [];
        return new PlanningSnapshot(Guid.NewGuid(), "Teste", false, [new SectorSnapshot(Sector, "Caixa")], [Morning, Evening], demands, [],
            people, null, [], rules ?? Rules(), null, op ?? Op(), following);
    }

    private static ScheduleSolver Solver(SolverOptions? o = null) =>
        new(Options.Create(o ?? new SolverOptions { Workers = 8, DiagnosisSeconds = 10 }), Evaluator, NullLogger<ScheduleSolver>.Instance);

    private static GenerateScheduleCommand Command(DateOnly start, int weeks, PlanningSnapshot snapshot) =>
        new(Guid.NewGuid(), 1, start, weeks, snapshot, 60);

    private SolveResult.Success SolveOk(DateOnly start, int weeks, PlanningSnapshot snapshot, SolverOptions? o = null)
    {
        var result = Solver(o).Solve(Command(start, weeks, snapshot), 25);
        if (result is SolveResult.Infeasible bad) Assert.Fail($"Inviável: {bad.Reason} | {string.Join(" | ", bad.Diagnosis?.Itens.Select(i => i.Mensagem) ?? [])}");
        if (result is SolveResult.TimedOut slow) Assert.Fail(slow.Reason);
        var ok = Assert.IsType<SolveResult.Success>(result);
        output.WriteLine($"{ok.Status} em {ok.Seconds:F1}s, objetivo {ok.Objective}");
        return ok;
    }

    private static int Min(TimeOnly t) => t.Hour * 60 + t.Minute;

    /// <summary>Presentes fora do intervalo em [from, from + 15), calculado direto das células (independente do avaliador).</summary>
    private static int PresentAt(IEnumerable<AssignmentDto> day, int from) => day.Count(a =>
    {
        var s = Min(a.Start);
        var e = Min(a.End) <= s ? Min(a.End) + 1440 : Min(a.End);
        if (s > from || e < from + 15) return false;
        if (a.BreakStart is not { } b) return true;
        var bs = Min(b) < s ? Min(b) + 1440 : Min(b);
        return !(bs < from + 15 && bs + 60 > from);
    });

    [Fact]
    public void Minimum_of_two_holds_in_every_slot_including_lunch_breaks_and_two_at_opening_and_closing()
    {
        var ok = SolveOk(Jan2027, 1, Snap(Team()));

        Assert.All(ok.Assignments, a => Assert.NotNull(a.BreakStart)); // intervalo decidido no modelo
        foreach (var day in ok.Assignments.GroupBy(a => a.Date))
        {
            for (var t = 7 * 60; t < 22 * 60; t += 15)
                Assert.True(PresentAt(day, t) >= 2, $"{day.Key:dd/MM} {t / 60:00}:{t % 60:00}: {PresentAt(day, t)} presente(s)");
            Assert.True(day.Count(a => Min(a.Start) == 7 * 60) >= 2, $"{day.Key:dd/MM}: menos de 2 na abertura");
            Assert.True(day.Count(a => Min(a.End) == 22 * 60) >= 2, $"{day.Key:dd/MM}: menos de 2 no fechamento");
        }
        var evaluation = Evaluator.Evaluate(ok.Ctx, ok.Assignments);
        Assert.DoesNotContain(evaluation.Violacoes, v => v.Severidade != "baixa");
        var coverage = Assert.Single(evaluation.PorRegra, r => r.Chave == RuleKeys.CoberturaMinima);
        Assert.Equal(0, coverage.Violacoes);
        Assert.True(coverage.Verificacoes >= 7 * 28);
        Assert.Equal("Rigida", coverage.Aplicacao);
        Assert.Equal(7, Assert.Single(evaluation.PorRegra, r => r.Chave == RuleKeys.MinimoAbertura).Verificacoes);
        Assert.Equal(7, Assert.Single(evaluation.PorRegra, r => r.Chave == RuleKeys.MinimoFechamento).Verificacoes);
        // Reforço de pico: preferencial (severidade baixa quando falta)
        Assert.Equal("Preferencial", Assert.Single(evaluation.PorRegra, r => r.Chave == RuleKeys.ReforcoDemanda).Aplicacao);
        output.WriteLine($"Conformidade {evaluation.Percentual}%; reforço de pico com {ok.Deficits.Sum(d => d.Missing)} falta(s)");
    }

    [Fact]
    public void Profiles_pick_their_shifts_and_flexible_entries_follow_the_increment_and_interjornada()
    {
        var ok = SolveOk(Jan2027, 2, Snap(Team()));
        var people = ok.Ctx.Employees.ToDictionary(e => e.Snapshot.EmployeeId);
        var entries = OperationPlan.FlexEntries(Op()).ToHashSet();
        Assert.Equal(25, entries.Count); // 07:15 a 13:25 de 15 em 15

        foreach (var a in ok.Assignments)
        {
            var profile = people[a.EmployeeId].Profile;
            var shift = ok.Ctx.ShiftById(a.ShiftId)!;
            Assert.Equal(profile, shift.Kind);
            switch (profile)
            {
                case JourneyProfiles.Abertura: Assert.Equal(7 * 60, Min(a.Start)); break;
                case JourneyProfiles.Fechamento: Assert.Equal(22 * 60, Min(a.End)); break;
                default:
                    Assert.Contains(Min(a.Start), entries);
                    Assert.Equal(500, shift.Duration);
                    break;
            }
        }
        // Interjornada ≥ 11h entre dias trabalhados seguidos
        foreach (var mine in ok.Assignments.GroupBy(a => a.EmployeeId))
        {
            var byDate = mine.ToDictionary(a => a.Date);
            foreach (var a in mine)
            {
                if (!byDate.TryGetValue(a.Date.AddDays(1), out var next)) continue;
                var gap = 1440 - Min(a.End) + Min(next.Start);
                Assert.True(gap >= 660, $"{people[a.EmployeeId].Snapshot.Name}: {gap} min entre {a.Date:dd/MM} e {next.Date:dd/MM}");
            }
        }
        // Os turnos flexíveis aparecem como turnos gerados "FX hh:mm"
        Assert.Contains(ok.Ctx.Shifts, s => s.Snapshot.Gerado && s.Snapshot.Code.StartsWith("FX ") && s.Kind == JourneyProfiles.Flexivel);
        Assert.Equal(JourneyProfiles.Abertura, ok.Ctx.ShiftById(Morning.ShiftId)!.Kind); // catálogo continua valendo
        Assert.Equal(JourneyProfiles.Fechamento, ok.Ctx.ShiftById(Evening.ShiftId)!.Kind);
        Assert.DoesNotContain(Evaluator.Evaluate(ok.Ctx, ok.Assignments).Violacoes, v => v.Severidade != "baixa");
    }

    [Fact]
    public void Weekly_availability_and_absences_are_respected()
    {
        var team = Team();
        var noSunday = team[0] with
        {
            Jornada = team[0].Jornada! with { Disponibilidade = [new AvailabilityWindow(DayOfWeek.Sunday, false)] }
        };
        var window = team[8] with // flexível que só pode das 09:00 às 18:00
        {
            Jornada = team[8].Jornada! with { Disponibilidade = Enum.GetValues<DayOfWeek>().Select(d => new AvailabilityWindow(d, true, new TimeOnly(9, 0), new TimeOnly(18, 0))).ToList() }
        };
        var vacation = new[] { Jan2027.AddDays(1), Jan2027.AddDays(2) };
        var onLeave = team[5] with { UnavailableDates = vacation, Absences = vacation.Select(d => new UnavailabilitySnapshot(d, UnavailabilityKinds.Ferias)).ToList() };
        team[0] = noSunday;
        team[8] = window;
        team[5] = onLeave;

        var ok = SolveOk(Jan2027, 1, Snap(team));

        Assert.DoesNotContain(ok.Assignments, a => a.EmployeeId == noSunday.EmployeeId && a.Date.DayOfWeek == DayOfWeek.Sunday);
        Assert.All(ok.Assignments.Where(a => a.EmployeeId == window.EmployeeId), a => Assert.True(Min(a.Start) >= 9 * 60 && Min(a.End) <= 18 * 60));
        Assert.DoesNotContain(ok.Assignments, a => a.EmployeeId == onLeave.EmployeeId && vacation.Contains(a.Date));
        // A disponibilidade é verificada pelo avaliador (rígida)
        var evaluation = Evaluator.Evaluate(ok.Ctx, ok.Assignments);
        Assert.True(Assert.Single(evaluation.PorRegra, r => r.Chave == RuleKeys.Disponibilidade).Verificacoes > 0);
        Assert.DoesNotContain(evaluation.Violacoes, v => v.Severidade != "baixa");
        // Turno fora da disponibilidade numa célula (ex.: manual): violação alta
        var sunday = Jan2027.AddDays(6);
        var bad = ok.Assignments.Append(new AssignmentDto(noSunday.EmployeeId, sunday, Morning.ShiftId, Sector, Morning.Start, Morning.End, 440, new TimeOnly(11, 0)))
            .Where(a => !(a.EmployeeId == noSunday.EmployeeId && a.Date == sunday && a.ShiftId != Morning.ShiftId)).ToList();
        Assert.Contains(Evaluator.Evaluate(ok.Ctx, bad).Violacoes, v => v.Chave == RuleKeys.Disponibilidade && v.Severidade == "alta");
    }

    [Fact]
    public void Following_week_already_published_limits_the_last_days()
    {
        var team = Team();
        var closer = team[4];  // fecha às 22h; na segunda seguinte entra às 07:00 (outro setor): domingo não dá interjornada
        var runner = team[1];  // trabalha de segunda a sexta da semana seguinte: domingo seria o 6º dia seguido (5x2: máx. 5)
        var next = Jan2027.AddDays(7);
        List<HistoryEntry> following =
        [
            new(closer.EmployeeId, next, new TimeOnly(7, 0), new TimeOnly(15, 20)),
            .. Enumerable.Range(0, 5).Select(i => new HistoryEntry(runner.EmployeeId, next.AddDays(i), new TimeOnly(7, 0), new TimeOnly(15, 20)))
        ];

        var ok = SolveOk(Jan2027, 1, Snap(team, following: following));

        var sunday = Jan2027.AddDays(6);
        Assert.DoesNotContain(ok.Assignments, a => a.EmployeeId == closer.EmployeeId && a.Date == sunday);
        Assert.DoesNotContain(ok.Assignments, a => a.EmployeeId == runner.EmployeeId && a.Date == sunday);
        Assert.DoesNotContain(Evaluator.Evaluate(ok.Ctx, ok.Assignments).Violacoes, v => v.Severidade != "baixa");

        // O avaliador também olha a semana seguinte: o fechador no domingo descumpre a interjornada
        var cells = ok.Assignments.Where(a => !(a.Date == sunday && a.EmployeeId == closer.EmployeeId))
            .Append(new AssignmentDto(closer.EmployeeId, sunday, Evening.ShiftId, Sector, Evening.Start, Evening.End, 440, new TimeOnly(17, 0))).ToList();
        Assert.Contains(Evaluator.Evaluate(ok.Ctx, cells).Violacoes, v => v.Chave == RuleKeys.InterjornadaMinima && v.EmployeeId == closer.EmployeeId);
    }

    [Fact]
    public void Three_people_cannot_cover_07_to_22_with_two_minimum_and_the_engine_says_why()
    {
        // Cobertura como meta com quadro insuficiente desligada: o diagnóstico de inviabilidade (ligada, a escala sai com meta)
        var result = Solver().Solve(Command(Jan2027, 1, Snap(Team(1, 1, 1), Op() with { CoberturaComoMetaQuadroInsuficiente = false })), 20);

        var bad = Assert.IsType<SolveResult.Infeasible>(result);
        Assert.NotNull(bad.Diagnosis);
        // 1 de abertura para o mínimo de 2: com outro perfil o setor cobriria (falta de perfil, provada por contagem)
        Assert.Contains(bad.Diagnosis!.Itens, i => i.Tipo is "perfil" or "capacidade" && i.Mensagem.Contains("07:00"));
        Assert.NotEmpty(bad.Deficits);
        Assert.NotEmpty(bad.Diagnosis.Sugestoes);
        Assert.StartsWith("Inviável", bad.Reason);
        output.WriteLine(bad.Reason);
        foreach (var i in bad.Diagnosis.Itens) output.WriteLine($"- [{i.Tipo}] {i.Mensagem}");
    }

    [Fact]
    public void Breaks_that_cannot_be_covered_are_proven_infeasible_by_count()
    {
        // 3 de abertura e 3 de fechamento, nenhum flexível, mínimo 2: sem os intervalos a conta fecharia (4 turnos/dia = 28
        // ≤ 30 por semana), mas com o almoço dentro da jornada são 5 por dia (35): a contagem com os intervalos prova
        // (cobertura como meta com quadro insuficiente desligada: o diagnóstico de inviabilidade)
        var result = Solver().Solve(Command(Jan2027, 1, Snap(Team(3, 3, 0), Op() with { CoberturaComoMetaQuadroInsuficiente = false }, withPeaks: false)), 30);

        var bad = Assert.IsType<SolveResult.Infeasible>(result);
        output.WriteLine(bad.Reason);
        foreach (var i in bad.Diagnosis!.Itens) output.WriteLine($"- [{i.Tipo}] {i.Mensagem}");
        Assert.Contains("provado por contagem", bad.Reason);
        Assert.Contains(bad.Diagnosis.Itens, i => i.Tipo == "capacidade" && i.Mensagem.Contains("com os intervalos são necessários 35 turnos/semana")
                                                   && i.Mensagem.Contains("Sem contar os intervalos seriam 28"));
    }

    [Fact]
    public void Too_short_time_is_no_solution_in_time_not_infeasible()
    {
        var result = Solver().Solve(Command(Jan2027, 3, Snap(Team(5, 5, 5))), 0.01);

        var slow = Assert.IsType<SolveResult.TimedOut>(result);
        Assert.Contains("mais tempo", slow.Reason);
    }

    [Fact]
    public void Opening_and_closing_balance_improves_with_its_weight()
    {
        // Sem operação (catálogo M/T, qualquer pessoa em qualquer turno): a mudança de horário prende as pessoas no mesmo
        // turno; o peso de equilíbrio reparte aberturas e fechamentos
        var people = Enumerable.Range(0, 9).Select(i => new EmployeeSnapshot(Guid.NewGuid(), $"E{i}", $"Pessoa {i}", [Sector], Guid.NewGuid(),
            true, false, SundayRule.OneInThreeWeeks, [])).ToList();
        var demands = Enum.GetValues<DayOfWeek>().SelectMany(d => new[]
        {
            new DemandSnapshot(Sector, d, new TimeOnly(8, 0), new TimeOnly(13, 0), 3),
            new DemandSnapshot(Sector, d, new TimeOnly(16, 0), new TimeOnly(21, 0), 2)
        }).ToList();
        var snapshot = new PlanningSnapshot(Guid.NewGuid(), "Teste", false, [new SectorSnapshot(Sector, "Caixa")], [Morning, Evening], demands, [],
            people, null, [], ScheduleSolverTests.DefaultRules());

        int Spread(SolveResult.Success ok) => ok.Ctx.Employees
            .Select(e => ok.Assignments.Count(a => a.EmployeeId == e.Snapshot.EmployeeId && a.ShiftId == Morning.ShiftId))
            .Aggregate((Max: int.MinValue, Min: int.MaxValue), (acc, c) => (Math.Max(acc.Max, c), Math.Min(acc.Min, c)), acc => acc.Max - acc.Min);

        var without = SolveOk(Jan2027, 3, snapshot, new SolverOptions { Workers = 8, WeightOpeningClosingEquity = 0 });
        var with = SolveOk(Jan2027, 3, snapshot, new SolverOptions { Workers = 8 });
        output.WriteLine($"diferença de aberturas: sem peso {Spread(without)}, com peso {Spread(with)}");
        Assert.True(Spread(with) < Spread(without));
    }

    [Fact]
    public void Regime_6x1_and_5x2_still_apply_with_the_operation()
    {
        // 07/12/2026 (44h → 6x1) e 14/12/2026 (virada para 42h → 5x2)
        var start = new DateOnly(2026, 12, 7);
        var ok = SolveOk(start, 2, Snap(Team(4, 4, 3)));
        Assert.Equal(["6x1", "5x2"], ok.Ctx.WeekRegimes().Select(r => r.Regime));
        foreach (var e in ok.Ctx.Employees)
        {
            var w1 = ok.Assignments.Count(a => a.EmployeeId == e.Snapshot.EmployeeId && a.Date < start.AddDays(7));
            var w2 = ok.Assignments.Count(a => a.EmployeeId == e.Snapshot.EmployeeId && a.Date >= start.AddDays(7));
            Assert.True(w1 is 6 or 5, $"{e.Snapshot.Name}: {w1} dias na semana 6x1"); // 5 só com a folga extra de domingo
            Assert.Equal(5, w2);
        }
        Assert.DoesNotContain(Evaluator.Evaluate(ok.Ctx, ok.Assignments).Violacoes, v => v.Severidade != "baixa");
    }

    [Fact]
    public void Consecutive_days_off_in_5x2_when_the_policy_is_active()
    {
        var rule = ScheduleSolverTests.Rule(110, "regime", RuleKeys.FolgasConsecutivas5x2, 1, "booleano");
        var ok = SolveOk(Jan2027, 2, Snap(Team(), rules: Rules(rule)));

        foreach (var e in ok.Ctx.Employees)
        {
            for (var w = 0; w < 2; w++)
            {
                var off = Enumerable.Range(w * 7, 7).Where(d => !ok.Assignments.Any(a => a.EmployeeId == e.Snapshot.EmployeeId && a.Date == ok.Ctx.Dates[d])).ToList();
                Assert.Equal(2, off.Count);
                Assert.Equal(1, off[1] - off[0]);
            }
        }
        var evaluation = Evaluator.Evaluate(ok.Ctx, ok.Assignments);
        Assert.Equal(11 * 2, Assert.Single(evaluation.PorRegra, r => r.Chave == RuleKeys.FolgasConsecutivas5x2).Verificacoes);
        Assert.DoesNotContain(evaluation.Violacoes, v => v.Severidade != "baixa");

        // Bloqueada (padrão do cadastro): não é aplicada nem verificada
        var blocked = Evaluator.Evaluate(PlanningContext.Create(Snap(Team(), rules: Rules(rule with { Status = RuleStatus.Bloqueado })), Jan2027, 2), ok.Assignments);
        Assert.True(Assert.Single(blocked.PorRegra, r => r.Chave == RuleKeys.FolgasConsecutivas5x2).Bloqueada);
    }

    [Fact]
    public void Operation_validation_and_shift_expansion()
    {
        Assert.Empty(OperationPlan.Validate(Op()));
        Assert.Equal(new TimeOnly(7, 15), OperationPlan.Time(OperationPlan.EarliestFlex(Op())));
        Assert.Equal(new TimeOnly(13, 25), OperationPlan.Time(OperationPlan.LatestFlex(Op())));
        Assert.Contains(OperationPlan.Validate(Op() with { Fechamento = new TimeOnly(6, 0) }), e => e.Campo == "fechamento");
        Assert.Contains(OperationPlan.Validate(Op() with { IncrementoFlexivelMinutos = 10 }), e => e.Campo == "incrementoFlexivelMinutos");
        Assert.Contains(OperationPlan.Validate(Op() with { CoberturaMinima = 0 }), e => e.Campo == "coberturaMinima");
        Assert.Contains(OperationPlan.Validate(Op() with { EntradaFlexivelMaisTarde = new TimeOnly(14, 0) }), e => e.Campo == "entradaFlexivelMaisTarde");
        Assert.Contains(OperationPlan.Validate(Op() with { EntradaFlexivelMaisCedo = new TimeOnly(6, 30) }), e => e.Campo == "entradaFlexivelMaisCedo");

        var shifts = OperationPlan.Expand(Op() with { IncrementoFlexivelMinutos = 30 }, Sector, [Morning, Evening], 105);
        Assert.Equal(JourneyProfiles.Abertura, shifts.Single(s => s.ShiftId == Morning.ShiftId).Tipo);
        Assert.Equal(JourneyProfiles.Fechamento, shifts.Single(s => s.ShiftId == Evening.ShiftId).Tipo);
        var flex = shifts.Where(s => s.Tipo == JourneyProfiles.Flexivel).ToList();
        Assert.Equal(["FX 07:30", "FX 08:00"], flex.Take(2).Select(s => s.Code));
        Assert.All(flex, s => Assert.True(s.Gerado && s.BreakMinutes == 60));
        // Idempotente e com id determinístico
        Assert.Equal(shifts.Select(s => s.ShiftId), OperationPlan.Expand(Op() with { IncrementoFlexivelMinutos = 30 }, Sector, shifts, 105).Select(s => s.ShiftId));
    }
}

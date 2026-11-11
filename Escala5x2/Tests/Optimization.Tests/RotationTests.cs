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
/// Rodízio de abertura/fechamento (operação com RodizioAberturaFechamento) e diagnóstico de falta por perfil de jornada.
/// Caso real: setor Açougue com 5 pessoas, todas Flexível (perfil de quem vem da LG), operação 07h–22h com mínimo 1.
/// Coleção sem paralelismo: a equidade do rodízio depende do CP-SAT ter os núcleos (com outras classes em paralelo a
/// busca fica bem mais lenta).
/// </summary>
[Collection(nameof(RotationTests))]
public sealed class RotationTests(ITestOutputHelper output)
{
    private static readonly Guid Sector = Guid.NewGuid();
    private static readonly ComplianceEvaluator Evaluator = new();
    /// <summary>Semana de 42h (5x2) depois de 16/12/2026.</summary>
    private static readonly DateOnly Jan2027 = new(2027, 1, 4);

    /// <param name="target">Cobertura como meta com quadro insuficiente (desligada nos testes do diagnóstico de inviabilidade).</param>
    private static OperationSnapshot Op(int minimum = 1, bool rotation = true, bool target = true) =>
        new(new TimeOnly(7, 0), new TimeOnly(22, 0), minimum, minimum, minimum, 15, null, null, 500, 60, rotation,
            CoberturaComoMetaQuadroInsuficiente: target);

    private static List<ComplianceRuleSnapshot> Rules()
    {
        var list = ScheduleSolverTests.DefaultRules();
        list.Add(ScheduleSolverTests.Rule(101, "intervalo", RuleKeys.IntervaloObrigatorioAcima, 360, "minutos"));
        list.Add(ScheduleSolverTests.Rule(102, "intervalo", RuleKeys.IntervaloJornada4a6h, 15, "minutos"));
        list.Add(ScheduleSolverTests.Rule(103, "intervalo", RuleKeys.IntervaloInicioMinimoAposEntrada, 60, "minutos", aplicacao: RuleApplication.Preferencial));
        list.Add(ScheduleSolverTests.Rule(104, "intervalo", RuleKeys.IntervaloFimMinimoAntesSaida, 60, "minutos", aplicacao: RuleApplication.Preferencial));
        list.Add(ScheduleSolverTests.Rule(105, "intervalo", RuleKeys.JornadaContinuaMaxima, 360, "minutos"));
        return list;
    }

    private static EmployeeSnapshot Person(string name, string profile, IReadOnlyList<UnavailabilitySnapshot>? absences = null) =>
        new(Guid.NewGuid(), name[..Math.Min(6, name.Length)], name, [Sector], Guid.NewGuid(), true, false, SundayRule.OneInThreeWeeks,
            absences?.Select(a => a.Date).ToList() ?? [], absences, new EmployeeJourneySnapshot(profile));

    private static List<EmployeeSnapshot> Team(int opening, int closing, int flexible) =>
    [
        .. Enumerable.Range(1, opening).Select(i => Person($"Abre{i}", JourneyProfiles.Abertura)),
        .. Enumerable.Range(1, closing).Select(i => Person($"Fecha{i}", JourneyProfiles.Fechamento)),
        .. Enumerable.Range(1, flexible).Select(i => Person($"Flex{i}", JourneyProfiles.Flexivel)),
    ];

    private static PlanningSnapshot Snap(List<EmployeeSnapshot> people, OperationSnapshot op, bool withPeaks = false)
    {
        var demands = withPeaks
            ? Enum.GetValues<DayOfWeek>().SelectMany(d => new[]
            {
                new DemandSnapshot(Sector, d, new TimeOnly(10, 0), new TimeOnly(13, 0), 3),
                new DemandSnapshot(Sector, d, new TimeOnly(15, 0), new TimeOnly(19, 0), 3)
            }).ToList()
            : [];
        return new PlanningSnapshot(Guid.NewGuid(), "Teste", false, [new SectorSnapshot(Sector, "Açougue")], [], demands, [],
            people, null, [], Rules(), null, op);
    }

    private static ScheduleSolver Solver() =>
        new(Options.Create(new SolverOptions { Workers = 8, DiagnosisSeconds = 10 }), Evaluator, NullLogger<ScheduleSolver>.Instance);

    private static GenerateScheduleCommand Command(DateOnly start, int weeks, PlanningSnapshot snapshot) =>
        new(Guid.NewGuid(), 1, start, weeks, snapshot, 60);

    private SolveResult.Success SolveOk(DateOnly start, int weeks, PlanningSnapshot snapshot, double seconds = 25)
    {
        var result = Solver().Solve(Command(start, weeks, snapshot), seconds);
        if (result is SolveResult.Infeasible bad) Assert.Fail($"Inviável: {bad.Reason} | {string.Join(" | ", bad.Diagnosis?.Itens.Select(i => i.Mensagem) ?? [])}");
        if (result is SolveResult.TimedOut slow) Assert.Fail(slow.Reason);
        var ok = Assert.IsType<SolveResult.Success>(result);
        output.WriteLine($"{ok.Status} em {ok.Seconds:F1}s, objetivo {ok.Objective}");
        output.WriteLine($"Aviso: {ok.Notice}");
        return ok;
    }

    private static int Min(TimeOnly t) => t.Hour * 60 + t.Minute;

    private static int Spread(IEnumerable<int> counts)
    {
        var list = counts.ToList();
        return list.Max() - list.Min();
    }

    private static void AssertInterjornada(SolveResult.Success ok)
    {
        foreach (var mine in ok.Assignments.GroupBy(a => a.EmployeeId))
        {
            var byDate = mine.ToDictionary(a => a.Date);
            foreach (var a in mine)
            {
                if (!byDate.TryGetValue(a.Date.AddDays(1), out var next)) continue;
                var gap = 1440 - Min(a.End) + Min(next.Start);
                Assert.True(gap >= 660, $"{a.EmployeeId}: {gap} min entre {a.Date:dd/MM} e {next.Date:dd/MM}");
            }
        }
    }

    [Fact]
    public void Five_flexible_with_rotation_are_feasible_and_openings_and_closings_are_spread()
    {
        var ok = SolveOk(Jan2027, 2, Snap(Team(0, 0, 5), Op()), 40);

        var opening = ok.Ctx.Employees.Select(e => ok.Assignments.Count(a => a.EmployeeId == e.Snapshot.EmployeeId && Min(a.Start) == 7 * 60)).ToList();
        var closing = ok.Ctx.Employees.Select(e => ok.Assignments.Count(a => a.EmployeeId == e.Snapshot.EmployeeId && Min(a.End) == 22 * 60)).ToList();
        output.WriteLine($"aberturas por pessoa: {string.Join(", ", opening)}; fechamentos: {string.Join(", ", closing)}");
        Assert.All(ok.Ctx.Dates, d => Assert.Contains(ok.Assignments, a => a.Date == d && Min(a.Start) == 7 * 60));
        Assert.All(ok.Ctx.Dates, d => Assert.Contains(ok.Assignments, a => a.Date == d && Min(a.End) == 22 * 60));
        Assert.True(Spread(opening) <= 2, $"aberturas mal distribuídas: {string.Join(", ", opening)}");
        Assert.True(Spread(closing) <= 2, $"fechamentos mal distribuídos: {string.Join(", ", closing)}");
        AssertInterjornada(ok);

        // Avaliador: fora do perfil com o rodízio ligado não é violação
        var evaluation = Evaluator.Evaluate(ok.Ctx, ok.Assignments);
        Assert.DoesNotContain(evaluation.Violacoes, v => v.Severidade != "baixa");
        Assert.DoesNotContain(evaluation.Violacoes, v => v.Chave == RuleKeys.PerfilJornada);
        Assert.Contains("Abertura em rodízio: 14 turno(s) fora do perfil distribuídos entre", ok.Notice);
        Assert.Contains("Fechamento em rodízio: 14 turno(s)", ok.Notice);
        // Opções do motor: componentes do rodízio no detalhamento
        Assert.Contains(ok.Solutions!.Componentes, c => c.Chave == EngineComponents.ForaDoPerfil);
        Assert.Contains(ok.Solutions.Componentes, c => c.Chave == EngineComponents.EquidadeRodizio);

        // Mesmas células com o rodízio desligado: o perfil volta a ser apontado (preferencial)
        var off = PlanningContext.Create(Snap(Team(0, 0, 5), Op(rotation: false)) with { Employees = ok.Ctx.Snapshot.Employees }, Jan2027, 2);
        Assert.Contains(Evaluator.Evaluate(off, ok.Assignments).Violacoes, v => v.Chave == RuleKeys.PerfilJornada && v.Severidade == "baixa");
    }

    [Fact]
    public void One_opener_does_every_opening_he_can_and_the_rest_rotates()
    {
        var ok = SolveOk(Jan2027, 2, Snap(Team(1, 0, 4), Op()), 40);

        var opener = ok.Ctx.Employees.Single(e => e.Profile == JourneyProfiles.Abertura).Snapshot.EmployeeId;
        var mine = ok.Assignments.Where(a => a.EmployeeId == opener).ToList();
        Assert.Equal(10, mine.Count);                                   // 5 dias por semana (5x2)
        Assert.All(mine, a => Assert.Equal(7 * 60, Min(a.Start)));      // todos de abertura
        var others = ok.Assignments.Count(a => a.EmployeeId != opener && Min(a.Start) == 7 * 60);
        Assert.True(others >= 4, $"{others} abertura(s) em rodízio"); // 14 aberturas − 10 do perfil
        var rotated = ok.Ctx.Employees.Where(e => e.Profile == JourneyProfiles.Flexivel)
            .Select(e => ok.Assignments.Count(a => a.EmployeeId == e.Snapshot.EmployeeId && Min(a.Start) == 7 * 60)).ToList();
        Assert.True(Spread(rotated) <= 2, $"aberturas em rodízio: {string.Join(", ", rotated)}");
        AssertInterjornada(ok);
        Assert.Contains($"Abertura em rodízio: {others} turno(s)", ok.Notice);
        Assert.DoesNotContain(Evaluator.Evaluate(ok.Ctx, ok.Assignments).Violacoes, v => v.Severidade != "baixa");
    }

    [Fact]
    public void With_enough_people_in_each_profile_no_shift_is_out_of_profile()
    {
        var ok = SolveOk(Jan2027, 1, Snap(Team(4, 4, 3), Op(2), withPeaks: true));

        Assert.All(ok.Assignments, a =>
            Assert.Equal(ok.Ctx.Employees.Single(e => e.Snapshot.EmployeeId == a.EmployeeId).Profile, ok.Ctx.ShiftById(a.ShiftId)!.Kind));
        Assert.DoesNotContain("rodízio", ok.Notice ?? "");
    }

    [Fact]
    public void Without_rotation_the_diagnosis_says_nobody_has_the_opening_profile()
    {
        var result = Solver().Solve(Command(Jan2027, 2, Snap(Team(0, 0, 5), Op(rotation: false, target: false))), 20);

        var bad = Assert.IsType<SolveResult.Infeasible>(result);
        foreach (var i in bad.Diagnosis!.Itens) output.WriteLine($"- [{i.Tipo}] {i.Mensagem}");
        foreach (var s in bad.Diagnosis.Sugestoes) output.WriteLine($"* {s}");
        var opening = Assert.Single(bad.Diagnosis.Itens, i => i.Tipo == "perfil" && i.Mensagem.StartsWith("Abertura"));
        Assert.Contains("Nenhum colaborador do setor tem perfil Abertura (5 são Flexível)", opening.Mensagem);
        Assert.Contains("Colaboradores → Jornada", opening.Mensagem);
        Assert.Contains("rodízio", opening.Mensagem);
        Assert.Contains(bad.Diagnosis.Itens, i => i.Tipo == "perfil" && i.Mensagem.Contains("Nenhum colaborador do setor tem perfil Fechamento (5 são Flexível)"));
        Assert.DoesNotContain(bad.Diagnosis.Itens, i => i.Tipo == "capacidade");
        Assert.Contains("Nenhum colaborador do setor tem perfil", bad.Reason);
        Assert.Contains(bad.Diagnosis.Sugestoes, s => s.Contains("ligue o rodízio"));
    }

    [Fact]
    public void Without_rotation_one_opener_is_not_enough_for_seven_openings()
    {
        var result = Solver().Solve(Command(Jan2027, 1, Snap(Team(1, 1, 3), Op(rotation: false, target: false))), 20);

        var bad = Assert.IsType<SolveResult.Infeasible>(result);
        foreach (var i in bad.Diagnosis!.Itens) output.WriteLine($"- [{i.Tipo}] {i.Mensagem}");
        var opening = Assert.Single(bad.Diagnosis.Itens, i => i.Tipo == "perfil" && i.Mensagem.StartsWith("Abertura"));
        Assert.Contains("Só 1 colaborador do setor tem perfil Abertura (Abre1) e ele trabalha no máximo 5 dia(s) na semana de 04/01 (são 7 aberturas)", opening.Mensagem);
        Assert.Contains("são necessários pelo menos 2 com esse perfil", opening.Mensagem);
    }

    [Fact]
    public void Capacity_counts_the_rotation_and_a_real_lack_of_people_names_the_absences_without_health_data()
    {
        // Rodízio ligado: a contagem considera todos do setor (não prova inviável)
        Assert.Empty(InfeasibilityDiagnostics.Capacity(PlanningContext.Create(Snap(Team(0, 0, 5), Op()), Jan2027, 1)).Items);
        Assert.NotEmpty(InfeasibilityDiagnostics.Capacity(PlanningContext.Create(Snap(Team(0, 0, 5), Op(rotation: false)), Jan2027, 1)).Items);

        // Quarta-feira com todo mundo ausente: falta de gente (não de perfil), com os nomes e motivos resumidos
        var wednesday = Jan2027.AddDays(2);
        var people = new List<EmployeeSnapshot>
        {
            Person("Ana", JourneyProfiles.Flexivel, [new(wednesday, UnavailabilityKinds.Ferias)]),
            Person("Bruno", JourneyProfiles.Flexivel, [new(wednesday, UnavailabilityKinds.AtestadoMedico)]),
            Person("Carla", JourneyProfiles.Flexivel, [new(wednesday, UnavailabilityKinds.Licenca)]),
        };
        var (items, deficits) = InfeasibilityDiagnostics.Capacity(PlanningContext.Create(Snap(people, Op()), Jan2027, 1));
        foreach (var i in items) output.WriteLine($"- [{i.Tipo}] {i.Mensagem}");
        Assert.NotEmpty(deficits);
        var item = Assert.Single(items, i => i.Tipo == "capacidade" && i.Mensagem.StartsWith("Das 07:00 às 22:00"));
        Assert.Contains("Ana (férias)", item.Mensagem);
        Assert.Contains("Bruno (afastado)", item.Mensagem);
        Assert.Contains("Carla (licença)", item.Mensagem);
        Assert.DoesNotContain("médic", item.Mensagem);
        Assert.DoesNotContain(items, i => i.Tipo == "perfil");
    }
}

/// <summary>Roda sozinha (sem outras classes de teste em paralelo).</summary>
[CollectionDefinition(nameof(RotationTests), DisableParallelization = true)]
public sealed class RotationTestsCollection;

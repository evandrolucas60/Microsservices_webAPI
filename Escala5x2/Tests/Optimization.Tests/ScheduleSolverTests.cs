using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Escala5x2.BuildingBlocks.Compliance;
using Escala5x2.BuildingBlocks.Contracts.Compliance;
using Escala5x2.BuildingBlocks.Contracts.Planning;
using Escala5x2.BuildingBlocks.Contracts.Scheduling;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Optimization.Worker.Engine;
using Xunit;

namespace Optimization.Tests;

public sealed class ScheduleSolverTests
{
    private static readonly Guid Sector = Guid.NewGuid();
    private static readonly ShiftSnapshot Morning = new(Guid.NewGuid(), "M", new TimeOnly(7, 0), new TimeOnly(15, 20), 60);
    private static readonly ShiftSnapshot Evening = new(Guid.NewGuid(), "T", new TimeOnly(13, 40), new TimeOnly(22, 0), 60);
    private static readonly ComplianceEvaluator Evaluator = new();

    internal static ComplianceRuleSnapshot Rule(int id, string categoria, string chave, decimal valor, string unidade,
        DateOnly? from = null, DateOnly? to = null, RuleApplication aplicacao = RuleApplication.Rigida) =>
        new(id, categoria, chave, valor, unidade, chave, from, to, RuleStatus.Ativo, aplicacao);

    /// <summary>Regras comuns aos dois modelos (antes e depois do regime 6x1/5x2).</summary>
    private static List<ComplianceRuleSnapshot> CommonRules() =>
    [
        Rule(1, "jornada", RuleKeys.CargaHorariaSemanal, 44, "horas", null, new DateOnly(2026, 12, 15)),
        Rule(2, "jornada", RuleKeys.CargaHorariaSemanal, 42, "horas", new DateOnly(2026, 12, 16), new DateOnly(2027, 12, 15)),
        Rule(3, "jornada", RuleKeys.CargaHorariaSemanal, 40, "horas", new DateOnly(2027, 12, 16)),
        Rule(4, "jornada", RuleKeys.LimiteDiario, 480, "minutos"),
        Rule(5, "jornada", RuleKeys.LimiteDiarioComAcordo, 504, "minutos", new DateOnly(2026, 12, 16), new DateOnly(2027, 12, 15)),
        Rule(6, "jornada", RuleKeys.HoraExtraMaxDiaria, 120, "minutos"),
        Rule(7, "compliance", RuleKeys.LimiteDomingo, 480, "minutos"),
        Rule(10, "descanso", RuleKeys.InterjornadaMinima, 660, "minutos"),
        Rule(11, "descanso", RuleKeys.FolgaIsoladaHorasLivres, 35, "horas"),
        Rule(12, "domingo", RuleKeys.DomingoFolgaJanela, 3, "semanas"),
        Rule(13, "domingo", RuleKeys.DomingoQuinzenalJanela, 2, "semanas"),
        Rule(14, "intervalo", RuleKeys.IntervaloPadrao, 60, "minutos", aplicacao: RuleApplication.Preferencial),
        Rule(15, "intervalo", RuleKeys.IntervaloMinimo, 60, "minutos"),
        Rule(16, "intervalo", RuleKeys.IntervaloMaximo, 120, "minutos"),
        Rule(17, "cobertura", RuleKeys.CoberturaFaixa, 30, "minutos"),
        Rule(18, "feriado", RuleKeys.FeriadoExigeAutorizacao, 1, "booleano"),
    ];

    /// <summary>
    /// Mesmo conjunto do cadastro depois da migration RegimePorCargaSemanal (intervalo padrão = 60 para os turnos do
    /// teste): piso do DSR 1 até 15/12/2026 e 2 depois; regime 6x1 com carga ≥ 44h, senão 5x2.
    /// </summary>
    internal static List<ComplianceRuleSnapshot> DefaultRules() =>
    [
        .. CommonRules(),
        Rule(8, "descanso", RuleKeys.FolgasMinimasSemana, 1, "dias", null, new DateOnly(2026, 12, 15)),
        Rule(9, "descanso", RuleKeys.FolgasMinimasSemana, 2, "dias", new DateOnly(2026, 12, 16)),
        Rule(20, "regime", RuleKeys.CargaMinimaRegime6x1, 44, "horas"),
        Rule(21, "regime", RuleKeys.FolgasSemana6x1, 1, "dias"),
        Rule(22, "regime", RuleKeys.FolgasSemana5x2, 2, "dias"),
        Rule(23, "regime", RuleKeys.DiasConsecutivosMax6x1, 6, "dias"),
        Rule(24, "regime", RuleKeys.DiasConsecutivosMax5x2, 5, "dias"),
        Rule(25, "regime", RuleKeys.FolgaExtraDomingo6x1, 1, "booleano"),
        // Migration RegraMaxFolgasSeguidas: no máximo 2 folgas seguidas (ativa)
        Rule(26, "descanso", RuleKeys.MaxFolgasSeguidas, 2, "dias"),
    ];

    /// <summary>Fotografia gravada no 5x2 estrito (antes do regime): folgas mínimas/máximas = 2 e dias_consecutivos_max = 5.</summary>
    internal static List<ComplianceRuleSnapshot> StrictRules() =>
    [
        .. CommonRules(),
        Rule(8, "descanso", RuleKeys.FolgasMinimasSemana, 2, "dias"),
        Rule(9, "descanso", RuleKeys.DiasConsecutivosMax, 5, "dias"),
        Rule(19, "descanso", RuleKeys.FolgasMaximasSemana, 2, "dias"),
    ];

    private static readonly LegalRuleSnapshot[] LegacyRules =
    [
        new(Guid.NewGuid(), "PRE-EC141", new DateOnly(2000, 1, 1), new DateOnly(2026, 12, 15), 2640, 480, 0, 2, 5, 660, 3, 120),
        new(Guid.NewGuid(), "EC141-TRANSICAO", new DateOnly(2026, 12, 16), new DateOnly(2027, 12, 15), 2520, 480, 504, 2, 5, 660, 3, 120),
        new(Guid.NewGuid(), "EC141-DEFINITIVA", new DateOnly(2027, 12, 16), null, 2400, 480, 0, 2, 5, 660, 3, 120)
    ];

    private static readonly string[] Names = ["Ana", "Bruno", "Carla", "Diego", "Elisa", "Fábio", "Gabi"];
    private static readonly List<EmployeeSnapshot> Employees = Names.Select((n, i) => new EmployeeSnapshot(Guid.NewGuid(), $"D{i:000}", n, [Sector], Guid.NewGuid(),
        true, false, n is "Ana" or "Gabi" ? SundayRule.Biweekly : SundayRule.OneInThreeWeeks, [])).ToList();

    /// <summary>
    /// Quadro de 9 (7 + Hugo e Iara): no 5x2 estrito, semana com feriado sem autorização tem 4 dias de trabalho por
    /// pessoa e afastamento reduz os dias da pessoa; 7 pessoas (28 turnos) não cobrem os ~30 exigidos nessas semanas.
    /// </summary>
    private static readonly List<EmployeeSnapshot> Staff9 = [.. Employees,
        .. new[] { "Hugo", "Iara" }.Select((n, i) => new EmployeeSnapshot(Guid.NewGuid(), $"D{7 + i:000}", n, [Sector], Guid.NewGuid(),
            true, false, SundayRule.OneInThreeWeeks, []))];

    /// <summary>Fotografia gravada antes do 5x2 estrito: sem folgas_maximas_semana.</summary>
    private static List<ComplianceRuleSnapshot> RulesBeforeStrict() => StrictRules().Where(r => r.Chave != RuleKeys.FolgasMaximasSemana).ToList();

    internal static PlanningSnapshot Snapshot(int sundayMorning = 2, IReadOnlyList<HolidaySnapshot>? holidays = null,
        IReadOnlyList<ComplianceRuleSnapshot>? rules = null, bool legacy = false, IReadOnlyList<EmployeeSnapshot>? employees = null)
    {
        var demands = new List<DemandSnapshot>();
        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            var (m, e) = day switch { DayOfWeek.Saturday => (4, 2), DayOfWeek.Sunday => (sundayMorning, 2), _ => (3, 2) };
            demands.Add(new DemandSnapshot(Sector, day, new TimeOnly(8, 0), new TimeOnly(13, 30), m));
            demands.Add(new DemandSnapshot(Sector, day, new TimeOnly(15, 30), new TimeOnly(21, 30), e));
        }

        return new PlanningSnapshot(Guid.NewGuid(), "Teste", false, [new SectorSnapshot(Sector, "Caixa")],
            [Morning, Evening], demands, holidays ?? [], employees ?? Employees, legacy ? LegacyRules : null, [],
            legacy ? null : rules ?? DefaultRules());
    }

    private static GenerateScheduleCommand Command(DateOnly start, int weeks, PlanningSnapshot snapshot,
        string mode = GenerationModes.Completa, IReadOnlyList<CellDto>? fixedCells = null) =>
        new(Guid.NewGuid(), 1, start, weeks, snapshot, 20, mode, fixedCells);

    private static ScheduleSolver Solver() => new(Options.Create(new SolverOptions { Workers = 4 }), Evaluator, NullLogger<ScheduleSolver>.Instance);

    private static Guid Id(string name) => Employees.Single(e => e.Name == name).EmployeeId;

    [Theory]
    [InlineData(2026, 11, 2, 3, 7)]   // 44h → 6x1
    [InlineData(2026, 12, 14, 3, 9)]  // semana de virada para 42h (5x2) + Natal sem autorização (4 dias na semana → 9 pessoas)
    [InlineData(2027, 12, 13, 2, 7)]  // virada para 40h (5x2)
    public void Demo_store_is_feasible_and_fully_compliant(int y, int m, int d, int weeks, int staff)
    {
        var cmd = Command(new DateOnly(y, m, d), weeks, Snapshot(holidays: [new HolidaySnapshot(new DateOnly(2026, 12, 25), "Natal", false)],
            employees: staff == 9 ? Staff9 : Employees));

        var result = Solver().Solve(cmd);

        if (result is SolveResult.Infeasible bad)
            Assert.Fail($"Inviável: {bad.Reason} Déficits: {string.Join(", ", bad.Deficits.Select(x => $"{x.Date:dd/MM} {x.SlotStart:HH\\:mm} −{x.Missing}"))}");
        var ok = Assert.IsType<SolveResult.Success>(result);
        Assert.Contains(ok.Status, new[] { EngineResults.Otima, EngineResults.Viavel });
        var evaluation = Evaluator.Evaluate(ok.Ctx, ok.Assignments);
        Assert.Empty(evaluation.Violacoes);
        Assert.Equal(100, evaluation.Percentual);
        Assert.DoesNotContain(ok.Assignments, a => a.Date == new DateOnly(2026, 12, 25));
    }

    [Fact]
    public void Old_snapshot_with_legal_rule_versions_still_solves_and_evaluates()
    {
        var cmd = Command(new DateOnly(2026, 11, 2), 2, Snapshot(legacy: true));

        var ok = Assert.IsType<SolveResult.Success>(Solver().Solve(cmd));

        var evaluation = Evaluator.Evaluate(ok.Ctx, ok.Assignments);
        Assert.Equal(100, evaluation.Percentual);
        Assert.Contains(evaluation.PorRegra, r => r.Chave == RuleKeys.CargaHorariaSemanal && r.Verificacoes == 7 * 2);
    }

    [Fact]
    public void Simultaneous_sunday_demand_above_rotation_capacity_is_reported_before_solving()
    {
        // 5 pessoas ao mesmo tempo no domingo com 7 no quadro (2 quinzenais): capacidade ⌊2/3·5⌋ + ⌊2/2⌋ = 4.
        var result = Solver().Solve(Command(new DateOnly(2026, 11, 2), 3, Snapshot(sundayMorning: 5)));

        var bad = Assert.IsType<SolveResult.Infeasible>(result);
        Assert.NotEmpty(bad.Deficits);
    }

    [Fact]
    public void Original_example_with_five_people_every_sunday_has_proven_deficit()
    {
        // Caso do exemplo original: 3 de manhã + 2 à tarde = 5 pessoas por domingo.
        // Em 3 domingos: 15 presenças exigidas contra no máximo 14 permitidas pelo rodízio.
        var result = Solver().Solve(Command(new DateOnly(2026, 11, 2), 3, Snapshot(sundayMorning: 3)));

        var bad = Assert.IsType<SolveResult.Infeasible>(result);
        Assert.Contains(bad.Deficits, d => d.Date.DayOfWeek == DayOfWeek.Sunday);
    }

    [Fact]
    public void Blocked_rule_is_not_applied_by_the_engine_nor_by_the_evaluator()
    {
        var christmas = new DateOnly(2026, 12, 25);
        HolidaySnapshot[] holidays = [new HolidaySnapshot(christmas, "Natal", false)];
        var start = new DateOnly(2026, 12, 21);

        // Regra ativa: ninguém trabalha no Natal
        var active = Assert.IsType<SolveResult.Success>(Solver().Solve(Command(start, 1, Snapshot(holidays: holidays, employees: Staff9))));
        Assert.DoesNotContain(active.Assignments, a => a.Date == christmas);

        // Regra bloqueada: o feriado vira dia comum (com demanda) e o avaliador mostra a regra como bloqueada
        var rules = DefaultRules().Select(r => r.Chave == RuleKeys.FeriadoExigeAutorizacao ? r with { Status = RuleStatus.Bloqueado } : r).ToList();
        var blocked = Assert.IsType<SolveResult.Success>(Solver().Solve(Command(start, 1, Snapshot(holidays: holidays, rules: rules, employees: Staff9))));
        Assert.Contains(blocked.Assignments, a => a.Date == christmas);

        var evaluation = Evaluator.Evaluate(blocked.Ctx, blocked.Assignments);
        var feriado = Assert.Single(evaluation.PorRegra, r => r.Chave == RuleKeys.FeriadoExigeAutorizacao);
        Assert.True(feriado.Bloqueada);
        Assert.Equal(0, feriado.Verificacoes);
        Assert.Equal(100, evaluation.Percentual);
    }

    [Fact]
    public void Changing_weekly_hours_changes_the_result_and_the_evaluation()
    {
        var start = new DateOnly(2026, 11, 2);
        var ok = Assert.IsType<SolveResult.Success>(Solver().Solve(Command(start, 1, Snapshot())));

        // Mesmo quadro com carga de 30h: cada pessoa faz no máximo 4 turnos de 7h20 → não cobre a demanda
        var rules30 = DefaultRules().Where(r => r.Chave != RuleKeys.CargaHorariaSemanal)
            .Append(Rule(99, "jornada", RuleKeys.CargaHorariaSemanal, 30, "horas")).ToList();
        var tighter = Solver().Solve(Command(start, 1, Snapshot(rules: rules30)));
        Assert.IsType<SolveResult.Infeasible>(tighter);

        // A escala de 44h avaliada com a regra de 30h acusa a carga semanal
        var ctx30 = PlanningContext.Create(Snapshot(rules: rules30), start, 1);
        var evaluation = Evaluator.Evaluate(ctx30, ok.Assignments);
        Assert.Contains(evaluation.Violacoes, v => v.Chave == RuleKeys.CargaHorariaSemanal && v.Severidade == "alta");
        Assert.True(evaluation.Percentual < 100);
    }

    [Fact]
    public void Fixed_manual_cells_are_kept_in_fill_empty_mode()
    {
        var start = new DateOnly(2026, 11, 2);
        CellDto[] manual =
        [
            new(Id("Ana"), start, Evening.ShiftId, false),
            new(Id("Ana"), start.AddDays(1), null, true),           // folga manual
            new(Id("Bruno"), start.AddDays(2), Morning.ShiftId, false),
            new(Id("Carla"), start.AddDays(3), null, true),
        ];

        var ok = Assert.IsType<SolveResult.Success>(Solver().Solve(Command(start, 1, Snapshot(), GenerationModes.SomenteVazios, manual)));

        Assert.Contains(ok.Assignments, a => a.EmployeeId == Id("Ana") && a.Date == start && a.ShiftId == Evening.ShiftId);
        Assert.DoesNotContain(ok.Assignments, a => a.EmployeeId == Id("Ana") && a.Date == start.AddDays(1));
        Assert.Contains(ok.Assignments, a => a.EmployeeId == Id("Bruno") && a.Date == start.AddDays(2) && a.ShiftId == Morning.ShiftId);
        Assert.DoesNotContain(ok.Assignments, a => a.EmployeeId == Id("Carla") && a.Date == start.AddDays(3));
        Assert.Equal(100, Evaluator.Evaluate(ok.Ctx, ok.Assignments).Percentual);

        // No modo completo as células manuais não são respeitadas (não entram como fixas)
        var full = Assert.IsType<SolveResult.Success>(Solver().Solve(Command(start, 1, Snapshot(), GenerationModes.Completa, manual)));
        Assert.Empty(full.Ctx.Fixed);
    }

    [Fact]
    public void Conflicting_fixed_cells_make_the_schedule_infeasible_with_a_diagnosis_and_no_flexible_mode()
    {
        var start = new DateOnly(2026, 11, 2);
        // Ana trabalhando os 7 dias da semana: descumpre folgas e dias consecutivos — não existe mais modo flexível
        var manual = Enumerable.Range(0, 7).Select(d => new CellDto(Id("Ana"), start.AddDays(d), Morning.ShiftId, false)).ToList();

        var result = Solver().Solve(Command(start, 1, Snapshot(), GenerationModes.SomenteVazios, manual));

        var bad = Assert.IsType<SolveResult.Infeasible>(result);
        Assert.NotNull(bad.Diagnosis);
        // O núcleo de restrições aponta as células manuais da Ana e a regra que elas violam
        Assert.Contains(bad.Diagnosis!.Itens, i => i.Tipo == "celulas_manuais" && i.EmployeeId == Id("Ana"));
        Assert.Contains(bad.Diagnosis.Itens, i => i.Tipo == "regra");
        Assert.Contains(bad.Diagnosis.Sugestoes, s => s.Contains("células manuais"));
        Assert.DoesNotContain(Enum.GetNames<SolveStage>(), n => n.Contains("Flex"));
    }

    [Fact]
    public void Evaluator_percentage_is_conforming_checks_over_total_checks()
    {
        var start = new DateOnly(2026, 11, 2);
        var ctx = PlanningContext.Create(Snapshot(), start, 1);

        // Escala vazia: a cobertura falha (todas as faixas com demanda)
        var empty = Evaluator.Evaluate(ctx, []);
        var coverage = Assert.Single(empty.PorRegra, r => r.Chave == RuleKeys.CoberturaFaixa);
        Assert.Equal(ctx.Slots.Count, coverage.Verificacoes);
        Assert.Equal(ctx.Slots.Count, coverage.Violacoes);
        Assert.Equal(empty.TotalViolacoes, empty.Violacoes.Count);
        Assert.Equal(empty.TotalVerificacoes, empty.PorRegra.Sum(r => r.Verificacoes));
        Assert.Equal(Math.Round((empty.TotalVerificacoes - empty.TotalViolacoes) * 100.0 / empty.TotalVerificacoes, 1), empty.Percentual);
        Assert.All(empty.Violacoes.Where(v => v.Chave == RuleKeys.CoberturaFaixa), v => Assert.Equal("media", v.Severidade));
        // Semana de 44h = 6x1 (exatamente 1 folga): semana vazia = 7 folgas por pessoa (alta) e 7 folgas seguidas
        // (max_folgas_seguidas = 2, uma sequência por pessoa); nenhuma outra regra falha
        var folgas = Assert.Single(empty.PorRegra, r => r.Chave == RuleKeys.FolgasSemana6x1);
        Assert.Equal(7, folgas.Violacoes);
        var seguidas = Assert.Single(empty.PorRegra, r => r.Chave == RuleKeys.MaxFolgasSeguidas);
        Assert.Equal((7, 7), (seguidas.Verificacoes, seguidas.Violacoes));
        // ... e os 7 dias abertos sem ninguém no setor (dia_aberto_sem_ninguem, rígida: nunca 100%)
        var vazios = Assert.Single(empty.PorRegra, r => r.Chave == ComplianceEvaluator.DiaSemNinguem);
        Assert.Equal((7, 7), (vazios.Verificacoes, vazios.Violacoes));
        Assert.All(empty.Violacoes.Where(v => v.Chave != RuleKeys.CoberturaFaixa),
            v => Assert.Contains(v.Chave, new[] { RuleKeys.FolgasSemana6x1, RuleKeys.MaxFolgasSeguidas, ComplianceEvaluator.DiaSemNinguem }));
        Assert.Equal(0, Assert.Single(empty.PorRegra, r => r.Chave == RuleKeys.FolgasSemana5x2).Verificacoes);

        // Turno fora do intervalo padrão (preferencial): violação de severidade baixa
        var rules = DefaultRules().Select(r => r.Chave == RuleKeys.IntervaloPadrao ? r with { Valor = 105 } : r).ToList();
        var ctx105 = PlanningContext.Create(Snapshot(rules: rules), start, 1);
        var one = new AssignmentDto(Id("Ana"), start, Morning.ShiftId, Sector, Morning.Start, Morning.End, 440);
        var evaluation = Evaluator.Evaluate(ctx105, [one]);
        var padrao = Assert.Single(evaluation.Violacoes, v => v.Chave == RuleKeys.IntervaloPadrao);
        Assert.Equal("baixa", padrao.Severidade);
    }

    [Fact]
    public void Suggestion_fixes_a_manual_violation_with_few_changes()
    {
        var start = new DateOnly(2026, 11, 2);
        var snapshot = Snapshot();
        var ok = Assert.IsType<SolveResult.Success>(Solver().Solve(Command(start, 1, snapshot)));

        // Coloca a Ana para trabalhar em todos os dias de folga (6/7 dias na semana)
        var cells = ok.Assignments.Select(a => new CellDto(a.EmployeeId, a.Date, a.ShiftId, false)).ToList();
        var anaDays = cells.Where(c => c.EmployeeId == Id("Ana")).Select(c => c.Date).ToHashSet();
        foreach (var d in Enumerable.Range(0, 7).Select(start.AddDays).Where(d => !anaDays.Contains(d)))
            cells.Add(new CellDto(Id("Ana"), d, Morning.ShiftId, false));

        var outcome = Solver().Suggest(new SuggestCorrectionsCommand(Guid.NewGuid(), Guid.NewGuid(), start, 1, snapshot, cells, 20));

        Assert.Equal(SuggestionStatus.Pronta, outcome.Status);
        Assert.True(outcome.PercentualPrevisto > outcome.PercentualAtual);
        Assert.NotEmpty(outcome.Changes);
        Assert.True(outcome.Changes.Count <= 10, $"{outcome.Changes.Count} alterações");
    }

    // ---------------- Feriados por abrangência ----------------

    [Fact]
    public void Municipal_holiday_blocks_only_stores_of_that_municipality()
    {
        var start = new DateOnly(2026, 11, 2);
        var end = start.AddDays(6);
        var emancipacao = new DateOnly(2026, 11, 4);
        HolidayDefinition[] cadastro =
        [
            new(Guid.NewGuid(), HolidayScope.Municipal, "PE", "Cabo de Santo Agostinho", emancipacao, "Emancipação do Cabo", false, false, null),
            new(Guid.NewGuid(), HolidayScope.Estadual, "PB", null, new DateOnly(2026, 11, 5), "Feriado da Paraíba", false, false, null),
            new(Guid.NewGuid(), HolidayScope.Municipal, "SP", "Cabo de Santo Agostinho", new DateOnly(2026, 11, 6), "Mesmo nome, outra UF", false, false, null),
        ];

        // Cidade/UF da loja com maiúsculas e espaços diferentes: casa do mesmo jeito
        var cabo = HolidayCalendar.Snapshot(cadastro, " CABO DE SANTO  AGOSTINHO", "pe", start, end);
        var recife = HolidayCalendar.Snapshot(cadastro, "Recife", "PE", start, end);
        var municipal = Assert.Single(cabo);
        Assert.Equal(emancipacao, municipal.Date);
        Assert.Equal("Municipal", municipal.Abrangencia);
        Assert.Empty(recife);

        var caboOk = Assert.IsType<SolveResult.Success>(Solver().Solve(Command(start, 1, Snapshot(holidays: cabo, employees: Staff9))));
        var recifeOk = Assert.IsType<SolveResult.Success>(Solver().Solve(Command(start, 1, Snapshot(holidays: recife, employees: Staff9))));
        Assert.DoesNotContain(caboOk.Assignments, a => a.Date == emancipacao);
        Assert.Contains(recifeOk.Assignments, a => a.Date == emancipacao);
        Assert.Equal(100, Evaluator.Evaluate(caboOk.Ctx, caboOk.Assignments).Percentual);
    }

    [Fact]
    public void Recurring_holidays_repeat_every_year_from_their_base_year_and_compare_without_accents()
    {
        var independencia = new HolidayDefinition(Guid.NewGuid(), HolidayScope.Nacional, null, null, new DateOnly(2002, 9, 7), "Independência", true, false, null);
        var consciencia = new HolidayDefinition(Guid.NewGuid(), HolidayScope.Nacional, null, null, new DateOnly(2024, 11, 20), "Consciência Negra", true, false, null);
        var bissexto = new HolidayDefinition(Guid.NewGuid(), HolidayScope.Nacional, null, null, new DateOnly(2024, 2, 29), "Teste 29/02", true, false, null);
        var municipal = new HolidayDefinition(Guid.NewGuid(), HolidayScope.Municipal, "PE", "São Lourenço da Mata", new DateOnly(2026, 6, 10), "Aniversário", false, false, null);

        Assert.Equal([new DateOnly(2026, 9, 7), new DateOnly(2027, 9, 7)], HolidayCalendar.Occurrences(independencia, new DateOnly(2026, 1, 1), new DateOnly(2027, 12, 31)));
        Assert.Empty(HolidayCalendar.Occurrences(consciencia, new DateOnly(2023, 1, 1), new DateOnly(2023, 12, 31)));
        Assert.Equal([new DateOnly(2028, 2, 29)], HolidayCalendar.Occurrences(bissexto, new DateOnly(2026, 1, 1), new DateOnly(2028, 12, 31)));
        Assert.True(HolidayCalendar.AppliesTo(municipal, "sao lourenco da mata", "PE"));
        Assert.False(HolidayCalendar.AppliesTo(municipal, "Recife", "PE"));
        Assert.True(HolidayCalendar.SameDay(independencia, independencia with { Recorrente = false, Date = new DateOnly(2030, 9, 7) }));

        // Nacional + municipal no mesmo dia: um feriado só, o mais restritivo (um sem autorização = sem trabalho)
        var natalAutorizado = municipal with { Date = new DateOnly(2026, 12, 25), Name = "Natal municipal", WorkAuthorized = true, AuthorizationRef = "CCT 2026" };
        var natal = new HolidayDefinition(Guid.NewGuid(), HolidayScope.Nacional, null, null, new DateOnly(2002, 12, 25), "Natal", true, false, null);
        var dia = Assert.Single(HolidayCalendar.Snapshot([natal, natalAutorizado], "São Lourenço da Mata", "PE", new DateOnly(2026, 12, 21), new DateOnly(2026, 12, 27)));
        Assert.False(dia.WorkAuthorized);
        Assert.Equal("Nacional, Municipal", dia.Abrangencia);
    }

    // ---------------- Afastamento médico ----------------

    // Quadro de 9: no 5x2 estrito os dias de afastamento reduzem os dias de trabalho da pessoa (7 não cobririam a semana)
    private static List<EmployeeSnapshot> WithMedicalLeave(string name, params DateOnly[] days) =>
        Staff9.Select(e => e.Name != name ? e : e with
        {
            UnavailableDates = days,
            Absences = days.Select(d => new UnavailabilitySnapshot(d, UnavailabilityKinds.AtestadoMedico)).ToList()
        }).ToList();

    [Fact]
    public void Medical_leave_gets_no_shift_and_is_a_typed_absence()
    {
        var start = new DateOnly(2026, 11, 2);
        var leave = new[] { start.AddDays(1), start.AddDays(2) };
        var snapshot = Snapshot(employees: WithMedicalLeave("Ana", leave));

        var ok = Assert.IsType<SolveResult.Success>(Solver().Solve(Command(start, 1, snapshot)));
        Assert.DoesNotContain(ok.Assignments, a => a.EmployeeId == Id("Ana") && leave.Contains(a.Date));
        var ana = ok.Ctx.Employees.Single(e => e.Snapshot.EmployeeId == Id("Ana"));
        Assert.Equal(UnavailabilityKinds.AtestadoMedico, ok.Ctx.AbsenceType(ana, 1));
        Assert.Null(ok.Ctx.AbsenceType(ana, 0));
        Assert.Equal("Afastamento médico", UnavailabilityKinds.Rotulo(ok.Ctx.AbsenceType(ana, 2)));
        Assert.Equal(100, Evaluator.Evaluate(ok.Ctx, ok.Assignments).Percentual);

        // Turno no dia do afastamento (ex.: escala gerada antes do atestado): violação "indisponibilidade" alta
        var withWork = ok.Assignments.Append(new AssignmentDto(Id("Ana"), leave[0], Morning.ShiftId, Sector, Morning.Start, Morning.End, 440)).ToList();
        var evaluation = Evaluator.Evaluate(ok.Ctx, withWork);
        var v = Assert.Single(evaluation.Violacoes, x => x.Chave == ComplianceEvaluator.Indisponibilidade);
        Assert.Equal("alta", v.Severidade);
        Assert.Equal([leave[0]], v.Dates);

        // Célula manual de trabalho no afastamento não é mantida ao gerar só os vazios
        var fill = Assert.IsType<SolveResult.Success>(Solver().Solve(Command(start, 1, snapshot, GenerationModes.SomenteVazios,
            [new CellDto(Id("Ana"), leave[0], Morning.ShiftId, false)])));
        Assert.DoesNotContain(fill.Assignments, a => a.EmployeeId == Id("Ana") && leave.Contains(a.Date));
        Assert.Contains(fill.W, w => w.Contains("Ana") && w.Contains("afastado"));

        // A sugestão de correção tira o turno do dia de afastamento
        var cells = withWork.Select(a => new CellDto(a.EmployeeId, a.Date, a.ShiftId, false)).ToList();
        var outcome = Solver().Suggest(new SuggestCorrectionsCommand(Guid.NewGuid(), Guid.NewGuid(), start, 1, snapshot, cells, 20));
        Assert.Equal(SuggestionStatus.Pronta, outcome.Status);
        Assert.Contains(outcome.Changes, c => c.EmployeeId == Id("Ana") && c.Date == leave[0] && c.ToShiftId is null);
    }

    // ---------------- Férias da LG (DataDaOcorrencia..DataFim, inclusive) ----------------

    [Fact]
    public void Lg_vacation_blocks_start_through_end_and_frees_the_day_before_and_after()
    {
        // Férias de quarta a sexta, como o Employee.Api entrega (um dia por data, início e fim inclusive)
        var start = new DateOnly(2026, 11, 2);
        var (inicio, fim) = (start.AddDays(2), start.AddDays(4));
        var (vespera, depois) = (inicio.AddDays(-1), fim.AddDays(1));
        var vacation = Enumerable.Range(0, fim.DayNumber - inicio.DayNumber + 1).Select(inicio.AddDays).ToArray();
        var snapshot = Snapshot(employees: Staff9.Select(e => e.Name != "Ana" ? e : e with
        {
            UnavailableDates = vacation,
            Absences = vacation.Select(d => new UnavailabilitySnapshot(d, UnavailabilityKinds.Ferias)).ToList()
        }).ToList());

        var ok = Assert.IsType<SolveResult.Success>(Solver().Solve(Command(start, 1, snapshot)));
        var ana = ok.Ctx.Employees.Single(e => e.Snapshot.EmployeeId == Id("Ana"));
        Assert.True(ok.Ctx.IsAvailable(ana, ok.Ctx.DayIndex(vespera)));
        Assert.False(ok.Ctx.IsAvailable(ana, ok.Ctx.DayIndex(inicio)));
        Assert.False(ok.Ctx.IsAvailable(ana, ok.Ctx.DayIndex(fim)));
        Assert.True(ok.Ctx.IsAvailable(ana, ok.Ctx.DayIndex(depois)));
        Assert.Equal("Férias", UnavailabilityKinds.Rotulo(ok.Ctx.AbsenceType(ana, ok.Ctx.DayIndex(inicio.AddDays(1)))));
        Assert.DoesNotContain(ok.Assignments, a => a.EmployeeId == Id("Ana") && a.Date >= inicio && a.Date <= fim);

        // Células manuais: véspera e dia seguinte ficam; a do primeiro dia de férias é descartada
        var fill = Assert.IsType<SolveResult.Success>(Solver().Solve(Command(start, 1, snapshot, GenerationModes.SomenteVazios,
        [
            new CellDto(Id("Ana"), vespera, Morning.ShiftId, false),
            new CellDto(Id("Ana"), inicio, Morning.ShiftId, false),
            new CellDto(Id("Ana"), depois, Morning.ShiftId, false)
        ])));
        Assert.Contains(fill.Assignments, a => a.EmployeeId == Id("Ana") && a.Date == vespera);
        Assert.Contains(fill.Assignments, a => a.EmployeeId == Id("Ana") && a.Date == depois);
        Assert.DoesNotContain(fill.Assignments, a => a.EmployeeId == Id("Ana") && a.Date == inicio);

        // Escala já existente com a pessoa escalada nas férias: o avaliador aponta só os dias de férias
        AssignmentDto Turno(DateOnly d) => new(Id("Ana"), d, Morning.ShiftId, Sector, Morning.Start, Morning.End, 440);
        var existing = ok.Assignments.Where(a => !(a.EmployeeId == Id("Ana") && (a.Date == vespera || a.Date == depois)))
            .Concat([Turno(vespera), Turno(inicio), Turno(fim), Turno(depois)]).ToList();
        var flagged = Evaluator.Evaluate(ok.Ctx, existing).Violacoes
            .Where(v => v.Chave == ComplianceEvaluator.Indisponibilidade).SelectMany(v => v.Dates).Distinct().OrderBy(d => d).ToList();
        Assert.Equal(new[] { inicio, fim }, flagged);
    }

    [Fact]
    public void Old_snapshot_without_absence_types_or_holiday_scope_still_reads()
    {
        var start = new DateOnly(2026, 12, 21);
        var christmas = new DateOnly(2026, 12, 25);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        var current = Snapshot(holidays: [new HolidaySnapshot(christmas, "Natal", false, "Nacional")], employees: WithMedicalLeave("Bruno", start),
            rules: RulesBeforeStrict());

        // Fotografia gravada antes desta versão: sem "absences" nos colaboradores e sem "abrangencia" nos feriados
        var node = JsonNode.Parse(JsonSerializer.Serialize(current, json))!;
        foreach (var e in node["employees"]!.AsArray()) e!.AsObject().Remove("absences");
        foreach (var h in node["holidays"]!.AsArray()) h!.AsObject().Remove("abrangencia");
        var old = node.Deserialize<PlanningSnapshot>(json)!;
        Assert.Null(old.Employees.Single(e => e.Name == "Bruno").Absences);
        Assert.Null(old.Holidays.Single().Abrangencia);

        var ok = Assert.IsType<SolveResult.Success>(Solver().Solve(Command(start, 1, old)));
        Assert.DoesNotContain(ok.Assignments, a => a.Date == christmas);
        Assert.DoesNotContain(ok.Assignments, a => a.EmployeeId == Id("Bruno") && a.Date == start);
        var bruno = ok.Ctx.Employees.Single(e => e.Snapshot.EmployeeId == Id("Bruno"));
        Assert.Equal(UnavailabilityKinds.Indisponivel, ok.Ctx.AbsenceType(bruno, 0));
        Assert.Equal(100, Evaluator.Evaluate(ok.Ctx, ok.Assignments).Percentual);
    }
}

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

/// <summary>Intervalo de refeição por célula: regras, cobertura descontando o almoço e posicionamento automático.</summary>
public sealed class BreakTests
{
    private static readonly Guid Sector = Guid.NewGuid();
    private static readonly DateOnly Monday = new(2026, 11, 2);
    private static readonly ShiftSnapshot Day = new(Guid.NewGuid(), "D", new TimeOnly(8, 0), new TimeOnly(17, 0), 60);
    private static readonly ShiftSnapshot Short = new(Guid.NewGuid(), "C", new TimeOnly(8, 0), new TimeOnly(13, 0), 15);
    private static readonly ShiftSnapshot Night = new(Guid.NewGuid(), "N", new TimeOnly(22, 0), new TimeOnly(7, 0), 60);
    private static readonly ComplianceEvaluator Evaluator = new();

    private static ComplianceRuleSnapshot Rule(int id, string chave, decimal valor, RuleApplication aplicacao = RuleApplication.Rigida,
        string categoria = "intervalo", string unidade = "minutos") =>
        new(id, categoria, chave, valor, unidade, chave, null, null, RuleStatus.Ativo, aplicacao);

    /// <summary>Regras de intervalo como semeadas pelas migrations (padrão 60 para casar com os turnos do teste).</summary>
    private static List<ComplianceRuleSnapshot> Rules(bool withNewKeys = true)
    {
        var list = new List<ComplianceRuleSnapshot>
        {
            Rule(1, RuleKeys.LimiteDiario, 480, categoria: "jornada"),
            Rule(2, RuleKeys.CoberturaFaixa, 30, categoria: "cobertura"),
            Rule(3, RuleKeys.IntervaloMinimo, 60),
            Rule(4, RuleKeys.IntervaloMaximo, 120),
            Rule(5, RuleKeys.IntervaloPadrao, 60, RuleApplication.Preferencial),
        };
        if (withNewKeys)
        {
            list.Add(Rule(6, RuleKeys.IntervaloObrigatorioAcima, 360));
            list.Add(Rule(7, RuleKeys.IntervaloJornada4a6h, 15));
            list.Add(Rule(8, RuleKeys.IntervaloInicioMinimoAposEntrada, 60, RuleApplication.Preferencial));
            list.Add(Rule(9, RuleKeys.IntervaloFimMinimoAntesSaida, 60, RuleApplication.Preferencial));
            list.Add(Rule(10, RuleKeys.JornadaContinuaMaxima, 360));
        }
        return list;
    }

    private static List<EmployeeSnapshot> People(int n) => Enumerable.Range(0, n)
        .Select(i => new EmployeeSnapshot(Guid.NewGuid(), $"B{i:000}", $"Pessoa {i}", [Sector], Guid.NewGuid(), true, false, SundayRule.OneInThreeWeeks, []))
        .ToList();

    private static PlanningContext Context(List<EmployeeSnapshot> people, int demand, bool withNewKeys = true, TimeOnly? demandFrom = null, TimeOnly? demandTo = null)
    {
        // Demanda só na segunda (as células dos testes são todas da segunda-feira)
        List<DemandSnapshot> demands = [new(Sector, DayOfWeek.Monday, demandFrom ?? new TimeOnly(8, 0), demandTo ?? new TimeOnly(17, 0), demand)];
        var snapshot = new PlanningSnapshot(Guid.NewGuid(), "Teste", false, [new SectorSnapshot(Sector, "Caixa")], [Day, Short, Night],
            demands, [], people, null, [], Rules(withNewKeys));
        return PlanningContext.Create(snapshot, Monday, 1);
    }

    private static AssignmentDto Cell(EmployeeSnapshot e, ShiftSnapshot shift, TimeOnly? breakStart = null, int? breakMinutes = null)
    {
        var duration = ((shift.End.ToTimeSpan() - shift.Start.ToTimeSpan()).TotalMinutes + 1440) % 1440;
        return new AssignmentDto(e.EmployeeId, Monday, shift.ShiftId, Sector, shift.Start, shift.End,
            (int)duration - (breakMinutes ?? shift.BreakMinutes), breakStart, breakMinutes);
    }

    private static int CoverageViolations(ComplianceResult r) => r.Violacoes.Count(v => v.Chave == RuleKeys.CoberturaFaixa);

    [Fact]
    public void Automatic_placement_staggers_lunches_and_keeps_coverage_better_than_everyone_at_the_same_time()
    {
        var people = People(4);
        var ctx = Context(people, demand: 3);
        var cells = people.Select(p => Cell(p, Day)).ToList();

        var planned = BreakPlanner.Plan(ctx, cells, _ => true);
        var sameTime = cells.Select(c => c with { BreakStart = new TimeOnly(12, 0) }).ToList();

        var starts = planned.Select(c => c.BreakStart).ToList();
        Assert.All(starts, s => Assert.NotNull(s));
        Assert.Equal(4, starts.Distinct().Count()); // escalonados: um por vez
        var withPlan = Evaluator.Evaluate(ctx, planned);
        var withSame = Evaluator.Evaluate(ctx, sameTime);
        Assert.Equal(0, CoverageViolations(withPlan));
        Assert.True(CoverageViolations(withSame) > CoverageViolations(withPlan));
        // Posições dentro da janela legal e recomendada (1h após a entrada, 1h antes da saída, ≤ 6h contínuas)
        Assert.DoesNotContain(withPlan.Violacoes, v => IntervalPolicy.AllKeys.Contains(v.Chave));
        Assert.All(starts, s => Assert.InRange(s!.Value, new TimeOnly(10, 0), new TimeOnly(14, 0)));

        // Determinístico
        Assert.Equal(starts, BreakPlanner.Plan(ctx, cells, _ => true).Select(c => c.BreakStart).ToList());
    }

    [Fact]
    public void Without_coverage_pressure_lunch_goes_to_the_middle_of_the_journey_and_manual_breaks_are_kept()
    {
        var people = People(2);
        var ctx = Context(people, demand: 0);
        var manual = Cell(people[0], Day, new TimeOnly(13, 0));
        var planned = BreakPlanner.Plan(ctx, [manual, Cell(people[1], Day)], c => c.EmployeeId == people[1].EmployeeId);

        Assert.Equal(new TimeOnly(13, 0), planned[0].BreakStart); // não móvel: fica como está
        Assert.Equal(new TimeOnly(12, 0), planned[1].BreakStart); // 08–17 com 1h: meio da jornada = 12h–13h
    }

    [Fact]
    public void Interval_rules_are_checked_per_cell_with_the_right_severity_and_legal_basis()
    {
        var people = People(1);
        var p = people[0];
        var ctx = Context(people, demand: 0);

        ComplianceResult Eval(AssignmentDto c) => Evaluator.Evaluate(ctx, [c]);
        ComplianceViolationDto Single(ComplianceResult r, string key) => Assert.Single(r.Violacoes, v => v.Chave == key);

        // Intervalo curto (30 min em jornada de 8h30 > 6h): mínimo da CLT, rígida
        var shortBreak = Single(Eval(Cell(p, Day, new TimeOnly(12, 0), 30)), RuleKeys.IntervaloMinimo);
        Assert.Equal("alta", shortBreak.Severidade);
        Assert.Contains("CLT art. 71", shortBreak.Detalhe);
        Assert.Contains("30 min", shortBreak.Detalhe);

        // Intervalo longo (150 min): máximo, rígida
        Assert.Equal("alta", Single(Eval(Cell(p, Day, new TimeOnly(11, 0), 150)), RuleKeys.IntervaloMaximo).Severidade);

        // Logo no início: preferencial (baixa) + trecho contínuo de 7h30 depois do almoço (rígida)
        var atStart = Eval(Cell(p, Day, new TimeOnly(8, 30)));
        Assert.Equal("baixa", Single(atStart, RuleKeys.IntervaloInicioMinimoAposEntrada).Severidade);
        Assert.Equal("alta", Single(atStart, RuleKeys.JornadaContinuaMaxima).Severidade);

        // No fim: preferencial (baixa) + trecho contínuo antes (rígida)
        var atEnd = Eval(Cell(p, Day, new TimeOnly(15, 30)));
        Assert.Equal("baixa", Single(atEnd, RuleKeys.IntervaloFimMinimoAntesSaida).Severidade);
        Assert.Equal("alta", Single(atEnd, RuleKeys.JornadaContinuaMaxima).Severidade);

        // Sem intervalo em jornada acima de 6h: obrigatório (rígida), sem contar o mínimo de novo
        var none = Eval(Cell(p, Day, null, 0));
        Assert.Equal("alta", Single(none, RuleKeys.IntervaloObrigatorioAcima).Severidade);
        Assert.DoesNotContain(none.Violacoes, v => v.Chave == RuleKeys.IntervaloMinimo);

        // Jornada de 5h sem intervalo: 15 min do §1º
        var shortDay = Eval(Cell(p, Short, null, 0));
        Assert.Equal("alta", Single(shortDay, RuleKeys.IntervaloJornada4a6h).Severidade);
        Assert.DoesNotContain(Eval(Cell(p, Short, new TimeOnly(10, 0), 15)).Violacoes, v => IntervalPolicy.AllKeys.Contains(v.Chave));

        // Válido: 1h no meio da jornada
        Assert.DoesNotContain(Eval(Cell(p, Day, new TimeOnly(12, 0))).Violacoes, v => IntervalPolicy.AllKeys.Contains(v.Chave));

        // Janela legal calculada pelas regras ativas: 10h às 14h para 1h de almoço; durações de 60 a 120 min
        var window = ctx.Interval(0).Window(8 * 60, 17 * 60, 60);
        Assert.Equal((10 * 60, 14 * 60), (window.LegalEarliest, window.LegalLatest));
        Assert.Equal((60, 120), (window.MinMinutes, window.MaxMinutes));
        Assert.True(window.Required);
    }

    [Fact]
    public void Night_shift_lunch_after_midnight_is_handled()
    {
        var people = People(1);
        var ctx = Context(people, demand: 0);
        var cell = Cell(people[0], Night, new TimeOnly(2, 0)); // 22:00–07:00, almoço 02:00–03:00
        var t = ctx.Times(cell);
        Assert.Equal((26 * 60, 22 * 60, 31 * 60), (t.BreakStart!.Value, t.Start, t.EndAbs));
        Assert.DoesNotContain(Evaluator.Evaluate(ctx, [cell]).Violacoes, v => IntervalPolicy.AllKeys.Contains(v.Chave));

        var planned = Assert.Single(BreakPlanner.Plan(ctx, [Cell(people[0], Night)], _ => true));
        Assert.Equal(new TimeOnly(2, 0), planned.BreakStart); // meio da jornada 22h–7h
    }

    [Fact]
    public void Coverage_discounts_who_is_on_break_and_cells_without_break_start_keep_the_old_behavior()
    {
        var people = People(1);
        var ctx = Context(people, demand: 1);

        var old = Evaluator.Evaluate(ctx, [Cell(people[0], Day)]);
        Assert.Equal(0, CoverageViolations(old)); // sem início de intervalo: presente o turno inteiro (como antes)

        var lunch = Evaluator.Evaluate(ctx, [Cell(people[0], Day, new TimeOnly(12, 0))]);
        var gaps = lunch.Violacoes.Where(v => v.Chave == RuleKeys.CoberturaFaixa).ToList();
        Assert.Equal(2, gaps.Count); // 12:00–12:30 e 12:30–13:00
        Assert.All(gaps, v => Assert.Contains("em intervalo", v.Detalhe));
    }

    [Fact]
    public void Paid_minutes_follow_the_cell_break_and_change_the_daily_limit_check()
    {
        var people = People(1);
        var ctx = Context(people, demand: 0);

        var halfHour = Cell(people[0], Day, new TimeOnly(12, 0), 30); // 9h − 30 min = 8h30 pagas
        Assert.Equal(510, halfHour.PaidMinutes);
        Assert.Equal(510, ctx.Times(halfHour).Worked);
        Assert.Contains(Evaluator.Evaluate(ctx, [halfHour]).Violacoes, v => v.Chave == RuleKeys.LimiteDiario);

        var hour = Cell(people[0], Day, new TimeOnly(12, 0), 60); // 8h pagas
        Assert.DoesNotContain(Evaluator.Evaluate(ctx, [hour]).Violacoes, v => v.Chave == RuleKeys.LimiteDiario);
    }

    [Fact]
    public void Old_snapshot_and_cells_without_break_fields_still_evaluate()
    {
        var people = People(3);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        var ctx = Context(people, demand: 2, withNewKeys: false);

        // Fotografia e células gravadas antes desta versão: sem baseLegal nas regras e sem breakStart/breakMinutes nas células
        var node = JsonNode.Parse(JsonSerializer.Serialize(ctx.Snapshot, json))!;
        foreach (var r in node["complianceRules"]!.AsArray()) r!.AsObject().Remove("baseLegal");
        var snapshot = node.Deserialize<PlanningSnapshot>(json)!;
        var cellsNode = JsonNode.Parse(JsonSerializer.Serialize(people.Select(p => Cell(p, Day)).ToList(), json))!;
        foreach (var c in cellsNode.AsArray()) { c!.AsObject().Remove("breakStart"); c.AsObject().Remove("breakMinutes"); }
        var cells = cellsNode.Deserialize<List<AssignmentDto>>(json)!;

        var oldCtx = PlanningContext.Create(snapshot, Monday, 1);
        var result = Evaluator.Evaluate(oldCtx, cells);
        Assert.Equal(100, result.Percentual);
        // Só as chaves que estavam na fotografia são verificadas; o limite de 6h da CLT continua valendo
        Assert.Contains(result.PorRegra, r => r.Chave == RuleKeys.IntervaloMinimo && r.Verificacoes == 3);
        Assert.DoesNotContain(result.PorRegra, r => r.Chave == RuleKeys.JornadaContinuaMaxima && r.Verificacoes > 0);
    }

    [Fact]
    public void Generated_schedule_comes_with_lunches_decided_by_the_engine_and_full_coverage_outside_breaks()
    {
        // Com as regras de posição (jornada contínua ≤ 6h) o almoço cai dentro da demanda: o motor precisa de gente a mais
        // nos horários de almoço (quadro de 9) e entrega a escala já com os intervalos posicionados, sem buraco
        var nine = Enumerable.Range(0, 9).Select(i => new EmployeeSnapshot(Guid.NewGuid(), $"N{i:000}", $"Pessoa {i}", [Guid.Empty], Guid.NewGuid(),
            true, false, SundayRule.OneInThreeWeeks, [])).ToList();
        var snapshot = ScheduleSolverTests.Snapshot();
        var rules = snapshot.ComplianceRules!.Concat(Rules().Where(r => r.RuleId >= 6).Select(r => r with { RuleId = r.RuleId + 100 })).ToList();
        snapshot = snapshot with { ComplianceRules = rules, Employees = nine.Select(e => e with { SectorIds = [snapshot.Sectors[0].SectorId] }).ToList() };
        var solver = new ScheduleSolver(Options.Create(new SolverOptions { Workers = 4 }), Evaluator, NullLogger<ScheduleSolver>.Instance);
        var ok = Assert.IsType<SolveResult.Success>(solver.Solve(new GenerateScheduleCommand(Guid.NewGuid(), 1, Monday, 1, snapshot, 20)));

        Assert.All(ok.Assignments, a => Assert.NotNull(a.BreakStart));
        var result = Evaluator.Evaluate(ok.Ctx, ok.Assignments);
        Assert.DoesNotContain(result.Violacoes, v => v.Severidade != "baixa");
        Assert.Equal(0, MaxShortfall(ok.Ctx, ok.Assignments));
        // Todos almoçando juntos no meio do turno abriria buraco: o motor escalonou
        var together = ok.Assignments.Select(a => a with { BreakStart = a.Start.AddMinutes(240) }).ToList();
        Assert.True(MaxShortfall(ok.Ctx, together) > 0);
    }

    [Fact]
    public void Planner_prefers_staggering_to_emptying_a_slot_when_demand_equals_the_headcount()
    {
        // 3 pessoas 08–17 (almoço legal de 10h às 14h) e demanda de 3 até 14:30: todos às 14:00 deixaria só a faixa 14:00–14:30 abaixo, mas vazia
        var people = People(3);
        var ctx = Context(people, demand: 3, demandTo: new TimeOnly(14, 30));
        var planned = BreakPlanner.Plan(ctx, people.Select(p => Cell(p, Day)).ToList(), _ => true);

        Assert.Equal(3, planned.Select(c => c.BreakStart).Distinct().Count());
        Assert.Equal(1, MaxShortfall(ctx, planned));
        var together = planned.Select(c => c with { BreakStart = new TimeOnly(14, 0) }).ToList();
        Assert.Equal(3, MaxShortfall(ctx, together));
    }

    private static int MaxShortfall(PlanningContext ctx, IReadOnlyList<AssignmentDto> cells) =>
        ctx.Slots.Select(s => s.Required - ComplianceEvaluator.Presence(ctx,
                cells.Where(c => c.Date == ctx.Dates[s.Day] && c.SectorId == ctx.Sectors[s.Sector].SectorId), s).Present)
            .DefaultIfEmpty(0).Max();
}

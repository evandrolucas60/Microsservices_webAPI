using System.Text.Json;
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
/// Caso relatado em 06/11/2026: (1) "segundo as regras de conformidade é permitido até 42h semanais" e a geração falhava
/// com "os turnos têm pelo menos 8h48 de jornada (9h48 com o intervalo) e o limite diário é 8h" — o limite diário
/// (limite_diario_minutos = 480) era aplicado sem a compensação semanal do 5x2; (2) "se a loja já tem horário de
/// funcionamento, por que definir turno?" — turnos automáticos pelo horário de cada dia e pelas regras vigentes
/// (PlanningContext.AutoShifts). Loja 07:00–22:00 (domingo 07:00–13:00), Açougue com 13 pessoas.
/// </summary>
[Collection(nameof(ConfigurationErrorsTests))]
public sealed class AutomaticShiftsTests(ITestOutputHelper output)
{
    private static readonly ComplianceEvaluator Evaluator = new();
    private static readonly Guid Sector = Guid.NewGuid();
    private static readonly DateOnly November = new(2026, 11, 1);
    private static readonly DateOnly NovemberEnd = new(2026, 11, 30);
    private static readonly DateOnly December = new(2026, 12, 1);
    private static readonly DateOnly DecemberEnd = new(2026, 12, 31);

    private static ScheduleSolver Solver() =>
        new(Options.Create(new SolverOptions { Workers = 8, DiagnosisSeconds = 6 }), Evaluator, NullLogger<ScheduleSolver>.Instance);

    /// <summary>Migration RegraCompensacaoJornada5x2: compensação semanal no 5x2 (ativa).</summary>
    private static ComplianceRuleSnapshot Compensation() =>
        ScheduleSolverTests.Rule(27, "jornada", RuleKeys.CompensacaoJornada5x2, 1, "booleano");

    /// <summary>42h em todas as semanas (5x2), com a compensação.</summary>
    private static List<ComplianceRuleSnapshot> Rules42() =>
        [.. ScheduleSolverTests.DefaultRules().Select(r => r.Chave == RuleKeys.CargaHorariaSemanal ? r with { Valor = 42 } : r), Compensation()];

    /// <summary>Cadastro semeado: 44h até 15/12/2026 (6x1, limiar 44h) e 42h depois (5x2), com a compensação.</summary>
    private static List<ComplianceRuleSnapshot> Seeded() => [.. ScheduleSolverTests.DefaultRules(), Compensation()];

    /// <summary>44h no 5x2 (limiar do 6x1 acima de 44h), com ou sem a compensação.</summary>
    private static List<ComplianceRuleSnapshot> Rules44In5x2(bool compensation) =>
    [
        .. ScheduleSolverTests.DefaultRules()
            .Select(r => r.Chave == RuleKeys.CargaHorariaSemanal ? r with { Valor = 44 } : r.Chave == RuleKeys.CargaMinimaRegime6x1 ? r with { Valor = 46 } : r),
        .. compensation ? [Compensation()] : Array.Empty<ComplianceRuleSnapshot>()
    ];

    /// <summary>Horário da loja: segunda a sábado 07:00–22:00, domingo 07:00–13:00.</summary>
    private static DayHours[] StoreHours() => Enum.GetValues<DayOfWeek>().Select(d => d == DayOfWeek.Sunday
        ? new DayHours(d, false, new TimeOnly(7, 0), new TimeOnly(13, 0))
        : new DayHours(d, false, new TimeOnly(7, 0), new TimeOnly(22, 0))).ToArray();

    /// <summary>
    /// Operação como a migration TurnosAutomaticos deixa as existentes: turnos automáticos ligados e a jornada personalizada
    /// de 9h48 (a do caso relatado) guardada, mas sem valer. Intermediários de 30 em 30 min.
    /// </summary>
    private static OperationSnapshot Auto(bool automatic = true, int duration = 588) =>
        new(new TimeOnly(7, 0), new TimeOnly(22, 0), 1, 1, 1, 30, null, null, duration, 60, Horarios: StoreHours(), TurnosAutomaticos: automatic);

    private static readonly List<HolidaySnapshot> NovemberHolidays =
    [
        new(new DateOnly(2026, 11, 2), "Finados", false, "Nacional"),
        new(new DateOnly(2026, 11, 15), "Proclamação da República", false, "Nacional"),
        new(new DateOnly(2026, 11, 20), "Consciência Negra", false, "Nacional"),
    ];

    /// <summary>Véspera de Natal com horário especial 07:00–13:00 (trabalho autorizado) e Natal fechado.</summary>
    private static readonly List<HolidaySnapshot> DecemberHolidays =
    [
        new(new DateOnly(2026, 12, 24), "Véspera de Natal", true, "Loja", nameof(FuncionamentoDia.Especial), new TimeOnly(7, 0), new TimeOnly(13, 0)),
        new(new DateOnly(2026, 12, 25), "Natal", false, "Nacional", nameof(FuncionamentoDia.Fechada)),
    ];

    /// <summary>13 colaboradores (4 Abertura, 4 Fechamento, 5 Flexível), como em ConfigurationErrorsTests.</summary>
    private static List<EmployeeSnapshot> Butchers()
    {
        string[] profiles = [.. Enumerable.Repeat(JourneyProfiles.Abertura, 4), .. Enumerable.Repeat(JourneyProfiles.Fechamento, 4),
            .. Enumerable.Repeat(JourneyProfiles.Flexivel, 5)];
        return profiles.Select((p, i) => new EmployeeSnapshot(Guid.NewGuid(), $"AC{i:000}", $"Operador {i + 1}", [Sector], Guid.NewGuid(), true, false,
            i % 2 == 0 ? SundayRule.OneInThreeWeeks : SundayRule.Biweekly, [], null, new EmployeeJourneySnapshot(p), "Operador de carne")).ToList();
    }

    /// <summary>Fotografia como o Scheduling.Api monta (PlanningContext.OperationShifts), com um turno antigo no catálogo.</summary>
    private static PlanningSnapshot Snapshot(OperationSnapshot op, List<ComplianceRuleSnapshot> rules, List<HolidaySnapshot> holidays,
        DateOnly from, DateOnly to)
    {
        var catalog = new ShiftSnapshot(Guid.NewGuid(), "M", new TimeOnly(7, 0), new TimeOnly(16, 48), 60);
        var shifts = PlanningContext.OperationShifts(op, Sector, [catalog], rules, holidays, from, to, false, 60);
        return new PlanningSnapshot(Guid.NewGuid(), "Loja 01", false, [new SectorSnapshot(Sector, "Açougue")], shifts, [], holidays,
            Butchers(), null, [], rules, null, op);
    }

    private static int Day(PlanningContext ctx, int month, int day) => ctx.DayIndex(new DateOnly(2026, month, day));

    // ---------------- (1) Limite diário pelas regras vigentes: compensação semanal do 5x2 ----------------

    [Fact]
    public void Daily_limit_comes_from_the_rules_in_force_with_the_5x2_weekly_compensation()
    {
        // 44h no 5x2 com a compensação: 44h ÷ 5 = 8h48 (limite_diario_minutos 8h + compensação); sem ela, 8h
        var with = PlanningContext.Create(Snapshot(Auto(), Rules44In5x2(true), NovemberHolidays, November, NovemberEnd), November, NovemberEnd);
        var tuesday = Day(with, 11, 3);
        Assert.Equal(new RuleValue(528, true, RuleKeys.CompensacaoJornada5x2), with.DailyLimit(tuesday));
        var without = PlanningContext.Create(Snapshot(Auto(), Rules44In5x2(false), NovemberHolidays, November, NovemberEnd), November, NovemberEnd);
        Assert.Equal(new RuleValue(480, true, RuleKeys.LimiteDiario), without.DailyLimit(tuesday));

        // 42h: 8h24; cadastro semeado: 6x1 até 15/12 (sem compensação: 8h) e 5x2 de 42h depois (8h24, inclusive na semana de virada)
        var r42 = PlanningContext.Create(Snapshot(Auto(), Rules42(), NovemberHolidays, November, NovemberEnd), November, NovemberEnd);
        Assert.Equal(504, r42.DailyLimit(tuesday)!.Value.Value);
        var seeded = PlanningContext.Create(Snapshot(Auto(), Seeded(), DecemberHolidays, December, DecemberEnd), December, DecemberEnd);
        Assert.Equal(480, seeded.DailyLimit(Day(seeded, 12, 1))!.Value.Value);
        Assert.Equal(504, seeded.DailyLimit(Day(seeded, 12, 14))!.Value.Value);
        Assert.Equal(504, seeded.DailyLimit(Day(seeded, 12, 21))!.Value.Value);
    }

    [Fact]
    public void Personalized_journey_of_8h48_fits_a_44h_5x2_week_with_compensation_and_reports_the_rule_value_otherwise()
    {
        // O caso relatado com os turnos personalizados (9h48 com 1h de intervalo): com 44h no 5x2 e a compensação, cabe nos
        // dias úteis (no domingo vale o limite de domingo de 8h da política: com turnos personalizados de 8h48 não cabe)
        var manual = Auto(automatic: false);
        var ok = PlanningContext.Create(Snapshot(manual, Rules44In5x2(true), NovemberHolidays, November, NovemberEnd), November, NovemberEnd);
        Assert.DoesNotContain(ok.ConfigurationIssues(), i => i.Days.Any(d => !ok.IsSunday(d)));
        Assert.True(ok.SectorHasShift(0, Day(ok, 11, 3)));

        // Com 42h a jornada de 8h48 passa de 42h ÷ 5 = 8h24: a mensagem cita o valor da regra e a compensação
        var bad = PlanningContext.Create(Snapshot(manual, Rules42(), NovemberHolidays, November, NovemberEnd), November, NovemberEnd);
        var issue = bad.ConfigurationIssues().First(i => i.Days.Contains(Day(bad, 11, 3)));
        output.WriteLine(issue.Text);
        Assert.Contains("8h48 de jornada (9h48 com o intervalo)", issue.Text);
        Assert.Contains("8h24", issue.Text);
        Assert.Contains(RuleKeys.CompensacaoJornada5x2, issue.Text);
    }

    // ---------------- (2) Turnos automáticos pelo horário da loja e pelas regras ----------------

    [Fact]
    public void Automatic_shifts_follow_the_store_hours_and_the_rules_of_each_day()
    {
        var snap = Snapshot(Auto(), Rules42(), NovemberHolidays, November, NovemberEnd);
        var ctx = PlanningContext.Create(snap, November, NovemberEnd);
        Assert.True(ctx.AutomaticShifts);
        Assert.Empty(ctx.ConfigurationIssues());

        // Dia útil: 42h ÷ 5 = 8h24 + 1h; abertura às 07:00, fechamento às 22:00, intermediários de 30 em 30 min
        var tuesday = Day(ctx, 11, 3);
        Assert.Equal(new DayJourney(504, 60), ctx.JourneyAt(tuesday));
        var day = ctx.ShiftsOfDay(tuesday);
        foreach (var t in day) output.WriteLine($"{t.Kind} {t.Snapshot.Code} {OperationPlan.Hm(t.StartMin)}–{OperationPlan.Hm(t.EndMin)} int. {t.Snapshot.BreakMinutes}");
        Assert.Contains(day, t => t.Kind == JourneyProfiles.Abertura && t.StartMin == 7 * 60 && t.EndMin == 16 * 60 + 24);
        Assert.Contains(day, t => t.Kind == JourneyProfiles.Fechamento && t.StartMin == 12 * 60 + 36 && t.EndMin == 22 * 60);
        var flex = day.Where(t => t.Kind == JourneyProfiles.Flexivel).ToList();
        Assert.Equal(Enumerable.Range(0, 10).Select(i => 7 * 60 + 30 + 30 * i), flex.Select(t => t.StartMin));
        Assert.All(day, t => Assert.Equal(504, t.PaidMinutes));
        Assert.Contains("42h ÷ 5 dias (5x2) = 8h24", ctx.JourneyBasis(tuesday));

        // Domingo 07:00–13:00: a maior jornada que cabe com o intervalo que ela exige (5h45 + 15 min)
        var sunday = Day(ctx, 11, 8);
        Assert.Equal(new DayJourney(345, 15), ctx.JourneyAt(sunday));
        Assert.Contains(ctx.ShiftsOfDay(sunday), t => t.Kind == JourneyProfiles.Abertura && t.StartMin == 7 * 60 && t.EndMin == 13 * 60);
        Assert.Contains(ctx.ShiftsOfDay(sunday), t => t.Kind == JourneyProfiles.Fechamento && t.EndMin == 13 * 60);
        Assert.DoesNotContain(ctx.ShiftsOfDay(sunday), t => t.PaidMinutes == 504);
        Assert.Contains("expediente de 6h", ctx.JourneyBasis(sunday));

        // O catálogo fica só para edição manual (sem tipo); a fotografia já tem todos os turnos do motor (idempotente)
        Assert.Single(ctx.Shifts, t => t.Snapshot.Code == "M" && t.Kind is null);
        Assert.Equal(snap.Shifts.Select(s => s.ShiftId).Order(), ctx.Shifts.Select(t => t.Snapshot.ShiftId).Order());
        var again = PlanningContext.Create(ctx.Snapshot, November, NovemberEnd);
        Assert.Equal(ctx.Shifts.Select(t => (t.Snapshot.ShiftId, t.Snapshot.Code)), again.Shifts.Select(t => (t.Snapshot.ShiftId, t.Snapshot.Code)));
    }

    [Fact]
    public void Seeded_rules_change_the_journey_with_the_weekly_load_and_holidays_get_shorter_shifts()
    {
        var ctx = PlanningContext.Create(Snapshot(Auto(), Seeded(), DecemberHolidays, December, DecemberEnd), December, DecemberEnd);
        Assert.Empty(ctx.ConfigurationIssues());
        // Até 13/12: 44h no 6x1 (44h ÷ 6 = 7h20); a partir da semana de virada (14/12): 42h no 5x2 (8h24)
        Assert.Equal(new DayJourney(440, 60), ctx.JourneyAt(Day(ctx, 12, 1)));
        Assert.Equal(new DayJourney(504, 60), ctx.JourneyAt(Day(ctx, 12, 14)));
        Assert.Equal(new DayJourney(504, 60), ctx.JourneyAt(Day(ctx, 12, 22)));
        // Véspera de Natal com horário especial 07:00–13:00 e Natal fechado
        var eve = Day(ctx, 12, 24);
        Assert.Equal(new DayJourney(345, 15), ctx.JourneyAt(eve));
        Assert.Contains(ctx.ShiftsOfDay(eve), t => t.Kind == JourneyProfiles.Abertura && t.StartMin == 7 * 60 && t.EndMin == 13 * 60);
        Assert.Empty(ctx.ShiftsOfDay(Day(ctx, 12, 25)));
        // Mesma entrada com jornadas diferentes no mês: códigos distintos
        Assert.Equal(ctx.Shifts.Count(), ctx.Shifts.Select(t => t.Snapshot.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Personalized_shifts_still_work_and_old_snapshots_are_not_automatic()
    {
        // "Personalizar turnos": catálogo classificado + gerados da operação com a duração informada (8h20 com 1h)
        var manual = Auto(automatic: false, duration: 500);
        var ctx = PlanningContext.Create(Snapshot(manual, Rules42(), NovemberHolidays, November, NovemberEnd), November, NovemberEnd);
        Assert.False(ctx.AutomaticShifts);
        Assert.Empty(ctx.DayJourneys);
        Assert.Contains(ctx.Shifts, t => t.Kind == JourneyProfiles.Abertura && t.StartMin == 7 * 60 && t.EndMin == 15 * 60 + 20);
        Assert.DoesNotContain(ctx.Shifts, t => t.Snapshot.Gerado && t.Duration != 500);
        // O turno do catálogo que começa na abertura continua de abertura (classificação pelo horário, como antes)
        Assert.Single(ctx.Shifts, t => t.Snapshot.Code == "M" && t.Kind == JourneyProfiles.Abertura);
        Assert.DoesNotContain(ctx.ConfigurationIssues(), i => i.Days.Any(d => !ctx.IsSunday(d)));

        // Fotografia antiga (sem o campo): turnos personalizados, como foram gerados
        var json = JsonSerializer.Serialize(manual with { TurnosAutomaticos = true }, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            .Replace(",\"turnosAutomaticos\":true", "");
        Assert.False(JsonSerializer.Deserialize<OperationSnapshot>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.TurnosAutomaticos);
        // Validação: com turnos automáticos a jornada personalizada não é exigida
        Assert.Empty(OperationPlan.Validate(Auto(duration: 0)));
        Assert.NotEmpty(OperationPlan.Validate(Auto(automatic: false, duration: 0)));
    }

    [Fact]
    public void Evaluator_uses_the_same_daily_limit_as_the_engine()
    {
        var ctx = PlanningContext.Create(Snapshot(Auto(), Rules42(), NovemberHolidays, November, NovemberEnd), November, NovemberEnd);
        var e = ctx.Employees[0].Snapshot.EmployeeId;
        AssignmentDto Cell(int day, int paid) => new(e, new DateOnly(2026, 11, day), Guid.NewGuid(), Sector, new TimeOnly(7, 0),
            OperationPlan.Time(7 * 60 + paid + 60), paid);
        // 8h24 cabe (42h ÷ 5); 8h48 passa do limite com a compensação (a mesma regra que o motor usa)
        var evaluation = Evaluator.Evaluate(ctx, [Cell(3, 504), Cell(4, 528)]);
        var v = Assert.Single(evaluation.Violacoes, x => x.Chave == RuleKeys.CompensacaoJornada5x2);
        output.WriteLine(v.Detalhe);
        Assert.Contains(new DateOnly(2026, 11, 4), v.Dates);
        Assert.Contains("máximo 8h24", v.Detalhe);
        Assert.DoesNotContain(evaluation.Violacoes, x => x.Chave == RuleKeys.LimiteDiario);
    }

    // ---------------- Geração do mês: abertura às 07:00, fechamento às 22:00, carga semanal ----------------

    private void SolveMonth(PlanningSnapshot snap, DateOnly from, DateOnly to, Action<PlanningContext, IReadOnlyList<AssignmentDto>>? extra = null)
    {
        var command = new GenerateScheduleCommand(Guid.NewGuid(), 1, from, PlanningContext.Horizon(from, to).Weeks, snap, 60,
            GenerationModes.Completa, [], Guid.NewGuid(), to);
        var result = Solver().Solve(command, 60);
        if (result is SolveResult.Infeasible bad) output.WriteLine($"inviável: {bad.Reason}");
        var ok = Assert.IsType<SolveResult.Success>(result);
        output.WriteLine($"{ok.Status} em {ok.Seconds:F1}s; aviso: {ok.Notice}");

        // Todo turno do motor existe na fotografia montada como o Scheduling.Api monta
        var ids = snap.Shifts.Select(s => s.ShiftId).ToHashSet();
        Assert.All(ok.Assignments, a => Assert.Contains(a.ShiftId, ids));

        // O avaliador (como o Scheduling.Api grava) concorda com o motor: nenhuma violação rígida
        var stored = PlanningContext.Create(snap with { CoberturaMetaSetores = ok.Ctx.CoverageTargetSectorIds(), CoberturaMetaDias = ok.Ctx.CoverageTargetDayList() },
            from, to);
        var evaluation = Evaluator.Evaluate(stored, ok.Assignments);
        foreach (var v in evaluation.Violacoes.Where(v => v.Severidade != "baixa")) output.WriteLine($"{v.Chave}: {v.Detalhe}");
        Assert.Equal(0, ComplianceEvaluator.RigidViolations(evaluation));

        var byDay = ok.Assignments.GroupBy(a => stored.DayIndex(a.Date)).ToDictionary(g => g.Key, g => g.ToList());
        for (var d = stored.FirstDay; d <= stored.LastDay; d++)
        {
            if (!stored.OpenWorkDay(d)) continue;
            var cells = byDay.GetValueOrDefault(d) ?? [];
            var (open, close) = stored.DayWindows[d]!.Value;
            // Abertura na abertura do dia e alguém no fechamento do dia; jornada do dia em todas as células
            Assert.Contains(cells, a => PlanningContext.ToMinutes(a.Start) == open);
            var last = stored.Slots.Single(s => s.Day == d && s.SlotEnd == close);
            Assert.True(ComplianceEvaluator.Presence(stored, cells, last).Present >= 1, $"ninguém no fechamento de {stored.Dates[d]:dd/MM}");
            Assert.All(cells, a => Assert.Equal(stored.JourneyAt(d)!.Value.Paid, a.PaidMinutes));
        }
        // Carga semanal: ninguém passa do limite vigente da semana
        foreach (var e in stored.Employees)
            for (var w = 0; w < stored.Weeks; w++)
            {
                var week = w;
                var minutes = ok.Assignments.Where(a => a.EmployeeId == e.Snapshot.EmployeeId && stored.DayIndex(a.Date) / 7 == week).Sum(a => a.PaidMinutes);
                Assert.True(minutes <= stored.WeeklyLimit(w)!.Value.Value, $"{e.Snapshot.Name}: {minutes} min na semana {w}");
            }
        extra?.Invoke(stored, ok.Assignments);
    }

    [Fact]
    public void November_with_42h_generates_with_automatic_shifts_opening_at_7_and_closing_at_22()
    {
        SolveMonth(Snapshot(Auto(), Rules42(), NovemberHolidays, November, NovemberEnd), November, NovemberEnd, (ctx, cells) =>
        {
            // Semana inteira sem feriado: 5 dias de 8h24 = 42h
            var monday = new DateOnly(2026, 11, 23);
            Assert.All(ctx.Employees, e => Assert.Equal(5, cells.Count(a => a.EmployeeId == e.Snapshot.EmployeeId && a.Date >= monday && a.Date < monday.AddDays(7))));
            // Domingo mais curto (5h45 + 15 min)
            Assert.All(cells.Where(a => a.Date.DayOfWeek == DayOfWeek.Sunday), a => Assert.Equal(345, a.PaidMinutes));
        });
    }

    [Fact]
    public void December_with_the_seeded_rules_generates_with_the_journey_of_each_week()
    {
        SolveMonth(Snapshot(Auto(), Seeded(), DecemberHolidays, December, DecemberEnd), December, DecemberEnd, (ctx, cells) =>
        {
            Assert.Contains(cells, a => a.Date == new DateOnly(2026, 12, 2) && a.PaidMinutes == 440);
            Assert.Contains(cells, a => a.Date == new DateOnly(2026, 12, 15) && a.PaidMinutes == 504);
            Assert.All(cells.Where(a => a.Date == new DateOnly(2026, 12, 24)), a => Assert.True(PlanningContext.ToMinutes(a.End) <= 13 * 60));
            Assert.DoesNotContain(cells, a => a.Date == new DateOnly(2026, 12, 25));
        });
    }
}

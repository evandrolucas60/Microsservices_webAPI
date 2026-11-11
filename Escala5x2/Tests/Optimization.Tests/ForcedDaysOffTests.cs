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
/// Caso relatado (novembro/2026): "Provado que não existe escala ... Inviável: Máximo de folgas seguidas
/// (max_folgas_seguidas)". Causas: dias em que a pessoa NÃO PODE trabalhar por motivo alheio ao motor (loja fechada no
/// domingo, indisponibilidade na disponibilidade semanal, feriado com horário especial em que nenhum turno cabe) contavam
/// como folga para max_folgas_seguidas — três seguidos tornavam o mês inviável — e, na semana, mais dias assim do que as 2
/// folgas do 5x2 também (o núcleo reduzido ficava só com max_folgas_seguidas). Agora esses dias são NEUTROS na sequência
/// (não contam nem interrompem) e são folga inevitável na conta da semana; ausência (inclusive antes da admissão) e
/// feriado sem autorização interrompem. Motor e avaliador usam a mesma classificação (PlanningContext.DaysOff). Conflito
/// que ainda existe (folgas manuais) aponta a pessoa e os dias no diagnóstico.
/// </summary>
public sealed class ForcedDaysOffTests(ITestOutputHelper output)
{
    private static readonly ComplianceEvaluator Evaluator = new();
    private static readonly Guid Sector = Guid.NewGuid();
    private static readonly ShiftSnapshot Morning = new(Guid.NewGuid(), "M", new TimeOnly(7, 0), new TimeOnly(15, 20), 60);
    private static readonly ShiftSnapshot Evening = new(Guid.NewGuid(), "T", new TimeOnly(13, 40), new TimeOnly(22, 0), 60);
    private static readonly DateOnly Nov2026 = new(2026, 11, 1);

    private ScheduleSolver Solver() =>
        new(Options.Create(new SolverOptions { Workers = 8 }), Evaluator, new OutputLogger(output));

    /// <summary>Log do motor na saída do teste (mostra as tentativas e o diagnóstico).</summary>
    private sealed class OutputLogger(ITestOutputHelper output) : Microsoft.Extensions.Logging.ILogger<ScheduleSolver>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            try { output.WriteLine($"[{logLevel}] {formatter(state, exception)}{(exception is null ? "" : $" {exception}")}"); }
            catch (InvalidOperationException) { /* teste já terminou */ }
        }
    }

    /// <summary>Regras padrão com a carga de 42h (5x2: exatamente 2 folgas na semana) e no máximo 2 folgas seguidas.</summary>
    private static List<ComplianceRuleSnapshot> Rules5x2() =>
        ScheduleSolverTests.DefaultRules().Select(r => r.Chave == RuleKeys.CargaHorariaSemanal ? r with { Valor = 42 } : r).ToList();

    private static List<EmployeeSnapshot> People(int size, SundayRule? rule = null) => Enumerable.Range(0, size).Select(i => new EmployeeSnapshot(
        Guid.NewGuid(), $"FO{i:000}", $"Pessoa {i + 1}", [Sector], Guid.NewGuid(), true, false,
        rule ?? (i % 2 == 0 ? SundayRule.OneInThreeWeeks : SundayRule.Biweekly), [])).ToList();

    /// <summary>
    /// Setor com operação 07:00–22:00 (mínimo 1) e a loja FECHADA no domingo (horário de funcionamento); entradas flexíveis
    /// só das 09:00 às 11:00, de 30 em 30 min (poucos turnos: o mês sai rápido no teste).
    /// </summary>
    private static PlanningSnapshot ClosedSundaySector(int size, IReadOnlyList<HolidaySnapshot>? holidays = null)
    {
        var op = new OperationSnapshot(new TimeOnly(7, 0), new TimeOnly(22, 0), 1, 1, 1, 30, new TimeOnly(9, 0), new TimeOnly(11, 0), 500, 60,
            Horarios: [new DayHours(DayOfWeek.Sunday, true)], FolgasFixasSetorDuasPessoas: true, CoberturaComoMetaQuadroInsuficiente: true);
        return new PlanningSnapshot(Guid.NewGuid(), "Teste", false, [new SectorSnapshot(Sector, "Padaria")], [], [], holidays ?? [],
            People(size), null, [], Rules5x2(), null, op);
    }

    /// <summary>Setor sem operação: 1 pessoa de manhã (08:00–13:30) e 1 à noite (15:30–21:30) todo dia, turnos M e T.</summary>
    private static PlanningSnapshot DemandSector(int size, SundayRule? rule = null)
    {
        var demands = Enum.GetValues<DayOfWeek>().SelectMany(d => new[]
        {
            new DemandSnapshot(Sector, d, new TimeOnly(8, 0), new TimeOnly(13, 30), 1),
            new DemandSnapshot(Sector, d, new TimeOnly(15, 30), new TimeOnly(21, 30), 1)
        }).ToList();
        return new PlanningSnapshot(Guid.NewGuid(), "Teste", false, [new SectorSnapshot(Sector, "Caixa")], [Morning, Evening], demands, [],
            People(size, rule), null, [], Rules5x2());
    }

    /// <summary>Disponibilidade semanal da pessoa <paramref name="index"/>.</summary>
    private static PlanningSnapshot WithAvailability(PlanningSnapshot snap, int index, params AvailabilityWindow[] windows) =>
        snap with
        {
            Employees = snap.Employees.Select((e, i) => i != index ? e : e with
            {
                Jornada = new EmployeeJourneySnapshot(JourneyProfiles.Flexivel, windows)
            }).ToList()
        };

    private static AvailabilityWindow Off(DayOfWeek day) => new(day, false);

    private static GenerateScheduleCommand MonthCommand(DateOnly anyDay, PlanningSnapshot snapshot, int seconds = 20,
        string mode = GenerationModes.Completa, IReadOnlyList<CellDto>? fixedCells = null)
    {
        var (from, to) = PlanningContext.MonthOf(anyDay);
        return new(Guid.NewGuid(), 1, from, PlanningContext.Horizon(from, to).Weeks, snapshot, seconds, mode, fixedCells ?? [], Guid.NewGuid(), to);
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
        output.WriteLine($"{ok.Status} em {ok.Seconds:F1}s");
        return ok;
    }

    /// <summary>Sem violação rígida no avaliador independente; max_folgas_seguidas verificada e cumprida.</summary>
    private ComplianceResult AssertCompliant(SolveResult.Success ok)
    {
        var result = Evaluator.Evaluate(ok.Ctx, ok.Assignments);
        foreach (var v in result.Violacoes.Where(v => v.Severidade is "alta" or "media")) output.WriteLine($"{v.Chave}: {v.Detalhe}");
        Assert.Equal(0, ComplianceEvaluator.RigidViolations(result));
        var rule = Assert.Single(result.PorRegra, r => r.Chave == RuleKeys.MaxFolgasSeguidas);
        Assert.True(rule.Verificacoes > 0);
        Assert.Equal(0, rule.Violacoes);
        return result;
    }

    private static bool Works(SolveResult.Success ok, int person, DateOnly date) =>
        ok.Assignments.Any(a => a.EmployeeId == ok.Ctx.Employees[person].Snapshot.EmployeeId && a.Date == date);

    // ---------------- Classificação comum (motor e avaliador) ----------------

    [Fact]
    public void Closed_store_and_weekly_unavailability_are_neutral_absence_and_holiday_interrupt()
    {
        var snap = WithAvailability(ClosedSundaySector(4, [new HolidaySnapshot(new DateOnly(2026, 11, 2), "Finados", false, Funcionamento: "Fechada")]),
            0, Off(DayOfWeek.Saturday));
        snap = snap with
        {
            Employees = snap.Employees.Select((e, i) => i != 1 ? e : e with
            {
                UnavailableDates = [new DateOnly(2026, 11, 10)],
                Absences = [new UnavailabilitySnapshot(new DateOnly(2026, 11, 10), UnavailabilityKinds.AtestadoMedico)]
            }).ToList()
        };
        var (from, to) = PlanningContext.MonthOf(Nov2026);
        var ctx = PlanningContext.Create(snap, from, to);
        int D(int day) => ctx.DayIndex(new DateOnly(2026, 11, day));
        var ana = ctx.Employees[0];

        Assert.Equal(DayOffKind.Neutral, ctx.DayOffKindOf(ana, D(1), false));     // domingo, loja fechada
        Assert.Equal(DayOffKind.Interrupts, ctx.DayOffKindOf(ana, D(2), false));  // Finados, loja fechada no feriado
        Assert.Equal(DayOffKind.Rest, ctx.DayOffKindOf(ana, D(3), false));
        Assert.Equal(DayOffKind.Neutral, ctx.DayOffKindOf(ana, D(7), false));     // sábado indisponível
        Assert.Equal(DayOffKind.Interrupts, ctx.DayOffKindOf(ctx.Employees[1], D(10), false)); // afastamento
        Assert.Equal("loja fechada", ctx.ForcedOffReason(ana, D(8)));
        Assert.Equal("indisponível na disponibilidade semanal", ctx.ForcedOffReason(ana, D(7)));
        // Semana de 09/11: sábado indisponível + domingo fechado = as 2 folgas; com 3 dias assim, 3
        Assert.Equal(2, ctx.WeekRestDays(ana, D(9) / 7, 2));
    }

    [Fact]
    public void Evaluator_does_not_count_the_closed_sunday_and_names_it_when_the_sequence_breaks_the_rule()
    {
        var snap = ClosedSundaySector(4);
        var (from, to) = PlanningContext.MonthOf(Nov2026);
        var ctx = PlanningContext.Create(snap, from, to);
        var shift = ctx.Shifts.First(t => t.Kind == JourneyProfiles.Abertura);
        AssignmentDto Work(int person, int day) => new(ctx.Employees[person].Snapshot.EmployeeId, new DateOnly(2026, 11, day), shift.Snapshot.ShiftId,
            Sector, shift.Snapshot.Start, shift.Snapshot.End, shift.PaidMinutes);
        // Pessoa 1: folga sáb 07, (dom 08 fechado), seg 09 → 2 folgas seguidas: cumpre
        // Pessoa 2: folga sex 06, sáb 07, (dom 08 fechado), seg 09 → 3: descumpre, sem contar o domingo
        var work = new List<AssignmentDto>();
        for (var day = 2; day <= 30; day++)
        {
            if (new DateOnly(2026, 11, day).DayOfWeek == DayOfWeek.Sunday) continue;
            if (day is not (7 or 9)) work.Add(Work(0, day));
            if (day is not (6 or 7 or 9)) work.Add(Work(1, day));
        }

        var result = Evaluator.Evaluate(ctx, work);
        var v = result.Violacoes.Where(x => x.Chave == RuleKeys.MaxFolgasSeguidas).ToList();
        foreach (var x in v) output.WriteLine(x.Detalhe);
        Assert.DoesNotContain(v, x => x.EmployeeId == ctx.Employees[0].Snapshot.EmployeeId);
        var broken = Assert.Single(v, x => x.EmployeeId == ctx.Employees[1].Snapshot.EmployeeId);
        Assert.Contains("3 folgas seguidas de 06/11 a 09/11 (máximo 2), sem contar 08/11 (loja fechada)", broken.Detalhe);
    }

    // ---------------- Geração: os dias forçados não tornam o mês inviável ----------------

    [Theory]
    [InlineData(2026, 11)] // começa no domingo (fechado) e termina na segunda
    [InlineData(2027, 2)]  // começa na segunda, 4 semanas inteiras
    public void Unavailable_saturday_and_monday_next_to_the_closed_sunday_is_generated(int y, int m)
    {
        // Pessoa 1: indisponível sábado e segunda + loja fechada no domingo = 3 dias seguidos sem poder trabalhar toda semana
        // (antes: "Inviável — max_folgas_seguidas"; e 3 > 2 folgas do 5x2). Pessoa 2: indisponível no sábado.
        var snap = WithAvailability(WithAvailability(ClosedSundaySector(5), 0, Off(DayOfWeek.Saturday), Off(DayOfWeek.Monday)),
            1, Off(DayOfWeek.Saturday));

        var ok = Success(Solver().Solve(MonthCommand(new DateOnly(y, m, 1), snap, 45)));

        var result = AssertCompliant(ok);
        Assert.DoesNotContain(result.Violacoes, v => v.Chave == RuleKeys.FolgasSemana5x2);
        // Pessoa 1 trabalha de terça a sexta nas semanas inteiras do mês (as 3 folgas são os dias que ela não pode)
        var tuesday = Enumerable.Range(0, 7).Select(i => new DateOnly(y, m, 1).AddDays(i)).First(d => d.DayOfWeek == DayOfWeek.Tuesday);
        Assert.All(Enumerable.Range(0, 4).Select(i => tuesday.AddDays(i)).Where(d => d <= ok.Ctx.PeriodEnd), d => Assert.True(Works(ok, 0, d), $"{d:dd/MM}"));
    }

    [Fact]
    public void Closed_sunday_and_the_holidays_of_november_2026_are_generated()
    {
        // 02/11 (seg) Finados com a loja fechada; 15/11 (dom) Proclamação sem autorização; 20/11 (sex) Consciência Negra com
        // horário especial 09:00–13:00. Pessoa 1 indisponível no sábado; Pessoa 2 indisponível no sábado e na sexta só das
        // 13:00 às 22:00 — em 20/11 nenhum turno cabe: sex 20 + sáb 21 + dom 22 (fechado) = 3 dias sem poder trabalhar.
        var holidays = new List<HolidaySnapshot>
        {
            new(new DateOnly(2026, 11, 2), "Finados", false, "Nacional", "Fechada"),
            new(new DateOnly(2026, 11, 15), "Proclamação da República", false, "Nacional", "Normal"),
            new(new DateOnly(2026, 11, 20), "Dia Nacional de Zumbi e da Consciência Negra", true, "Nacional", "Especial", new TimeOnly(9, 0), new TimeOnly(13, 0))
        };
        var snap = WithAvailability(ClosedSundaySector(5, holidays), 0, Off(DayOfWeek.Saturday));
        snap = WithAvailability(snap, 1, Off(DayOfWeek.Saturday), new AvailabilityWindow(DayOfWeek.Friday, true, new TimeOnly(13, 0), new TimeOnly(22, 0)));

        var ok = Success(Solver().Solve(MonthCommand(Nov2026, snap, 30)));

        var bruno = ok.Ctx.Employees[1];
        Assert.True(ok.Ctx.ForcedOff(bruno, ok.Ctx.DayIndex(new DateOnly(2026, 11, 20))));
        Assert.False(ok.Ctx.ForcedOff(bruno, ok.Ctx.DayIndex(new DateOnly(2026, 11, 13))));
        Assert.True(ok.Ctx.NonWorkable(bruno, ok.Ctx.DayIndex(new DateOnly(2026, 11, 2))));
        AssertCompliant(ok);
        Assert.All(new[] { 2, 15 }, day => Assert.DoesNotContain(ok.Assignments, a => a.Date == new DateOnly(2026, 11, day)));
    }

    [Fact]
    public void Admission_in_the_middle_of_the_week_is_generated()
    {
        // Pessoa 1 admitida na quinta 12/11: os dias antes (Employee.Api: sem contrato = "Indisponivel") são ausência —
        // interrompem a sequência e reduzem os dias de trabalho da semana; não são folga. Também indisponível aos sábados.
        var snap = WithAvailability(DemandSector(5), 0, Off(DayOfWeek.Saturday));
        var before = Enumerable.Range(0, 17).Select(i => new DateOnly(2026, 10, 26).AddDays(i)).ToList(); // 26/10 a 11/11
        snap = snap with
        {
            Employees = snap.Employees.Select((e, i) => i != 0 ? e : e with
            {
                UnavailableDates = before,
                Absences = before.Select(d => new UnavailabilitySnapshot(d, UnavailabilityKinds.Indisponivel)).ToList()
            }).ToList()
        };

        var ok = Success(Solver().Solve(MonthCommand(Nov2026, snap)));

        Assert.DoesNotContain(ok.Assignments, a => a.EmployeeId == snap.Employees[0].EmployeeId && a.Date < new DateOnly(2026, 11, 12));
        Assert.Contains(ok.Assignments, a => a.EmployeeId == snap.Employees[0].EmployeeId && a.Date >= new DateOnly(2026, 11, 12));
        AssertCompliant(ok);
    }

    [Fact]
    public void Two_person_sector_with_the_store_closed_on_sunday_is_generated()
    {
        // Setor de 2 pessoas (folgas fixas e intercaladas): domingo fechado entra no padrão dos dois; Ana indisponível na segunda
        var snap = WithAvailability(ClosedSundaySector(2), 0, Off(DayOfWeek.Monday));

        var ok = Success(Solver().Solve(MonthCommand(Nov2026, snap, 30)));

        output.WriteLine(ok.Notice ?? "(sem aviso)");
        AssertCompliant(ok);
    }

    [Fact]
    public void Biweekly_sunday_rule_with_unavailable_weekdays_is_generated()
    {
        // Todos com a regra quinzenal (domingo de folga a cada 2 semanas): Pessoa 1 indisponível no sábado, Pessoa 2 na
        // segunda — sáb + dom de folga ou dom + seg são 2 seguidas; o motor trabalha o dia do outro lado
        var snap = WithAvailability(WithAvailability(DemandSector(5, SundayRule.Biweekly), 0, Off(DayOfWeek.Saturday)), 1, Off(DayOfWeek.Monday));

        var ok = Success(Solver().Solve(MonthCommand(Nov2026, snap)));

        AssertCompliant(ok);
    }

    // ---------------- Conflito que ainda existe: o diagnóstico aponta pessoa e dias ----------------

    [Fact]
    public void Three_manual_days_off_in_a_row_are_infeasible_and_the_diagnosis_names_the_person_and_the_dates()
    {
        // Folgas marcadas à mão para a Pessoa 1 no sábado 07, domingo 08 e segunda 09/11 (geração "só os dias não inseridos")
        var snap = DemandSector(5);
        var person = snap.Employees[0];
        CellDto[] manual = [.. new[] { 7, 8, 9 }.Select(d => new CellDto(person.EmployeeId, new DateOnly(2026, 11, d), null, true))];

        var bad = Assert.IsType<SolveResult.Infeasible>(Solver().Solve(MonthCommand(Nov2026, snap, 10, GenerationModes.SomenteVazios, manual)));

        output.WriteLine(bad.Reason);
        var diagnosis = Assert.IsType<InfeasibilityDiagnosis>(bad.Diagnosis);
        foreach (var i in diagnosis.Itens) output.WriteLine($"{i.Tipo}: {i.Mensagem}");
        Assert.DoesNotContain(diagnosis.Itens, i => i.Tipo == "continuidade");
        var item = Assert.Single(diagnosis.Itens, i => i.Chave == RuleKeys.MaxFolgasSeguidas && i.EmployeeId == person.EmployeeId);
        Assert.Contains("Pessoa 1: folgas em 07/11, 08/11 e 09/11", item.Mensagem);
        Assert.Contains("marcadas manualmente", item.Mensagem);
        Assert.Equal([new DateOnly(2026, 11, 7), new DateOnly(2026, 11, 8), new DateOnly(2026, 11, 9)], item.Dates);
        // As células manuais da pessoa entram no conflito e o resumo cita a pessoa e os dias
        Assert.Contains(diagnosis.Itens, i => i.Tipo == "celulas_manuais" && i.EmployeeId == person.EmployeeId);
        Assert.Contains("Pessoa 1: folgas em 07/11, 08/11 e 09/11", diagnosis.Resumo);
    }
}

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
/// Horário de funcionamento da loja (dia fechado, expediente por dia), funcionamento nos feriados (fechada, horário
/// especial) e picos manuais (alvo da faixa, prevalência da data, aviso quando o pico não pode ser atendido).
/// </summary>
public sealed class StoreHoursAndPeaksTests(ITestOutputHelper output)
{
    private static readonly Guid Sector = Guid.NewGuid();
    private static readonly ShiftSnapshot Morning = new(Guid.NewGuid(), "M", new TimeOnly(7, 0), new TimeOnly(15, 20), 60);
    private static readonly ShiftSnapshot Evening = new(Guid.NewGuid(), "T", new TimeOnly(13, 40), new TimeOnly(22, 0), 60);
    private static readonly ComplianceEvaluator Evaluator = new();
    /// <summary>Segunda-feira, semana de 42h (5x2).</summary>
    private static readonly DateOnly Jan2027 = new(2027, 1, 4);

    private static OperationSnapshot Op(IReadOnlyList<DayHours>? horarios = null) =>
        new(new TimeOnly(7, 0), new TimeOnly(22, 0), 2, 2, 2, 15, null, null, 500, 60, RodizioAberturaFechamento: true, Horarios: horarios);

    private static EmployeeSnapshot Person(string name, string profile) =>
        new(Guid.NewGuid(), name[..Math.Min(6, name.Length)], name, [Sector], Guid.NewGuid(), true, false, SundayRule.OneInThreeWeeks,
            [], [], new EmployeeJourneySnapshot(profile));

    private static List<EmployeeSnapshot> Team() =>
    [
        .. Enumerable.Range(1, 4).Select(i => Person($"Abre{i}", JourneyProfiles.Abertura)),
        .. Enumerable.Range(1, 4).Select(i => Person($"Fecha{i}", JourneyProfiles.Fechamento)),
        .. Enumerable.Range(1, 3).Select(i => Person($"Flex{i}", JourneyProfiles.Flexivel)),
    ];

    private static PlanningSnapshot Snap(OperationSnapshot? op, IReadOnlyList<DemandSnapshot>? demands = null,
        IReadOnlyList<HolidaySnapshot>? holidays = null, List<EmployeeSnapshot>? people = null) =>
        new(Guid.NewGuid(), "Teste", false, [new SectorSnapshot(Sector, "Caixa")], [Morning, Evening], demands ?? [], holidays ?? [],
            people ?? Team(), null, [], ScheduleSolverTests.DefaultRules(), null, op);

    private static ScheduleSolver Solver() =>
        new(Options.Create(new SolverOptions { Workers = 8, DiagnosisSeconds = 10 }), Evaluator, NullLogger<ScheduleSolver>.Instance);

    private SolveResult.Success SolveOk(PlanningSnapshot snapshot, int weeks = 1)
    {
        var result = Solver().Solve(new GenerateScheduleCommand(Guid.NewGuid(), 1, Jan2027, weeks, snapshot, 60), 25);
        if (result is SolveResult.Infeasible bad) Assert.Fail($"Inviável: {bad.Reason} | {string.Join(" | ", bad.Diagnosis?.Itens.Select(i => i.Mensagem) ?? [])}");
        if (result is SolveResult.TimedOut slow) Assert.Fail(slow.Reason);
        var ok = Assert.IsType<SolveResult.Success>(result);
        output.WriteLine($"{ok.Status} em {ok.Seconds:F1}s; aviso: {ok.Notice}");
        return ok;
    }

    private static DayHours[] Week(Func<DayOfWeek, DayHours> f) => Enum.GetValues<DayOfWeek>().Select(f).ToArray();

    // ---------------- Feriados: loja fechada / horário especial ----------------

    [Fact]
    public void Closed_holiday_has_no_demand_and_nobody_works_even_with_work_rule_off()
    {
        var natal = Jan2027.AddDays(2); // quarta
        // Sem a regra feriado_trabalho_exige_autorizacao: antes, o feriado não fechava a loja; com funcionamento Fechada, fecha
        var rules = ScheduleSolverTests.DefaultRules().Where(r => r.Chave != RuleKeys.FeriadoExigeAutorizacao).ToList();
        var holiday = new HolidaySnapshot(natal, "Feriado", false, "Nacional", nameof(FuncionamentoDia.Fechada));
        var snap = Snap(Op(), holidays: [holiday]) with { ComplianceRules = rules };

        var ctx = PlanningContext.Create(snap, Jan2027, 1);
        Assert.True(ctx.IsClosed(2));
        Assert.True(ctx.DayBlocked[2]); // não conta como folga
        Assert.DoesNotContain(ctx.Slots, s => s.Day == 2);

        var ok = SolveOk(snap);
        Assert.DoesNotContain(ok.Assignments, a => a.Date == natal);
        var evaluation = Evaluator.Evaluate(ok.Ctx, ok.Assignments);
        Assert.Equal(0, Assert.Single(evaluation.PorRegra, r => r.Chave == ComplianceEvaluator.LojaFechada).Violacoes);
    }

    [Fact]
    public void Old_snapshot_holiday_without_funcionamento_keeps_previous_behaviour()
    {
        var holiday = new HolidaySnapshot(Jan2027.AddDays(2), "Feriado", true, "Nacional");
        var ctx = PlanningContext.Create(Snap(Op(), holidays: [holiday]), Jan2027, 1);
        Assert.False(ctx.IsClosed(2));
        Assert.False(ctx.HasDayWindows);
        Assert.Equal(30, ctx.Slots.Count(s => s.Day == 2)); // 07:00–22:00 em faixas de 30 min
    }

    [Fact]
    public void Special_holiday_hours_restrict_the_window_and_generate_opening_shift()
    {
        var vespera = Jan2027.AddDays(4); // sexta
        var holiday = new HolidaySnapshot(vespera, "Véspera", true, "Loja", nameof(FuncionamentoDia.Especial), new TimeOnly(8, 0), new TimeOnly(14, 0));
        var ctx = PlanningContext.Create(Snap(Op(), holidays: [holiday]), Jan2027, 1);

        Assert.True(ctx.HasDayWindows);
        Assert.Equal((8 * 60, 14 * 60), ctx.DayWindows[4]);
        var slots = ctx.Slots.Where(s => s.Day == 4).ToList();
        Assert.Equal(8 * 60, slots.Min(s => s.SlotStart));
        Assert.Equal(14 * 60, slots.Max(s => s.SlotEnd));
        Assert.Equal(RuleKeys.MinimoAbertura, slots.First().MinimumKey);
        Assert.Equal(RuleKeys.MinimoFechamento, slots.Last().MinimumKey);
        // Turno de abertura do horário especial (08:00) gerado; quem começa às 07:00 não entra na sexta
        var ab8 = Assert.Single(ctx.Shifts, t => t.StartMin == 8 * 60 && t.Kind == JourneyProfiles.Abertura);
        Assert.True(ctx.OpenAt(4, ab8));
        Assert.False(ctx.OpenAt(4, Assert.Single(ctx.Shifts, t => t.Snapshot.ShiftId == Morning.ShiftId)));
        Assert.True(ctx.OpenAt(0, Assert.Single(ctx.Shifts, t => t.Snapshot.ShiftId == Morning.ShiftId)));
    }

    // ---------------- Horário de funcionamento por dia ----------------

    [Fact]
    public void Store_closed_on_sunday_and_short_saturday_follow_the_weekly_hours()
    {
        var horarios = Week(d => d switch
        {
            DayOfWeek.Sunday => new DayHours(d, true),
            DayOfWeek.Saturday => new DayHours(d, false, new TimeOnly(7, 0), new TimeOnly(18, 0)),
            _ => new DayHours(d, false, new TimeOnly(7, 0), new TimeOnly(22, 0))
        });
        var snap = Snap(Op(horarios));
        var ctx = PlanningContext.Create(snap, Jan2027, 1);

        Assert.True(ctx.IsClosed(6));
        Assert.False(ctx.DayBlocked[6]); // dia fechado da semana é folga normal, não feriado
        Assert.DoesNotContain(ctx.Slots, s => s.Day == 6);
        Assert.Equal(18 * 60, ctx.Slots.Where(s => s.Day == 5).Max(s => s.SlotEnd));
        Assert.Equal(22 * 60, ctx.Slots.Where(s => s.Day == 0).Max(s => s.SlotEnd));
        // Fechamento de sábado gerado (18:00 − 8h20 = 09:40)
        Assert.Contains(ctx.Shifts, t => t.Kind == JourneyProfiles.Fechamento && t.EndMin == 18 * 60);

        var ok = SolveOk(snap);
        Assert.DoesNotContain(ok.Assignments, a => a.Date.DayOfWeek == DayOfWeek.Sunday);
        Assert.DoesNotContain(ok.Assignments, a => a.Date.DayOfWeek == DayOfWeek.Saturday && a.Start.Hour * 60 + a.Start.Minute >= 18 * 60);
    }

    [Fact]
    public void Hours_equal_to_the_operation_change_nothing()
    {
        var same = Week(d => new DayHours(d, false, new TimeOnly(7, 0), new TimeOnly(22, 0)));
        var before = PlanningContext.Create(Snap(Op()), Jan2027, 1);
        var after = PlanningContext.Create(Snap(Op(same)), Jan2027, 1);
        Assert.Equal(before.Slots.Select(s => (s.Day, s.SlotStart, s.Required, s.Minimum)), after.Slots.Select(s => (s.Day, s.SlotStart, s.Required, s.Minimum)));
        Assert.Equal(before.Shifts.Select(s => s.Snapshot.ShiftId), after.Shifts.Select(s => s.Snapshot.ShiftId));
    }

    // ---------------- Picos manuais ----------------

    [Fact]
    public void Manual_peak_overrides_the_demand_of_the_slot_and_date_peak_wins_over_weekday()
    {
        var demands = new List<DemandSnapshot>
        {
            new(Sector, DayOfWeek.Saturday, new TimeOnly(10, 0), new TimeOnly(16, 0), 5),               // demanda comum
            new(Sector, DayOfWeek.Saturday, new TimeOnly(11, 0), new TimeOnly(14, 0), 3, PicoManual: true), // pico: 3 (substitui 5)
            new(Sector, DayOfWeek.Monday, new TimeOnly(12, 0), new TimeOnly(13, 0), 0, PicoManual: true, Adicional: 1), // +1 sobre o mínimo
            new(Sector, DayOfWeek.Tuesday, new TimeOnly(12, 0), new TimeOnly(13, 0), 4, PicoManual: true),
            new(Sector, DayOfWeek.Tuesday, new TimeOnly(12, 0), new TimeOnly(13, 0), 2, PicoManual: true, Data: Jan2027.AddDays(1)), // a data prevalece
        };
        var ctx = PlanningContext.Create(Snap(Op(), demands), Jan2027, 1);
        CoverageSlot At(int day, int h, int m = 0) => ctx.Slots.Single(s => s.Day == day && s.SlotStart == h * 60 + m);

        Assert.Equal(3, At(5, 11).Required);
        Assert.Equal("Pico manual 11:00–14:00 de sábado", At(5, 11).ManualPeak);
        Assert.Equal(5, At(5, 10).Required);   // fora do pico: a demanda comum
        Assert.Null(At(5, 10).ManualPeak);
        Assert.Equal(5, At(5, 14).Required);
        Assert.Equal(3, At(0, 12).Required);   // mínimo 2 + adicional 1
        Assert.Equal(2, At(1, 12).Required);   // pico da data (2) no lugar do pico de terça (4); nunca abaixo do mínimo
        Assert.Equal("Pico manual 12:00–13:00 de 05/01", At(1, 12).ManualPeak);
    }

    [Fact]
    public void Manual_peak_without_operation_replaces_demand_in_legacy_model()
    {
        var demands = new List<DemandSnapshot>
        {
            new(Sector, DayOfWeek.Monday, new TimeOnly(8, 0), new TimeOnly(12, 0), 2),
            new(Sector, DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(10, 0), 4, PicoManual: true),
        };
        var ctx = PlanningContext.Create(Snap(null, demands), Jan2027, 1);
        var monday = ctx.Slots.Where(s => s.Day == 0).ToDictionary(s => s.SlotStart);
        Assert.Equal(2, monday[8 * 60].Required);
        Assert.Equal(4, monday[9 * 60].Required);
        Assert.NotNull(monday[9 * 60].ManualPeak);
    }

    [Fact]
    public void Manual_peak_that_the_sector_cannot_staff_is_reported_with_the_maximum_possible()
    {
        // 3 pessoas no setor; o pico de sábado pede 4
        var people = new List<EmployeeSnapshot>
        {
            Person("Abre1", JourneyProfiles.Abertura), Person("Fecha1", JourneyProfiles.Fechamento), Person("Flex1", JourneyProfiles.Flexivel)
        };
        var demands = new List<DemandSnapshot> { new(Sector, DayOfWeek.Saturday, new TimeOnly(11, 0), new TimeOnly(14, 0), 4, PicoManual: true) };
        var op = Op() with { CoberturaMinima = 1, MinimoAbertura = 1, MinimoFechamento = 1 };
        var ctx = PlanningContext.Create(Snap(op, demands, people: people), Jan2027, 1);

        var notices = InfeasibilityDiagnostics.ManualPeakNotices(ctx);
        var notice = Assert.Single(notices);
        output.WriteLine(notice);
        Assert.StartsWith("Pico manual 11:00–14:00 de sábado pede 4, máximo possível 3", notice);
    }

    [Fact]
    public void Solver_staffs_the_manual_peak_and_never_justifies_out_of_profile_only_by_it()
    {
        // Pico de segunda 11–14h pedindo 4: há gente do perfil suficiente (rodízio ligado, mas não deve ser usado)
        var demands = new List<DemandSnapshot> { new(Sector, DayOfWeek.Monday, new TimeOnly(11, 0), new TimeOnly(14, 0), 4, PicoManual: true) };
        var ok = SolveOk(Snap(Op(), demands));
        var monday = ok.Assignments.Where(a => a.Date == Jan2027).ToList();
        var present = monday.Count(a => a.Start.Hour * 60 + a.Start.Minute <= 11 * 60 && (a.End.Hour * 60 + a.End.Minute) >= 14 * 60);
        Assert.True(present >= 4, $"{present} pessoas cobrem o pico inteiro (com intervalo)");
        var people = ok.Ctx.Employees.ToDictionary(e => e.Snapshot.EmployeeId);
        Assert.All(ok.Assignments, a => Assert.False(ok.Ctx.OutOfProfile(people[a.EmployeeId], ok.Ctx.ShiftById(a.ShiftId)!),
            $"{people[a.EmployeeId].Snapshot.Name} fora do perfil em {a.Date:dd/MM}"));
    }
}

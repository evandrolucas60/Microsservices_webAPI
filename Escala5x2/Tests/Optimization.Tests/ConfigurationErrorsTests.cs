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
/// Caso relatado (novembro/2026, Açougue com 13 colaboradores): (A) escala de novembro APROVADA com FOLGA em todos os dias
/// de todos (só FRD nos feriados e FER nas férias), "Conformidade 100%", aproveitamento 0% e presença 0; (B) dezembro com
/// todo mundo no MESMO turno (entrada 15:21) todos os dias, "100%". Causa: configuração em que nenhum turno cabe no dia
/// (ex.: jornada da operação de 9h48 com 1h de intervalo = 8h48 pagas contra o limite diário de 8h) virava "não trabalho
/// forçado" para todos (PlanningContext.DaysOff: folga inevitável) e a cobertura como meta aceitava o dia vazio; com só um
/// turno do catálogo possível (15:21–22:00), todos iam para ele e a manhã ficava descoberta em silêncio; setor sem operação
/// nem demanda não tem o que cobrir. Agora: erro de configuração explícito antes do motor (PlanningContext.Configuration),
/// o avaliador aponta dia aberto sem ninguém (dia_aberto_sem_ninguem) e a aprovação de escala vazia é bloqueada. A
/// configuração realista (horário da loja por dia pelo cadastro + operação + 13 pessoas no 5x2) gera abertura e
/// fechamento todo dia e 5 dias de trabalho por semana.
/// </summary>
[Collection(nameof(ConfigurationErrorsTests))]
public sealed class ConfigurationErrorsTests(ITestOutputHelper output)
{
    private static readonly ComplianceEvaluator Evaluator = new();
    private static readonly Guid Sector = Guid.NewGuid();
    private static readonly DateOnly November = new(2026, 11, 1);
    private static readonly DateOnly NovemberEnd = new(2026, 11, 30);

    private static ScheduleSolver Solver() =>
        new(Options.Create(new SolverOptions { Workers = 8, DiagnosisSeconds = 6 }), Evaluator, NullLogger<ScheduleSolver>.Instance);

    /// <summary>Regras padrão com a carga de 42h (5x2: exatamente 2 folgas na semana).</summary>
    private static List<ComplianceRuleSnapshot> Rules5x2() =>
        ScheduleSolverTests.DefaultRules().Select(r => r.Chave == RuleKeys.CargaHorariaSemanal ? r with { Valor = 42 } : r).ToList();

    private static DayHours[] Week(Func<DayOfWeek, DayHours> f) => Enum.GetValues<DayOfWeek>().Select(f).ToArray();

    /// <summary>Horário cadastrado na loja (Cadastro da loja): segunda a sábado 07:00–21:00, domingo 07:00–13:00.</summary>
    private static DayHours[] CadastroHours() => Week(d => d == DayOfWeek.Sunday
        ? new DayHours(d, false, new TimeOnly(7, 0), new TimeOnly(13, 0))
        : new DayHours(d, false, new TimeOnly(7, 0), new TimeOnly(21, 0)));

    /// <summary>
    /// Operação da loja com o expediente padrão do horário (o mais frequente: 07:00–21:00, como StoreCadastroEndpoints
    /// grava), mínimo 1, jornada <paramref name="duration"/> com 1h de intervalo; entradas flexíveis das 09:00 às 11:00 de 30
    /// em 30 min (poucos turnos: o mês sai rápido no teste).
    /// </summary>
    private static OperationSnapshot Op(IReadOnlyList<DayHours>? hours, int duration = 500, TimeOnly? close = null) =>
        new(new TimeOnly(7, 0), close ?? new TimeOnly(21, 0), 1, 1, 1, 30, new TimeOnly(9, 0), new TimeOnly(11, 0), duration, 60, Horarios: hours);

    private static readonly List<HolidaySnapshot> Holidays =
    [
        new(new DateOnly(2026, 11, 2), "Finados", false, "Nacional"),
        new(new DateOnly(2026, 11, 15), "Proclamação da República", false, "Nacional"),
        new(new DateOnly(2026, 11, 20), "Consciência Negra", false, "Nacional"),
    ];

    /// <summary>13 colaboradores (4 Abertura, 4 Fechamento, 5 Flexível); o último de férias de 03/11 ao fim do mês.</summary>
    private static List<EmployeeSnapshot> Butchers()
    {
        string[] profiles = [.. Enumerable.Repeat(JourneyProfiles.Abertura, 4), .. Enumerable.Repeat(JourneyProfiles.Fechamento, 4),
            .. Enumerable.Repeat(JourneyProfiles.Flexivel, 5)];
        var vacation = Enumerable.Range(0, 28).Select(i => new DateOnly(2026, 11, 3).AddDays(i)).ToList();
        return profiles.Select((p, i) => new EmployeeSnapshot(Guid.NewGuid(), $"AC{i:000}", $"Operador {i + 1}", [Sector], Guid.NewGuid(), true, false,
            i % 2 == 0 ? SundayRule.OneInThreeWeeks : SundayRule.Biweekly,
            i == 12 ? vacation : [], i == 12 ? vacation.Select(d => new UnavailabilitySnapshot(d, UnavailabilityKinds.Ferias)).ToList() : null,
            new EmployeeJourneySnapshot(p), "Operador de carne")).ToList();
    }

    /// <summary>
    /// Fotografia como o Scheduling.Api monta: turnos do catálogo + os da operação (com os do horário especial de feriado,
    /// PlanningContext.HolidayWindows), demanda do setor e feriados do horizonte.
    /// </summary>
    private static PlanningSnapshot Snapshot(OperationSnapshot? op, IReadOnlyList<ShiftSnapshot>? catalog = null,
        IReadOnlyList<HolidaySnapshot>? holidays = null, IReadOnlyList<DemandSnapshot>? demands = null)
    {
        holidays ??= Holidays;
        var shifts = OperationPlan.Expand(op, Sector, catalog ?? [], 60, PlanningContext.HolidayWindows(holidays, op, November, NovemberEnd));
        return new PlanningSnapshot(Guid.NewGuid(), "Loja 01", false, [new SectorSnapshot(Sector, "Açougue")], shifts, demands ?? [], holidays,
            Butchers(), null, [], Rules5x2(), null, op);
    }

    private static GenerateScheduleCommand MonthCommand(PlanningSnapshot snapshot, int seconds) =>
        new(Guid.NewGuid(), 1, November, PlanningContext.Horizon(November, NovemberEnd).Weeks, snapshot, seconds, GenerationModes.Completa, [],
            Guid.NewGuid(), NovemberEnd);

    private void Print(PlanningContext ctx, IReadOnlyList<AssignmentDto> cells)
    {
        foreach (var e in ctx.Employees)
        {
            var row = Enumerable.Range(0, ctx.PeriodDays).Select(i => ctx.PeriodStart.AddDays(i)).Select(d =>
            {
                var a = cells.FirstOrDefault(x => x.EmployeeId == e.Snapshot.EmployeeId && x.Date == d);
                return (d.DayOfWeek == DayOfWeek.Monday ? "| " : "") + (a is null ? "-----" : a.Start.ToString("HH:mm"));
            });
            output.WriteLine($"{e.Snapshot.Name,-12}{string.Join(" ", row)}");
        }
    }

    // ---------------- Configuração realista: abertura, fechamento e 5 dias por semana ----------------

    [Theory]
    [InlineData("cadastro")]  // horário por dia pelo cadastro (seg–sáb 07:00–21:00, dom 07:00–13:00)
    [InlineData("migration")] // migration CadastroLojaFeriadosPicos: o expediente da operação nos 7 dias
    [InlineData("none")]      // loja sem horário cadastrado
    public void Realistic_configurations_have_a_shift_for_every_open_day(string variant)
    {
        var hours = variant switch
        {
            "cadastro" => CadastroHours(),
            "migration" => Week(d => new DayHours(d, false, new TimeOnly(7, 0), new TimeOnly(21, 0))),
            _ => null
        };
        var ctx = PlanningContext.Create(Snapshot(Op(hours)), November, NovemberEnd);

        Assert.Empty(ctx.ConfigurationIssues());
        for (var d = ctx.FirstDay; d <= ctx.LastDay; d++)
        {
            if (!ctx.OpenWorkDay(d)) continue;
            var day = d;
            var open = ctx.DayWindows[d]?.Open ?? 7 * 60;
            var close = ctx.DayWindows[d]?.Close ?? 21 * 60;
            // Turno de abertura que começa na abertura do dia; alguém pode estar no fechamento
            Assert.Contains(ctx.Shifts, t => t.Kind == JourneyProfiles.Abertura && t.StartMin == open && ctx.ShiftFitsDay(0, day, t));
            Assert.Contains(ctx.Shifts, t => t.EndMin >= close && ctx.ShiftFitsDay(0, day, t));
            Assert.All(ctx.Employees.Where(e => ctx.IsAvailable(e, day)), e => Assert.False(ctx.ForcedOff(e, day), $"{e.Snapshot.Name} em {ctx.Dates[day]:dd/MM}"));
        }
    }

    [Fact]
    public void Store_hours_from_the_cadastro_generate_opening_closing_and_five_days_a_week()
    {
        var snap = Snapshot(Op(CadastroHours()));
        var result = Solver().Solve(MonthCommand(snap, 60), 60);
        if (result is SolveResult.Infeasible bad) output.WriteLine($"inviável: {bad.Reason}");
        var ok = Assert.IsType<SolveResult.Success>(result);
        output.WriteLine($"{ok.Status} em {ok.Seconds:F1}s; aviso: {ok.Notice}");
        Print(ok.Ctx, ok.Assignments);

        // Todo turno do motor existe na fotografia (o Scheduling.Api não descarta nenhuma célula)
        var ids = snap.Shifts.Select(s => s.ShiftId).ToHashSet();
        Assert.All(ok.Assignments, a => Assert.Contains(a.ShiftId, ids));

        // Avaliação como o Scheduling.Api grava (fotografia + setores/dias com cobertura como meta do resultado)
        var stored = PlanningContext.Create(snap with { CoberturaMetaSetores = ok.Ctx.CoverageTargetSectorIds(), CoberturaMetaDias = ok.Ctx.CoverageTargetDayList() },
            November, NovemberEnd);
        var evaluation = Evaluator.Evaluate(stored, ok.Assignments);
        foreach (var v in evaluation.Violacoes.Where(v => v.Severidade != "baixa")) output.WriteLine($"{v.Chave}: {v.Detalhe}");
        Assert.Equal(0, ComplianceEvaluator.RigidViolations(evaluation));
        Assert.DoesNotContain(evaluation.Violacoes, v => v.Chave == ComplianceEvaluator.DiaSemNinguem);
        Assert.Null(ComplianceEvaluator.ApprovalBlock(evaluation, ok.Assignments.Count));

        var byDay = ok.Assignments.GroupBy(a => stored.DayIndex(a.Date)).ToDictionary(g => g.Key, g => g.ToList());
        for (var d = stored.FirstDay; d <= stored.LastDay; d++)
        {
            if (!stored.OpenWorkDay(d)) continue;
            var cells = byDay.GetValueOrDefault(d) ?? [];
            var (open, close) = stored.DayWindows[d]!.Value;
            var date = stored.Dates[d];
            // Abertura na abertura do dia, alguém no fechamento e mais de um horário no dia (nada de todos no mesmo turno)
            Assert.Contains(cells, a => PlanningContext.ToMinutes(a.Start) == open);
            var last = stored.Slots.Single(s => s.Day == d && s.SlotEnd == close);
            Assert.True(ComplianceEvaluator.Presence(stored, cells, last).Present >= 1, $"ninguém no fechamento de {date:dd/MM}");
            if (!stored.IsSunday(d)) Assert.True(cells.Select(a => a.Start).Distinct().Count() >= 2, $"um só horário em {date:dd/MM}");
        }
        Assert.Contains(ok.Assignments, a => stored.ShiftById(a.ShiftId)?.Kind == JourneyProfiles.Abertura);
        Assert.Contains(ok.Assignments, a => stored.ShiftById(a.ShiftId)?.Kind == JourneyProfiles.Fechamento);

        // 5 dias de trabalho nas semanas inteiras do mês sem feriado nem ausência (09/11 a 15/11 tem o feriado de domingo)
        foreach (var monday in new[] { new DateOnly(2026, 11, 23) })
        {
            foreach (var e in stored.Employees.Where(e => e.Unavailable.Count == 0))
            {
                var worked = ok.Assignments.Count(a => a.EmployeeId == e.Snapshot.EmployeeId && a.Date >= monday && a.Date < monday.AddDays(7));
                Assert.Equal(5, worked);
            }
        }
        // Semana com 1 feriado sem autorização (16/11 a 22/11, sexta 20): 4 dias
        Assert.All(stored.Employees.Where(e => e.Unavailable.Count == 0), e => Assert.Equal(4,
            ok.Assignments.Count(a => a.EmployeeId == e.Snapshot.EmployeeId && a.Date >= new DateOnly(2026, 11, 16) && a.Date <= new DateOnly(2026, 11, 22))));
        // Presença média bem acima de zero (aproveitamento da carga real)
        Assert.True(ok.Assignments.Count >= 200, $"{ok.Assignments.Count} turnos no mês");
    }

    // ---------------- (A) nenhum turno cabe: diagnóstico explícito, nunca todos de folga ----------------

    [Fact]
    public void Journey_longer_than_the_daily_limit_fails_with_configuration_diagnosis_instead_of_everyone_off()
    {
        // Jornada da operação 9h48 com 1h de intervalo = 8h48 pagas > limite diário de 8h: nenhum turno é possível
        var snap = Snapshot(Op(CadastroHours(), duration: 588));
        var ctx = PlanningContext.Create(snap, November, NovemberEnd);
        var day = ctx.DayIndex(new DateOnly(2026, 11, 3));
        // Antes: dia de "não trabalho forçado" para todos (folga inevitável) → escala com todos de folga e 100%
        Assert.False(ctx.SectorHasShift(0, day));
        Assert.True(ctx.NoShiftConfigured(ctx.Employees[0], day));
        Assert.False(ctx.ForcedOff(ctx.Employees[0], day));

        var bad = Assert.IsType<SolveResult.Infeasible>(Solver().Solve(MonthCommand(snap, 30), 30));
        output.WriteLine(bad.Reason);
        Assert.StartsWith("Escala não gerada — erro de configuração", bad.Reason);
        Assert.Contains("nenhum turno cabe no expediente", bad.Reason);
        Assert.Contains("loja 07:00–21:00", bad.Reason);
        Assert.Contains("8h48 de jornada (9h48 com o intervalo) e o limite diário é 8h", bad.Reason);
        Assert.Contains("Cadastro da loja", bad.Reason);
        var diagnosis = Assert.IsType<InfeasibilityDiagnosis>(bad.Diagnosis);
        Assert.All(diagnosis.Itens, i => Assert.Equal(InfeasibilityDiagnostics.ConfigurationType, i.Tipo));
        Assert.Contains(diagnosis.Itens, i => i.Dates!.Contains(new DateOnly(2026, 11, 3)));
        Assert.Contains(diagnosis.Sugestoes, s => s.Contains("Lojas → Operação"));

        // A escala de novembro que já saiu assim (todos de folga) não é "100%": o avaliador conta as folgas do 5x2 e o dia
        // aberto sem ninguém, e a aprovação é bloqueada
        var evaluation = Evaluator.Evaluate(ctx, []);
        output.WriteLine($"conformidade da escala vazia: {evaluation.Percentual}%");
        Assert.True(evaluation.Percentual < 100);
        Assert.True(ComplianceEvaluator.RigidViolations(evaluation) > 0);
        Assert.Contains(evaluation.Violacoes, v => v.Chave == RuleKeys.FolgasSemana5x2);
        Assert.Contains(evaluation.Violacoes, v => v.Chave == ComplianceEvaluator.DiaSemNinguem && v.Dates.Contains(new DateOnly(2026, 11, 3)));
        Assert.Contains("nenhum turno de trabalho", ComplianceEvaluator.ApprovalBlock(evaluation, 0));
        Assert.Contains("nenhum turno de trabalho", ComplianceEvaluator.ApprovalBlock(evaluation, 0, confirmed: true));
    }

    [Fact]
    public void Coverage_as_target_never_accepts_a_configuration_without_shifts()
    {
        // Mesmo com a cobertura como meta (padrão) e o setor de 2 pessoas, nenhum turno possível é erro de configuração
        var snap = Snapshot(Op(null, duration: 588, close: new TimeOnly(22, 0)));
        snap = snap with { Employees = snap.Employees.Take(2).ToList() };
        var bad = Assert.IsType<SolveResult.Infeasible>(Solver().Solve(MonthCommand(snap, 10), 10));
        output.WriteLine(bad.Reason);
        Assert.Contains("erro de configuração", bad.Reason);
        Assert.Contains("loja 07:00–22:00", bad.Reason);
    }

    // ---------------- (B) um só turno possível / sem cobertura: diagnóstico, nunca todos no mesmo horário ----------------

    [Fact]
    public void Only_a_late_catalog_shift_possible_fails_instead_of_everyone_on_the_same_shift()
    {
        // Jornada da operação longa demais (nenhum turno gerado é possível) e um turno do catálogo 15:21–22:00 (5h39 pagas),
        // que termina no fechamento (tipo Fechamento): antes, todos iam para ele todos os dias e a manhã ficava descoberta
        var late = new ShiftSnapshot(Guid.NewGuid(), "T", new TimeOnly(15, 21), new TimeOnly(22, 0), 60);
        var snap = Snapshot(Op(null, duration: 588, close: new TimeOnly(22, 0)), [late]);
        var ctx = PlanningContext.Create(snap, November, NovemberEnd);
        var t = Assert.Single(ctx.Shifts, x => x.Snapshot.ShiftId == late.ShiftId);
        Assert.Equal(JourneyProfiles.Fechamento, t.Kind);
        Assert.True(ctx.SectorHasShift(0, ctx.DayIndex(new DateOnly(2026, 11, 3))));

        var bad = Assert.IsType<SolveResult.Infeasible>(Solver().Solve(MonthCommand(snap, 30), 30));
        output.WriteLine(bad.Reason);
        Assert.Contains("erro de configuração", bad.Reason);
        Assert.Contains("nenhum turno possível cobre das 07:00 às 15:30", bad.Reason);
    }

    [Fact]
    public void Sector_without_operation_and_without_demand_fails_with_configuration_diagnosis()
    {
        // Sem operação e sem demanda: nada a cobrir — antes, todos no mesmo turno do catálogo e 100%
        var morning = new ShiftSnapshot(Guid.NewGuid(), "M", new TimeOnly(7, 0), new TimeOnly(15, 20), 60);
        var evening = new ShiftSnapshot(Guid.NewGuid(), "T", new TimeOnly(13, 40), new TimeOnly(22, 0), 60);
        var snap = Snapshot(null, [morning, evening]);
        var bad = Assert.IsType<SolveResult.Infeasible>(Solver().Solve(MonthCommand(snap, 10), 10));
        output.WriteLine(bad.Reason);
        Assert.Contains("Setor Açougue sem cobertura configurada no período", bad.Reason);
        Assert.Contains("não tem operação cadastrada", bad.Reason);
    }

    // ---------------- Avaliador e aprovação: dia aberto sem ninguém ----------------

    [Fact]
    public void Open_day_without_anybody_is_reported_even_with_coverage_as_target_and_needs_confirmation()
    {
        var snap = Snapshot(Op(CadastroHours()));
        var ctx = PlanningContext.Create(snap, November, NovemberEnd);
        var opening = ctx.Shifts.First(t => t.Kind == JourneyProfiles.Abertura && t.StartMin == 7 * 60);
        var closing = ctx.Shifts.First(t => t.Kind == JourneyProfiles.Fechamento && t.EndMin == 21 * 60);
        var empty = new DateOnly(2026, 11, 4);
        // Dois por dia (abertura e fechamento) nos dias abertos, exceto 04/11 (ninguém)
        var cells = Enumerable.Range(ctx.FirstDay, ctx.PeriodDays).Where(d => ctx.OpenWorkDay(d) && ctx.Dates[d] != empty && !ctx.IsSunday(d))
            .SelectMany(d => new[]
            {
                new AssignmentDto(ctx.Employees[d % 4].Snapshot.EmployeeId, ctx.Dates[d], opening.Snapshot.ShiftId, Sector, opening.Snapshot.Start, opening.Snapshot.End, opening.PaidMinutes),
                new AssignmentDto(ctx.Employees[4 + d % 4].Snapshot.EmployeeId, ctx.Dates[d], closing.Snapshot.ShiftId, Sector, closing.Snapshot.Start, closing.Snapshot.End, closing.PaidMinutes)
            }).ToList();

        // Cobertura rígida: violação rígida e aprovação bloqueada mesmo confirmando
        var hard = Evaluator.Evaluate(ctx, cells);
        var v = Assert.Single(hard.Violacoes, x => x.Chave == ComplianceEvaluator.DiaSemNinguem && x.Dates.Contains(empty));
        Assert.Equal("alta", v.Severidade);
        Assert.Contains("ninguém está escalado no setor", v.Detalhe);
        Assert.NotNull(ComplianceEvaluator.ApprovalBlock(hard, cells.Count, confirmed: true));

        // Cobertura como meta (quadro insuficiente gravado na fotografia): aviso (baixa), mas nunca 100% e a aprovação pede confirmação
        var target = PlanningContext.Create(snap with { CoberturaMetaSetores = [Sector] }, November, NovemberEnd);
        var soft = Evaluator.Evaluate(target, cells);
        var w = Assert.Single(soft.Violacoes, x => x.Chave == ComplianceEvaluator.DiaSemNinguem && x.Dates.Contains(empty));
        Assert.Equal("baixa", w.Severidade);
        Assert.True(soft.Percentual < 100);
        Assert.Contains("confirme a aprovação", ComplianceEvaluator.ApprovalBlock(soft, cells.Count));
        Assert.Null(ComplianceEvaluator.ApprovalBlock(soft, cells.Count, confirmed: true));
    }

    // ---------------- Turnos do horário especial de feriado existem na fotografia ----------------

    [Fact]
    public void Holiday_special_hours_shifts_are_in_the_scheduling_snapshot()
    {
        // Véspera com horário especial 08:00–14:00: o motor gera a abertura das 08:00. Antes o Scheduling.Api não a tinha
        // na fotografia e a célula do motor era descartada ao gravar (virava folga na grade)
        var holidays = new List<HolidaySnapshot> { new(new DateOnly(2026, 11, 20), "Véspera", true, "Loja", nameof(FuncionamentoDia.Especial), new TimeOnly(8, 0), new TimeOnly(14, 0)) };
        var op = Op(CadastroHours());
        var snap = Snapshot(op, holidays: holidays);
        var ctx = PlanningContext.Create(snap, November, NovemberEnd);
        Assert.Contains(ctx.Shifts, t => t.Kind == JourneyProfiles.Abertura && t.StartMin == 8 * 60);
        Assert.Equal(snap.Shifts.Select(s => s.ShiftId).Order(), ctx.Shifts.Select(s => s.Snapshot.ShiftId).Order());

        // Sem os expedientes do feriado (como o Scheduling.Api expandia antes), o motor tem turno que a fotografia não tem
        var before = snap with { Shifts = OperationPlan.Expand(op, Sector, [], 60) };
        var engine = PlanningContext.Create(before, November, NovemberEnd);
        Assert.Contains(engine.Shifts, t => before.Shifts.All(s => s.ShiftId != t.Snapshot.ShiftId));
    }
}

/// <summary>Mês inteiro com 13 pessoas: roda sem outros testes do CP-SAT em paralelo (a qualidade depende do tempo).</summary>
[CollectionDefinition(nameof(ConfigurationErrorsTests), DisableParallelization = true)]
public sealed class ConfigurationErrorsTestsCollection;

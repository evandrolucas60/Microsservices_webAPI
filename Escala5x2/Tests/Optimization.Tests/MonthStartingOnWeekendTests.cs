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
/// Caso relatado (novembro/2026): 01/11 é DOMINGO — a 1ª semana civil (26/10 a 01/11) tem só 1 dia no mês e 6 dias de
/// borda; o mês termina na segunda 30/11 (mais 6 dias de borda em dezembro). Sem escala de outubro a geração dava "Inviável:
/// Continuidade na emenda com a escala anterior ... max_folgas_seguidas". Causas: (1) a continuidade lia como "escala
/// anterior" outra versão do próprio mês (as antigas, por semanas, começam na segunda 26/10), com folgas registradas nos
/// dias de borda; (2) o motor exigia max_folgas_seguidas em janelas só de dias de borda/da escala vizinha (o avaliador não
/// mede essas janelas) e (3) o diagnóstico chamava de "emenda" um dia de borda sem escala vizinha (decisão livre do motor).
/// </summary>
public sealed class MonthStartingOnWeekendTests(ITestOutputHelper output)
{
    private static readonly ComplianceEvaluator Evaluator = new();
    private static readonly DateTime Now = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Sector = Guid.NewGuid();
    private static readonly ShiftSnapshot Morning = new(Guid.NewGuid(), "M", new TimeOnly(7, 0), new TimeOnly(15, 20), 60);
    private static readonly ShiftSnapshot Evening = new(Guid.NewGuid(), "T", new TimeOnly(13, 40), new TimeOnly(22, 0), 60);

    private static ScheduleSolver Solver() =>
        new(Options.Create(new SolverOptions { Workers = 8 }), Evaluator, NullLogger<ScheduleSolver>.Instance);

    /// <summary>
    /// Regras padrão do cadastro com a carga de 42h (regime 5x2: exatamente 2 folgas na semana civil, máx. 5 dias seguidos),
    /// máx. 2 folgas seguidas, domingo a cada 3 semanas e quinzenal (CLT art. 386) para quem tem a regra.
    /// </summary>
    private static List<ComplianceRuleSnapshot> Rules5x2() =>
        ScheduleSolverTests.DefaultRules().Select(r => r.Chave == RuleKeys.CargaHorariaSemanal ? r with { Valor = 42 } : r).ToList();

    private static List<EmployeeSnapshot> People(int size) => Enumerable.Range(0, size).Select(i => new EmployeeSnapshot(Guid.NewGuid(),
        $"FS{i:000}", $"Pessoa {i + 1}", [Sector], Guid.NewGuid(), true, false, i % 2 == 0 ? SundayRule.OneInThreeWeeks : SundayRule.Biweekly, [])).ToList();

    /// <summary>Setor de 2 e 3 pessoas: operação 07:00–22:00 (mínimo 1), cobertura como meta (quadro insuficiente/2 pessoas).</summary>
    private static PlanningSnapshot SmallSector(int size)
    {
        var op = new OperationSnapshot(new TimeOnly(7, 0), new TimeOnly(22, 0), 1, 1, 1, 15, null, null, 500, 60,
            FolgasFixasSetorDuasPessoas: true, CoberturaComoMetaQuadroInsuficiente: true);
        return new PlanningSnapshot(Guid.NewGuid(), "Teste", false, [new SectorSnapshot(Sector, "Padaria")], [], [], [], People(size), null, [],
            Rules5x2(), null, op);
    }

    /// <summary>Setor sem operação: 1 pessoa de manhã (08:00–13:30) e 1 à noite (15:30–21:30) todo dia, turnos M e T.</summary>
    private static PlanningSnapshot DemandSector(int size)
    {
        var demands = Enum.GetValues<DayOfWeek>().SelectMany(d => new[]
        {
            new DemandSnapshot(Sector, d, new TimeOnly(8, 0), new TimeOnly(13, 30), 1),
            new DemandSnapshot(Sector, d, new TimeOnly(15, 30), new TimeOnly(21, 30), 1)
        }).ToList();
        return new PlanningSnapshot(Guid.NewGuid(), "Teste", false, [new SectorSnapshot(Sector, "Caixa")], [Morning, Evening], demands, [],
            People(size), null, [], Rules5x2());
    }

    private static PlanningSnapshot Snap(int size) => size <= 3 ? SmallSector(size) : DemandSector(size);

    private static GenerateScheduleCommand MonthCommand(DateOnly anyDay, PlanningSnapshot snapshot, int seconds = 15,
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

    /// <summary>Escala do mês com células só no mês e sem violação rígida no avaliador independente.</summary>
    private void AssertCompliantMonth(SolveResult.Success ok)
    {
        Assert.All(ok.Assignments, a => Assert.InRange(a.Date, ok.Ctx.PeriodStart, ok.Ctx.PeriodEnd));
        var result = Evaluator.Evaluate(ok.Ctx, ok.Assignments);
        foreach (var v in result.Violacoes.Where(v => v.Severidade is "alta" or "media")) output.WriteLine($"{v.Chave}: {v.Detalhe}");
        Assert.Equal(0, ComplianceEvaluator.RigidViolations(result));
    }

    [Theory]
    [InlineData(2026, 11, 2)] // começa no domingo (1 dia na 1ª semana) e termina na segunda (1 dia na última)
    [InlineData(2026, 11, 3)]
    [InlineData(2026, 11, 5)]
    [InlineData(2026, 8, 3)]  // começa no sábado (2 dias na 1ª semana) e termina na segunda
    [InlineData(2026, 2, 5)]  // começa no domingo e termina no sábado (6 dias na última semana)
    public void Month_starting_on_the_weekend_without_neighbour_schedules_is_generated(int y, int m, int size)
    {
        // Setor pequeno (cobertura como meta): a primeira escala de um mês inteiro leva mais tempo para sair
        var ok = Success(Solver().Solve(MonthCommand(new DateOnly(y, m, 1), Snap(size), size <= 3 ? 30 : 15)));

        // Sem escala vizinha: todos os dias de borda ficam livres no motor (nenhum vira trabalho/folga registrados)
        Assert.All(ok.Ctx.Employees, e => Assert.Equal(ok.Ctx.Days - ok.Ctx.PeriodDays, e.EdgeUnknown.Count));
        Assert.All(ok.Ctx.Employees, e => Assert.Empty(e.EdgeRest));
        AssertCompliantMonth(ok);
    }

    [Fact]
    public void Neighbour_border_days_that_already_break_max_folgas_seguidas_do_not_make_the_month_infeasible()
    {
        // A escala lida como anterior registrou 3 folgas seguidas antes do mês (dom 25, seg 26 e ter 27/10 — ex.: escala
        // antiga, de antes da regra max_folgas_seguidas) e trabalho de quarta a sábado. A janela 25–27/10 não tem nenhum dia
        // de novembro: é da escala vizinha (o avaliador não a mede) e não pode tornar novembro inviável. Antes: "Inviável —
        // Continuidade na emenda com a escala anterior (início da escala em 01/11): max_folgas_seguidas".
        var snap = DemandSector(5);
        var person = snap.Employees[0].EmployeeId;
        var work = Enumerable.Range(28, 4).Select(d => new HistoryEntry(person, new DateOnly(2026, 10, d), Morning.Start, Morning.End)).ToList();
        var rest = new[] { 25, 26, 27 }.Select(d => new RestDayEntry(person, new DateOnly(2026, 10, d))).ToList();

        var ok = Success(Solver().Solve(MonthCommand(new DateOnly(2026, 11, 1), snap with { History = work, HistoryRest = rest })));

        var e = ok.Ctx.Employees[0];
        Assert.Equal([0, 1], e.EdgeRest.Order());
        Assert.All(new[] { 2, 3, 4, 5 }, d => Assert.True(e.Busy.ContainsKey(d)));
        // Semana 26/10–01/11 com as 2 folgas já registradas (seg e ter): o domingo 01/11 é trabalhado
        Assert.Contains(ok.Assignments, a => a.EmployeeId == person && a.Date == new DateOnly(2026, 11, 1));
        AssertCompliantMonth(ok);
    }

    [Fact]
    public void Without_neighbour_schedule_the_diagnosis_names_the_rule_and_not_the_seam()
    {
        // Sem escala de outubro, a Pessoa 1 tem folga manual no domingo 01/11, na segunda 02/11 e na terça 03/11 (3 seguidas;
        // as 2 folgas da semana de 02/11 já são seg e ter). O conflito é com a regra (e as células manuais), não com uma
        // escala anterior que não existe. (Antes o caso usava o sábado 31/10 indisponível como 3ª folga: dia que a pessoa
        // não pode trabalhar agora é neutro em max_folgas_seguidas, ver PlanningContext.DaysOff.)
        var snap = DemandSector(5);
        var person = snap.Employees[0];
        CellDto[] manual = [.. new[] { 1, 2, 3 }.Select(d => new CellDto(person.EmployeeId, new DateOnly(2026, 11, d), null, true))];
        var bad = Assert.IsType<SolveResult.Infeasible>(Solver().Solve(MonthCommand(new DateOnly(2026, 11, 1), snap, 10, GenerationModes.SomenteVazios, manual)));

        output.WriteLine(bad.Reason);
        var diagnosis = Assert.IsType<InfeasibilityDiagnosis>(bad.Diagnosis);
        foreach (var i in diagnosis.Itens) output.WriteLine($"{i.Tipo}: {i.Mensagem}");
        Assert.DoesNotContain(diagnosis.Itens, i => i.Tipo == "continuidade");
        Assert.DoesNotContain(diagnosis.Itens, i => i.Mensagem.Contains("emenda"));
        Assert.Contains(diagnosis.Itens, i => i.Chave == RuleKeys.MaxFolgasSeguidas);
        Assert.Contains(diagnosis.Itens, i => i.Chave == RuleKeys.MaxFolgasSeguidas && i.EmployeeId == person.EmployeeId
                                              && i.Mensagem.Contains("01/11, 02/11 e 03/11"));
    }

    [Fact]
    public void Other_versions_of_the_same_month_are_not_the_previous_or_following_schedule()
    {
        // Novembro/2026 (01 a 30/11). Candidatas do setor como o Scheduling.Api as lê:
        var from = new DateOnly(2026, 11, 1);
        var to = new DateOnly(2026, 11, 30);
        // versão antiga de novembro POR SEMANAS (seg 26/10 a dom 29/11), principal: plano velho deste mês
        var novWeekly = Candidate(new DateOnly(2026, 10, 26), new DateOnly(2026, 11, 29), principal: true);
        // outra versão de novembro por semanas terminando em dezembro (02/11 a 06/12)
        var novWeeklyToDecember = Candidate(new DateOnly(2026, 11, 2), new DateOnly(2026, 12, 6));
        // outra versão mensal de novembro
        var novMonthly = Candidate(from, to);
        // outubro antigo por semanas (28/09 a 01/11): é a escala anterior (só os dias até 31/10 contam)
        var octWeekly = Candidate(new DateOnly(2026, 9, 28), new DateOnly(2026, 11, 1));
        // outubro mensal e dezembro por semanas (30/11 a 03/01): vizinhas
        var octMonthly = Candidate(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));
        var decWeekly = Candidate(new DateOnly(2026, 11, 30), new DateOnly(2027, 1, 3));

        Assert.False(ContinuityHistory.IsNeighbour(novWeekly.Start, novWeekly.End, from, to));
        Assert.False(ContinuityHistory.IsNeighbour(novWeeklyToDecember.Start, novWeeklyToDecember.End, from, to));
        Assert.False(ContinuityHistory.IsNeighbour(novMonthly.Start, novMonthly.End, from, to));
        Assert.True(ContinuityHistory.IsNeighbour(octWeekly.Start, octWeekly.End, from, to));
        Assert.True(ContinuityHistory.IsNeighbour(octMonthly.Start, octMonthly.End, from, to));
        Assert.True(ContinuityHistory.IsNeighbour(decWeekly.Start, decWeekly.End, from, to));

        // A versão antiga de novembro tem 3 folgas marcadas em 29 a 31/10; a de outubro, trabalho nesses dias
        var person = Guid.NewGuid();
        var names = new Dictionary<Guid, string> { [person] = "Pessoa 1" };
        var cells = Enumerable.Range(26, 6).Select(d => new DateOnly(2026, 10, d)).SelectMany(d => new[]
        {
            new ContinuityCell(novWeekly.Id, person, d, d.Day < 29 ? Morning.Start : null, d.Day < 29 ? Morning.End : null, d.Day >= 29),
            new ContinuityCell(octWeekly.Id, person, d, Morning.Start, Morning.End, false)
        }).ToList();

        // Sem o filtro a versão antiga de novembro (principal) ganharia de outubro nos dias de borda
        var unfiltered = ContinuityHistory.Previous([novWeekly, octWeekly], cells, from, 27, names, restWindow: 13);
        Assert.Contains(unfiltered.Rest!, r => r.Date == new DateOnly(2026, 10, 31));

        var candidates = ContinuityHistory.Neighbours([novWeekly, novWeeklyToDecember, novMonthly, octWeekly], from, to);
        Assert.Equal([octWeekly], candidates);
        var previous = ContinuityHistory.Previous(candidates, cells, from, 27, names, restWindow: 13);
        Assert.Equal(6, previous.History.Count(h => h.Date >= new DateOnly(2026, 10, 26)));
        Assert.Empty(previous.Rest!);
        Assert.DoesNotContain(previous.Notices, n => n.Contains("29/11"));

        // Escala seguinte: a versão de novembro que termina em dezembro também não conta
        Assert.Equal([decWeekly], ContinuityHistory.Neighbours([novWeeklyToDecember, decWeekly], from, to));
    }

    private static ContinuitySchedule Candidate(DateOnly start, DateOnly end, bool principal = false) =>
        new(Guid.NewGuid(), Sector, "Caixa", 1, start, end, 2, Now, "Gerada", Published: false, Principal: principal);
}

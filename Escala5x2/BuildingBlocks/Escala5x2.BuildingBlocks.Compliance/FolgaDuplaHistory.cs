using Escala5x2.BuildingBlocks.Contracts.Planning;
using Escala5x2.BuildingBlocks.Contracts.Scheduling;

namespace Escala5x2.BuildingBlocks.Compliance;

/// <summary>
/// FOLGA DUPLA: duas folgas de descanso em dias seguidos (o máximo da regra max_folgas_seguidas = 2), com os DOIS dias
/// dentro do mesmo mês civil. A folga dupla que cruza a virada do mês (último dia + dia 1º) não conta para nenhum dos dois
/// meses: cada mês precisa da sua dentro do próprio período — assim o motor não "atende" a prioridade com uma folga já
/// registrada na escala anterior, e a contagem do histórico é a mesma que o motor usa. Ausência (férias, afastamento,
/// atestado, indisponibilidade) e feriado sem autorização não são folga de descanso: não formam folga dupla.
/// <para>Pontuação (<see cref="FolgaDuplaScore"/>): meses seguidos sem folga dupla antes do mês da escala, lidos do mais
/// recente para trás (até <see cref="MesesHistorico"/>), nas escalas que valem por setor e dia (principal; sem ela,
/// Publicada &gt; Aprovada). Mês com menos de <see cref="MinDiasNoMes"/> dias conhecidos (trabalho ou folga registrados:
/// férias/afastamento no mês, admissão no meio do mês, mês sem escala) é neutro — não soma nem zera a sequência —, para a
/// ausência não dar prioridade nem tirar a vez de ninguém. Sem nenhum mês avaliado (colaborador novo): pontuação 0.</para>
/// </summary>
public static class FolgaDuplaHistory
{
    /// <summary>Meses anteriores lidos para a pontuação.</summary>
    public const int MesesHistorico = 6;

    /// <summary>Dias conhecidos (trabalho ou folga de descanso registrados) para o mês contar na pontuação.</summary>
    public const int MinDiasNoMes = 14;

    /// <summary>Primeiro e último dia do mês civil <paramref name="k"/> meses antes do mês de <paramref name="day"/>.</summary>
    public static (DateOnly From, DateOnly To) MonthBefore(DateOnly day, int k)
    {
        var from = new DateOnly(day.Year, day.Month, 1).AddMonths(-k);
        return (from, from.AddMonths(1).AddDays(-1));
    }

    /// <summary>Primeiro dia da folga dupla mais recente em [<paramref name="from"/>, <paramref name="to"/>] (os dois dias no intervalo); null = nenhuma.</summary>
    public static DateOnly? LastPair(Func<DateOnly, bool> rest, DateOnly from, DateOnly to)
    {
        for (var d = to.AddDays(-1); d >= from; d = d.AddDays(-1))
            if (rest(d) && rest(d.AddDays(1))) return d;
        return null;
    }

    /// <summary>Primeiro dia da primeira folga dupla em [<paramref name="from"/>, <paramref name="to"/>]; null = nenhuma.</summary>
    public static DateOnly? FirstPair(Func<DateOnly, bool> rest, DateOnly from, DateOnly to)
    {
        for (var d = from; d < to; d = d.AddDays(1))
            if (rest(d) && rest(d.AddDays(1))) return d;
        return null;
    }

    /// <summary>
    /// Pontuação de folga dupla de cada colaborador para a escala que começa em <paramref name="from"/> (o mês dela não
    /// entra): para k = 1..<paramref name="months"/>, o mês k antes — neutro com menos de <see cref="MinDiasNoMes"/> dias
    /// conhecidos; com folga dupla, para; sem, soma 1. Por pessoa, setor e dia vale a escala de maior prioridade
    /// (<see cref="ContinuityHistory.InPriority"/>) entre as que têm a pessoa (alguma célula dela) e cobrem o dia: trabalho
    /// em qualquer uma = dia trabalhado; senão folga marcada ou célula vazia em escala gerada
    /// (<see cref="ContinuitySchedule.EmptyIsRest"/>) = folga de descanso, salvo ausência/feriado sem autorização da
    /// fotografia (<paramref name="notRest"/>).
    /// </summary>
    public static List<FolgaDuplaScore> Score(IReadOnlyList<ContinuitySchedule> schedules, IReadOnlyList<ContinuityCell> cells,
        DateOnly from, IEnumerable<Guid> employees, NotRestDay? notRest = null, int months = MesesHistorico)
    {
        var people = employees.Distinct().ToList();
        var first = MonthBefore(from, months).From;
        var last = MonthBefore(from, 1).To;
        var set = people.ToHashSet();
        var inRange = cells.Where(c => set.Contains(c.EmployeeId) && c.Date >= first && c.Date <= last).ToList();
        var byId = schedules.ToDictionary(s => s.Id);
        var cellOf = inRange.Where(c => byId.ContainsKey(c.ScheduleId))
            .GroupBy(c => (c.ScheduleId, c.EmployeeId, c.Date)).ToDictionary(g => g.Key, g => g.First());

        var result = new List<FolgaDuplaScore>();
        foreach (var e in people)
        {
            // Escalas em que a pessoa está (tem alguma célula); por setor e dia vale a de maior prioridade que cobre o dia.
            // Diferente da continuidade, o dia não precisa ter célula de alguém: a escala do mês cobre todos os seus dias
            // (ex.: domingo com a loja fechada é folga de todos)
            var mine = InPriorityOf(inRange.Where(c => c.EmployeeId == e).Select(c => c.ScheduleId).Distinct().Select(id => byId.GetValueOrDefault(id))
                .OfType<ContinuitySchedule>());
            var worked = new HashSet<DateOnly>();
            var rest = new HashSet<DateOnly>();
            for (var d = first; d <= last; d = d.AddDays(1))
            {
                var day = d;
                var chosen = mine.Where(s => s.Start <= day && s.End >= day).GroupBy(s => s.SectorId).Select(g => g.First()).ToList();
                if (chosen.Count == 0) continue; // sem escala: não se sabe
                var dayCells = chosen.Select(s => cellOf.GetValueOrDefault((s.Id, e, day))).ToList();
                if (dayCells.Any(c => c is { Folga: false, Start: not null, End: not null })) { worked.Add(day); continue; }
                var isRest = chosen.Zip(dayCells).Any(x => x.Second is { Folga: true } || (x.Second is null && x.First.EmptyIsRest));
                if (isRest && (notRest is null || !chosen.Any(s => notRest(s.Id, e, day)))) rest.Add(day);
            }

            var count = 0;
            var evaluated = 0;
            DateOnly? lastPair = null;
            for (var k = 1; k <= months; k++)
            {
                var (mFrom, mTo) = MonthBefore(from, k);
                var known = 0;
                for (var d = mFrom; d <= mTo; d = d.AddDays(1))
                    if (worked.Contains(d) || rest.Contains(d)) known++;
                if (known < MinDiasNoMes) continue; // mês neutro (ausência, sem escala, admissão no meio do mês)
                evaluated++;
                lastPair = LastPair(rest.Contains, mFrom, mTo);
                if (lastPair is not null) break;
                count++;
            }
            result.Add(new FolgaDuplaScore(e, count, lastPair, evaluated));
        }
        return result;
    }

    private static List<ContinuitySchedule> InPriorityOf(IEnumerable<ContinuitySchedule> schedules) =>
        ContinuityHistory.InPriority(schedules).ToList();

    /// <summary>
    /// Folga dupla no período da escala, por colaborador (primeiro dia da primeira; null = nenhuma): folga de descanso =
    /// dia do período sem trabalho nas células (<paramref name="work"/>) nem em outro setor, sem ausência nem feriado sem
    /// autorização (<see cref="PlanningContext.NonWorkable"/>).
    /// </summary>
    public static Dictionary<Guid, DateOnly?> InPeriod(PlanningContext ctx, IEnumerable<AssignmentDto> work)
    {
        var worked = work.Select(a => (a.EmployeeId, a.Date)).ToHashSet();
        var result = new Dictionary<Guid, DateOnly?>();
        foreach (var e in ctx.Employees)
        {
            var id = e.Snapshot.EmployeeId;
            bool Rest(DateOnly date)
            {
                var d = ctx.DayIndex(date);
                return ctx.InPeriod(d) && !worked.Contains((id, date)) && !e.Busy.ContainsKey(d) && !ctx.NonWorkable(e, d);
            }
            result[id] = FirstPair(Rest, ctx.PeriodStart, ctx.PeriodEnd);
        }
        return result;
    }

    /// <summary>Meses sem folga dupla de cada colaborador na fotografia (ausente/fotografia antiga: 0).</summary>
    public static int MonthsWithout(PlanningSnapshot snapshot, Guid employeeId) =>
        snapshot.FolgaDupla?.FirstOrDefault(s => s.EmployeeId == employeeId)?.MesesSemFolgaDupla ?? 0;

    /// <summary>"1 mês" / "3 meses".</summary>
    public static string MonthsText(int n) => n == 1 ? "1 mês" : $"{n} meses";
}

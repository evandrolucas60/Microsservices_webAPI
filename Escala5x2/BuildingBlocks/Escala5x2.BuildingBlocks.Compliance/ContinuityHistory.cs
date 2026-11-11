using Escala5x2.BuildingBlocks.Contracts.Planning;

namespace Escala5x2.BuildingBlocks.Compliance;

/// <summary>
/// Escala candidata a fornecer a continuidade com a semana anterior/seguinte. <paramref name="Principal"/>: escala
/// principal do setor no período (escolhida pelo gestor) — vem antes de qualquer outra. Sem principal:
/// <paramref name="Rank"/> = prioridade do status (0 = Publicada, 1 = Aprovada, 2 = Gerada/Rascunho); empate: a mais
/// recente (<paramref name="RequestedAt"/>). <paramref name="SectorId"/> null = escala antiga por loja (vale como um
/// "setor" próprio). <paramref name="EmptyIsRest"/>: célula vazia da pessoa nessa escala é folga (Gerada, Aprovada,
/// Publicada: o motor não grava as folgas); no Rascunho a célula vazia é "sem definição" e só a folga marcada conta.
/// </summary>
public sealed record ContinuitySchedule(Guid Id, Guid? SectorId, string SectorName, int Version, DateOnly Start, DateOnly End,
    int Rank, DateTime RequestedAt, string StatusLabel, bool Published, bool Principal = false, bool EmptyIsRest = true);

/// <summary>Célula de uma escala candidata (trabalho com Start/End, ou folga).</summary>
public sealed record ContinuityCell(Guid ScheduleId, Guid EmployeeId, DateOnly Date, TimeOnly? Start, TimeOnly? End, bool Folga);

/// <summary>
/// Histórico escolhido, os avisos sobre a origem dele e as folgas de descanso da semana anterior
/// (<see cref="ContinuityHistory.RestDays"/>; regra max_folgas_seguidas).
/// </summary>
public sealed record ContinuityResult(List<HistoryEntry> History, List<string> Notices, List<RestDayEntry>? Rest = null);

/// <summary>
/// Dia que NÃO é folga de descanso numa escala anterior/seguinte, mesmo sem trabalho: ausência (férias, afastamento,
/// atestado, indisponibilidade) ou feriado sem autorização registrados na fotografia da escala que cobre o dia.
/// </summary>
public delegate bool NotRestDay(Guid scheduleId, Guid employeeId, DateOnly date);

/// <summary>
/// Continuidade com a semana ANTERIOR (R2 dias seguidos, R5 interjornada, R5b folga isolada, R6 domingos) e SEGUINTE:
/// por setor e dia vale uma escala só — a PRINCIPAL do setor, se houver; senão a de melhor status (Publicada &gt;
/// Aprovada &gt; Gerada/Rascunho) e, no empate, a mais recente; escala sem nenhuma célula no dia não conta (rascunho vazio
/// não esconde a gerada). A pessoa pode ter trabalhado em qualquer setor da loja. Dia sem trabalho nas escalas escolhidas
/// (folga, feriado, afastamento ou sem escala) não é trabalhado: interrompe a sequência de dias seguidos, como sempre foi.
/// Para max_folgas_seguidas a continuidade leva também as folgas de descanso da semana junto da emenda
/// (<see cref="RestDays"/>): só dias que a escala que vale realmente cobre, sem ausência nem feriado sem autorização.
/// </summary>
public static class ContinuityHistory
{
    /// <summary>Nomes listados no aviso de "sem escala" antes de resumir em "e mais N".</summary>
    private const int MaxNames = 8;

    /// <summary>Dias junto da emenda (antes do início / depois do fim) em que as folgas registradas são levantadas.</summary>
    public const int RestDaysWindow = 7;

    /// <summary>
    /// A escala [<paramref name="start"/>, <paramref name="end"/>] é VIZINHA do período [<paramref name="from"/>,
    /// <paramref name="to"/>] (escala anterior/seguinte) e pode fornecer a continuidade: a maior parte dos dias dela fica
    /// fora do período. Outra versão do mesmo mês — inclusive as antigas por semanas, que começam na segunda do mês anterior
    /// (ex.: 26/10 a 29/11 para novembro/2026) ou terminam no mês seguinte — não é escala anterior nem seguinte: os dias de
    /// borda dela são o plano velho deste mesmo mês e não podem restringir a geração. Escala antiga por semanas que só
    /// encosta no período (ex.: 28/09 a 01/11 para novembro) continua vizinha (só os dias fora do período são lidos).
    /// </summary>
    public static bool IsNeighbour(DateOnly start, DateOnly end, DateOnly from, DateOnly to)
    {
        var overlap = Math.Min(end.DayNumber, to.DayNumber) - Math.Max(start.DayNumber, from.DayNumber) + 1;
        return overlap <= 0 || overlap * 2 <= end.DayNumber - start.DayNumber + 1;
    }

    /// <summary>Só as candidatas vizinhas do período (<see cref="IsNeighbour"/>): sem as outras versões do mesmo mês.</summary>
    public static List<ContinuitySchedule> Neighbours(IEnumerable<ContinuitySchedule> schedules, DateOnly from, DateOnly to) =>
        schedules.Where(s => IsNeighbour(s.Start, s.End, from, to)).ToList();

    /// <summary>Ordem de escolha: principal, depois status (Rank) e a mais recente.</summary>
    public static IOrderedEnumerable<ContinuitySchedule> InPriority(IEnumerable<ContinuitySchedule> schedules) =>
        schedules.OrderByDescending(s => s.Principal).ThenBy(s => s.Rank).ThenByDescending(s => s.RequestedAt);

    /// <summary>
    /// Por setor e dia em [<paramref name="first"/>, <paramref name="last"/>], a escala que vale (principal &gt; status &gt;
    /// mais recente), só entre as que têm célula das pessoas no dia (<paramref name="hasCells"/>).
    /// </summary>
    public static HashSet<(Guid ScheduleId, DateOnly Date)> ChooseByDay(IReadOnlyList<ContinuitySchedule> schedules,
        IReadOnlySet<(Guid ScheduleId, DateOnly Date)> hasCells, DateOnly first, DateOnly last)
    {
        var chosen = new HashSet<(Guid ScheduleId, DateOnly Date)>();
        foreach (var bySector in schedules.GroupBy(s => s.SectorId))
        {
            for (var date = first; date <= last; date = date.AddDays(1))
            {
                var day = date;
                var best = InPriority(bySector.Where(s => s.Start <= day && s.End >= day && hasCells.Contains((s.Id, day)))).FirstOrDefault();
                if (best is not null) chosen.Add((best.Id, day));
            }
        }
        return chosen;
    }

    /// <summary>Dias trabalhados (um por pessoa e dia: a célula da escala de maior prioridade) das células escolhidas.</summary>
    private static List<HistoryEntry> Worked(IEnumerable<ContinuityCell> used, IReadOnlyDictionary<Guid, ContinuitySchedule> byId) =>
        used.Where(c => !c.Folga && c.Start is not null && c.End is not null)
            .GroupBy(c => (c.EmployeeId, c.Date))
            .Select(g => g.OrderByDescending(c => byId[c.ScheduleId].Principal).ThenBy(c => byId[c.ScheduleId].Rank)
                .ThenByDescending(c => byId[c.ScheduleId].RequestedAt).First())
            .Select(c => new HistoryEntry(c.EmployeeId, c.Date, c.Start!.Value, c.End!.Value))
            .OrderBy(h => h.Date).ThenBy(h => h.EmployeeId)
            .ToList();

    /// <param name="from">Primeiro dia da escala que está sendo gerada (o histórico vai até o dia anterior).</param>
    /// <param name="days">Dias de histórico (21).</param>
    /// <summary>
    /// Folgas de descanso registradas em [<paramref name="first"/>, <paramref name="last"/>] (regra max_folgas_seguidas na
    /// emenda): por pessoa e dia, sem trabalho em nenhuma das escalas que valem no dia (<see cref="ChooseByDay"/>) e coberto
    /// por pelo menos uma delas — a pessoa está na escala (tem alguma célula nela) e, no dia, tem folga marcada ou a célula
    /// vazia é folga (<see cref="ContinuitySchedule.EmptyIsRest"/>) —, sem ausência nem feriado sem autorização
    /// (<paramref name="notRest"/>). Dia que nenhuma escala cobre não entra: não é folga conhecida e não restringe.
    /// </summary>
    public static List<RestDayEntry> RestDays(IReadOnlyList<ContinuitySchedule> schedules, IReadOnlyList<ContinuityCell> cells,
        DateOnly first, DateOnly last, IEnumerable<Guid> employees, NotRestDay? notRest = null)
    {
        var people = employees.ToHashSet();
        var mine = cells.Where(c => people.Contains(c.EmployeeId)).ToList();
        var inRange = mine.Where(c => c.Date >= first && c.Date <= last).ToList();
        var chosen = ChooseByDay(schedules, inRange.Select(c => (c.ScheduleId, c.Date)).ToHashSet(), first, last);
        var byId = schedules.ToDictionary(s => s.Id);
        // A pessoa está na escala: tem alguma célula nela (trabalho ou folga marcada) nas células lidas
        var member = mine.Select(c => (c.ScheduleId, c.EmployeeId)).ToHashSet();
        var worked = inRange.Where(c => !c.Folga && c.Start is not null && c.End is not null && chosen.Contains((c.ScheduleId, c.Date)))
            .Select(c => (c.EmployeeId, c.Date)).ToHashSet();
        var marked = inRange.Where(c => c.Folga && chosen.Contains((c.ScheduleId, c.Date)))
            .Select(c => (c.ScheduleId, c.EmployeeId, c.Date)).ToHashSet();

        var result = new List<RestDayEntry>();
        for (var date = first; date <= last; date = date.AddDays(1))
        {
            var day = date;
            var ofDay = chosen.Where(c => c.Date == day).Select(c => c.ScheduleId).ToList();
            if (ofDay.Count == 0) continue;
            foreach (var e in people)
            {
                if (worked.Contains((e, day))) continue;
                var covering = ofDay.Where(id => member.Contains((id, e))).ToList();
                if (!covering.Any(id => marked.Contains((id, e, day)) || byId[id].EmptyIsRest)) continue;
                if (notRest is not null && covering.Any(id => notRest(id, e, day))) continue;
                result.Add(new RestDayEntry(e, day));
            }
        }
        return result.OrderBy(r => r.Date).ThenBy(r => r.EmployeeId).ToList();
    }

    /// <param name="employees">Colaboradores da escala (id → nome), para filtrar e avisar.</param>
    /// <param name="notRest">Ausência/feriado sem autorização nas escalas anteriores (dia sem trabalho que não é folga).</param>
    /// <param name="restWindow">Dias antes do início com as folgas registradas (max_folgas_seguidas; padrão 7).</param>
    public static ContinuityResult Previous(IReadOnlyList<ContinuitySchedule> schedules, IReadOnlyList<ContinuityCell> cells,
        DateOnly from, int days, IReadOnlyDictionary<Guid, string> employees, NotRestDay? notRest = null, int restWindow = RestDaysWindow)
    {
        var historyFrom = from.AddDays(-days);
        var weekFrom = from.AddDays(-7);
        var cellsOfPeople = cells.Where(c => employees.ContainsKey(c.EmployeeId) && c.Date >= historyFrom && c.Date < from).ToList();
        var hasCells = cellsOfPeople.Select(c => (c.ScheduleId, c.Date)).ToHashSet();

        // Por setor e dia: a escala escolhida
        var chosen = ChooseByDay(schedules, hasCells, historyFrom, from.AddDays(-1));
        var chosenInWeek = chosen.Any(c => c.Date >= weekFrom);

        var byId = schedules.ToDictionary(s => s.Id);
        var used = cellsOfPeople.Where(c => chosen.Contains((c.ScheduleId, c.Date))).ToList();
        var history = Worked(used, byId);

        var notices = new List<string>();
        var week = $"{weekFrom:dd/MM} a {from.AddDays(-1):dd/MM}";
        if (!chosenInWeek)
        {
            notices.Add($"A semana anterior ({week}) não tem escala cadastrada na loja: a continuidade (dias seguidos, interjornada, " +
                        "folga isolada e domingos) não considera o que foi trabalhado nela.");
        }
        else
        {
            var withCells = used.Where(c => c.Date >= weekFrom).Select(c => c.EmployeeId).ToHashSet();
            var missing = employees.Where(e => !withCells.Contains(e.Key)).Select(e => e.Value).Order().ToList();
            if (missing.Count > 0)
            {
                var names = string.Join(", ", missing.Take(MaxNames)) + (missing.Count > MaxNames ? $" e mais {missing.Count - MaxNames}" : "");
                notices.Add($"A semana anterior ({week}) não tem escala cadastrada para {missing.Count} colaborador(es): {names}; " +
                            "a continuidade não considera o que trabalharam.");
            }
        }

        // Escalas principais e escalas ainda não publicadas que fornecem o histórico
        foreach (var s in used.Select(c => c.ScheduleId).Distinct().Select(id => byId[id]).Where(s => s.Principal || !s.Published)
                     .OrderBy(s => s.Start).ThenBy(s => s.SectorName))
        {
            var escala = $"{(s.Principal ? "a escala principal" : "a escala")} {s.SectorName} v{s.Version} de {s.Start:dd/MM} a {s.End:dd/MM} ({s.StatusLabel})";
            notices.Add(s.Published
                ? $"A semana anterior considera {escala}."
                : $"A semana anterior considera {escala}, ainda não publicada: se ela mudar, gere esta escala de novo.");
        }
        var rest = RestDays(schedules, cellsOfPeople, from.AddDays(-Math.Min(days, restWindow)), from.AddDays(-1), employees.Keys, notRest);
        return new ContinuityResult(history, notices, rest);
    }

    /// <summary>
    /// Continuidade com a semana SEGUINTE: dias trabalhados (qualquer setor da loja) em (<paramref name="to"/>,
    /// <paramref name="to"/> + <paramref name="days"/>], por setor e dia da escala que vale (principal &gt; status &gt; mais
    /// recente). Quem chama decide as candidatas (as mesmas da semana anterior: escalas vivas — Publicada, Aprovada,
    /// Gerada, Rascunho —, como na visão do mês).
    /// </summary>
    /// <param name="to">Último dia da escala que está sendo gerada.</param>
    public static List<HistoryEntry> Following(IReadOnlyList<ContinuitySchedule> schedules, IReadOnlyList<ContinuityCell> cells,
        DateOnly to, int days, IReadOnlyCollection<Guid> employees) =>
        FollowingWithNotices(schedules, cells, to, days, employees).History;

    /// <summary>
    /// <see cref="Following"/> com os avisos sobre a origem (como na semana anterior): a escala seguinte que fornece a
    /// continuidade é a principal ou ainda não foi publicada — se ela mudar, gere esta escala de novo.
    /// </summary>
    public static ContinuityResult FollowingWithNotices(IReadOnlyList<ContinuitySchedule> schedules, IReadOnlyList<ContinuityCell> cells,
        DateOnly to, int days, IReadOnlyCollection<Guid> employees)
    {
        var people = employees as IReadOnlySet<Guid> ?? employees.ToHashSet();
        var until = to.AddDays(days);
        var cellsOfPeople = cells.Where(c => people.Contains(c.EmployeeId) && c.Date > to && c.Date <= until).ToList();
        var chosen = ChooseByDay(schedules, cellsOfPeople.Select(c => (c.ScheduleId, c.Date)).ToHashSet(), to.AddDays(1), until);
        var byId = schedules.ToDictionary(s => s.Id);
        var used = cellsOfPeople.Where(c => chosen.Contains((c.ScheduleId, c.Date))).ToList();
        var notices = new List<string>();
        foreach (var s in used.Select(c => c.ScheduleId).Distinct().Select(id => byId[id]).Where(s => s.Principal || !s.Published)
                     .OrderBy(s => s.Start).ThenBy(s => s.SectorName))
        {
            var escala = $"{(s.Principal ? "a escala principal" : "a escala")} {s.SectorName} v{s.Version} de {s.Start:dd/MM} a {s.End:dd/MM} ({s.StatusLabel})";
            notices.Add(s.Published
                ? $"A semana seguinte considera {escala}."
                : $"A semana seguinte considera {escala}, ainda não publicada: se ela mudar, gere esta escala de novo.");
        }
        return new ContinuityResult(Worked(used, byId), notices);
    }
}

using Escala5x2.BuildingBlocks.Contracts.Compliance;
using Escala5x2.BuildingBlocks.Contracts.Planning;

namespace Escala5x2.BuildingBlocks.Compliance;

/// <summary>Jornada derivada de um dia (turnos automáticos): minutos pagos e intervalo; duração = pagos + intervalo.</summary>
public readonly record struct DayJourney(int Paid, int Break)
{
    public int Duration => Paid + Break;
}

/// <summary>
/// TURNOS AUTOMÁTICOS (<see cref="OperationSnapshot.TurnosAutomaticos"/>; caso relatado em 06/11/2026: "se as lojas já
/// possuem um horário de funcionamento, por que eu preciso definir um turno?"). Decisões:
/// <list type="bullet">
/// <item><b>Jornada do dia</b> (<see cref="DeriveJourney"/>): carga_horaria_semanal da semana civil (na virada, a mais
/// restritiva) ÷ dias de trabalho do regime (<see cref="WorkDaysPerWeek"/>: 5 no 5x2, 6 no 6x1), limitada pelo limite
/// diário do dia (<see cref="DailyLimit"/>, já com a compensação do 5x2) e, no domingo, pelo limite_domingo_minutos. Ex.:
/// 44h no 5x2 = 8h48; 42h = 8h24. Regras bloqueadas: sem carga semanal, o limite diário; sem nenhum dos dois, a jornada
/// personalizada guardada na operação.</item>
/// <item><b>Intervalo</b>: pelas regras de intervalo do dia (<see cref="IntervalPolicy"/>) — acima de
/// intervalo_obrigatorio_acima_minutos (6h), intervalo_padrao_minutos (ou o mínimo; 60 sem regra); acima de 4h,
/// intervalo_jornada_4a6h_minutos (15); até 4h, sem intervalo. A posição é escolhida pelo motor entre as legais.</item>
/// <item><b>Expediente curto</b> (domingo 07:00–13:00, feriado com horário especial): a maior jornada que cabe no expediente
/// com o intervalo que ela exige (6h → 5h45 + 15 min). Dias mais curtos somam menos na semana; a carga semanal continua
/// sendo teto (o avaliador mede igual).</item>
/// <item><b>Turnos</b> (<see cref="AutoShifts"/>): para cada expediente × jornada do horizonte, abertura começando na
/// abertura do dia ("AB 07:00"), fechamento terminando no fechamento ("FE 13:36") e intermediários a cada
/// <see cref="OperationSnapshot.IncrementoFlexivelMinutos"/> ("FX 07:30" ... até fechamento − duração − incremento), com
/// id determinístico (<see cref="OperationPlan.GeneratedId(Guid, int, int, int, string?)"/>). Perfis (Abertura,
/// Fechamento, Flexível) e o rodízio continuam como antes.</item>
/// <item><b>Uso por dia</b> (<see cref="OpenAt"/>): o turno automático só vale no dia cujo expediente o contém inteiro e cuja
/// jornada é a dele (a semana de 44h usa 8h48; a de 42h, 8h24). Turnos do catálogo ficam fora da operação (só manual).</item>
/// <item>Motor, verificação de configuração, avaliador, Scheduling.Api (fotografia), Store.Api (prévia na tela) e
/// Dimensionamento.Api usam esta mesma conta.</item>
/// </list>
/// </summary>
public sealed partial class PlanningContext
{
    /// <summary>Jornada de cada dia do horizonte com turnos automáticos (null = loja fechada ou nenhuma jornada cabe; vazio = personalizados).</summary>
    public DayJourney?[] DayJourneys { get; init; } = [];

    /// <summary>A operação usa turnos automáticos (<see cref="OperationSnapshot.TurnosAutomaticos"/>).</summary>
    public bool AutomaticShifts => Operation?.TurnosAutomaticos == true;

    /// <summary>Jornada automática do dia (null = sem turnos automáticos, loja fechada ou nenhuma jornada cabe).</summary>
    public DayJourney? JourneyAt(int day) => day >= 0 && day < DayJourneys.Length ? DayJourneys[day] : null;

    /// <summary>Turno gerado pelos turnos automáticos (com tipo; os do catálogo ficam sem tipo).</summary>
    public bool IsAutomatic(ShiftInfo t) => AutomaticShifts && t.Snapshot.Gerado && t.Kind is not null;

    /// <summary>Jornada-alvo da semana (minutos pagos): carga semanal ÷ dias de trabalho do regime. null = carga bloqueada.</summary>
    public int? WeeklyJourney(int week) =>
        WeeklyLimit(week) is { } load ? load.Value / Math.Max(1, WorkDaysPerWeek(week)) : null;

    /// <summary>Jornada do dia antes de caber no expediente (ver PlanningContext.AutoShifts): alvo da semana, limite diário e de domingo.</summary>
    private int? TargetJourney(int day)
    {
        var target = WeeklyJourney(day / 7);
        if (DailyLimit(day) is { } daily) target = target is { } t ? Math.Min(t, daily.Value) : daily.Value;
        if (IsSunday(day) && SundayLimit(day) is { } sunday) target = target is { } s ? Math.Min(s, sunday.Value) : sunday.Value;
        // Sem carga semanal nem limite diário (regras bloqueadas): a jornada personalizada guardada na operação
        if (target is null && Operation is { } op) target = op.DuracaoJornadaMinutos - (op.IntervaloMinutos ?? 60);
        return target;
    }

    /// <summary>
    /// Faixas de intervalo pelas regras do dia: (intervalo, jornada paga mínima, máxima). Acima do limite de 6h, o padrão
    /// (ou o mínimo); acima de 4h, o dos 4h a 6h; até 4h, nenhum.
    /// </summary>
    private static IEnumerable<(int Break, int From, int To)> BreakTiers(IntervalPolicy policy)
    {
        var threshold = policy.Threshold;
        yield return (policy.Rule(RuleKeys.IntervaloPadrao)?.Value ?? policy.Rule(RuleKeys.IntervaloMinimo)?.Value ?? 60, threshold + 1, int.MaxValue);
        yield return (policy.Rule(RuleKeys.IntervaloJornada4a6h)?.Value ?? 15, IntervalPolicy.ShortJourneyFromMinutes + 1, threshold);
        yield return (0, 1, IntervalPolicy.ShortJourneyFromMinutes);
    }

    /// <summary>
    /// Jornada automática do dia: a maior jornada até o alvo (<see cref="TargetJourney"/>) que cabe no expediente do dia com
    /// o intervalo que as regras exigem para ela. null = loja fechada, sem expediente ou nada cabe.
    /// </summary>
    internal DayJourney? DeriveJourney(int day)
    {
        if (day < 0 || day >= DayWindows.Length || DayWindows[day] is not { } w || w.Close <= w.Open) return null;
        if (TargetJourney(day) is not { } target || target <= 0) return null;
        var length = w.Close - w.Open;
        DayJourney? best = null;
        foreach (var (brk, from, to) in BreakTiers(Interval(day)))
        {
            var paid = Math.Min(Math.Min(target, to), length - brk);
            if (paid < from) continue;
            if (best is not { } b || paid > b.Paid) best = new DayJourney(paid, brk);
        }
        return best;
    }

    private static string H(int minutes) => minutes % 60 == 0 ? $"{minutes / 60}h" : $"{minutes / 60}h{minutes % 60:00}";

    /// <summary>
    /// De onde vem a jornada automática do dia, para a prévia e as mensagens: "42h ÷ 5 dias (5x2)", "limite diário 8h",
    /// "limite de domingo 8h", "expediente de 6h".
    /// </summary>
    public string JourneyBasis(int day)
    {
        if (JourneyAt(day) is not { } j) return DayWindows.Length > day && day >= 0 && DayWindows[day] is null ? "loja fechada" : "nenhuma jornada cabe no expediente";
        var week = day / 7;
        var parts = new List<string>();
        if (WeeklyLimit(week) is { } load)
            parts.Add($"{H(load.Value)} ÷ {WorkDaysPerWeek(week)} dias{(Regime(week) is { } r ? $" ({r.Name})" : "")} = {H(WeeklyJourney(week)!.Value)}");
        var target = TargetJourney(day);
        if (DailyLimit(day) is { } daily && daily.Value == target && WeeklyJourney(week) is { } wj && daily.Value < wj)
            parts.Add($"limite diário {H(daily.Value)}");
        if (IsSunday(day) && SundayLimit(day) is { } sunday && sunday.Value == target && (WeeklyJourney(week) ?? int.MaxValue) > sunday.Value)
            parts.Add($"limite de domingo {H(sunday.Value)}");
        if (target is { } t && j.Paid < t && DayWindows[day] is { } w)
            parts.Add($"expediente de {H(w.Close - w.Open)}");
        return string.Join("; ", parts);
    }

    /// <summary>Jornadas do horizonte pelas regras (contexto só com regras, expediente e regime: sem pessoas nem turnos).</summary>
    private static DayJourney?[] Journeys(PlanningSnapshot snap, OperationSnapshot operation, DateOnly start, int weeks, DateOnly periodStart,
        DateOnly periodEnd, DateOnly[] dates, SectorSnapshot[] sectors, RuleBook rules, StoreDays storeDays)
    {
        var probe = new PlanningContext
        {
            Snapshot = snap, Start = start, Weeks = weeks, PeriodStart = periodStart, PeriodEnd = periodEnd, Dates = dates, Sectors = sectors,
            Shifts = [], Employees = [], Rules = rules, DayBlocked = new bool[dates.Length], Slots = [],
            StoreHasExtendedDayAgreement = snap.StoreHasExtendedDayAgreement, Fixed = [], Warnings = [], Operation = operation,
            DayClosed = storeDays.Closed, DayWindows = storeDays.Windows
        };
        return Enumerable.Range(0, dates.Length).Select(probe.DeriveJourney).ToArray();
    }

    /// <summary>
    /// Turnos da fotografia com turnos automáticos (ver PlanningContext.AutoShifts): os automáticos de cada expediente ×
    /// jornada do horizonte (com tipo) e os demais da fotografia SEM tipo (catálogo e turnos antigos: só manual). Idempotente
    /// (os automáticos que já estão na fotografia ficam, com o mesmo id e código).
    /// </summary>
    internal static List<ShiftSnapshot> AutoShifts(OperationSnapshot op, Guid sectorId, IReadOnlyList<ShiftSnapshot> shifts,
        (int Open, int Close)?[] windows, DayJourney?[] journeys)
    {
        var generated = new List<(Guid Id, int Start, int End, int Break, string Kind, string Prefix)>();
        var kinds = new Dictionary<Guid, string>();
        void Add(int start, int end, int brk, string kind, string prefix)
        {
            var id = OperationPlan.GeneratedId(sectorId, start, end, brk, kind);
            if (kinds.TryAdd(id, kind)) generated.Add((id, start, end, brk, kind, prefix));
        }

        var increment = op.IncrementoFlexivelMinutos > 0 ? op.IncrementoFlexivelMinutos : 30;
        var combos = windows.Zip(journeys)
            .Where(x => x.First is not null && x.Second is not null)
            .Select(x => (Window: x.First!.Value, Journey: x.Second!.Value))
            .Distinct().ToList();
        foreach (var (w, j) in combos)
        {
            var d = j.Duration;
            Add(w.Open, w.Open + d, j.Break, JourneyProfiles.Abertura, "AB");
            Add(w.Close - d, w.Close, j.Break, JourneyProfiles.Fechamento, "FE");
            for (var e = w.Open + increment; e + d <= w.Close - increment; e += increment)
                Add(e, e + d, j.Break, JourneyProfiles.Flexivel, "FX");
        }

        var list = shifts.Select(s => kinds.TryGetValue(s.ShiftId, out var kind) ? s with { Tipo = kind, Gerado = true } : s with { Tipo = null })
            .ToList();
        var codes = list.Select(s => s.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var present = list.Select(s => s.ShiftId).ToHashSet();
        foreach (var g in generated.Where(g => !present.Contains(g.Id)))
        {
            // Mesma entrada com outra jornada (semana de 44h e de 42h no mesmo mês): o código leva a saída
            var code = $"{g.Prefix} {OperationPlan.Hm(g.Start)}";
            if (codes.Contains(code)) code = $"{g.Prefix} {OperationPlan.Hm(g.Start)}-{OperationPlan.Hm(g.End)}";
            for (var n = 2; codes.Contains(code); n++) code = $"{g.Prefix} {OperationPlan.Hm(g.Start)}-{OperationPlan.Hm(g.End)}#{n}";
            codes.Add(code);
            list.Add(new ShiftSnapshot(g.Id, code, OperationPlan.Time(g.Start), OperationPlan.Time(g.End), g.Break, g.Kind, true));
        }
        return list.OrderBy(s => OperationPlan.Min(s.Start)).ThenBy(s => s.Code).ToList();
    }

    /// <summary>
    /// O turno automático vale no dia: o expediente do dia o contém inteiro, a jornada e o intervalo são os do dia
    /// (<see cref="DayJourneys"/>), a abertura começa na abertura e o fechamento termina no fechamento.
    /// </summary>
    private bool AutomaticOpenAt(int day, ShiftInfo t)
    {
        if (day < 0 || day >= DayWindows.Length || DayWindows[day] is not { } w || JourneyAt(day) is not { } j) return false;
        if (t.PaidMinutes != j.Paid || t.Snapshot.BreakMinutes != j.Break || t.StartMin < w.Open || t.EndMin > w.Close) return false;
        return t.Kind switch
        {
            JourneyProfiles.Abertura => t.StartMin == w.Open,
            JourneyProfiles.Fechamento => t.EndMin == w.Close,
            _ => true
        };
    }

    /// <summary>
    /// Turnos da fotografia de um setor com a operação, como o motor os usa: turnos automáticos (pelo expediente de cada dia
    /// e pelas regras vigentes do período) ou, com "Personalizar turnos", os do catálogo classificados + os gerados da
    /// operação (<see cref="OperationPlan.Expand"/>, com o intervalo <paramref name="defaultBreak"/> e os expedientes
    /// especiais de feriado). Usado pelo Scheduling.Api ao montar/atualizar a fotografia.
    /// </summary>
    public static List<ShiftSnapshot> OperationShifts(OperationSnapshot? op, Guid sectorId, IReadOnlyList<ShiftSnapshot> shifts,
        IReadOnlyList<ComplianceRuleSnapshot> rules, IReadOnlyList<HolidaySnapshot> holidays, DateOnly from, DateOnly to, bool storeAgreement,
        int defaultBreak)
    {
        if (op is null) return shifts.ToList();
        if (!op.TurnosAutomaticos) return OperationPlan.Expand(op, sectorId, shifts, defaultBreak, HolidayWindows(holidays, op, from, to));
        return ForOperation(op, sectorId, shifts, rules, holidays, from, to, storeAgreement).Snapshot.Shifts.ToList();
    }

    /// <summary>
    /// Contexto só com a operação de um setor, as regras e os feriados do período (sem colaboradores): expediente, jornada e
    /// turnos de cada dia exatamente como o motor os monta. Prévia dos turnos automáticos (Store.Api), fotografia
    /// (Scheduling.Api) e dimensionamento.
    /// </summary>
    public static PlanningContext ForOperation(OperationSnapshot op, Guid sectorId, IReadOnlyList<ShiftSnapshot> shifts,
        IReadOnlyList<ComplianceRuleSnapshot> rules, IReadOnlyList<HolidaySnapshot> holidays, DateOnly from, DateOnly to, bool storeAgreement,
        string sectorName = "Setor") =>
        Create(new PlanningSnapshot(Guid.Empty, string.Empty, storeAgreement, [new SectorSnapshot(sectorId, sectorName)], shifts, [], holidays, [],
            null, [], rules, Operacao: op), from, to);

    /// <summary>Turnos com tipo que valem no dia (expediente, jornada do dia): a prévia "Abertura 07:00–15:24".</summary>
    public List<ShiftInfo> ShiftsOfDay(int day) =>
        IsClosed(day) || DayBlocked[day] ? [] : Shifts.Where(t => t.Kind is not null && OpenAt(day, t)).OrderBy(t => t.StartMin).ToList();
}

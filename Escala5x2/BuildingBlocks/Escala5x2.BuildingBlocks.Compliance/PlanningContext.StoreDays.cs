using Escala5x2.BuildingBlocks.Contracts.Planning;

namespace Escala5x2.BuildingBlocks.Compliance;

/// <summary>
/// Funcionamento da loja por dia e picos manuais (cadastro da loja no Store.Api):
/// <list type="bullet">
/// <item><b>Dia fechado</b> (<see cref="DayClosed"/>): dia da semana em que a loja não abre (horário de funcionamento) ou
/// feriado com funcionamento Fechada. Sem faixas de cobertura e ninguém escalado (célula manual é mantida e o avaliador
/// aponta). Feriado fechado também é dia não trabalhável (<see cref="DayBlocked"/>): não conta como folga.</item>
/// <item><b>Expediente do dia</b> (<see cref="DayWindows"/>, só com operação): o horário da loja no dia da semana ou, no
/// feriado com horário especial, o cruzamento com ele. As faixas de cobertura e os mínimos de abertura/fechamento seguem o
/// expediente do dia; com expediente por dia (<see cref="HasDayWindows"/>), o turno só pode COMEÇAR dentro dele.</item>
/// <item><b>Pico manual</b> (<see cref="DemandSnapshot.PicoManual"/>): na faixa, o alvo é o valor cadastrado (substitui a
/// demanda comum; pico de uma data prevalece sobre o do dia da semana), nunca abaixo do mínimo rígido; acima do mínimo é
/// PREFERENCIAL como o reforço de pico (<see cref="CoverageSlot.ManualPeak"/>).</item>
/// </list>
/// Fotografias antigas (sem horário/funcionamento/picos) seguem exatamente o comportamento anterior.
/// </summary>
public sealed partial class PlanningContext
{
    /// <summary>Loja fechada no dia (horário da semana ou feriado fechado). Vazio = fotografia sem o dado.</summary>
    public bool[] DayClosed { get; init; } = [];

    /// <summary>Expediente efetivo de cada dia (minutos do dia) com operação; null = fechado ou sem operação.</summary>
    public (int Open, int Close)?[] DayWindows { get; init; } = [];

    /// <summary>Há expediente diferente por dia (horário da loja cadastrado ou horário especial de feriado).</summary>
    public bool HasDayWindows { get; init; }

    /// <summary>A loja está fechada no dia (índice do horizonte).</summary>
    public bool IsClosed(int day) => day >= 0 && day < DayClosed.Length && DayClosed[day];

    /// <summary>
    /// O expediente do dia permite o turno: sem expediente por dia, sempre; com ele, o turno começa dentro do expediente
    /// (ninguém entra antes de a loja abrir nem depois de fechar), o de abertura começa na abertura DO DIA e o de
    /// fechamento termina no fechamento do dia (turnos de abertura/fechamento de outros expedientes ficam fora). Turno
    /// automático: só no dia cujo expediente o contém e cuja jornada é a dele (PlanningContext.AutoShifts).
    /// </summary>
    public bool OpenAt(int day, ShiftInfo t)
    {
        if (IsAutomatic(t)) return AutomaticOpenAt(day, t);
        if (!HasDayWindows || day < 0 || day >= DayWindows.Length) return true;
        if (DayWindows[day] is not { } w || t.StartMin < w.Open || t.StartMin >= w.Close) return false;
        return t.Kind switch
        {
            JourneyProfiles.Abertura => t.StartMin == w.Open,
            JourneyProfiles.Fechamento => t.EndMin == w.Close,
            _ => true
        };
    }

    /// <summary>Texto do expediente do dia para mensagens ("loja fechada", "expediente 08:00–13:00").</summary>
    public string DayHoursText(int day) =>
        IsClosed(day) ? "loja fechada" : day >= 0 && day < DayWindows.Length && DayWindows[day] is { } w
            ? $"expediente {OperationPlan.Hm(w.Open)}–{OperationPlan.Hm(w.Close)}"
            : "expediente normal";

    /// <summary>Dias fechados, feriados fechados (não trabalháveis), expedientes por dia e janelas especiais de feriado.</summary>
    internal sealed record StoreDays(bool[] Closed, bool[] HolidayClosed, (int Open, int Close)?[] Windows, bool PerDay,
        List<(int Open, int Close)> HolidayWindows);

    internal static StoreDays BuildStoreDays(PlanningSnapshot snap, OperationSnapshot? op, DateOnly[] dates) => BuildStoreDays(snap.Holidays, op, dates);

    /// <summary>
    /// Expedientes especiais de feriado (abertura/fechamento) do horizonte do período, como o motor os usa para gerar os
    /// turnos de abertura/fechamento desses dias (<see cref="OperationPlan.Expand"/>, <c>extraWindows</c>). O Scheduling.Api
    /// expande os turnos da fotografia com eles: assim os turnos que o motor usa no feriado existem na fotografia (antes
    /// eram gerados só no motor e a célula era descartada ao gravar o resultado).
    /// </summary>
    public static List<(int Open, int Close)> HolidayWindows(IReadOnlyList<HolidaySnapshot> holidays, OperationSnapshot? op, DateOnly periodStart, DateOnly periodEnd)
    {
        if (op is null || periodEnd < periodStart) return [];
        var (start, weeks) = Horizon(periodStart, periodEnd);
        return BuildStoreDays(holidays, op, Enumerable.Range(0, weeks * 7).Select(start.AddDays).ToArray()).HolidayWindows;
    }

    private static StoreDays BuildStoreDays(IReadOnlyList<HolidaySnapshot> holidays, OperationSnapshot? op, DateOnly[] dates)
    {
        var closed = new bool[dates.Length];
        var holidayClosed = new bool[dates.Length];
        var windows = new (int Open, int Close)?[dates.Length];
        var holidayWindows = new List<(int Open, int Close)>();
        var perDay = op?.Horarios is { Count: > 0 };
        var byDate = holidays.GroupBy(h => h.Date).ToDictionary(g => g.Key, g => g.ToList());
        for (var d = 0; d < dates.Length; d++)
        {
            var hs = byDate.GetValueOrDefault(dates[d]) ?? [];
            holidayClosed[d] = hs.Any(h => h.Funcionamento == nameof(FuncionamentoDia.Fechada));
            var window = op is null ? null : OperationPlan.DayWindow(op, dates[d].DayOfWeek);
            closed[d] = holidayClosed[d] || (op is not null && window is null);
            foreach (var h in hs.Where(h => h.Funcionamento == nameof(FuncionamentoDia.Especial) && h.Abertura is not null && h.Fechamento is not null))
            {
                perDay = true;
                var (from, to) = (ToMinutes(h.Abertura!.Value), ToMinutes(h.Fechamento!.Value));
                if (window is { } w) window = (Math.Max(w.Open, from), Math.Min(w.Close, to));
                if (window is { } x && x.Open >= x.Close)
                {
                    window = null;
                    closed[d] = true;
                }
                if (op is null && !closed[d]) windows[d] = (from, to); // sem operação: só recorta a demanda
            }
            if (op is not null) windows[d] = closed[d] ? null : window;
            if (op is not null && windows[d] is { } hw && hs.Any(h => h.Funcionamento == nameof(FuncionamentoDia.Especial)))
                holidayWindows.Add(hw);
        }
        return new StoreDays(closed, holidayClosed, windows, perDay, holidayWindows.Distinct().ToList());
    }

    /// <summary>A linha (demanda comum ou pico manual) vale para a data?</summary>
    private static bool RowApplies(DemandSnapshot x, DateOnly date) =>
        x.PicoManual && x.Data is { } only ? only == date : x.DayOfWeek == date.DayOfWeek;

    /// <summary>
    /// Alvo da faixa [<paramref name="start"/>, <paramref name="end"/>) na data, com as linhas do setor: a maior demanda
    /// comum que toca a faixa e, havendo pico manual que toca a faixa (os da data prevalecem sobre os do dia da semana),
    /// o valor do pico (quantidade cadastrada, ou a demanda/mínimo quando só há adicional, mais o adicional) no lugar da
    /// demanda. Nunca abaixo de <paramref name="minimum"/>. Devolve também o rótulo do pico (null = sem pico manual).
    /// </summary>
    internal static (int Target, string? Peak) SlotTarget(IEnumerable<DemandSnapshot> rows, DateOnly date, int start, int end, int minimum)
    {
        var touching = rows.Where(x => RowApplies(x, date) && ToMinutes(x.Start) < end && ToMinutes(x.End) > start).ToList();
        var common = touching.Where(x => !x.PicoManual).Select(x => x.MinStaff).DefaultIfEmpty(0).Max();
        var peaks = touching.Where(x => x.PicoManual).ToList();
        if (peaks.Any(x => x.Data is not null)) peaks = peaks.Where(x => x.Data is not null).ToList();
        if (peaks.Count == 0) return (Math.Max(minimum, common), null);
        var basis = Math.Max(minimum, common);
        var best = peaks.Select(p => (Row: p, Value: (p.MinStaff > 0 ? p.MinStaff : basis) + Math.Max(0, p.Adicional ?? 0)))
            .OrderByDescending(p => p.Value).First();
        return (Math.Max(minimum, best.Value), best.Row.Rotulo ?? ManualPeaks.Label(best.Row));
    }

    /// <summary>
    /// Modelo anterior (sem operação): aplica os picos manuais às faixas do dia já montadas a partir da demanda comum (o
    /// pico substitui a demanda nas faixas que toca, inclusive onde não havia demanda) e recorta o dia ao horário especial
    /// do feriado (<paramref name="window"/>).
    /// </summary>
    internal static void ApplyDayRules(Dictionary<(int Sector, int Day, int Slot), (int Required, int Size, bool Rigid)> required,
        Dictionary<(int Sector, int Day, int Slot), string> peakLabels, PlanningSnapshot snap, Dictionary<Guid, int> sectorIndex,
        DateOnly date, int d, int size, bool rigid, (int Open, int Close)? window)
    {
        foreach (var bySector in snap.Demands.Where(x => x.PicoManual && sectorIndex.ContainsKey(x.SectorId) && RowApplies(x, date))
                     .GroupBy(x => sectorIndex[x.SectorId]))
        {
            var s = bySector.Key;
            var rows = snap.Demands.Where(x => sectorIndex.GetValueOrDefault(x.SectorId, -1) == s).ToList();
            var from = bySector.Min(x => ToMinutes(x.Start)) / size * size;
            var to = bySector.Max(x => ToMinutes(x.End));
            for (var slot = from; slot < to; slot += size)
            {
                var (target, peak) = SlotTarget(rows, date, slot, slot + size, 0);
                if (peak is null) continue;
                required[(s, d, slot)] = (target, size, rigid);
                peakLabels[(s, d, slot)] = peak;
            }
        }
        if (window is not { } w) return;
        foreach (var key in required.Keys.Where(k => k.Day == d && (k.Slot < w.Open / size * size || k.Slot >= w.Close)).ToList())
            required.Remove(key);
    }
}

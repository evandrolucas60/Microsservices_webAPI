using Escala5x2.BuildingBlocks.Contracts.Compliance;
using Escala5x2.BuildingBlocks.Contracts.Planning;

namespace Escala5x2.BuildingBlocks.Compliance;

/// <summary>Problema de configuração de um setor no período (dias do horizonte, texto para o diagnóstico).</summary>
public sealed record ConfigurationIssue(int Sector, IReadOnlyList<int> Days, string Text);

/// <summary>
/// CONFIGURAÇÃO SEM TURNO POSSÍVEL (caso relatado em novembro/2026: escala aprovada com FOLGA em todos os dias de todos do
/// Açougue e "Conformidade 100%"). Decisões:
/// <list type="bullet">
/// <item><b>Turno que cabe no dia</b> (<see cref="ShiftFitsDay"/>): pela CONFIGURAÇÃO, independente de quem — tem tipo da
/// operação (com operação), começa dentro do expediente do dia, tem posição legal de intervalo e algum colaborador do
/// setor pode fazê-lo pelas regras rígidas por turno (limite diário, domingo, duração do intervalo). Perfil de jornada
/// (falta de gente com o perfil: diagnóstico "perfil"), ausência, outro setor e disponibilidade semanal são da PESSOA e
/// não entram.</item>
/// <item><b>Dia aberto sem nenhum turno que caiba</b> (loja aberta, dia do período): é erro de configuração (horário da loja
/// × operação × regras — ex.: jornada de 9h48 com 1h de intervalo = 8h48 pagas contra o limite diário de 8h). NUNCA é
/// "não trabalho forçado" (<see cref="ForcedOff"/>): antes, esse dia virava folga inevitável para todos e a escala saía
/// com todo mundo de folga o mês inteiro. Agora a geração falha com o diagnóstico (<see cref="ConfigurationIssues"/>,
/// tipo "configuracao") e o avaliador conta as folgas como sempre (a escala antiga deixa de aparecer conforme).</item>
/// <item><b>Faixa que nenhum turno possível cobre</b> (mínimo &gt; 0): também é configuração (ex.: expediente começa
/// antes do primeiro turno possível) — com a cobertura como meta ela ficaria descoberta todo dia em silêncio.</item>
/// <item><b>Setor sem nenhuma faixa de cobertura</b> no período com a loja aberta (sem operação e sem demanda por faixa):
/// o motor não tem o que cobrir e a escala sairia com qualquer turno (ex.: todos no mesmo horário) e 100%. É
/// configuração: cadastre a operação da loja (ou a demanda do setor).</item>
/// <item>Restrição por PESSOA (perfil sem rodízio, disponibilidade, regra por turno só dela) continua sendo dia de não
/// trabalho forçado, neutro para max_folgas_seguidas (PlanningContext.DaysOff).</item>
/// </list>
/// </summary>
public sealed partial class PlanningContext
{
    private readonly Dictionary<(int Sector, int Day), bool> _sectorDayShift = [];

    /// <summary>Colaboradores do setor (com o setor na fotografia).</summary>
    private IEnumerable<EmployeeInfo> SectorPeople(int sector) => Employees.Where(e => e.Sectors.Contains(sector));

    /// <summary>
    /// O turno cabe no dia do setor pela configuração (ver <see cref="PlanningContext"/>, CONFIGURAÇÃO SEM TURNO POSSÍVEL):
    /// tipo da operação, expediente do dia, posição legal do intervalo e algum colaborador do setor sem regra rígida por
    /// turno violada (contrato: controle de jornada, acordo de prorrogação). Não olha perfil de jornada (falta de gente com
    /// o perfil tem o diagnóstico próprio, "perfil"), ausências, outro setor nem disponibilidade semanal.
    /// </summary>
    public bool ShiftFitsDay(int sector, int day, ShiftInfo t) =>
        (!OperationMode || t.Kind is not null) && OpenAt(day, t)
        && (t.Snapshot.BreakMinutes == 0 || BreakOptions(day, t, t.Snapshot.BreakMinutes).Count > 0)
        && SectorPeople(sector).Any(e => !ShiftIssues(e, day, t).Any(i => i.Rigid));

    /// <summary>Algum turno cabe no dia do setor (<see cref="ShiftFitsDay"/>).</summary>
    public bool SectorHasShift(int sector, int day)
    {
        if (!_sectorDayShift.TryGetValue((sector, day), out var any))
            _sectorDayShift[(sector, day)] = any = Shifts.Any(t => ShiftFitsDay(sector, day, t));
        return any;
    }

    /// <summary>Dia do período em que a loja abre e precisa de gente (não é feriado sem autorização/fechado).</summary>
    public bool OpenWorkDay(int day) => InPeriod(day) && !IsClosed(day) && !DayBlocked[day];

    /// <summary>
    /// Dia do período, com a loja aberta, em que NENHUM turno cabe nos setores da pessoa: erro de configuração, não "não
    /// trabalho forçado" (<see cref="ForcedOff"/> devolve false; a geração falha com <see cref="ConfigurationIssues"/>).
    /// </summary>
    public bool NoShiftConfigured(EmployeeInfo e, int day) =>
        e.Sectors.Length > 0 && OpenWorkDay(day) && !e.Sectors.Any(s => SectorHasShift(s, day));

    private static string Hours(int minutes) => minutes % 60 == 0 ? $"{minutes / 60}h" : $"{minutes / 60}h{minutes % 60:00}";

    /// <summary>Expediente do dia para as mensagens: "loja 07:00–21:00" (sem operação: "sem operação cadastrada").</summary>
    public string DayWindowText(int day)
    {
        if (Operation is not { } op) return "sem operação cadastrada";
        var (open, close) = day >= 0 && day < DayWindows.Length && DayWindows[day] is { } w ? w : (ToMinutes(op.Abertura), ToMinutes(op.Fechamento));
        return $"loja {OperationPlan.Hm(open)}–{OperationPlan.Hm(close)}";
    }

    private static readonly HashSet<string> DailyKeys =
        [RuleKeys.LimiteDiario, RuleKeys.LimiteDiarioComAcordo, RuleKeys.HoraExtraMaxDiaria, RuleKeys.LimiteDomingo, RuleKeys.CompensacaoJornada5x2];

    /// <summary>Por que nenhum turno cabe no dia do setor (texto curto, sem nomes de colaboradores).</summary>
    private string NoShiftReason(int sector, int day)
    {
        // Turnos automáticos: o expediente do dia não comporta nenhuma jornada pelas regras vigentes
        if (AutomaticShifts && JourneyAt(day) is null)
            return "o expediente do dia não comporta nenhuma jornada pelas regras vigentes (carga semanal, limite diário, intervalo)";
        var typed = Shifts.Where(t => !OperationMode || t.Kind is not null).ToList();
        if (typed.Count == 0)
            return OperationMode ? "a operação não tem turnos de abertura, fechamento nem entradas flexíveis" : "a loja não tem turnos ativos";
        var open = typed.Where(t => OpenAt(day, t)).ToList();
        if (open.Count == 0)
            return $"nenhum turno começa dentro do expediente do dia (os turnos começam entre {OperationPlan.Hm(typed.Min(t => t.StartMin))} e " +
                   $"{OperationPlan.Hm(typed.Max(t => t.StartMin))})";
        var people = SectorPeople(sector).ToList();
        var withBreak = open.Where(t => t.Snapshot.BreakMinutes == 0 || BreakOptions(day, t, t.Snapshot.BreakMinutes).Count > 0).ToList();
        if (withBreak.Count == 0) return "nenhum turno tem posição legal para o intervalo (regras de intervalo)";
        var issues = withBreak
            .SelectMany(t => people.SelectMany(e => ShiftIssues(e, day, t).Where(i => i.Rigid)).Select(i => (T: t, I: i)))
            .GroupBy(x => x.I.Key).OrderBy(g => g.Key).ToList();
        if (issues.Count == 0) return "nenhum turno passa nas regras rígidas do dia";
        return string.Join("; ", issues.Select(g =>
        {
            var shortest = g.MinBy(x => x.T.PaidMinutes);
            if (!DailyKeys.Contains(g.Key))
                return $"a regra rígida {g.Key} proíbe os turnos ({Hours(shortest.T.Duration)} com {shortest.T.Snapshot.BreakMinutes} min de intervalo)";
            var limit = g.Max(x => x.I.Limit);
            var what = g.Key switch
            {
                RuleKeys.LimiteDomingo => "o limite de domingo",
                RuleKeys.CompensacaoJornada5x2 => "o limite diário com a compensação do 5x2 (carga semanal ÷ dias de trabalho)",
                _ => "o limite diário"
            };
            return $"os turnos têm pelo menos {Hours(shortest.T.PaidMinutes)} de jornada ({Hours(shortest.T.Duration)} com o intervalo) e {what} é " +
                   $"{Hours(limit)} ({g.Key})";
        }));
    }

    /// <summary>"03/11 (ter)" ou "26 dia(s): 03/11, 04/11, ..." para as mensagens.</summary>
    public string DaysText(IReadOnlyList<int> days) => days.Count == 1
        ? $"de {Dates[days[0]]:dd/MM} ({WeekdayShort(Dates[days[0]].DayOfWeek)})"
        : $"de {days.Count} dia(s) ({string.Join(", ", days.Take(7).Select(d => Dates[d].ToString("dd/MM")))}{(days.Count > 7 ? "..." : "")})";

    /// <summary>
    /// Problemas de configuração do período, por setor (ver CONFIGURAÇÃO SEM TURNO POSSÍVEL): dias abertos sem nenhum turno
    /// que caiba, faixas com mínimo que nenhum turno possível cobre e setor sem nenhuma faixa de cobertura. Vazio = ok.
    /// Só setores com colaboradores; dias com a loja fechada ou feriado sem autorização não entram.
    /// </summary>
    public List<ConfigurationIssue> ConfigurationIssues()
    {
        var list = new List<ConfigurationIssue>();
        for (var s = 0; s < Sectors.Length; s++)
        {
            var sector = s;
            if (!SectorPeople(s).Any()) continue;
            var name = Sectors[s].Name;
            var openDays = Enumerable.Range(FirstDay, PeriodDays).Where(OpenWorkDay).ToList();
            if (openDays.Count == 0) continue;

            // Dias abertos sem nenhum turno que caiba
            var noShift = openDays.Where(d => !SectorHasShift(sector, d)).ToList();
            // (o grupo com mais dias primeiro: é o que o resumo mostra)
            foreach (var g in noShift.GroupBy(d => (Window: DayWindowText(d), Why: NoShiftReason(sector, d))).OrderByDescending(g => g.Count()))
            {
                var days = g.ToList();
                list.Add(new ConfigurationIssue(s, days,
                    $"Setor {name}: nenhum turno cabe no expediente {DaysText(days)} ({g.Key.Window}): {g.Key.Why}. " +
                    (AutomaticShifts
                        ? "Os turnos são automáticos (horário da loja + regras de conformidade): confira o horário da loja em Cadastro da loja " +
                          "e as regras de jornada e intervalo vigentes; "
                        : "Confira o horário da loja em Cadastro da loja e os turnos personalizados da operação (duração da jornada, intervalo e " +
                          "entradas flexíveis) em Lojas → Operação, ou volte aos turnos automáticos; ") +
                    "com isso a escala sairia com todos de folga."));
            }

            // Faixas com mínimo que nenhum turno possível cobre (nos dias que têm turno)
            var uncovered = new List<(int Day, string Runs)>();
            foreach (var day in Slots.Where(x => x.Sector == sector && x.Minimum > 0 && OpenWorkDay(x.Day) && !noShift.Contains(x.Day)).GroupBy(x => x.Day))
            {
                var fits = Shifts.Where(t => ShiftFitsDay(sector, day.Key, t)).ToList();
                var runs = new List<(int From, int To)>();
                foreach (var slot in day.OrderBy(x => x.SlotStart).Where(x => !fits.Any(t => Covers(t, x))))
                {
                    if (runs.Count > 0 && runs[^1].To == slot.SlotStart) runs[^1] = (runs[^1].From, slot.SlotEnd);
                    else runs.Add((slot.SlotStart, slot.SlotEnd));
                }
                if (runs.Count > 0)
                    uncovered.Add((day.Key, string.Join(", ", runs.Select(r => $"das {OperationPlan.Hm(r.From)} às {OperationPlan.Hm(r.To)}"))));
            }
            foreach (var g in uncovered.GroupBy(x => x.Runs))
            {
                var days = g.Select(x => x.Day).ToList();
                list.Add(new ConfigurationIssue(s, days,
                    $"Setor {name}: nenhum turno possível cobre {g.Key} {DaysText(days)} ({DayWindowText(days[0])}). O expediente pede gente nesse " +
                    (AutomaticShifts
                        ? "horário: confira o horário da loja em Cadastro da loja e as regras de intervalo vigentes (posição do intervalo)."
                        : "horário: confira o horário da loja em Cadastro da loja e os turnos/entradas da operação em Lojas → Operação.")));
            }

            // Sem nenhuma faixa de cobertura no período (sem operação e sem demanda por faixa)
            if (!Slots.Any(x => x.Sector == sector && InPeriod(x.Day)) && noShift.Count == 0)
                list.Add(new ConfigurationIssue(s, openDays,
                    $"Setor {name} sem cobertura configurada no período: " +
                    (OperationMode
                        ? "a operação não gera faixas de cobertura nos dias abertos. "
                        : "a loja não tem operação cadastrada (Lojas → Operação) e o setor não tem demanda por faixa. ") +
                    "Sem isso o motor não sabe quantas pessoas são necessárias em cada horário e a escala sairia com qualquer turno (ex.: todos no mesmo horário)."));
        }
        return list;
    }
}

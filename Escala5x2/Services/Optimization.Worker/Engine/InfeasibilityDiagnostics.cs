using Escala5x2.BuildingBlocks.Compliance;
using Escala5x2.BuildingBlocks.Contracts.Compliance;
using Escala5x2.BuildingBlocks.Contracts.Planning;
using Escala5x2.BuildingBlocks.Contracts.Scheduling;
using Google.OrTools.Sat;
using System.Runtime.CompilerServices;

namespace Optimization.Worker.Engine;

/// <summary>
/// Diagnóstico compreensível de inviabilidade.
/// <list type="number">
/// <item><b>Capacidade (prova por contagem, antes do motor)</b>: (a) por faixa e dia, quantas pessoas PODEM estar
/// presentes (setor, sem ausência/outro setor, com algum turno do perfil — ou, com o rodízio de abertura/fechamento
/// ligado, qualquer turno da operação — e da disponibilidade que cubra a faixa, sem regra rígida que proíba o turno)
/// contra o mínimo; (b) por semana e horário de faixa, a soma dos mínimos dos dias contra o máximo de dias que cada pessoa
/// capaz pode trabalhar na semana (dias normais − folgas do regime − dias em outro setor); (c) o rodízio de domingos
/// (PlanningContext.CheckSundayCapacity); (d) por setor e semana, os turnos que o expediente pede CONTANDO A COBERTURA
/// DOS INTERVALOS (07:00–22:00 com 1h de intervalo e mínimo 1: 3 turnos/dia = 21; sem os intervalos seriam 2/dia = 14)
/// contra os que o quadro faz (3 pessoas no 5x2 = 15): mensagem direta, primeiro — e, por dia, os turnos do dia contra
/// quem pode trabalhar nele (ausências, folgas manuais). Só os mínimos rígidos (PlanningContext.HardCoverage): dia de borda
/// e faixas com cobertura como meta (setor de 2 pessoas, setor ou dia com quadro insuficiente) nunca entram na prova.
/// <para>Quando a conta fecharia ignorando o perfil de jornada, a falta é do PERFIL: item "perfil" com as contagens por
/// perfil e quantos são necessários ("Abertura — Nenhum colaborador do setor tem perfil Abertura (5 são Flexível)...").
/// Senão é falta de gente (item "capacidade"), com quem não pode e por quê (ausência, disponibilidade, outro setor;
/// ausência sem o tipo de saúde: "afastado").</para></item>
/// <item><b>Cobertura relaxada</b> (depois de um INFEASIBLE): falta mínima por faixa com todas as outras regras.</item>
/// <item><b>Núcleo de restrições</b>: grupos que juntos não admitem escala (regras, células manuais, continuidade).</item>
/// </list>
/// </summary>
public static class InfeasibilityDiagnostics
{
    private const int MaxItems = 12;
    /// <summary>Até quantas pessoas são citadas pelo nome nos motivos (acima disso, contagem por motivo).</summary>
    private const int MaxNames = 5;

    /// <summary>Tipo do item de diagnóstico de erro de configuração (PlanningContext.ConfigurationIssues).</summary>
    public const string ConfigurationType = "configuracao";

    /// <summary>
    /// Erros de configuração do período (PlanningContext.Configuration): dia aberto sem nenhum turno que caiba, faixa que
    /// nenhum turno possível cobre, setor sem cobertura configurada. Vêm ANTES de tudo e a geração falha com eles, com ou
    /// sem a cobertura como meta: não é falta de gente, e a escala sairia com todos de folga ou com faixas sempre vazias.
    /// </summary>
    public static List<DiagnosisItem> Configuration(PlanningContext ctx) =>
        ctx.ConfigurationIssues()
            .Select(i => new DiagnosisItem(ConfigurationType, i.Text, i.Days.Select(d => ctx.Dates[d]).ToList()))
            .ToList();

    private static string Hm(int minutes) => $"{minutes / 60 % 24:00}:{minutes % 60:00}";

    private static string Label(string key) => key switch
    {
        RuleKeys.MinimoAbertura => "abertura",
        RuleKeys.MinimoFechamento => "fechamento",
        RuleKeys.CoberturaMinima => "cobertura mínima",
        _ => "demanda"
    };

    /// <summary>O turno passa nas regras do dia (disponibilidade, regra rígida por turno, posição legal do intervalo).</summary>
    private static bool ShiftOk(PlanningContext ctx, EmployeeInfo e, int d, ShiftInfo t) =>
        ctx.AvailableAt(e, d, t)
        && ctx.OpenAt(d, t)
        && !ctx.ShiftIssues(e, d, t).Any(i => i.Rigid)
        && (t.Snapshot.BreakMinutes == 0 || ctx.BreakOptions(d, t, t.Snapshot.BreakMinutes).Count > 0);

    /// <summary>
    /// Turnos que a pessoa pode fazer no dia (perfil — ou rodízio —, disponibilidade, regra rígida por turno, posição legal
    /// do intervalo, célula fixa). <paramref name="ignoreProfile"/>: qualquer turno com tipo da operação (para saber se a
    /// falta é do perfil).
    /// </summary>
    private static List<ShiftInfo> Allowed(PlanningContext ctx, EmployeeInfo e, int d, bool ignoreProfile, bool rotation)
    {
        if (e.Busy.ContainsKey(d) || !ctx.IsAvailable(e, d) || e.Sectors.Length == 0) return [];
        if (ctx.HolidayRule(d) is { Rigid: true }) return [];
        if (ctx.Fixed.TryGetValue((e.Index, d), out var f)) return f is { } t ? [ctx.Shifts[t]] : [];
        if (ctx.IsClosed(d)) return [];
        return ctx.Shifts.Where(t => (ignoreProfile ? !ctx.OperationMode || t.Kind is not null : ctx.ProfileAllows(e, t) || (rotation && ctx.RotationAllows(e, t))) && ShiftOk(ctx, e, d, t))
            .ToList();
    }

    /// <summary>Máximo de dias de trabalho no setor na semana (dias normais − folgas do regime/piso − dias em outro setor).</summary>
    private static int MaxWorkDays(PlanningContext ctx, EmployeeInfo e, int w)
    {
        var normal = ctx.NormalDays(e, w);
        var off = Math.Max(ctx.RestDays(w)?.Value ?? 0, ctx.MinRestDays(w)?.Value ?? 0);
        var busy = Enumerable.Range(w * 7, 7).Count(e.Busy.ContainsKey);
        return Math.Max(0, Math.Max(0, normal.Count - off) - busy);
    }

    /// <summary>Ausência para as mensagens, sem dado de saúde (afastamento médico aparece como "afastado").</summary>
    private static string AbsenceLabel(string? tipo) => tipo switch
    {
        UnavailabilityKinds.Ferias => "férias",
        UnavailabilityKinds.Licenca => "licença",
        UnavailabilityKinds.Indisponivel => "indisponível",
        _ => "afastado"
    };

    /// <summary>Por que a pessoa do setor não pode cobrir a faixa (null = pode).</summary>
    private static (string Text, string Category)? Reason(PlanningContext ctx, EmployeeInfo e, CoverageSlot s, Func<EmployeeInfo, CoverageSlot, bool> canCover)
    {
        if (canCover(e, s)) return null;
        var d = s.Day;
        if (e.Busy.TryGetValue(d, out var b)) return ($"em {b.SectorName}", "em outro setor");
        if (!ctx.IsAvailable(e, d)) return (AbsenceLabel(ctx.AbsenceType(e, d)), "por ausência/afastamento");
        if (ctx.HolidayRule(d) is { Rigid: true }) return ("feriado sem autorização", "pelo feriado");
        if (ctx.IsClosed(d) && !ctx.Fixed.ContainsKey((e.Index, d))) return ("loja fechada", "loja fechada no dia");
        if (ctx.Fixed.ContainsKey((e.Index, d))) return ("célula manual", "por célula manual");
        var covering = ctx.Shifts.Where(t => (!ctx.OperationMode || t.Kind is not null) && PlanningContext.Covers(t, s)).ToList();
        if (covering.Count > 0 && !covering.Any(t => ctx.AvailableAt(e, d, t)))
            return ($"disponibilidade: {ctx.AvailabilityText(e, d)} na {ScheduleModel.DayName(ctx.Dates[d])}", "pela disponibilidade");
        if (!covering.Any(t => ShiftOk(ctx, e, d, t))) return ("regras de jornada", "pelas regras de jornada");
        return ($"perfil {JourneyProfiles.Rotulo(e.Profile)}", "pelo perfil de jornada");
    }

    /// <summary>
    /// Quem do setor não pode cobrir as faixas e por quê: " Fora: Ana (férias), Bruno (perfil Fechamento)." (até
    /// <see cref="MaxNames"/> pessoas; acima disso, contagem por motivo). Vazio quando todos podem.
    /// </summary>
    private static string WhoCannot(PlanningContext ctx, IReadOnlyCollection<CoverageSlot> slots, Func<EmployeeInfo, CoverageSlot, bool> canCover)
    {
        if (slots.Count == 0) return "";
        var sector = slots.First().Sector;
        var people = new List<(string Name, List<(string Text, string Category)> Reasons)>();
        foreach (var e in ctx.Employees.Where(e => e.Sectors.Contains(sector)).OrderBy(e => e.Snapshot.Name))
        {
            var reasons = slots.Select(s => Reason(ctx, e, s, canCover)).OfType<(string Text, string Category)>().Distinct().ToList();
            if (reasons.Count > 0) people.Add((e.Snapshot.Name, reasons));
        }
        if (people.Count == 0) return "";
        if (people.Count <= MaxNames)
            return " Fora: " + string.Join(", ", people.Select(p =>
                $"{p.Name} ({string.Join(", ", p.Reasons.Select(r => r.Text).Take(3))}{(p.Reasons.Count > 3 ? "..." : "")})")) + ".";
        return " Fora: " + string.Join(", ", people.Select(p => p.Reasons[0].Category).GroupBy(c => c).OrderByDescending(g => g.Count())
            .Select(g => $"{g.Count()} {g.Key}")) + ".";
    }

    /// <summary>Quando: "em 10/11 (ter)" ou "em 33 dia(s) (09/11, 10/11, ...)".</summary>
    private static string When(List<DateOnly> dates) => dates.Count == 1
        ? $"em {dates[0]:dd/MM} ({ScheduleModel.DayName(dates[0])})"
        : $"em {dates.Count} dia(s) ({string.Join(", ", dates.Take(7).Select(d => d.ToString("dd/MM")))}{(dates.Count > 7 ? "..." : "")})";

    private static string Count(int n) => n == 1 ? "1 colaborador do setor tem" : $"{n} colaboradores do setor têm";

    private static string Names(IReadOnlyList<EmployeeInfo> people) =>
        string.Join(", ", people.Take(3).Select(e => e.Snapshot.Name)) + (people.Count > 3 ? $" e mais {people.Count - 3}" : "");

    /// <summary>
    /// Falta causada pelo perfil numa faixa: <paramref name="Daily"/> = no dia (só <paramref name="Can"/> podem estar presentes,
    /// mas <paramref name="CanAny"/> poderiam com outro perfil); senão na semana (<paramref name="Need"/> presenças contra
    /// <paramref name="Cap"/> dias de quem tem o perfil). <paramref name="Kinds"/>: tipos de turno que cobrem a faixa.
    /// </summary>
    private sealed record ProfileGap(int Sector, IReadOnlyList<string> Kinds, int From, int To, List<DateOnly> Dates, bool Daily,
        int Can, int CanAny, int Minimum, int Week, int Need, int Cap, List<CoverageSlot> Slots)
    {
        public string KindsKey => string.Join("+", Kinds);
    }

    /// <summary>
    /// Resultado das provas por contagem: itens do diagnóstico, faltas por faixa, os setores (índices) em que alguma prova
    /// mostra que o mínimo não fecha e os dias (setor × dia) em que só aquele dia não fecha — menos gente do que turnos
    /// (candidatos à cobertura como meta por quadro insuficiente: no setor inteiro ou só no dia).
    /// </summary>
    public sealed record CapacityProof(List<DiagnosisItem> Items, List<DeficitDto> Deficits, IReadOnlySet<int> Sectors,
        IReadOnlySet<(int Sector, int Day)> Days)
    {
        public void Deconstruct(out List<DiagnosisItem> items, out List<DeficitDto> deficits)
        {
            items = Items;
            deficits = Deficits;
        }
    }

    public static CapacityProof Capacity(PlanningContext ctx, bool? rotation = null)
    {
        var items = new List<DiagnosisItem>();
        var deficits = new List<DeficitDto>();
        var sectors = new HashSet<int>();
        // Só mínimos rígidos: dia de borda e setor com cobertura como meta não entram (PlanningContext.HardCoverage)
        var slots = ctx.Slots.Where(s => s.Minimum > 0 && ctx.HardCoverage(s)).ToList();
        if (slots.Count == 0) return new CapacityProof(items, deficits, sectors, new HashSet<(int, int)>());

        var allowed = new Dictionary<(int E, int D, bool Any), List<ShiftInfo>>();
        List<ShiftInfo> AllowedOf(EmployeeInfo e, int d, bool any)
        {
            if (!allowed.TryGetValue((e.Index, d, any), out var list)) allowed[(e.Index, d, any)] = list = Allowed(ctx, e, d, any, rotation ?? ctx.Rotation);
            return list;
        }
        bool CanCover(EmployeeInfo e, CoverageSlot s) =>
            e.Sectors.Contains(s.Sector) && AllowedOf(e, s.Day, false).Any(t => PlanningContext.Covers(t, s));
        // Ignorando o perfil (qualquer turno da operação): se a conta fecha assim, a falta é do perfil
        bool CanCoverAny(EmployeeInfo e, CoverageSlot s) =>
            e.Sectors.Contains(s.Sector) && AllowedOf(e, s.Day, true).Any(t => PlanningContext.Covers(t, s));
        List<string> Kinds(CoverageSlot s) => ctx.Shifts.Where(t => t.Kind is not null && PlanningContext.Covers(t, s))
            .Select(t => t.Kind!).Distinct().OrderBy(k => Array.IndexOf(JourneyProfiles.All, k)).ToList();

        var gaps = new List<ProfileGap>();

        // (a) por faixa e dia (o mesmo buraco em vários dias vira um item só, com os dias)
        var dailyRuns = new List<(DateOnly Date, int From, int To, int Can, int Minimum, string Keys, string Key, List<CoverageSlot> Slots)>();
        var slotDays = new HashSet<(int Sector, int Day)>();
        foreach (var day in slots.GroupBy(s => (s.Day, s.Sector)).OrderBy(g => g.Key.Day))
        {
            var shortSlots = day.Select(s => (Slot: s, Can: ctx.Employees.Count(e => CanCover(e, s)), Any: ctx.Employees.Count(e => CanCoverAny(e, s))))
                .Where(x => x.Can < x.Slot.Minimum).ToList();
            if (shortSlots.Count == 0) continue;
            var date = ctx.Dates[day.Key.Day];
            foreach (var x in shortSlots)
                deficits.Add(new DeficitDto(ctx.Sectors[x.Slot.Sector].SectorId, date, TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(x.Slot.SlotStart)), x.Slot.Minimum - x.Can));
            foreach (var run in Runs(shortSlots, x => x.Slot, x => (x.Can, x.Slot.Minimum, Profile: ctx.OperationMode && x.Any >= x.Slot.Minimum, Kinds: ctx.OperationMode && x.Any >= x.Slot.Minimum ? string.Join("+", Kinds(x.Slot)) : "")))
            {
                var first = run[0];
                if (ctx.OperationMode && first.Any >= first.Slot.Minimum)
                {
                    gaps.Add(new ProfileGap(first.Slot.Sector, Kinds(first.Slot), first.Slot.SlotStart, run[^1].Slot.SlotEnd, [date], true,
                        first.Can, first.Any, first.Slot.Minimum, day.Key.Day / 7, 0, 0, run.Select(r => r.Slot).ToList()));
                    continue;
                }
                // Falta de gente só neste dia (ausências, disponibilidade, outro setor): a meta pode ficar só no dia
                slotDays.Add((day.Key.Sector, day.Key.Day));
                dailyRuns.Add((date, first.Slot.SlotStart, run[^1].Slot.SlotEnd, first.Can, first.Slot.Minimum,
                    string.Join(", ", run.Select(r => r.Slot.MinimumKey).Distinct().Select(Label)), first.Slot.MinimumKey, run.Select(r => r.Slot).ToList()));
            }
        }
        foreach (var g in dailyRuns.GroupBy(r => (r.From, r.To, r.Can, r.Minimum, r.Keys, r.Key)).OrderBy(g => g.Key.From))
        {
            var dates = g.Select(r => r.Date).Distinct().Order().ToList();
            var why = WhoCannot(ctx, g.SelectMany(r => r.Slots).ToList(), CanCover);
            items.Add(new DiagnosisItem("capacidade",
                $"Das {Hm(g.Key.From)} às {Hm(g.Key.To)} {When(dates)}: só {g.Key.Can} colaborador(es) podem estar presentes " +
                $"(perfil de jornada, disponibilidade, ausências) e o mínimo é {g.Key.Minimum} ({g.Key.Keys}).{why}",
                dates, Chave: g.Key.Key));
        }

        // (b) por semana e horário da faixa
        for (var w = 0; w < ctx.Weeks; w++)
        {
            var week = w;
            var weekShort = slots.Where(s => s.Day / 7 == week).GroupBy(s => (s.SlotStart, s.Sector)).OrderBy(g => g.Key.SlotStart)
                .Select(g =>
                {
                    var list = g.ToList();
                    var need = list.Sum(s => s.Minimum);
                    var cap = ctx.Employees.Sum(e => Math.Min(MaxWorkDays(ctx, e, week), list.Count(s => CanCover(e, s))));
                    var capAny = ctx.Employees.Sum(e => Math.Min(MaxWorkDays(ctx, e, week), list.Count(s => CanCoverAny(e, s))));
                    return (Slot: list[0], Need: need, Cap: cap, CapAny: capAny, Key: list[0].MinimumKey, Slots: list);
                })
                .Where(x => x.Need > x.Cap).ToList();
            sectors.UnionWith(weekShort.Select(x => x.Slot.Sector));
            foreach (var run in Runs(weekShort, x => x.Slot, x => (x.Need, x.Cap, Profile: ctx.OperationMode && x.CapAny >= x.Need, Kinds: ctx.OperationMode && x.CapAny >= x.Need ? string.Join("+", Kinds(x.Slot)) : "")))
            {
                var first = run[0];
                var weekDates = ctx.Dates.Skip(w * 7).Take(7).ToList();
                if (ctx.OperationMode && first.CapAny >= first.Need)
                {
                    gaps.Add(new ProfileGap(first.Slot.Sector, Kinds(first.Slot), first.Slot.SlotStart, run[^1].Slot.SlotEnd, weekDates, false,
                        0, 0, first.Slot.Minimum, w, first.Need, first.Cap, first.Slots));
                    continue;
                }
                var why = WhoCannot(ctx, first.Slots, CanCover);
                items.Add(new DiagnosisItem("capacidade",
                    $"Semana de {ctx.Dates[w * 7]:dd/MM}, das {Hm(first.Slot.SlotStart)} às {Hm(run[^1].Slot.SlotEnd)}: o mínimo pede {first.Need} presenças na semana " +
                    $"e quem pode cobrir esse horário trabalha no máximo {first.Cap} dia(s) (folgas do regime, perfil, disponibilidade, ausências).{why}",
                    weekDates, Chave: first.Key));
            }
        }

        // Falta de perfil: um item por tipo de turno (abertura, fechamento, horário intermediário), antes dos de capacidade
        items.InsertRange(0, ProfileItems(ctx, gaps, CanCover, CanCoverAny));

        // (d) turnos por semana: o expediente pede mais turnos do que o quadro faz (falta de gente, qualquer perfil) — primeiro
        // (a faixa que fica descoberta depende de quem folga: não há falta por faixa a apontar)
        var (countItems, countSectors, shortDays) = ShiftCountItems(ctx, slots);
        items.InsertRange(0, countItems);
        sectors.UnionWith(countSectors);

        // (c) rodízio de domingos (sem células fixas: elas mudam a conta)
        if (ctx.Fixed.Count == 0)
        {
            foreach (var g in ctx.CheckSundayCapacity().GroupBy(x => x.Date))
            {
                items.Add(new DiagnosisItem("capacidade",
                    $"Domingo {g.Key:dd/MM}: o rodízio de domingos (folga em 1 domingo a cada N semanas) não deixa gente suficiente para o mínimo " +
                    $"(faltam até {g.Max(x => x.Missing)} pessoa(s)).", [g.Key], Chave: RuleKeys.DomingoFolgaJanela));
                deficits.AddRange(g);
                foreach (var x in g)
                    if (Array.FindIndex(ctx.Sectors, s => s.SectorId == x.SectorId) is var i and >= 0) sectors.Add(i);
            }
        }
        // Setores dos itens de perfil (falta de gente com o perfil, por faixa ou semana)
        foreach (var gap in gaps) sectors.Add(gap.Sector);
        shortDays.UnionWith(slotDays);
        return new CapacityProof(items.Take(MaxItems * 2).ToList(), deficits, sectors, shortDays);
    }

    /// <summary>
    /// Turnos necessários no dia para ter alguém em todas as faixas com mínimo, com os turnos <paramref name="shifts"/>:
    /// cobertura de pontos por intervalos (guloso, ótimo) — a faixa mais cedo ainda descoberta recebe o turno que a cobre e
    /// termina mais tarde. Faixa que nenhum turno cobre é ignorada (a contagem por faixa já aponta). Ignora o intervalo
    /// (ver <see cref="ShiftsNeededExact"/>).
    /// </summary>
    private static int ShiftsNeeded(List<CoverageSlot> daySlots, IReadOnlyCollection<ShiftInfo> shifts)
    {
        var ordered = daySlots.OrderBy(s => s.SlotStart).ToList();
        var count = 0;
        var i = 0;
        while (i < ordered.Count)
        {
            var slot = ordered[i];
            var best = shifts.Where(t => PlanningContext.Covers(t, slot)).MaxBy(t => t.EndMin);
            if (best is null)
            {
                i++;
                continue;
            }
            count++;
            while (i < ordered.Count && PlanningContext.Covers(best, ordered[i])) i++;
        }
        return count;
    }

    /// <summary>Cache das contas de turnos com intervalo por fotografia (mesmo dia-tipo → mesma conta).</summary>
    private static readonly ConditionalWeakTable<PlanningContext, Dictionary<string, int>> BreakNeedCache = new();

    /// <summary>
    /// Turnos necessários no dia com o MÍNIMO de cada faixa (inclusive mínimo 2+) e, com <paramref name="breaks"/>,
    /// CONTANDO OS INTERVALOS: quem está em intervalo não cobre a faixa (como no motor: o intervalo que toca a faixa desconta
    /// a pessoa dela). Programa inteiro pequeno no CP-SAT: quantas pessoas fazem cada turno com o intervalo em cada posição
    /// legal (<see cref="PlanningContext.BreakOptions"/>; sem posição legal, qualquer uma da jornada — a conta continua
    /// sendo um limite inferior), com o mínimo de cada faixa coberto, minimizando o total. Ex.: 07:00–22:00 com turnos de
    /// 8h20 e 1h de intervalo e mínimo 1: 2 turnos cobrem o expediente, mas o intervalo de quem abre cai fora da
    /// sobreposição com quem fecha (e vice-versa) — são 3 por dia. Faixa sem turno que a cubra fora do intervalo é ignorada
    /// (a contagem por faixa já aponta). Nunca menos que <see cref="ShiftsNeeded"/>.
    /// </summary>
    internal static int ShiftsNeededExact(PlanningContext ctx, int day, List<CoverageSlot> daySlots, IReadOnlyCollection<ShiftInfo> shifts,
        bool breaks)
    {
        var greedy = ShiftsNeeded(daySlots, shifts);
        var options = new List<(ShiftInfo T, int? Break)>();
        foreach (var t in shifts.Where(t => daySlots.Any(s => PlanningContext.Covers(t, s))).OrderBy(t => t.Index))
        {
            var minutes = t.Snapshot.BreakMinutes;
            if (minutes <= 0 || !breaks)
            {
                options.Add((t, null));
                continue;
            }
            var positions = ctx.BreakOptions(day, t, minutes).Select(o => o.Start).ToList();
            if (positions.Count == 0)
                for (var p = t.StartMin; p + minutes <= t.EndMin; p += BreakPlanner.StepMinutes) positions.Add(p);
            options.AddRange(positions.Select(p => (t, (int?)p)));
        }
        if (options.Count == 0) return greedy;

        var key = string.Join(";", daySlots.Where(s => s.Minimum > 0).Select(s => $"{s.SlotStart}+{s.SlotMinutes}x{s.Minimum}")) + "|" +
                  string.Join(",", options.Select(o => $"{o.T.StartMin}-{o.T.EndMin}@{o.Break}/{o.T.Snapshot.BreakMinutes}"));
        var cache = BreakNeedCache.GetOrCreateValue(ctx);
        lock (cache)
            if (cache.TryGetValue(key, out var cached)) return Math.Max(greedy, cached);

        var model = new CpModel();
        var upper = Math.Max(1, daySlots.Max(s => s.Minimum)) * Math.Max(1, daySlots.Count);
        var n = options.Select((_, i) => model.NewIntVar(0, upper, $"n_{i}")).ToList();
        foreach (var slot in daySlots.Where(s => s.Minimum > 0))
        {
            var terms = Enumerable.Range(0, options.Count).Where(i =>
            {
                var (t, b) = options[i];
                return PlanningContext.Covers(t, slot)
                       && !(b is { } start && start < slot.SlotEnd && start + t.Snapshot.BreakMinutes > slot.SlotStart);
            }).Select(i => n[i]).ToList();
            if (terms.Count == 0) continue;
            model.Add(ScheduleModel.Sum(terms) >= slot.Minimum);
        }
        model.Minimize(ScheduleModel.Sum(n));
        var solver = new CpSolver
        {
            StringParameters = "max_time_in_seconds:2 num_workers:1 random_seed:42"
        };
        var status = solver.Solve(model);
        // Sem prova de ótimo no tempo: o limite inferior provado (a conta continua sendo prova necessária)
        var value = status switch
        {
            CpSolverStatus.Optimal => (int)Math.Round(solver.ObjectiveValue),
            CpSolverStatus.Feasible or CpSolverStatus.Unknown => (int)Math.Ceiling(solver.BestObjectiveBound - 1e-6),
            _ => greedy
        };
        lock (cache) cache[key] = value;
        return Math.Max(greedy, value);
    }

    /// <summary>
    /// Semana × setor: <paramref name="Need"/> = turnos que o expediente pede SEM contar os intervalos (por dia, o maior
    /// entre <see cref="ShiftsNeeded"/> e o maior mínimo do dia); <paramref name="NeedBreaks"/> = os mesmos contando quem cobre
    /// os intervalos (<see cref="ShiftsNeededExact"/>, ≥ Need); <paramref name="Cap"/> = máximo de turnos que o quadro
    /// faz (por pessoa, o menor entre os dias de trabalho da semana — regime, ausências, outro setor — e os dias em que tem
    /// turno que cobre alguma faixa); <paramref name="Window"/> = expediente quando é o mesmo em todos os dias
    /// ("07:00–22:00"); <paramref name="PerDay"/>/<paramref name="PerDayBreaks"/> = turnos por dia (sem/com os intervalos)
    /// quando é o mesmo em todos os dias. <paramref name="ShortDays"/>: dias em que o expediente pede mais turnos (com os
    /// intervalos) do que as pessoas que podem trabalhar no dia (ausências, folgas manuais, disponibilidade, outro setor).
    /// </summary>
    public sealed record WeekShifts(int Sector, int Week, int Need, int Cap, int People, string? Window, int? PerDay, List<DateOnly> Dates,
        int NeedBreaks, int? PerDayBreaks, List<ShortDay> ShortDays);

    /// <summary>Dia com menos gente do que turnos: <paramref name="Need"/> sem os intervalos, <paramref name="NeedBreaks"/> com eles.</summary>
    public sealed record ShortDay(DateOnly Date, int Need, int NeedBreaks, int Can, string Window);

    /// <summary>
    /// Conta de turnos por semana (só dias do período e faixas com mínimo; qualquer turno da operação — o perfil não
    /// importa: a falta é de gente). Prova necessária: cada pessoa faz no máximo um turno por dia.
    /// </summary>
    public static List<WeekShifts> WeeklyShifts(PlanningContext ctx, IEnumerable<CoverageSlot> slots)
    {
        var result = new List<WeekShifts>();
        foreach (var bySector in slots.Where(s => s.Minimum > 0 && ctx.InPeriod(s.Day)).GroupBy(s => s.Sector).OrderBy(g => g.Key))
        {
            var sector = bySector.Key;
            var people = ctx.Employees.Where(e => e.Sectors.Contains(sector) && ctx.AvailableInPeriod(e)).ToList();
            foreach (var week in bySector.GroupBy(s => s.Day / 7).OrderBy(g => g.Key))
            {
                var perDay = new List<int>();
                var perDayBreaks = new List<int>();
                var windows = new HashSet<(int Open, int Close)>();
                var workable = people.ToDictionary(e => e.Index, _ => 0);
                var shortDays = new List<ShortDay>();
                foreach (var day in week.GroupBy(s => s.Day).OrderBy(g => g.Key))
                {
                    var daySlots = day.ToList();
                    var union = new HashSet<ShiftInfo>();
                    var can = 0;
                    foreach (var e in people)
                    {
                        var list = Allowed(ctx, e, day.Key, ignoreProfile: true, rotation: true)
                            .Where(t => daySlots.Any(s => PlanningContext.Covers(t, s))).ToList();
                        if (list.Count == 0) continue;
                        workable[e.Index]++;
                        can++;
                        union.UnionWith(list);
                    }
                    var plain = union.Count == 0 ? 0 : Math.Max(daySlots.Max(s => s.Minimum), ShiftsNeededExact(ctx, day.Key, daySlots, union, breaks: false));
                    var withBreaks = union.Count == 0 ? 0 : Math.Max(plain, ShiftsNeededExact(ctx, day.Key, daySlots, union, breaks: true));
                    perDay.Add(plain);
                    perDayBreaks.Add(withBreaks);
                    var open = daySlots.Min(s => s.SlotStart);
                    var close = daySlots.Max(s => s.SlotEnd);
                    windows.Add((open, close));
                    // Cada pessoa faz no máximo um turno no dia: com menos gente do que turnos, o dia não fecha
                    if (can > 0 && withBreaks > can) shortDays.Add(new ShortDay(ctx.Dates[day.Key], plain, withBreaks, can, $"{Hm(open)}–{Hm(close)}"));
                }
                var cap = people.Sum(e => Math.Min(MaxWorkDays(ctx, e, week.Key), workable[e.Index]));
                var window = windows.Count == 1 ? $"{Hm(windows.First().Open)}–{Hm(windows.First().Close)}" : null;
                var dates = week.Select(s => ctx.Dates[s.Day]).Distinct().Order().ToList();
                result.Add(new WeekShifts(sector, week.Key, perDay.Sum(), cap, people.Count, window,
                    perDay.Distinct().Count() == 1 ? perDay[0] : null, dates, perDayBreaks.Sum(),
                    perDayBreaks.Distinct().Count() == 1 ? perDayBreaks[0] : null, shortDays));
            }
        }
        return result;
    }

    private static string PeopleText(int n) => n == 1 ? "1 colaborador" : $"{n} colaboradores";

    /// <summary>
    /// Itens "capacidade" da conta de turnos (<see cref="WeeklyShifts"/>): semana em que o expediente pede mais turnos do que
    /// o quadro faz — mensagem direta, uma por setor e conta ("Setor Padaria tem 2 colaboradores: no máximo 10 turnos/semana
    /// para 14 necessários (07:00–22:00 com 2 turnos/dia; semanas de 02/11, 09/11...). Aumente o quadro ou reduza o
    /// expediente/cobertura."). Quando só a cobertura dos intervalos faz faltar turnos, a mensagem diz isso ("Setor Açougue
    /// tem 3 colaboradores: para cobrir 07:00–22:00 com os intervalos são necessários 21 turnos/semana (3/dia) e o setor faz
    /// no máximo 15 ..."). Devolve também os setores dos itens.
    /// </summary>
    private static (List<DiagnosisItem> Items, HashSet<int> Sectors, HashSet<(int Sector, int Day)> Days) ShiftCountItems(PlanningContext ctx,
        IEnumerable<CoverageSlot> slots)
    {
        var items = new List<DiagnosisItem>();
        var sectors = new HashSet<int>();
        var days = new HashSet<(int Sector, int Day)>();
        var all = WeeklyShifts(ctx, slots);
        // A conta que se repete em mais semanas primeiro (a semana parcial da ponta do mês por último)
        foreach (var g in all.Where(x => x.NeedBreaks > x.Cap)
                     .GroupBy(x => (x.Sector, x.Need, x.NeedBreaks, x.Cap, x.People, x.Window, x.PerDay, x.PerDayBreaks))
                     .OrderBy(g => g.Key.Sector).ThenByDescending(g => g.Count()).ThenBy(g => g.Min(x => x.Week)))
        {
            var weeks = g.OrderBy(x => x.Week).ToList();
            var k = g.Key;
            sectors.Add(k.Sector);
            var starts = weeks.Select(x => x.Dates[0].ToString("dd/MM")).ToList();
            var when = starts.Count == 1 ? $"semana de {starts[0]}" : $"semanas de {string.Join(", ", starts.Take(5))}{(starts.Count > 5 ? "..." : "")}";
            string text;
            if (k.Need > k.Cap)
            {
                var detail = k.Window is not null && k.PerDay is not null ? $"{k.Window} com {k.PerDay} turno(s)/dia"
                    : k.Window ?? "pelo expediente de cada dia";
                text = $"Setor {ctx.Sectors[k.Sector].Name} tem {PeopleText(k.People)}: no máximo {k.Cap} turnos/semana para {k.Need} necessários " +
                       $"({detail}; {when}).{(k.NeedBreaks > k.Need ? $" Contando quem cobre os intervalos, são {k.NeedBreaks}." : "")} " +
                       "Aumente o quadro ou reduza o expediente/cobertura.";
            }
            else
            {
                var perDay = k.PerDayBreaks is { } p ? $" ({p}/dia)" : "";
                text = $"Setor {ctx.Sectors[k.Sector].Name} tem {PeopleText(k.People)}: para cobrir {k.Window ?? "o expediente de cada dia"} com os intervalos " +
                       $"são necessários {k.NeedBreaks} turnos/semana{perDay} e o setor faz no máximo {k.Cap} ({when}). Sem contar os intervalos seriam " +
                       $"{k.Need}: quem está em intervalo não conta na cobertura, então alguém precisa cobrir o intervalo de cada um. " +
                       "Aumente o quadro ou reduza o expediente/cobertura.";
            }
            items.Add(new DiagnosisItem("capacidade", text,
                weeks.SelectMany(x => x.Dates).ToList(), Chave: ctx.OperationMode ? RuleKeys.CoberturaMinima : RuleKeys.CoberturaFaixa));
        }
        // Dias com menos gente do que turnos (ausências, folgas manuais...) nas semanas que a conta da semana não apontou: um
        // item por setor e conta, com os dias
        foreach (var g in all.Where(x => x.NeedBreaks <= x.Cap).SelectMany(w => w.ShortDays.Select(d => (w.Sector, Day: d)))
                     .GroupBy(x => (x.Sector, x.Day.Need, x.Day.NeedBreaks, x.Day.Can, x.Day.Window)))
        {
            var k = g.Key;
            var dates = g.Select(x => x.Day.Date).Order().ToList();
            foreach (var date in dates) days.Add((k.Sector, ctx.DayIndex(date)));
            var need = k.Need > k.Can ? $"pede {k.Need} turnos" : $"com os intervalos pede {k.NeedBreaks} turnos (sem eles seriam {k.Need})";
            items.Add(new DiagnosisItem("capacidade",
                $"Setor {ctx.Sectors[k.Sector].Name}, {When(dates)}: só {k.Can} colaborador(es) podem trabalhar no dia (ausências, folgas manuais, " +
                $"disponibilidade, outro setor) e cobrir {k.Window} {need}. Aumente o quadro do dia ou reduza o expediente/cobertura.",
                dates, Chave: ctx.OperationMode ? RuleKeys.CoberturaMinima : RuleKeys.CoberturaFaixa));
        }
        return (items, sectors, days);
    }

    /// <summary>
    /// Aviso da cobertura como meta (setor de 2 pessoas e setor com quadro insuficiente, PlanningContext.SoftCoverage), por
    /// setor com faixa descoberta no período (<paramref name="uncovered"/>: faltas no mínimo lidas da solução), com a causa
    /// (conta de turnos por semana; com os intervalos quando só eles fazem faltar) e as faixas agregadas por causa, horário e
    /// dias da semana (<paramref name="assignments"/>: células da escala, para saber quem estava em intervalo):
    /// "Setor Padaria tem 2 colaboradores: não é possível cobrir 07:00–22:00 todos os dias (são 14 turnos por semana e 2
    /// pessoas fazem 10). ... Faixas descobertas: fechamento ter/sex 15:20–22:00 (8x), ... — 120h no mês. ..." ou "Setor
    /// Açougue tem 3 colaboradores: para cobrir 07:00–22:00 todos os dias com os intervalos são necessários 21 turnos/semana e
    /// o setor faz 15. ... Faixas descobertas: intervalos (quem estava sozinho no setor) ter/qua 13:00–14:00 (4x); abertura
    /// sáb 07:00–11:00 (2x) — 14h no mês. ...". Sem a conta (provado pelo CP-SAT), a causa é o conjunto quadro + perfis +
    /// regras legais. Sem nome de quem estava ausente (LGPD).
    /// </summary>
    public static List<string> CoverageTargetNotices(PlanningContext ctx, IEnumerable<(CoverageSlot Slot, int Missing)> uncovered,
        IReadOnlyList<AssignmentDto>? assignments = null)
    {
        var notices = new List<string>();
        var cellsByDay = (assignments ?? []).GroupBy(a => a.Date).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var bySector in uncovered.Where(x => x.Missing > 0 && ctx.InPeriod(x.Slot.Day) && ctx.TargetCoverage(x.Slot))
                     .GroupBy(x => x.Slot.Sector).OrderBy(g => g.Key))
        {
            var sector = bySector.Key;
            if (!ctx.SoftCoverage(sector))
            {
                // Só alguns dias com a cobertura como meta (menos gente do que turnos no dia): o resto do mês é rígido
                var dates = bySector.Select(x => ctx.Dates[x.Slot.Day]).Distinct().Order().ToList();
                var lost = bySector.Sum(x => x.Slot.SlotMinutes);
                notices.Add($"Setor {ctx.Sectors[sector].Name}: {When(dates)} há menos gente do que turnos para cobrir o expediente com os " +
                            "intervalos (ausências, folgas manuais, disponibilidade, outro setor); a cobertura mínima desse(s) dia(s) é meta e a escala " +
                            $"cobre o máximo possível — {(lost % 60 == 0 ? $"{lost / 60}h" : $"{lost / 60}h{lost % 60:00}")} descoberta(s). " +
                            "Nos outros dias a cobertura mínima é cumprida.");
                continue;
            }
            var sectorSlots = ctx.Slots.Where(s => s.Sector == sector && s.Minimum > 0 && ctx.InPeriod(s.Day)).ToList();
            var weeks = WeeklyShifts(ctx, sectorSlots);
            // A semana inteira do período com a maior falta de turnos (as parciais só se não houver inteira)
            var worst = weeks.OrderByDescending(x => x.Dates.Count == 7).ThenByDescending(x => x.NeedBreaks - x.Cap).FirstOrDefault();
            var people = ctx.Employees.Count(e => e.Sectors.Contains(sector) && ctx.AvailableInPeriod(e));
            var windows = sectorSlots.GroupBy(s => s.Day).Select(g => (Open: g.Min(s => s.SlotStart), Close: g.Max(s => s.SlotEnd))).Distinct().ToList();
            var window = windows.Count == 1 ? $"{Hm(windows[0].Open)}–{Hm(windows[0].Close)} todos os dias" : "o expediente de todos os dias";
            var name = $"Setor {ctx.Sectors[sector].Name} tem {PeopleText(people)}";
            var head = worst switch
            {
                { } w when w.Need > w.Cap =>
                    $"{name}: não é possível cobrir {window} (são {w.Need} turnos por semana e {(people == 1 ? "1 pessoa faz" : $"{people} pessoas fazem")} {w.Cap})",
                { } w when w.NeedBreaks > w.Cap =>
                    $"{name}: para cobrir {window} com os intervalos são necessários {w.NeedBreaks} turnos/semana e o setor faz {w.Cap}",
                _ when ctx.TwoPersonCoverage(sector) => $"{name}: a cobertura mínima é meta e não foi possível cobrir todas as faixas",
                _ => $"{name}: com o quadro, os perfis de jornada e as regras legais (interjornada, folgas, domingos, ausências) não há escala " +
                     "que cubra todo o mínimo"
            };

            // Faixas seguidas descobertas por dia, separadas pelo motivo da faixa (alguém do setor em intervalo ou não)
            var runs = new List<(DateOnly Date, int From, int To, UncoveredKind Kind)>();
            foreach (var day in bySector.GroupBy(x => x.Slot.Day).OrderBy(g => g.Key))
            {
                var date = ctx.Dates[day.Key];
                var cells = cellsByDay.GetValueOrDefault(date) ?? [];
                var list = day.OrderBy(x => x.Slot.SlotStart)
                    .Select(x => (x.Slot, OnBreak: ComplianceEvaluator.Presence(ctx, cells, x.Slot).OnBreak > 0)).ToList();
                foreach (var run in Runs(list, x => x.Slot, x => x.OnBreak))
                    runs.Add((date, run[0].Slot.SlotStart, run[^1].Slot.SlotEnd,
                        RunKind(ctx, sector, day.Key, run[0].Slot.SlotStart, run[^1].Slot.SlotEnd, run[0].OnBreak)));
            }
            // Por causa (intervalo de quem estava sozinho, abertura, fechamento...), horário e dias da semana; as 12 com mais
            // tempo descoberto, na ordem das causas
            var groups = runs.GroupBy(r => (r.Kind, r.From, r.To))
                .Select(g => (g.Key.Kind, g.Key.From, Count: g.Count(), Minutes: g.Count() * (g.Key.To - g.Key.From),
                    Text: $"{PlanningContext.WeekdaysText(g.Select(r => r.Date.DayOfWeek))} {Hm(g.Key.From)}–{Hm(g.Key.To)} " +
                          $"({(g.Count() == 1 ? g.First().Date.ToString("dd/MM") : $"{g.Count()}x")})"))
                .ToList();
            var shown = groups.OrderByDescending(g => g.Minutes).ThenBy(g => g.Kind).Take(12)
                .OrderBy(g => g.Kind).ThenByDescending(g => g.Count).ThenBy(g => g.From)
                .GroupBy(g => g.Kind)
                .Select(k => $"{KindLabel(k.Key)} {string.Join(", ", k.Select(x => x.Text))}").ToList();
            var minutes = bySector.Sum(x => x.Slot.SlotMinutes);
            var hours = minutes % 60 == 0 ? $"{minutes / 60}h" : $"{minutes / 60}h{minutes % 60:00}";
            notices.Add($"{head}: a cobertura mínima do setor é meta e a escala cobre o máximo possível (prioridade: picos manuais, " +
                        $"horários de maior demanda, abertura e fechamento; intervalos revezados quando há mais alguém no setor). " +
                        $"Faixas descobertas: {string.Join("; ", shown)}" +
                        $"{(groups.Count > 12 ? $" e mais {groups.Count - 12}" : "")} — {hours} no mês. " +
                        "Para cobrir tudo, aumente o quadro do setor (veja o dimensionamento) ou reduza o expediente/cobertura.");
        }
        return notices;
    }

    /// <summary>Causa de uma faixa descoberta, na ordem em que aparece no aviso.</summary>
    private enum UncoveredKind { DiaInteiro, Intervalo, Abertura, Fechamento, EntreTurnos }

    private static string KindLabel(UncoveredKind kind) => kind switch
    {
        UncoveredKind.DiaInteiro => "dia inteiro",
        UncoveredKind.Intervalo => "intervalos (quem estava sozinho no setor)",
        UncoveredKind.Abertura => "abertura",
        UncoveredKind.Fechamento => "fechamento",
        _ => "entre turnos"
    };

    /// <summary>
    /// Causa da sequência descoberta [from, to) no dia: intervalo (alguém do setor tinha turno na faixa mas estava em
    /// intervalo); o expediente inteiro; abertura (começa na abertura do dia); fechamento (termina no fechamento); senão,
    /// entre turnos.
    /// </summary>
    private static UncoveredKind RunKind(PlanningContext ctx, int sector, int day, int from, int to, bool onBreak)
    {
        if (onBreak) return UncoveredKind.Intervalo;
        var daySlots = ctx.Slots.Where(s => s.Day == day && s.Sector == sector).ToList();
        var open = daySlots.Min(s => s.SlotStart);
        var close = daySlots.Max(s => s.SlotEnd);
        if (from == open && to == close) return UncoveredKind.DiaInteiro;
        if (from == open) return UncoveredKind.Abertura;
        return to == close ? UncoveredKind.Fechamento : UncoveredKind.EntreTurnos;
    }

    /// <summary>
    /// Itens "perfil": a conta fecharia com outro perfil. Um item por conjunto de tipos que cobre a faixa (abertura,
    /// fechamento, horário intermediário), com as contagens por perfil do setor e o que fazer.
    /// </summary>
    private static List<DiagnosisItem> ProfileItems(PlanningContext ctx, List<ProfileGap> gaps,
        Func<EmployeeInfo, CoverageSlot, bool> canCover, Func<EmployeeInfo, CoverageSlot, bool> canCoverAny)
    {
        var items = new List<DiagnosisItem>();
        var fix = ctx.Rotation
            ? "Altere o perfil de alguém em Colaboradores → Jornada."
            : "Altere o perfil de alguém em Colaboradores → Jornada ou ligue o rodízio de abertura/fechamento na operação da loja (Lojas → Operação).";
        foreach (var g in gaps.GroupBy(x => (x.Sector, x.KindsKey)))
        {
            var daily = g.Where(x => x.Daily).ToList();
            var first = daily.FirstOrDefault() ?? g.First();
            var kinds = first.Kinds;
            var people = ctx.Employees.Where(e => e.Sectors.Contains(first.Sector)).ToList();
            var inProfile = people.Where(e => e.Profile is null || kinds.Contains(e.Profile)).ToList();
            var others = string.Join(", ", people.Except(inProfile).GroupBy(e => e.Profile!).OrderByDescending(c => c.Count())
                .Select(c => $"{c.Count()} {(c.Count() == 1 ? "é" : "são")} {JourneyProfiles.Rotulo(c.Key)}"));
            var single = kinds.Count == 1 && kinds[0] != JourneyProfiles.Flexivel ? kinds[0] : null;
            var label = single is not null ? JourneyProfiles.Rotulo(single) : $"Das {Hm(first.From)} às {Hm(first.To)}";
            var perfil = string.Join(" ou ", kinds.Select(JourneyProfiles.Rotulo));
            var range = $"das {Hm(first.From)} às {Hm(first.To)}";
            string text;
            List<DateOnly> dates;
            if (inProfile.Count == 0 || daily.Count > 0)
            {
                dates = daily.Count > 0 ? daily.SelectMany(x => x.Dates).Distinct().Order().ToList() : g.SelectMany(x => x.Dates).Distinct().Order().ToList();
                text = inProfile.Count == 0
                    ? $"{label} — Nenhum colaborador do setor tem perfil {perfil} ({others}): ninguém pode cobrir {range} {When(dates)}."
                    : $"{label} — Só {Count(inProfile.Count)} perfil {perfil} ({Names(inProfile)}) " +
                      $"e {When(dates)} só {first.Can} pode(m) estar presente(s) {range} (mínimo {first.Minimum});" +
                      WhoCannot(ctx, daily.SelectMany(x => x.Slots).ToList(), canCover).Replace(" Fora:", " fora:") +
                      $" Com outro perfil, {first.CanAny} do setor poderiam cobrir.";
            }
            else
            {
                var weekly = g.OrderBy(x => x.Week).ToList();
                var wk = weekly[0];
                dates = weekly.SelectMany(x => x.Dates).Distinct().Order().ToList();
                var days = wk.Slots.Count;
                var needText = single switch
                {
                    JourneyProfiles.Abertura => $"são {days} aberturas{(wk.Need > days ? $", {wk.Need} presenças" : "")}",
                    JourneyProfiles.Fechamento => $"são {days} fechamentos{(wk.Need > days ? $", {wk.Need} presenças" : "")}",
                    _ => $"o mínimo pede {wk.Need} presença(s) {range}"
                };
                var needed = NeededPeople(ctx, wk, inProfile, people, canCover, canCoverAny);
                var more = weekly.Skip(1).Select(x => ctx.Dates[x.Week * 7].ToString("dd/MM")).Distinct().ToList();
                text = $"{label} — Só {Count(inProfile.Count)} perfil {perfil} ({Names(inProfile)}) " +
                       $"e {(inProfile.Count == 1 ? "ele trabalha" : "eles trabalham juntos")} no máximo {wk.Cap} dia(s) na semana de {ctx.Dates[wk.Week * 7]:dd/MM} ({needText})" +
                       (needed is { } n ? $": são necessários pelo menos {n} com esse perfil" : "") +
                       (more.Count > 0 ? $" (o mesmo na(s) semana(s) de {string.Join(", ", more)})" : "") + ".";
            }
            items.Add(new DiagnosisItem("perfil", $"{text} {fix}", dates, Chave: RuleKeys.PerfilJornada));
        }
        return items;
    }

    /// <summary>
    /// Quantas pessoas com o perfil a semana precisa: as que já têm o perfil mais as de outro perfil (das que mais podem
    /// cobrir para as que menos) até a soma dos dias chegar ao que o mínimo pede. null = nem assim.
    /// </summary>
    private static int? NeededPeople(PlanningContext ctx, ProfileGap gap, List<EmployeeInfo> inProfile, List<EmployeeInfo> people,
        Func<EmployeeInfo, CoverageSlot, bool> canCover, Func<EmployeeInfo, CoverageSlot, bool> canCoverAny)
    {
        var sum = inProfile.Sum(e => Math.Min(MaxWorkDays(ctx, e, gap.Week), gap.Slots.Count(s => canCover(e, s))));
        var count = inProfile.Count;
        foreach (var cap in people.Except(inProfile)
                     .Select(e => Math.Min(MaxWorkDays(ctx, e, gap.Week), gap.Slots.Count(s => canCoverAny(e, s))))
                     .Where(c => c > 0).OrderByDescending(c => c))
        {
            if (sum >= gap.Need) break;
            sum += cap;
            count++;
        }
        return sum >= gap.Need ? count : null;
    }

    /// <summary>Sequências de faixas seguidas (mesmo dia) com a mesma chave (para mensagens "das 07:00 às 09:30").</summary>
    private static List<List<T>> Runs<T>(List<T> list, Func<T, CoverageSlot> slotOf, Func<T, object> key)
    {
        var runs = new List<List<T>>();
        foreach (var x in list)
        {
            if (runs.Count > 0)
            {
                var prev = runs[^1][^1];
                var prevSlot = slotOf(prev);
                var slot = slotOf(x);
                if (Equals(key(prev), key(x)) && prevSlot.SlotEnd == slot.SlotStart && prevSlot.Day == slot.Day)
                {
                    runs[^1].Add(x);
                    continue;
                }
            }
            runs.Add([x]);
        }
        return runs;
    }

    /// <summary>Faltas no mínimo (cobertura relaxada) agrupadas por dia e horário.</summary>
    public static List<DiagnosisItem> CoverageItems(PlanningContext ctx, List<DeficitDto> deficits, List<CoverageSlot> slots)
    {
        var bySlot = slots.ToDictionary(s => (ctx.Dates[s.Day], s.SlotStart));
        var items = new List<DiagnosisItem>();
        foreach (var day in deficits.GroupBy(d => d.Date).OrderBy(g => g.Key))
        {
            var list = day.OrderBy(d => d.SlotStart)
                .Select(d => (Slot: bySlot[(d.Date, PlanningContext.ToMinutes(d.SlotStart))], d.Missing)).ToList();
            foreach (var run in Runs(list, x => x.Slot, x => x.Missing))
            {
                var first = run[0].Slot;
                items.Add(new DiagnosisItem("cobertura",
                    $"{day.Key:dd/MM} ({ScheduleModel.DayName(day.Key)}) das {Hm(first.SlotStart)} às {Hm(run[^1].Slot.SlotEnd)}: faltam {run[0].Missing} pessoa(s) " +
                    $"para o mínimo de {first.Minimum} ({Label(first.MinimumKey)}), mesmo usando todo o quadro dentro das regras.",
                    [day.Key], Chave: first.MinimumKey));
            }
        }
        return items;
    }

    public static InfeasibilityDiagnosis Compose(PlanningContext ctx, List<DiagnosisItem> items, List<DeficitDto> deficits, bool proven, bool fromCapacity)
    {
        var shown = items.Take(MaxItems * 2).ToList();
        string resumo;
        if (shown.Count > 0 && shown[0].Tipo == ConfigurationType)
        {
            resumo = $"Escala não gerada — erro de configuração (antes do motor): {shown[0].Mensagem}" +
                     (shown.Count > 1 ? $" Há mais {shown.Count - 1} ponto(s) no diagnóstico." : "");
        }
        else if (fromCapacity && shown.Count > 0)
        {
            resumo = $"Inviável (provado por contagem, sem precisar do motor): {shown[0].Mensagem}" +
                     (shown.Count > 1 ? $" Há mais {shown.Count - 1} ponto(s) no diagnóstico." : "");
        }
        else if (deficits.Count > 0)
        {
            var days = deficits.Select(d => d.Date).Distinct().Order().ToList();
            resumo = $"Inviável: com todas as regras rígidas, faltam pessoas para a cobertura mínima em {deficits.Count} faixa(s) de {days.Count} dia(s) " +
                     $"({string.Join(", ", days.Take(6).Select(d => d.ToString("dd/MM")))}{(days.Count > 6 ? "..." : "")})" +
                     (proven ? "." : " (falta mínima encontrada no tempo do diagnóstico).");
        }
        else if (shown.Count > 0)
        {
            resumo = "Inviável: estas restrições não podem ser cumpridas ao mesmo tempo — " +
                     string.Join("; ", shown.Take(4).Select(i => i.Mensagem)) + (shown.Count > 4 ? "; ..." : "") + ".";
        }
        else
        {
            resumo = "Inviável: as regras rígidas não admitem escala com o quadro, as ausências e as células atuais.";
        }

        var tipos = shown.Select(i => i.Tipo).Concat(deficits.Count > 0 && !fromCapacity ? ["cobertura"] : []).ToHashSet();
        var sugestoes = new List<string>();
        if (tipos.Contains(ConfigurationType))
        {
            if (ctx.AutomaticShifts)
            {
                sugestoes.Add("Os turnos são automáticos: saem do horário de funcionamento de cada dia (Cadastro da loja) e das regras de " +
                              "conformidade vigentes (carga semanal ÷ dias do regime, limite diário e de domingo, intervalo). Confira o horário " +
                              "da loja (abertura/fechamento de cada dia, dias fechados, feriados com horário especial) e as regras do período.");
            }
            else
            {
                sugestoes.Add("Confira o horário de funcionamento da loja em Cadastro da loja (abertura/fechamento de cada dia, dias fechados) e a " +
                              "operação em Lojas → Operação (turnos personalizados: duração da jornada com o intervalo, intervalo e entradas flexíveis).");
                sugestoes.Add("A jornada personalizada (sem o intervalo) precisa caber no limite diário das regras (ex.: 8h20 com 1h de intervalo = " +
                              "7h20 pagas); os turnos do catálogo precisam começar dentro do expediente. Ou desligue \"Personalizar turnos\" na " +
                              "operação: os turnos passam a ser calculados pelo horário da loja e pelas regras.");
            }
        }
        if (tipos.Contains("perfil"))
        {
            var kinds = shown.Where(i => i.Tipo == "perfil")
                .Select(i => i.Mensagem.StartsWith("Abertura") ? "Abertura" : i.Mensagem.StartsWith("Fechamento") ? "Fechamento" : "Flexível")
                .Distinct().ToList();
            sugestoes.Add($"Perfil de jornada: com a operação, Abertura só pega turnos que começam na abertura, Fechamento só os que terminam no " +
                          $"fechamento e Flexível só os intermediários. Passe colaboradores para {string.Join(" / ", kinds)} em Colaboradores → Jornada " +
                          "(o alerta da página Colaboradores mostra os setores sem Abertura/Fechamento).");
            if (!ctx.Rotation)
                sugestoes.Add("Ou ligue o rodízio de abertura/fechamento na operação da loja (Lojas → Operação): quando faltar gente com o perfil, " +
                              "o motor distribui as aberturas/fechamentos entre os colaboradores do setor.");
        }
        if (tipos.Contains("capacidade") || tipos.Contains("cobertura"))
        {
            sugestoes.Add("Aumente o quadro do setor (veja o dimensionamento) ou mude o perfil de jornada de alguém para cobrir os horários que faltam.");
            sugestoes.Add("Confira a disponibilidade semanal e as ausências (férias, afastamentos) do período.");
            if (ctx.OperationMode)
                sugestoes.Add("Se a operação permitir, reduza a cobertura mínima ou os mínimos de abertura/fechamento, ou ajuste o expediente e as entradas flexíveis.");
        }
        if (tipos.Contains("celulas_manuais"))
            sugestoes.Add("Gere a escala completa (substituindo as células manuais) ou ajuste as células manuais citadas.");
        if (tipos.Contains("disponibilidade"))
            sugestoes.Add("Há célula manual fora da disponibilidade semanal do colaborador: ajuste a célula ou a disponibilidade.");
        if (tipos.Contains("continuidade"))
        {
            var lados = shown.Where(i => i.Tipo == "continuidade").Select(i => i.Mensagem.Contains("escala anterior") ? "anterior" : "seguinte")
                .Distinct().Order().ToList();
            sugestoes.Add($"Continuidade (emenda): o que já está escalado na escala {string.Join(" e na ", lados)} limita os " +
                          $"{(lados.Count == 2 ? "primeiros e os últimos" : lados[0] == "anterior" ? "primeiros" : "últimos")} dias desta escala. " +
                          "Ajuste a escala vizinha (reabra a aprovada ou gere-a de novo), escolha outra escala como principal do setor nesse período " +
                          "ou gere um período que inclua a emenda.");
        }
        if (tipos.Contains("regra"))
            sugestoes.Add("Revise as regras citadas no cadastro de regras de conformidade (só o superAdmin altera).");
        if (sugestoes.Count == 0)
            sugestoes.Add("Revise o quadro, as ausências, as células manuais e as regras do período.");
        return new InfeasibilityDiagnosis(resumo, shown, sugestoes);
    }

    /// <summary>
    /// Picos manuais (horários de pico cadastrados pelo usuário) que a escala não atende: por pico e quantidade, o máximo
    /// possível (pessoas do setor que podem estar presentes em todas as faixas do pico no dia: ausências, perfil/rodízio,
    /// disponibilidade, regras rígidas por turno, expediente) ou, quando a gente existe mas a escala não alcançou, o que ela
    /// atende (<paramref name="deficits"/>: faltas lidas da solução). Ex.: "Pico manual 11:00–14:00 de sábado pede 4,
    /// máximo possível 3 (14/11, 21/11)." O pico manual é preferencial: a escala sai mesmo assim.
    /// </summary>
    public static List<string> ManualPeakNotices(PlanningContext ctx, IReadOnlyList<DeficitDto>? deficits = null)
    {
        var allowed = new Dictionary<(int E, int D), List<ShiftInfo>>();
        List<ShiftInfo> AllowedOf(EmployeeInfo e, int d)
        {
            if (!allowed.TryGetValue((e.Index, d), out var list)) allowed[(e.Index, d)] = list = Allowed(ctx, e, d, ignoreProfile: false, ctx.Rotation);
            return list;
        }
        var missing = (deficits ?? []).GroupBy(x => (x.Date, Minutes: x.SlotStart.Hour * 60 + x.SlotStart.Minute)).ToDictionary(g => g.Key, g => g.Sum(x => x.Missing));
        var found = new List<(string Peak, int Need, int Value, bool Capacity, DateOnly Date)>();
        // Só os dias do período (dia de borda é da escala vizinha)
        foreach (var g in ctx.Slots.Where(s => ctx.InPeriod(s.Day) && s.ManualPeak is not null && s.Required > s.Minimum).GroupBy(s => (Peak: s.ManualPeak!, s.Day)))
        {
            var need = g.Max(s => s.Required);
            var capacity = g.Min(slot => ctx.Employees.Count(e => e.Sectors.Contains(slot.Sector)
                                                                 && AllowedOf(e, slot.Day).Any(t => PlanningContext.Covers(t, slot))));
            var date = ctx.Dates[g.Key.Day];
            if (capacity < need)
            {
                found.Add((g.Key.Peak, need, capacity, true, date));
                continue;
            }
            var served = g.Min(s => s.Required - missing.GetValueOrDefault((date, s.SlotStart)));
            if (deficits is not null && served < need) found.Add((g.Key.Peak, need, served, false, date));
        }
        return found.GroupBy(x => (x.Peak, x.Need, x.Value, x.Capacity))
            .Select(g => $"{g.Key.Peak} pede {g.Key.Need}, {(g.Key.Capacity ? "máximo possível" : "a escala atende")} {g.Key.Value} " +
                         $"({string.Join(", ", g.Select(x => x.Date.ToString("dd/MM")).Distinct().Take(6))}{(g.Count() > 6 ? "..." : "")}).")
            .ToList();
    }
}

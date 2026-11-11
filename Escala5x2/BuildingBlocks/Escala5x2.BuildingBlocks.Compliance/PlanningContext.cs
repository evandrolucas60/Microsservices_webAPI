using Escala5x2.BuildingBlocks.Contracts.Compliance;
using Escala5x2.BuildingBlocks.Contracts.Planning;
using Escala5x2.BuildingBlocks.Contracts.Scheduling;

namespace Escala5x2.BuildingBlocks.Compliance;

/// <summary><paramref name="Kind"/>: tipo do turno na operação (Abertura/Fechamento/Flexivel; null = sem operação ou fora dela).</summary>
public sealed record ShiftInfo(int Index, ShiftSnapshot Snapshot, int StartMin, int EndMin, int PaidMinutes)
{
    public string? Kind => Snapshot.Tipo;
    public int Duration => EndMin - StartMin;
}

/// <summary>
/// Faixa de cobertura: <paramref name="SlotMinutes"/> vem da regra cobertura_faixa_minutos do dia.
/// <list type="bullet">
/// <item><see cref="Minimum"/>: presentes exigidos de forma RÍGIDA (restrição dura no motor, verificação rígida no
/// avaliador, chave <see cref="MinimumKey"/>). Com operação: cobertura_minima / minimo_abertura / minimo_fechamento.
/// Sem operação (modelo anterior): a demanda inteira quando cobertura_faixa_minutos é rígida.</item>
/// <item><paramref name="Required"/>: alvo da faixa (≥ <see cref="Minimum"/>). O que passa do mínimo é PREFERENCIAL
/// (reforço de pico: déficit penalizado, chave <see cref="DemandKey"/>).</item>
/// <item><paramref name="Rigid"/>: a demanda inteira é rígida (modelo anterior com cobertura_faixa_minutos rígida).</item>
/// </list>
/// Presença: o turno cobre a faixa inteira e a pessoa não está em intervalo em nenhum momento dela.
/// </summary>
public sealed record CoverageSlot(int Sector, int Day, int SlotStart, int SlotMinutes, int Required, bool Rigid)
{
    public int Minimum { get; init; } = Rigid ? Required : 0;
    public string MinimumKey { get; init; } = RuleKeys.CoberturaFaixa;
    public string DemandKey { get; init; } = RuleKeys.CoberturaFaixa;
    /// <summary>Faixa com pico manual cadastrado (o alvo vem dele): rótulo para avisos ("Pico manual 11:00–14:00 de sábado"); null = sem.</summary>
    public string? ManualPeak { get; init; }
    public int SlotEnd => SlotStart + SlotMinutes;
}

/// <summary>Posição possível do intervalo: início absoluto e se cumpre também as regras de posição preferenciais.</summary>
public readonly record struct BreakOption(int Start, bool Recommended);

/// <summary>Turno já alocado em outra escala viva (outro setor) num dia do horizonte.</summary>
public sealed record BusyDay(int Start, int EndAbs, int PaidMinutes, string SectorName);

/// <summary>Regra que deixa de ser cumprida quando a pessoa faz o turno naquele dia.</summary>
public readonly record struct ShiftIssue(string Key, bool Rigid, int Limit);

/// <summary>Horários de uma célula em minutos absolutos do dia (saída e intervalo de turno que vira o dia passam de 1440).</summary>
public readonly record struct CellTimes(int Start, int EndAbs, int BreakMinutes, int? BreakStart)
{
    public int Duration => EndAbs - Start;
    /// <summary>Jornada líquida: o intervalo não é computado na jornada (CLT art. 71, §2º).</summary>
    public int Worked => Duration - BreakMinutes;
    public int? BreakEnd => BreakStart + BreakMinutes;
    /// <summary>A pessoa está em intervalo em algum momento de [from, to).</summary>
    public bool OnBreak(int from, int to) => BreakStart is { } b && BreakMinutes > 0 && b < to && b + BreakMinutes > from;
}

/// <summary>
/// Regime da semana civil (ver <see cref="PlanningContext.Regime"/>): <paramref name="Load"/> = carga semanal usada (null =
/// bloqueada), <paramref name="Threshold"/> = limiar do 6x1 (null = bloqueado), <paramref name="RestDays"/> = número exato
/// de folgas (null = regra do regime bloqueada), <paramref name="ConsecutiveKey"/> = chave do máximo de dias seguidos,
/// <paramref name="SundayExtra"/> = folga_extra_domingo_6x1 aplicável (só 6x1).
/// </summary>
public sealed record WeekRegime(int Week, DateOnly Start, string Name, RuleValue? Load, RuleValue? Threshold, RuleValue? RestDays,
    string ConsecutiveKey, RuleValue? SundayExtra = null);

public sealed class EmployeeInfo
{
    public required int Index { get; init; }
    public required EmployeeSnapshot Snapshot { get; init; }
    public required int[] Sectors { get; init; }
    /// <summary>Dias de férias/afastamento/licença/indisponibilidade (UnavailableDates ∪ Absences da fotografia).</summary>
    public required HashSet<DateOnly> Unavailable { get; init; }
    /// <summary>Tipo da ausência por dia (fotografias antigas, sem tipo: Indisponivel).</summary>
    public required Dictionary<DateOnly, string> AbsenceTypes { get; init; }
    /// <summary>Dias trabalhados antes do horizonte: (início, fim absoluto em minutos a partir da meia-noite do dia).</summary>
    public required Dictionary<DateOnly, (int Start, int EndAbs)> History { get; init; }
    /// <summary>Dias do horizonte (índice) em que a pessoa já está em outro setor.</summary>
    public required Dictionary<int, BusyDay> Busy { get; init; }
    /// <summary>Dias trabalhados em escalas publicadas/aprovadas POSTERIORES ao horizonte (continuidade com a semana seguinte).</summary>
    public Dictionary<DateOnly, (int Start, int EndAbs)> Following { get; init; } = [];
    /// <summary>Folgas de descanso registradas na escala anterior (junto do início; max_folgas_seguidas na emenda).</summary>
    public HashSet<DateOnly> HistoryRest { get; init; } = [];
    /// <summary>Folgas de descanso registradas na escala seguinte (junto do fim; max_folgas_seguidas na emenda).</summary>
    public HashSet<DateOnly> FollowingRest { get; init; } = [];
    /// <summary>
    /// Dias de borda (índices do horizonte fora do período, ver <see cref="PlanningContext.PeriodStart"/>) com folga de
    /// descanso registrada na escala vizinha: folga fixa no motor e folga no avaliador.
    /// </summary>
    public HashSet<int> EdgeRest { get; init; } = [];
    /// <summary>
    /// Dias de borda sem escala vizinha (nem trabalho nem folga registrados): livres no motor (o resultado nesses dias não é
    /// gravado) e, no avaliador, nem folga nem trabalho — as contas da semana parcial aceitam qualquer valor possível neles.
    /// </summary>
    public HashSet<int> EdgeUnknown { get; init; } = [];
    /// <summary>Perfil de jornada (null = fotografia sem o dado: qualquer turno da operação).</summary>
    public string? Profile => Snapshot.Jornada?.PerfilJornada;
    /// <summary>Disponibilidade por dia da semana (dia ausente = disponível o dia inteiro).</summary>
    public Dictionary<DayOfWeek, AvailabilityWindow> Availability { get; init; } = [];
    public JourneyPreferences? Preferences => Snapshot.Jornada?.Preferencias;
    public decimal? CostPerHour => Snapshot.Jornada?.CustoHora;
}

/// <summary>
/// Tudo que o modelo e o avaliador precisam, já indexado. Lança exceção se a fotografia for inconsistente.
/// Nenhum parâmetro legal é fixo aqui: todos vêm do cadastro de regras (<see cref="Rules"/>), resolvidos por dia.
/// </summary>
public sealed partial class PlanningContext
{
    public const int MinutesPerDay = 24 * 60;

    public required PlanningSnapshot Snapshot { get; init; }
    public required DateOnly Start { get; init; }
    public required int Weeks { get; init; }
    public int Days => Weeks * 7;

    /// <summary>
    /// Período da escala (ex.: o mês civil, de 01 a 28/29/30/31). O horizonte do modelo (<see cref="Start"/>, <see cref="Dates"/>)
    /// vai da segunda-feira da semana do primeiro dia ao domingo da semana do último (<see cref="Horizon"/>): as semanas civis
    /// parciais nas pontas do mês entram INTEIRAS nas contas da semana (folgas exatas do 5x2/6x1, piso de folgas, carga
    /// semanal, regime, folgas seguidas), junto com os dias do mês vizinho — sem contar duas vezes nem deixar de contar.
    /// Os dias do horizonte fora do período ("dias de borda") não são desta escala: o que a escala anterior/seguinte
    /// registrou neles vale como trabalho (<see cref="EmployeeInfo.Busy"/>) ou folga (<see cref="EmployeeInfo.EdgeRest"/>);
    /// sem escala vizinha o dia fica livre no motor e indefinido no avaliador (<see cref="EmployeeInfo.EdgeUnknown"/>).
    /// Dia de borda não tem célula gravada nem é medido pelo avaliador; só tem faixa de cobertura (para o motor, como meta,
    /// nunca restrição dura: <see cref="HardCoverage"/>) quando não há escala vizinha nele. Escala de segunda a domingo (as antigas, por semanas): período = horizonte, sem borda.
    /// </summary>
    public required DateOnly PeriodStart { get; init; }
    public required DateOnly PeriodEnd { get; init; }
    /// <summary>Índice do horizonte do primeiro/último dia do período.</summary>
    public int FirstDay => PeriodStart.DayNumber - Start.DayNumber;
    public int LastDay => PeriodEnd.DayNumber - Start.DayNumber;
    /// <summary>O dia (índice do horizonte) é do período da escala (não é dia de borda nem fora do horizonte).</summary>
    public bool InPeriod(int d) => d >= FirstDay && d <= LastDay;
    /// <summary>Dias do período (o mês: 28, 29, 30 ou 31).</summary>
    public int PeriodDays => LastDay - FirstDay + 1;

    /// <summary>Horizonte do período: segunda-feira da semana do primeiro dia e número de semanas civis inteiras até o domingo.</summary>
    public static (DateOnly Start, int Weeks) Horizon(DateOnly from, DateOnly to)
    {
        var start = from.AddDays(-(((int)from.DayOfWeek + 6) % 7));
        var end = to.AddDays((7 - (int)to.DayOfWeek) % 7);
        return (start, (end.DayNumber - start.DayNumber + 1) / 7);
    }

    /// <summary>Mês civil do dia: primeiro e último dia (fevereiro com 28 ou 29, meses de 30 e 31 dias).</summary>
    public static (DateOnly From, DateOnly To) MonthOf(DateOnly day)
    {
        var from = new DateOnly(day.Year, day.Month, 1);
        return (from, from.AddDays(DateTime.DaysInMonth(day.Year, day.Month) - 1));
    }
    public required DateOnly[] Dates { get; init; }
    public required SectorSnapshot[] Sectors { get; init; }
    public required ShiftInfo[] Shifts { get; init; }
    public required EmployeeInfo[] Employees { get; init; }
    public required RuleBook Rules { get; init; }
    /// <summary>Feriado sem autorização com a regra feriado_trabalho_exige_autorizacao ativa.</summary>
    public required bool[] DayBlocked { get; init; }
    public required List<CoverageSlot> Slots { get; init; }
    public required bool StoreHasExtendedDayAgreement { get; init; }
    /// <summary>Células fixas (geração "somente_vazios" e sugestões): turno (índice) ou null = folga.</summary>
    public required Dictionary<(int E, int D), int?> Fixed { get; init; }
    /// <summary>Intervalo ajustado à mão nas células fixas: início absoluto (o motor mantém a posição).</summary>
    public Dictionary<(int E, int D), int> FixedBreaks { get; init; } = [];
    public required List<string> Warnings { get; init; }
    /// <summary>Operação do setor (null = modelo anterior: só a demanda, qualquer pessoa em qualquer turno).</summary>
    public OperationSnapshot? Operation { get; init; }
    public bool OperationMode => Operation is not null;
    /// <summary>
    /// Mínimo de dias antes/depois do horizonte considerados na continuidade com a escala anterior/seguinte. O valor usado
    /// é <see cref="ContinuityDays"/>: maior quando alguma regra de janela ativa precisa de mais (ex.: domingo a cada 5 semanas).
    /// </summary>
    public const int FollowingDays = 21;

    /// <summary>Teto da continuidade (26 semanas): regra com valor absurdo não faz ler meio ano de escalas.</summary>
    public const int MaxContinuityDays = 26 * 7;

    /// <summary>
    /// Dias antes e depois do horizonte que a continuidade precisa cobrir para TODAS as regras de janela ativas: a maior
    /// janela entre domingo_folga_janela_semanas / domingo_quinzenal_janela_semanas (N semanas = 7·N dias), dias seguidos
    /// (dias_consecutivos_max* + 1) e max_folgas_seguidas (+ 1), nunca menos que <see cref="FollowingDays"/> (21) nem mais
    /// que <see cref="MaxContinuityDays"/>. Usado pelo Scheduling.Api para ler o histórico/escala seguinte e pelo motor e
    /// avaliador para os dias depois do horizonte.
    /// </summary>
    public static int ContinuityDaysFor(IEnumerable<ComplianceRuleSnapshot> rules)
    {
        var days = FollowingDays;
        foreach (var r in rules.Where(r => r.Status == RuleStatus.Ativo))
        {
            var value = (int)Math.Ceiling(Math.Max(0, r.Valor));
            var needed = r.Chave switch
            {
                RuleKeys.DomingoFolgaJanela or RuleKeys.DomingoQuinzenalJanela => 7 * value,
                RuleKeys.DiasConsecutivosMax or RuleKeys.DiasConsecutivosMax5x2 or RuleKeys.DiasConsecutivosMax6x1 => value + 1,
                RuleKeys.MaxFolgasSeguidas => value + 1,
                _ => 0
            };
            days = Math.Max(days, needed);
        }
        return Math.Min(days, MaxContinuityDays);
    }

    /// <summary>
    /// Dias junto da emenda em que as folgas de descanso da escala anterior/seguinte são levantadas (max_folgas_seguidas):
    /// <see cref="ContinuityHistory.RestDaysWindow"/> (7) ou max_folgas_seguidas + 1, o que for maior.
    /// </summary>
    public static int RestWindowFor(IEnumerable<ComplianceRuleSnapshot> rules) =>
        Math.Min(MaxContinuityDays, Math.Max(ContinuityHistory.RestDaysWindow,
            rules.Where(r => r.Status == RuleStatus.Ativo && r.Chave == RuleKeys.MaxFolgasSeguidas)
                .Select(r => (int)Math.Ceiling(Math.Max(0, r.Valor)) + 1).DefaultIfEmpty(0).Max()));

    /// <summary>Dias depois (e antes) do horizonte considerados na continuidade desta fotografia (<see cref="ContinuityDaysFor"/>).</summary>
    public int ContinuityDays { get; init; } = FollowingDays;

    /// <summary>Dias junto da emenda com folgas registradas (<see cref="RestWindowFor"/>).</summary>
    public int RestWindow { get; init; } = ContinuityHistory.RestDaysWindow;

    /// <summary>
    /// Texto da emenda para avisos/diagnósticos quando a verificação envolve dias fora do horizonte: "na emenda com a escala
    /// anterior (início em 02/11)" / "seguinte (fim em 29/11)" / as duas. null = só dias do horizonte.
    /// </summary>
    public string? SeamText(IEnumerable<int> days)
    {
        var list = days as IReadOnlyCollection<int> ?? days.ToList();
        // Dias de borda (semana parcial nas pontas do mês) também são da escala vizinha
        var before = list.Any(d => d < FirstDay);
        var after = list.Any(d => d > LastDay);
        if (!before && !after) return null;
        var anterior = $"a escala anterior (emenda em {PeriodStart:dd/MM})";
        var seguinte = $"a escala seguinte (emenda em {PeriodEnd:dd/MM})";
        return before && after ? $"continuidade com {anterior} e {seguinte}" : $"continuidade com {(before ? anterior : seguinte)}";
    }

    public static int ToMinutes(TimeOnly t) => t.Hour * 60 + t.Minute;

    public static int EndAbs(int start, int end) => end <= start ? end + MinutesPerDay : end;

    /// <summary>Minutos livres entre um turno que termina em prevEndAbs (dia D) e outro que começa em nextStart (dia D+1).</summary>
    public static int Gap(int prevEndAbs, int nextStart) => MinutesPerDay - prevEndAbs + nextStart;

    public static bool Covers(int shiftStart, int shiftEndAbs, CoverageSlot slot) =>
        shiftStart <= slot.SlotStart && slot.SlotStart + slot.SlotMinutes <= shiftEndAbs;

    public static bool Covers(ShiftInfo t, CoverageSlot slot) => Covers(t.StartMin, t.EndMin, slot);

    /// <summary>Início do intervalo em minutos absolutos do dia da célula (madrugada de turno que vira o dia passa de 1440).</summary>
    public static int BreakAbs(int shiftStart, TimeOnly breakStart)
    {
        var m = ToMinutes(breakStart);
        return m < shiftStart ? m + MinutesPerDay : m;
    }

    private Dictionary<Guid, ShiftInfo>? _shiftsById;
    private readonly Dictionary<int, IntervalPolicy> _intervalPolicies = [];

    public ShiftInfo? ShiftById(Guid shiftId) =>
        (_shiftsById ??= Shifts.ToDictionary(s => s.Snapshot.ShiftId)).GetValueOrDefault(shiftId);

    /// <summary>Regras de intervalo ativas no dia (índice do horizonte).</summary>
    public IntervalPolicy Interval(int day)
    {
        if (!_intervalPolicies.TryGetValue(day, out var p)) _intervalPolicies[day] = p = new IntervalPolicy(Rules, day);
        return p;
    }

    /// <summary>
    /// Horários de uma célula de trabalho: entrada, saída (absoluta), intervalo efetivo (o da célula ou, sem ele,
    /// o do turno; turno fora da fotografia: duração − minutos pagos) e início do intervalo (null = não posicionado
    /// ou fora da jornada).
    /// </summary>
    public CellTimes Times(AssignmentDto a)
    {
        var start = ToMinutes(a.Start);
        var end = EndAbs(start, ToMinutes(a.End));
        var brk = a.BreakMinutes ?? ShiftById(a.ShiftId)?.Snapshot.BreakMinutes ?? Math.Max(0, end - start - a.PaidMinutes);
        brk = Math.Clamp(brk, 0, end - start);
        int? bs = a.BreakStart is { } t ? BreakAbs(start, t) : null;
        if (bs is { } b && (brk == 0 || b + brk > end)) bs = null;
        return new CellTimes(start, end, brk, bs);
    }

    public int DayIndex(DateOnly date) => date.DayNumber - Start.DayNumber;

    public bool IsSunday(int day) => Dates[day].DayOfWeek == DayOfWeek.Sunday;

    /// <summary>
    /// Férias, afastamentos (inclusive afastamento médico) e indisponibilidades (estrutural: não é regra do cadastro).
    /// Dia indisponível não recebe turno e conta como dia NÃO trabalhado: não soma jornada e interrompe a sequência de
    /// dias consecutivos. Na semana: com número exato de folgas (<see cref="RestDays"/>: regime 6x1/5x2) o afastamento
    /// não é folga e reduz os dias de trabalho (ver <see cref="NonWorkable"/>). Em nenhum caso o afastamento é marcado
    /// como folga.
    /// </summary>
    public bool IsAvailable(EmployeeInfo e, int day) => !e.Unavailable.Contains(Dates[day]);

    /// <summary>
    /// Dia que não é de trabalho nem de folga na conta da escala 5x2 estrita: feriado sem autorização (regra
    /// feriado_trabalho_exige_autorizacao ativa) ou ausência (férias, afastamento, licença...). Dia já ocupado em
    /// outro setor/escala conta como TRABALHADO (não entra aqui), mesmo que seja feriado ou ausência.
    /// </summary>
    public bool NonWorkable(EmployeeInfo e, int day) => !e.Busy.ContainsKey(day) && (DayBlocked[day] || !IsAvailable(e, day));

    /// <summary>Tipo da ausência da pessoa no dia (null = disponível).</summary>
    public string? AbsenceType(EmployeeInfo e, int day) => e.AbsenceTypes.GetValueOrDefault(Dates[day]);

    // ---------------- Perfil de jornada, disponibilidade e continuidade ----------------

    /// <summary>
    /// O perfil permite o turno: sem operação, qualquer turno; com operação, só turnos do tipo do perfil (Abertura,
    /// Fechamento ou Flexível); pessoa sem perfil na fotografia: qualquer turno com tipo. Turno sem tipo: só manual.
    /// </summary>
    public bool ProfileAllows(EmployeeInfo e, ShiftInfo t) =>
        !OperationMode || (t.Kind is not null && (e.Profile is null || e.Profile == t.Kind));

    /// <summary>
    /// Rodízio de abertura/fechamento ligado na operação (<see cref="OperationSnapshot.RodizioAberturaFechamento"/>): o
    /// perfil deixa de ser restrição dura — qualquer colaborador do setor pode receber turno com tipo de outro perfil,
    /// com penalidade no motor (<see cref="OutOfProfile"/>).
    /// </summary>
    public bool Rotation => Operation?.RodizioAberturaFechamento == true;

    /// <summary>
    /// O turno pode ser escalado para a pessoa pelo perfil: <see cref="ProfileAllows"/> ou, com o rodízio ligado, qualquer
    /// turno com tipo da operação (fora do perfil). Turno sem tipo continua só manual.
    /// </summary>
    public bool ShiftAllowed(EmployeeInfo e, ShiftInfo t) => ProfileAllows(e, t) || (Rotation && RotationAllows(e, t));

    private HashSet<int>? _sectorsWithFlexible;

    /// <summary>
    /// No rodízio, o turno fora do perfil que a pessoa pode receber: abertura e fechamento sempre (é o rodízio); horário
    /// intermediário (Flexível) para quem é Abertura/Fechamento só quando nenhum setor da pessoa tem colaborador Flexível
    /// (ex.: setor só com Abertura/Fechamento, para cobrir os almoços). Com Flexível no setor, os intermediários ficam com
    /// eles — e o modelo não cresce com ~25 entradas flexíveis por dia para cada Abertura/Fechamento.
    /// </summary>
    public bool RotationAllows(EmployeeInfo e, ShiftInfo t)
    {
        if (t.Kind is null) return false;
        if (t.Kind != JourneyProfiles.Flexivel) return true;
        _sectorsWithFlexible ??= Employees.Where(x => x.Profile == JourneyProfiles.Flexivel).SelectMany(x => x.Sectors).ToHashSet();
        return !e.Sectors.Any(_sectorsWithFlexible.Contains);
    }

    /// <summary>Turno com tipo diferente do perfil da pessoa (com operação; pessoa sem perfil na fotografia: nunca).</summary>
    public bool OutOfProfile(EmployeeInfo e, ShiftInfo t) =>
        OperationMode && t.Kind is not null && e.Profile is not null && e.Profile != t.Kind;

    /// <summary>
    /// A disponibilidade semanal permite o turno no dia: dia marcado indisponível = não; com janela, o turno inteiro
    /// cabe nela. O dia indisponível na disponibilidade semanal é dia normal (vira folga), não ausência.
    /// </summary>
    public bool AvailableAt(EmployeeInfo e, int day, ShiftInfo t)
    {
        if (!e.Availability.TryGetValue(Dates[day].DayOfWeek, out var w)) return true;
        if (!w.Disponivel) return false;
        if (w.Inicio is { } from && t.StartMin < ToMinutes(from)) return false;
        if (w.Fim is { } to && t.EndMin > (ToMinutes(to) == 0 ? MinutesPerDay : ToMinutes(to))) return false;
        return true;
    }

    /// <summary>Texto da disponibilidade do dia para mensagens ("indisponível" ou "das 08:00 às 14:00").</summary>
    public string AvailabilityText(EmployeeInfo e, int day) =>
        !e.Availability.TryGetValue(Dates[day].DayOfWeek, out var w) ? "disponível"
        : !w.Disponivel ? "indisponível"
        : $"das {w.Inicio?.ToString("HH:mm") ?? "00:00"} às {w.Fim?.ToString("HH:mm") ?? "24:00"}";

    /// <summary>Trabalho numa escala posterior (d ≥ Days), em minutos absolutos do dia d.</summary>
    public (int Start, int EndAbs)? FollowingAt(EmployeeInfo e, int d) =>
        d >= Days && e.Following.TryGetValue(Start.AddDays(d), out var f) ? f : null;

    private readonly Dictionary<(int Day, int Shift, int Break), List<BreakOption>> _breakOptions = [];

    /// <summary>
    /// Inícios possíveis do intervalo do turno no dia (de 15 em 15 minutos no relógio, dentro da jornada) que cumprem as
    /// regras de posição RÍGIDAS ativas; <see cref="BreakOption.Recommended"/> = cumpre também as preferenciais.
    /// Lista vazia = nenhuma posição legal (o turno não pode ser usado com esse intervalo).
    /// </summary>
    public List<BreakOption> BreakOptions(int day, ShiftInfo t, int breakMinutes)
    {
        if (_breakOptions.TryGetValue((day, t.Index, breakMinutes), out var cached)) return cached;
        var list = new List<BreakOption>();
        if (breakMinutes > 0)
        {
            var policy = Interval(day);
            var first = (t.StartMin + BreakPlanner.StepMinutes - 1) / BreakPlanner.StepMinutes * BreakPlanner.StepMinutes;
            for (var s = first; s + breakMinutes <= t.EndMin; s += BreakPlanner.StepMinutes)
            {
                if (!policy.PositionOk(t.StartMin, t.EndMin, s, breakMinutes, rigidOnly: true)) continue;
                list.Add(new BreakOption(s, policy.PositionOk(t.StartMin, t.EndMin, s, breakMinutes, rigidOnly: false)));
            }
        }
        _breakOptions[(day, t.Index, breakMinutes)] = list;
        return list;
    }

    // ---------------- Regras por dia/semana ----------------

    /// <summary>carga_horaria_semanal em minutos. Semana de virada (ex.: 14 a 20/12/2026) usa o limite mais restritivo da semana.</summary>
    public RuleValue? WeeklyLimit(int week) => Enumerable.Range(week * 7, 7)
        .Select(d => Rules.HoursAsMinutes(RuleKeys.CargaHorariaSemanal, d)).Where(v => v is not null)
        .OrderBy(v => v!.Value.Value).FirstOrDefault();

    /// <summary>folgas_minimas_semana (piso legal do DSR): o maior valor vigente na semana.</summary>
    public RuleValue? MinRestDays(int week) => Enumerable.Range(week * 7, 7)
        .Select(d => Rules.Value(RuleKeys.FolgasMinimasSemana, d)).Where(v => v is not null)
        .OrderByDescending(v => v!.Value.Value).FirstOrDefault();

    // ---------------- Regime da semana (6x1 / 5x2) ----------------

    public const string Regime6x1 = "6x1";
    public const string Regime5x2 = "5x2";

    private bool? _usesRegimes;
    private WeekRegime[]? _regimes;

    /// <summary>
    /// A fotografia tem o modelo de regime (alguma chave de <see cref="RuleKeys.Regime"/>, ativa ou bloqueada).
    /// Fotografias antigas (sem elas) seguem o modelo anterior: folgas_maximas_semana e dias_consecutivos_max.
    /// </summary>
    public bool UsesRegimes => _usesRegimes ??= Rules.All.Any(r => RuleKeys.Regime.Contains(r.Chave));

    /// <summary>
    /// Regime da semana civil: 6x1 quando a carga_horaria_semanal da semana (<see cref="WeeklyLimit"/>: na semana de
    /// virada, a mais restritiva) é ≥ carga_minima_regime_6x1_horas (na semana, o maior limiar vigente); senão 5x2.
    /// Comportamento com regras bloqueadas (documentado no cadastro):
    /// <list type="bullet">
    /// <item>limiar bloqueado/ausente, ou carga semanal bloqueada na semana → 5x2 (regime padrão do contrato);</item>
    /// <item>folgas_semana_&lt;regime&gt; bloqueada → sem número exato: vale só o piso folgas_minimas_semana
    /// (o motor pode dar folgas extras);</item>
    /// <item>dias_consecutivos_max_&lt;regime&gt; bloqueada → sem limite de dias seguidos nos dias daquele regime.</item>
    /// </list>
    /// Fotografia sem o modelo de regime: null.
    /// </summary>
    public WeekRegime? Regime(int week)
    {
        if (!UsesRegimes || week < 0 || week >= Weeks) return null;
        _regimes ??= Enumerable.Range(0, Weeks).Select(BuildRegime).ToArray();
        return _regimes[week];
    }

    private WeekRegime BuildRegime(int week)
    {
        var days = Enumerable.Range(week * 7, 7).ToList();
        var load = WeeklyLimit(week);
        var threshold = days.Select(d => Rules.HoursAsMinutes(RuleKeys.CargaMinimaRegime6x1, d)).Where(v => v is not null)
            .OrderByDescending(v => v!.Value.Value).FirstOrDefault();
        var name = load is { } l && threshold is { } t && l.Value >= t.Value ? Regime6x1 : Regime5x2;
        var restKey = name == Regime6x1 ? RuleKeys.FolgasSemana6x1 : RuleKeys.FolgasSemana5x2;
        // Vigência que muda no meio da semana: vale o maior número de folgas (mais protetivo)
        var rest = days.Select(d => Rules.Value(restKey, d)).Where(v => v is not null)
            .OrderByDescending(v => v!.Value.Value).FirstOrDefault();
        // O piso legal do DSR prevalece sobre o número do regime
        if (rest is { } r && MinRestDays(week) is { } min && min.Value > r.Value) rest = r with { Value = min.Value };
        var extra = name == Regime6x1 && rest is not null
            ? days.Select(d => Rules.Value(RuleKeys.FolgaExtraDomingo6x1, d)).FirstOrDefault(v => v is { Value: not 0 })
            : null;
        return new WeekRegime(week, Dates[week * 7], name, load, threshold, rest,
            name == Regime6x1 ? RuleKeys.DiasConsecutivosMax6x1 : RuleKeys.DiasConsecutivosMax5x2, extra);
    }

    /// <summary>
    /// Folga extra de domingo no 6x1 (folga_extra_domingo_6x1 ativa e ≠ 0): na semana em que a pessoa folga no domingo
    /// (dia normal), as folgas podem ser o número do regime + 1. null = não se aplica (5x2, regra bloqueada/0 ou sem
    /// número exato).
    /// </summary>
    public RuleValue? SundayExtraDayOff(int week) => Regime(week)?.SundayExtra;

    /// <summary>Regime de cada semana para as telas (vazio quando a fotografia não tem o modelo de regime).</summary>
    public List<WeekRegimeDto> WeekRegimes() =>
        !UsesRegimes ? [] : Enumerable.Range(0, Weeks).Select(w =>
        {
            var r = Regime(w)!;
            return new WeekRegimeDto(r.Start, r.Name, r.Load is { } l ? l.Value / 60m : null, r.RestDays?.Value,
                Rules.Value(r.ConsecutiveKey, w * 7)?.Value, r.Threshold is { } t ? t.Value / 60m : null, r.SundayExtra is not null);
        }).ToList();

    /// <summary>
    /// Número EXATO de folgas da semana (colaborador × semana civil), ou null quando só vale o piso
    /// folgas_minimas_semana. Modelo de regime: folgas_semana_6x1/_5x2 conforme o regime (nunca abaixo do piso).
    /// Fotografia antiga: folgas_maximas_semana (5x2 estrito, nunca abaixo do piso).
    /// <para>Conta da semana: dias normais = 7 − dias <see cref="NonWorkable"/>; folgas = dias normais sem trabalho;
    /// dias trabalhados (no setor e em outros setores) = dias normais − folgas, nunca negativo. Ex. (5x2): semana
    /// comum → 5 dias; com 1 feriado sem autorização → 4; com 2 dias de afastamento → 3. No 6x1: 6, 5 e 4.</para>
    /// </summary>
    public RuleValue? RestDays(int week)
    {
        if (UsesRegimes) return Regime(week)?.RestDays;
        var max = Enumerable.Range(week * 7, 7)
            .Select(d => Rules.Value(RuleKeys.FolgasMaximasSemana, d)).Where(v => v is not null)
            .OrderBy(v => v!.Value.Value).FirstOrDefault();
        if (max is not { } m) return null;
        return MinRestDays(week) is { } min && min.Value > m.Value ? m with { Value = min.Value } : m;
    }

    /// <summary>Dias da semana (índices) que entram na conta de folgas (não são feriado sem autorização nem ausência).</summary>
    public List<int> NormalDays(EmployeeInfo e, int week) =>
        Enumerable.Range(week * 7, 7).Where(d => !NonWorkable(e, d)).ToList();

    /// <summary>Texto do regime para as mensagens: "regime 6x1 — 44h" (null = fotografia sem o modelo de regime).</summary>
    public string? RegimeLabel(int week) => Regime(week) is not { } r
        ? null
        : r.Load is { } l ? $"regime {r.Name} — {FormatHours(l.Value)}" : $"regime {r.Name}";

    private static string FormatHours(int minutes) => minutes % 60 == 0 ? $"{minutes / 60}h" : $"{minutes / 60}h{minutes % 60:00}";

    /// <summary>
    /// Teto da jornada diária com a compensação semanal quando hora_extra_max_diaria_minutos não está ativa (CLT art. 59,
    /// §2º: a compensação não passa de 10h por dia). Com a regra ativa, o teto é limite diário + hora extra máxima.
    /// </summary>
    public const int CompensationMaxDailyMinutes = 600;

    /// <summary>Folgas por semana quando nem o regime nem o piso do DSR estão ativos (o contrato é 5x2).</summary>
    public const int DefaultRestDaysPerWeek = 2;

    /// <summary>
    /// Dias de trabalho da semana civil pelo regime: 7 − folgas do regime (folgas_semana_5x2/_6x1, nunca abaixo do piso do
    /// DSR); sem elas, 7 − folgas_minimas_semana; sem nenhuma, 7 − <see cref="DefaultRestDaysPerWeek"/>.
    /// </summary>
    public int WorkDaysPerWeek(int week) => 7 - ((RestDays(week) ?? MinRestDays(week))?.Value ?? DefaultRestDaysPerWeek);

    /// <summary>Limite diário do cadastro: com norma coletiva na loja vale limite_diario_com_acordo_minutos (&gt; 0).</summary>
    private RuleValue? BaseDailyLimit(int day)
    {
        if (StoreHasExtendedDayAgreement && Rules.Value(RuleKeys.LimiteDiarioComAcordo, day) is { Value: > 0 } agreement)
            return agreement;
        return Rules.Value(RuleKeys.LimiteDiario, day);
    }

    /// <summary>
    /// Limite diário sem hora extra no dia (MESMO valor no motor, na verificação de configuração, nos turnos automáticos e
    /// no avaliador): limite_diario_minutos ou, com norma coletiva na loja, limite_diario_com_acordo_minutos (&gt; 0). Com
    /// compensacao_jornada_5x2 ativa e a semana civil 5x2 (<see cref="Regime"/>), vale o maior entre ele e a carga semanal
    /// da semana ÷ dias de trabalho do regime (<see cref="CompensatedDailyLimit"/>: 44h → 8h48, 42h → 8h24) — a origem do
    /// antigo erro "os turnos têm pelo menos 8h48 de jornada e o limite diário é 8h" com 44h no 5x2.
    /// </summary>
    public RuleValue? DailyLimit(int day)
    {
        var limit = BaseDailyLimit(day);
        return CompensatedDailyLimit(day, limit) ?? limit;
    }

    /// <summary>
    /// Limite diário com o acordo de compensação semanal do 5x2 (<see cref="RuleKeys.CompensacaoJornada5x2"/>): carga
    /// semanal da semana (na virada, a mais restritiva) ÷ (7 − folgas do regime), no máximo limite + hora extra máxima
    /// (sem a regra de hora extra, <see cref="CompensationMaxDailyMinutes"/>). null = não se aplica (regra bloqueada,
    /// semana 6x1/sem regime, sem limite diário) ou não passa do limite diário.
    /// </summary>
    public RuleValue? CompensatedDailyLimit(int day, RuleValue? limit)
    {
        if (limit is not { } l || day < 0 || day >= Days) return null;
        if (Rules.Value(RuleKeys.CompensacaoJornada5x2, day) is not { Value: not 0 }) return null;
        var week = day / 7;
        if (Regime(week) is not { Name: Regime5x2, Load: { } load }) return null;
        var workDays = WorkDaysPerWeek(week);
        if (workDays <= 0) return null;
        var cap = Rules.Value(RuleKeys.HoraExtraMaxDiaria, day) is { } overtime ? l.Value + overtime.Value : CompensationMaxDailyMinutes;
        var compensated = Math.Min(load.Value / workDays, cap);
        return compensated > l.Value ? new RuleValue(compensated, l.Rigid, RuleKeys.CompensacaoJornada5x2) : null;
    }

    /// <summary>
    /// Minutos pagos permitidos no dia: limite diário (<see cref="DailyLimit"/>, com a compensação do 5x2) ou, para quem
    /// tem acordo de prorrogação, o limite do cadastro + hora_extra_max_diaria_minutos (nunca abaixo do compensado).
    /// null = sem limite (sem controle de jornada, regra bloqueada ou hora extra sem teto).
    /// </summary>
    public RuleValue? AllowedDaily(EmployeeInfo e, int day)
    {
        if (!e.Snapshot.JourneyControlled || DailyLimit(day) is not { } limit) return null;
        if (!e.Snapshot.OvertimeAgreement) return limit;
        var baseLimit = BaseDailyLimit(day) ?? limit;
        return Rules.Value(RuleKeys.HoraExtraMaxDiaria, day) is { } cap
            ? new RuleValue(Math.Max(limit.Value, baseLimit.Value + cap.Value), cap.Rigid, RuleKeys.HoraExtraMaxDiaria)
            : null;
    }

    /// <summary>Hora extra possível na semana: 0 sem acordo; com acordo, teto diário × dias de trabalho (null = sem teto).</summary>
    public RuleValue? WeeklyOvertimeCap(EmployeeInfo e, int week)
    {
        if (!e.Snapshot.OvertimeAgreement) return new RuleValue(0, true, RuleKeys.CargaHorariaSemanal);
        if (Rules.Value(RuleKeys.HoraExtraMaxDiaria, week * 7) is not { } cap) return null;
        var workDays = 7 - ((RestDays(week) ?? MinRestDays(week))?.Value ?? 0);
        return cap with { Value = cap.Value * Math.Max(0, workDays) };
    }

    /// <summary>
    /// Máximo de dias seguidos para a sequência que termina no dia: no modelo de regime, dias_consecutivos_max_6x1 ou
    /// _5x2 conforme o regime da semana do dia (na virada entre regimes, a sequência que termina numa semana 5x2 já
    /// conta os dias trabalhados da semana 6x1 anterior e do histórico). Fotografia antiga: dias_consecutivos_max.
    /// </summary>
    public RuleValue? MaxConsecutive(int day) => UsesRegimes
        ? Regime(day / 7) is { } r ? Rules.Value(r.ConsecutiveKey, day) : null
        : Rules.Value(RuleKeys.DiasConsecutivosMax, day);
    /// <summary>
    /// Máximo de dias seguidos para uma sequência que termina em qualquer dia, inclusive depois do horizonte (dias da
    /// escala seguinte usam a regra do último dia do horizonte).
    /// </summary>
    public RuleValue? MaxConsecutiveAt(int day) => MaxConsecutive(Math.Clamp(day, 0, Days - 1));

    /// <summary>
    /// folgas_consecutivas_5x2 na semana (só semana 5x2 do modelo de regime, regra ativa com valor ≠ 0): as duas folgas
    /// em dias seguidos da semana civil. null = não se aplica.
    /// </summary>
    public RuleValue? ConsecutiveDaysOff(int week)
    {
        if (!UsesRegimes || Regime(week) is not { Name: Regime5x2, RestDays: { Value: >= 2 } }) return null;
        return Enumerable.Range(week * 7, 7).Select(d => Rules.Value(RuleKeys.FolgasConsecutivas5x2, d)).FirstOrDefault(v => v is { Value: not 0 });
    }

    /// <summary>
    /// max_folgas_seguidas para a sequência/janela que termina no dia (dias da escala seguinte usam a regra do último dia do
    /// horizonte; antes do horizonte, a do primeiro). null = regra bloqueada/ausente.
    /// </summary>
    public RuleValue? MaxDaysOffInARow(int day) => Rules.Value(RuleKeys.MaxFolgasSeguidas, Math.Clamp(day, 0, Days - 1));

    /// <summary>
    /// Dia fora do horizonte que é folga de descanso REGISTRADA na escala anterior (d &lt; 0) ou seguinte (d ≥ Days):
    /// a escala cobre a pessoa no dia, ela não trabalha em nenhum setor e não é ausência nem feriado sem autorização.
    /// Falso também quando não se sabe (sem escala no dia, fotografia antiga): esse dia não conta como folga.
    /// Dia de borda (dentro do horizonte, fora do período): a folga registrada na escala vizinha (<see cref="EmployeeInfo.EdgeRest"/>).
    /// </summary>
    public bool RestOutside(EmployeeInfo e, int d)
    {
        if (InPeriod(d)) return false;
        if (d >= 0 && d < Days) return e.EdgeRest.Contains(d) && !e.Busy.ContainsKey(d);
        var date = Start.AddDays(d);
        return d < 0
            ? e.HistoryRest.Contains(date) && !e.History.ContainsKey(date)
            : e.FollowingRest.Contains(date) && !e.Following.ContainsKey(date);
    }

    public RuleValue? InterShiftRest(int day) => Rules.Value(RuleKeys.InterjornadaMinima, Math.Clamp(day, 0, Days - 1));
    /// <summary>folga_isolada_horas_livres em minutos.</summary>
    public RuleValue? IsolatedDayOffFree(int day) => Rules.HoursAsMinutes(RuleKeys.FolgaIsoladaHorasLivres, Math.Clamp(day, 0, Days - 1));
    public RuleValue? SundayWindow(int day) => Rules.Value(RuleKeys.DomingoFolgaJanela, Math.Clamp(day, 0, Days - 1));
    public RuleValue? BiweeklyWindow(int day) => Rules.Value(RuleKeys.DomingoQuinzenalJanela, Math.Clamp(day, 0, Days - 1));
    public RuleValue? SundayLimit(int day) => Rules.Value(RuleKeys.LimiteDomingo, day);

    /// <summary>Regras de jornada/domingo/intervalo descumpridas se a pessoa fizer o turno <paramref name="t"/> no dia.</summary>
    public IEnumerable<ShiftIssue> ShiftIssues(EmployeeInfo e, int day, ShiftInfo t)
    {
        if (AllowedDaily(e, day) is { } allowed && t.PaidMinutes > allowed.Value)
            yield return new ShiftIssue(allowed.Key, allowed.Rigid, allowed.Value);
        if (IsSunday(day) && e.Snapshot.JourneyControlled && SundayLimit(day) is { } sunday && t.PaidMinutes > sunday.Value)
            yield return new ShiftIssue(sunday.Key, sunday.Rigid, sunday.Value);
        foreach (var issue in IntervalIssues(t, day)) yield return issue;
    }

    /// <summary>
    /// Duração do intervalo do turno fora das regras de intervalo do dia (obrigatoriedade, mínimo, máximo, padrão,
    /// 4h a 6h). A posição do intervalo não entra aqui: o motor escolhe o início entre as posições legais
    /// (<see cref="BreakOptions"/>); a visão do dia e as edições manuais usam o BreakPlanner.
    /// </summary>
    public IEnumerable<ShiftIssue> IntervalIssues(ShiftInfo t, int day) =>
        Interval(day).Checks(t.StartMin, t.EndMin, null, t.Snapshot.BreakMinutes)
            .Where(c => !c.Ok)
            .Select(c => new ShiftIssue(c.Key, c.Rigid, Rules.Value(c.Key, day)?.Value ?? 0));

    /// <summary>Regra de feriado do dia, quando o dia é feriado sem autorização e a regra está ativa.</summary>
    public RuleValue? HolidayRule(int day) =>
        DayBlocked[day] ? Rules.Value(RuleKeys.FeriadoExigeAutorizacao, day) : null;

    // ---------------- Montagem ----------------

    /// <summary>Escala por semanas (as antigas, e os testes): de segunda a domingo, sem dias de borda.</summary>
    public static PlanningContext Create(PlanningSnapshot snap, DateOnly startDate, int weeks, IEnumerable<CellDto>? fixedCells = null)
    {
        if (startDate.DayOfWeek != DayOfWeek.Monday) throw new InvalidOperationException("A escala precisa começar numa segunda-feira.");
        if (weeks < 1) throw new InvalidOperationException("A escala precisa ter ao menos 1 semana.");
        return Create(snap, startDate, startDate.AddDays(weeks * 7 - 1), fixedCells);
    }

    /// <summary>
    /// Escala do período [<paramref name="periodStart"/>, <paramref name="periodEnd"/>] (o mês civil): o horizonte são as
    /// semanas civis inteiras que o cobrem (<see cref="Horizon"/>); os dias de borda vêm da escala vizinha (ver
    /// <see cref="PeriodStart"/>). Células fixas fora do período são ignoradas.
    /// </summary>
    public static PlanningContext Create(PlanningSnapshot snap, DateOnly periodStart, DateOnly periodEnd, IEnumerable<CellDto>? fixedCells = null)
    {
        if (periodEnd < periodStart) throw new InvalidOperationException("O fim da escala é anterior ao início.");
        var (startDate, weeks) = Horizon(periodStart, periodEnd);
        var firstDay = periodStart.DayNumber - startDate.DayNumber;
        var lastDay = periodEnd.DayNumber - startDate.DayNumber;

        var days = weeks * 7;
        var dates = Enumerable.Range(0, days).Select(startDate.AddDays).ToArray();
        var rules = new RuleBook(LegacyRules.Of(snap), dates);
        // Janela da continuidade pelas regras ativas do período (domingo a cada N semanas, dias/folgas seguidos)
        var continuityDays = ContinuityDaysFor(rules.All);
        // Avisos sobre a origem da continuidade (semana anterior sem escala, escala ainda não publicada)
        var warnings = new List<string>(snap.HistoryNotices ?? []);

        var sectors = snap.Sectors.ToArray();
        var sectorIndex = sectors.Select((s, i) => (s.SectorId, i)).ToDictionary(x => x.SectorId, x => x.i);

        // Operação: turnos do catálogo classificados + turnos gerados (idempotente: a fotografia do Scheduling.Api já vem expandida)
        var operation = snap.Operacao;
        // Funcionamento da loja por dia (horário da semana, feriado fechado/horário especial): ver PlanningContext.StoreDays
        var storeDays = BuildStoreDays(snap, operation, dates);
        // Turnos automáticos: jornada de cada dia pelo expediente e pelas regras vigentes (PlanningContext.AutoShifts)
        var automatic = operation is { TurnosAutomaticos: true } && sectors.Length > 0;
        var journeys = automatic
            ? Journeys(snap, operation!, startDate, weeks, periodStart, periodEnd, dates, sectors, rules, storeDays)
            : [];
        var snapShifts = operation is null || sectors.Length == 0
            ? snap.Shifts.ToList()
            : automatic
                ? AutoShifts(operation, sectors[0].SectorId, snap.Shifts, storeDays.Windows, journeys)
                : OperationPlan.Expand(operation, sectors[0].SectorId, snap.Shifts, rules.Value(RuleKeys.IntervaloPadrao, 0)?.Value ?? 60,
                    storeDays.HolidayWindows);
        if (operation is not null) snap = snap with { Shifts = snapShifts };

        var shifts = snapShifts.Select((s, i) =>
        {
            var start = ToMinutes(s.Start);
            var end = EndAbs(start, ToMinutes(s.End));
            return new ShiftInfo(i, s, start, end, end - start - s.BreakMinutes);
        }).ToArray();
        var shiftIndex = shifts.ToDictionary(s => s.Snapshot.ShiftId, s => s.Index);

        var occupied = (snap.Occupied ?? []).ToLookup(o => o.EmployeeId);
        var employees = snap.Employees.Select((e, i) => new EmployeeInfo
        {
            Index = i,
            Snapshot = e,
            Sectors = e.SectorIds.Where(sectorIndex.ContainsKey).Select(s => sectorIndex[s]).Distinct().ToArray(),
            Unavailable = e.UnavailableDates.Concat(e.Absences?.Select(a => a.Date) ?? []).ToHashSet(),
            AbsenceTypes = AbsenceTypesOf(e),
            History = snap.History.Where(h => h.EmployeeId == e.EmployeeId)
                .GroupBy(h => h.Date)
                .ToDictionary(g => g.Key, g =>
                {
                    var h = g.First();
                    var start = ToMinutes(h.Start);
                    return (start, EndAbs(start, ToMinutes(h.End)));
                }),
            Busy = occupied[e.EmployeeId]
                .Where(o => o.Date >= startDate && o.Date <= dates[^1])
                .GroupBy(o => o.Date)
                .ToDictionary(g => g.Key.DayNumber - startDate.DayNumber, g =>
                {
                    var o = g.First();
                    var start = ToMinutes(o.Start);
                    return new BusyDay(start, EndAbs(start, ToMinutes(o.End)), o.PaidMinutes, o.SectorName);
                }),
            Following = (snap.Following ?? []).Where(h => h.EmployeeId == e.EmployeeId && h.Date > dates[^1]
                                                         && h.Date.DayNumber - dates[^1].DayNumber <= continuityDays)
                .GroupBy(h => h.Date)
                .ToDictionary(g => g.Key, g =>
                {
                    var h = g.First();
                    var start = ToMinutes(h.Start);
                    return (start, EndAbs(start, ToMinutes(h.End)));
                }),
            Availability = (e.Jornada?.Disponibilidade ?? []).GroupBy(a => a.DiaSemana).ToDictionary(g => g.Key, g => g.Last()),
            HistoryRest = (snap.HistoryRest ?? []).Where(r => r.EmployeeId == e.EmployeeId && r.Date < startDate).Select(r => r.Date).ToHashSet(),
            FollowingRest = (snap.FollowingRest ?? []).Where(r => r.EmployeeId == e.EmployeeId && r.Date > dates[^1]).Select(r => r.Date).ToHashSet()
        }).ToArray();
        var employeeIndex = employees.ToDictionary(e => e.Snapshot.EmployeeId, e => e.Index);
        if (firstDay > 0 || lastDay < days - 1) FillEdges(snap, employees, shifts, dates, firstDay, lastDay, rules.Value(RuleKeys.IntervaloPadrao, 0)?.Value ?? 60);

        var blocked = new bool[days];
        for (var d = 0; d < days; d++)
        {
            var date = dates[d];
            blocked[d] = snap.Holidays.Any(h => h.Date == date && !h.WorkAuthorized)
                         && rules.Value(RuleKeys.FeriadoExigeAutorizacao, d) is { Value: not 0 };
            // Feriado com a loja fechada (funcionamento Fechada): dia não trabalhável mesmo com a regra de feriado desligada
            blocked[d] |= storeDays.HolidayClosed[d];
        }

        var slots = operation is not null && sectors.Length > 0
            ? OperationSlots(snap, operation, dates, blocked, rules, sectorIndex, storeDays)
            : DemandSlots(snap, dates, blocked, rules, sectorIndex, storeDays);

        var fixedMap = new Dictionary<(int E, int D), int?>();
        var fixedBreaks = new Dictionary<(int E, int D), int>();
        foreach (var c in fixedCells ?? [])
        {
            if (!employeeIndex.TryGetValue(c.EmployeeId, out var e)) { warnings.Add($"Célula fixa de pessoa fora da fotografia ignorada ({c.EmployeeId})."); continue; }
            var d = c.Date.DayNumber - startDate.DayNumber;
            if (d < firstDay || d > lastDay) continue;
            if (c.ShiftId is { } sid)
            {
                if (!shiftIndex.TryGetValue(sid, out var t)) { warnings.Add($"Célula fixa com turno fora da fotografia ignorada ({c.Date:dd/MM})."); continue; }
                // Ninguém trabalha em dia de afastamento/férias, nem por célula manual: o motor descarta a célula
                if (employees[e].Unavailable.Contains(c.Date))
                {
                    // Texto genérico: o aviso aparece para quem consulta a escala (o tipo da ausência não)
                    warnings.Add($"Célula manual de {employees[e].Snapshot.Name} em {c.Date:dd/MM} descartada: dia de ausência registrada (afastado).");
                    continue;
                }
                fixedMap[(e, d)] = t;
                // Intervalo ajustado à mão: a posição fica (dentro da jornada; a duração é a do turno no motor)
                if (c.BreakManual && c.BreakStart is { } bs)
                {
                    var abs = BreakAbs(shifts[t].StartMin, bs);
                    if (abs >= shifts[t].StartMin && abs + shifts[t].Snapshot.BreakMinutes <= shifts[t].EndMin) fixedBreaks[(e, d)] = abs;
                }
            }
            else if (c.Folga)
            {
                fixedMap[(e, d)] = null;
            }
        }

        return new PlanningContext
        {
            Snapshot = snap,
            Start = startDate,
            Weeks = weeks,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            Dates = dates,
            Sectors = sectors,
            Shifts = shifts,
            Employees = employees,
            Rules = rules,
            DayBlocked = blocked,
            DayClosed = storeDays.Closed,
            DayWindows = storeDays.Windows,
            HasDayWindows = operation is not null && storeDays.PerDay,
            DayJourneys = journeys,
            StoreHasExtendedDayAgreement = snap.StoreHasExtendedDayAgreement,
            Fixed = fixedMap,
            FixedBreaks = fixedBreaks,
            Warnings = warnings,
            Operation = operation,
            ContinuityDays = continuityDays,
            RestWindow = RestWindowFor(rules.All),
            // Dia de borda com escala vizinha: a cobertura é dela. Sem escala vizinha (ninguém com nada registrado no dia), o
            // motor planeja o dia com a cobertura, como continuação possível da semana — só para a semana parcial fechar com
            // folgas que o mês vizinho consiga cumprir; o resultado não é gravado e o avaliador não mede essas faixas. No motor
            // essas faixas são META (HardCoverage): a cobertura de um dia que esta escala não grava nunca a torna inviável
            Slots = slots.Where(s => (s.Day >= firstDay && s.Day <= lastDay) || employees.All(e => e.EdgeUnknown.Contains(s.Day)))
                .OrderBy(s => s.Day).ThenBy(s => s.Sector).ThenBy(s => s.SlotStart).ToList()
        };
    }

    /// <summary>
    /// Dias de borda (semana civil parcial antes do primeiro / depois do último dia do período), por pessoa: trabalho
    /// registrado na escala anterior (histórico) ou seguinte vira dia ocupado (<see cref="EmployeeInfo.Busy"/>, com os
    /// minutos pagos do turno da fotografia de mesmo horário ou, sem ele, duração − intervalo padrão); folga de descanso
    /// registrada vira <see cref="EmployeeInfo.EdgeRest"/>; sem nada, <see cref="EmployeeInfo.EdgeUnknown"/>.
    /// </summary>
    private static void FillEdges(PlanningSnapshot snap, EmployeeInfo[] employees, ShiftInfo[] shifts, DateOnly[] dates, int firstDay,
        int lastDay, int standardBreak)
    {
        int Paid(int start, int endAbs)
        {
            if (shifts.FirstOrDefault(t => t.StartMin == start && t.EndMin == endAbs) is { } same) return same.PaidMinutes;
            var duration = endAbs - start;
            // Sem o turno na fotografia: intervalo padrão acima de 6h, 15 min de 4h a 6h (CLT art. 71)
            return duration - (duration > 6 * 60 ? Math.Min(standardBreak, duration) : duration > 4 * 60 ? 15 : 0);
        }

        foreach (var e in employees)
        {
            var id = e.Snapshot.EmployeeId;
            var following = (snap.Following ?? []).Where(h => h.EmployeeId == id).GroupBy(h => h.Date).ToDictionary(g => g.Key, g => g.First());
            var historyRest = (snap.HistoryRest ?? []).Where(r => r.EmployeeId == id).Select(r => r.Date).ToHashSet();
            var followingRest = (snap.FollowingRest ?? []).Where(r => r.EmployeeId == id).Select(r => r.Date).ToHashSet();
            for (var d = 0; d < dates.Length; d++)
            {
                if (d >= firstDay && d <= lastDay) continue;
                if (e.Busy.ContainsKey(d)) continue;
                var date = dates[d];
                var before = d < firstDay;
                (int Start, int EndAbs)? worked = null;
                if (before && e.History.TryGetValue(date, out var h)) worked = h;
                else if (!before && following.TryGetValue(date, out var f))
                {
                    var start = ToMinutes(f.Start);
                    worked = (start, EndAbs(start, ToMinutes(f.End)));
                }
                if (worked is { } w)
                    e.Busy[d] = new BusyDay(w.Start, w.EndAbs, Paid(w.Start, w.EndAbs), before ? "escala anterior" : "escala seguinte");
                else if ((before ? historyRest : followingRest).Contains(date)) e.EdgeRest.Add(d);
                else e.EdgeUnknown.Add(d);
            }
        }
    }

    /// <summary>Modelo anterior: faixas do tamanho de cobertura_faixa_minutos onde há demanda (linhas sobrepostas: a maior).</summary>
    private static List<CoverageSlot> DemandSlots(PlanningSnapshot snap, DateOnly[] dates, bool[] blocked, RuleBook rules,
        Dictionary<Guid, int> sectorIndex, StoreDays storeDays)
    {
        var required = new Dictionary<(int Sector, int Day, int Slot), (int Required, int Size, bool Rigid)>();
        var peakLabels = new Dictionary<(int Sector, int Day, int Slot), string>();
        for (var d = 0; d < dates.Length; d++)
        {
            if (blocked[d] || storeDays.Closed[d]) continue; // loja fechada (feriado sem autorização/fechado)
            if (rules.Value(RuleKeys.CoberturaFaixa, d) is not { Value: > 0 } faixa) continue; // cobertura bloqueada: não é medida
            var size = Math.Min(faixa.Value, MinutesPerDay);
            foreach (var dem in snap.Demands.Where(x => !x.PicoManual && x.DayOfWeek == dates[d].DayOfWeek && sectorIndex.ContainsKey(x.SectorId)))
            {
                var s = sectorIndex[dem.SectorId];
                var from = ToMinutes(dem.Start) / size * size;
                var to = ToMinutes(dem.End);
                for (var slot = from; slot < to; slot += size)
                {
                    var key = (s, d, slot);
                    var current = required.GetValueOrDefault(key);
                    required[key] = (Math.Max(current.Required, dem.MinStaff), size, faixa.Rigid);
                }
            }
            // Picos manuais (substituem a demanda nas faixas) e horário especial do feriado
            ApplyDayRules(required, peakLabels, snap, sectorIndex, dates[d], d, size, faixa.Rigid, storeDays.Windows[d]);
        }
        return required.Where(kv => kv.Value.Required > 0)
            .Select(kv => new CoverageSlot(kv.Key.Sector, kv.Key.Day, kv.Key.Slot, kv.Value.Size, kv.Value.Required, kv.Value.Rigid)
            {
                ManualPeak = peakLabels.GetValueOrDefault(kv.Key)
            })
            .ToList();
    }

    /// <summary>
    /// Com operação: faixas de [abertura, fechamento) em todos os dias que a loja abre (feriado sem autorização: fechada),
    /// a partir da abertura, do tamanho de cobertura_faixa_minutos (bloqueada: 30 min; a última faixa pode ser menor).
    /// Mínimo RÍGIDO = cobertura_minima (na primeira faixa, também minimo_abertura; na última, minimo_fechamento — vale o
    /// maior, e a chave é a da exigência que manda). Alvo = maior entre o mínimo e a demanda das linhas que tocam a faixa
    /// (reforço de pico, PREFERENCIAL). Demanda fora do expediente é ignorada.
    /// </summary>
    private static List<CoverageSlot> OperationSlots(PlanningSnapshot snap, OperationSnapshot op, DateOnly[] dates, bool[] blocked,
        RuleBook rules, Dictionary<Guid, int> sectorIndex, StoreDays storeDays)
    {
        var list = new List<CoverageSlot>();
        if (ToMinutes(op.Fechamento) <= ToMinutes(op.Abertura)) return list;
        // Linhas do setor da escala: demanda comum e picos manuais (o dia de cada linha é conferido por faixa)
        var rows = snap.Demands.Where(x => sectorIndex.GetValueOrDefault(x.SectorId, -1) == 0).ToList();
        for (var d = 0; d < dates.Length; d++)
        {
            if (blocked[d]) continue;
            // Expediente do dia: horário da loja no dia da semana / horário especial do feriado (null = fechada)
            if (storeDays.Windows[d] is not { } window) continue;
            var (open, close) = window;
            var size = rules.Value(RuleKeys.CoberturaFaixa, d) is { Value: > 0 } faixa ? Math.Min(faixa.Value, MinutesPerDay) : 30;
            for (var start = open; start < close; start += size)
            {
                var end = Math.Min(close, start + size);
                var (minimum, key) = (op.CoberturaMinima, RuleKeys.CoberturaMinima);
                if (start == open && op.MinimoAbertura >= minimum) (minimum, key) = (op.MinimoAbertura, RuleKeys.MinimoAbertura);
                if (end == close && op.MinimoFechamento >= minimum) (minimum, key) = (op.MinimoFechamento, RuleKeys.MinimoFechamento);
                // Alvo: maior demanda comum que toca a faixa ou, com pico manual, o valor do pico (nunca abaixo do mínimo)
                var (target, peak) = SlotTarget(rows, dates[d], start, end, minimum);
                list.Add(new CoverageSlot(0, d, start, end - start, target, false)
                {
                    Minimum = minimum,
                    MinimumKey = key,
                    DemandKey = RuleKeys.ReforcoDemanda,
                    ManualPeak = peak
                });
            }
        }
        return list;
    }

    /// <summary>Tipo por dia: o de maior precedência entre os registros; dias só em UnavailableDates (fotografia antiga) = Indisponivel.</summary>
    private static Dictionary<DateOnly, string> AbsenceTypesOf(EmployeeSnapshot e)
    {
        var types = (e.Absences ?? [])
            .GroupBy(a => a.Date)
            .ToDictionary(g => g.Key, g => g.Select(a => a.Tipo).OrderBy(UnavailabilityKinds.Rank).First());
        foreach (var d in e.UnavailableDates) types.TryAdd(d, UnavailabilityKinds.Indisponivel);
        return types;
    }

    /// <summary>
    /// Teste necessário (não suficiente) do Documento de Alinhamento: demanda de domingo ≤
    /// ⌊(W−1)/W · (N − F)⌋ + ⌊F·(Q−1)/Q⌋ por setor (W = domingo_folga_janela_semanas, Q = domingo_quinzenal_janela_semanas).
    /// Só para o mínimo rígido das faixas (<see cref="CoverageSlot.Minimum"/>) e janelas de domingo rígidas.
    /// </summary>
    public List<DeficitDto> CheckSundayCapacity()
    {
        var result = new List<DeficitDto>();
        for (var s = 0; s < Sectors.Length; s++)
        {
            // Só mínimos rígidos: dia de borda e setor de 2 pessoas (cobertura como meta) não entram
            var sundaySlots = Slots.Where(x => x.Sector == s && x.Minimum > 0 && IsSunday(x.Day) && HardCoverage(x)).ToList();
            if (sundaySlots.Count == 0) continue;

            var windows = sundaySlots.Select(x => SundayWindow(x.Day)).Where(w => w is { Rigid: true }).Select(w => w!.Value.Value).ToList();
            if (windows.Count == 0) continue;
            var window = Math.Max(1, windows.Min());
            var biweeklyRule = sundaySlots.Select(x => BiweeklyWindow(x.Day)).Where(w => w is { Rigid: true }).Select(w => w!.Value.Value).ToList();

            var people = Employees.Where(e => e.Sectors.Contains(s)).ToList();
            var biweekly = biweeklyRule.Count == 0 ? 0 : people.Count(e => e.Snapshot.SundayRule == SundayRule.Biweekly);
            var q = biweeklyRule.Count == 0 ? 1 : Math.Max(1, Math.Min(window, biweeklyRule.Min()));
            var capacity = (window - 1) * (people.Count - biweekly) / window + biweekly * (q - 1) / q;

            foreach (var slot in sundaySlots.Where(x => x.Minimum > capacity))
            {
                result.Add(new DeficitDto(Sectors[s].SectorId, Dates[slot.Day],
                    TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(slot.SlotStart)), slot.Minimum - capacity));
            }
        }
        return result;
    }
}

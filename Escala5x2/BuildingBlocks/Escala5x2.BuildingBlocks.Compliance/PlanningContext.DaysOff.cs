using Escala5x2.BuildingBlocks.Contracts.Planning;

namespace Escala5x2.BuildingBlocks.Compliance;

/// <summary>Como um dia entra na contagem de max_folgas_seguidas (<see cref="PlanningContext.DayOffKindOf"/>).</summary>
public enum DayOffKind
{
    /// <summary>
    /// Interrompe a sequência: trabalho (outro setor, escala anterior/seguinte), ausência (férias, afastamento, licença,
    /// indisponibilidade, antes da admissão), feriado sem autorização/fechado, dia fora do horizonte sem folga registrada
    /// e, no avaliador, dia de borda sem escala vizinha.
    /// </summary>
    Interrupts,
    /// <summary>
    /// Neutro: a pessoa não pode trabalhar no dia por motivo alheio à escolha do motor (loja fechada no dia da semana ou
    /// com horário especial sem turno possível, indisponível na disponibilidade semanal, nenhum turno possível para ela).
    /// Não conta como folga nem interrompe: a sequência das folgas de verdade continua dos dois lados.
    /// </summary>
    Neutral,
    /// <summary>Pode ser folga de descanso: dia em que o motor escolhe (ou a folga foi marcada à mão / registrada na escala vizinha).</summary>
    Rest
}

/// <summary>
/// SEMÂNTICA DE max_folgas_seguidas (motor e avaliador usam a mesma classificação, <see cref="DayOffKind"/>):
/// <list type="bullet">
/// <item>A regra limita as folgas de DESCANSO seguidas: as que o motor escolhe, as marcadas à mão (célula manual de folga)
/// e as registradas na escala anterior/seguinte (continuidade). Em toda sequência de N + 1 dessas folgas que toca o
/// período, pelo menos um dia trabalhado.</item>
/// <item>Dia em que a pessoa NÃO PODE trabalhar por motivo alheio à escolha do motor não é folga para a regra e nunca a
/// torna inviável: ausência e feriado sem autorização/fechado INTERROMPEM a sequência (como sempre); loja fechada no dia
/// da semana (ou horário especial em que nenhum turno cabe), indisponibilidade na disponibilidade semanal e dia sem
/// nenhum turno possível para a pessoa (perfil/rodízio, expediente, regras rígidas por turno, intervalo — quando OUTROS
/// do setor poderiam: dia aberto sem nenhum turno para ninguém é erro de configuração, PlanningContext.Configuration) são NEUTROS:
/// não contam e a contagem continua dos dois lados (ex.: folga no sábado, loja fechada no domingo e folga na segunda =
/// 2 folgas seguidas). Na conta das folgas da SEMANA (5x2/6x1) esses dias continuam sendo folga, como antes.</item>
/// <item>Conflito que ainda pode existir (ex.: 3 folgas manuais seguidas): o diagnóstico de inviabilidade aponta a pessoa
/// e os dias (ScheduleModel, refinamento por pessoa).</item>
/// </list>
/// </summary>
public sealed partial class PlanningContext
{
    private readonly Dictionary<(int E, int D), bool> _forcedOff = [];

    /// <summary>
    /// A pessoa pode fazer o turno no dia pelas regras estruturais: perfil (ou rodízio da operação), expediente do dia,
    /// disponibilidade semanal, regras RÍGIDAS por turno (limite diário/hora extra, domingo, duração do intervalo) e
    /// alguma posição legal de intervalo — o mesmo filtro do motor (ScheduleModel) para criar a variável do turno.
    /// </summary>
    public bool CanWorkShift(EmployeeInfo e, int day, ShiftInfo t) =>
        ShiftAllowed(e, t) && OpenAt(day, t) && AvailableAt(e, day, t)
        && !ShiftIssues(e, day, t).Any(i => i.Rigid)
        && (t.Snapshot.BreakMinutes == 0 || BreakOptions(day, t, t.Snapshot.BreakMinutes).Count > 0);

    /// <summary>
    /// Dia de não trabalho FORÇADO por motivo alheio à escolha do motor (neutro para max_folgas_seguidas): loja fechada
    /// (dia da semana fechado, feriado fechado já é <see cref="NonWorkable"/>, horário especial sem expediente) ou nenhum
    /// turno possível para a pessoa (<see cref="CanWorkShift"/>: disponibilidade semanal, perfil, expediente...). Não se
    /// aplica a dia em outro setor, ausência/feriado sem autorização (interrompem), a dia com célula manual de TRABALHO nem
    /// a dia aberto do período em que nenhum turno cabe para o setor inteiro (<see cref="NoShiftConfigured"/>: erro de
    /// configuração, apontado na geração — nunca vira folga inevitável de todos).
    /// Fora do horizonte: dia da semana com a loja fechada, feriado fechado da fotografia ou indisponível na disponibilidade.
    /// </summary>
    public bool ForcedOff(EmployeeInfo e, int day)
    {
        if (day < 0 || day >= Days) return ForcedOffOutside(e, Start.AddDays(day));
        if (e.Busy.ContainsKey(day) || NonWorkable(e, day)) return false;
        if (Fixed.TryGetValue((e.Index, day), out var f) && f is not null) return false;
        if (IsClosed(day)) return true;
        // Nenhum turno cabe no dia para o setor inteiro (configuração: horário da loja × operação × regras) NÃO é forçado:
        // senão todos ficariam de folga o mês inteiro em silêncio (PlanningContext.Configuration)
        if (!_forcedOff.TryGetValue((e.Index, day), out var forced))
            _forcedOff[(e.Index, day)] = forced = !Shifts.Any(t => CanWorkShift(e, day, t)) && !NoShiftConfigured(e, day);
        return forced;
    }

    private bool ForcedOffOutside(EmployeeInfo e, DateOnly date)
    {
        if (Operation is { } op && OperationPlan.DayWindow(op, date.DayOfWeek) is null) return true;
        if (Snapshot.Holidays.Any(h => h.Date == date && h.Funcionamento == nameof(FuncionamentoDia.Fechada))) return true;
        return e.Availability.TryGetValue(date.DayOfWeek, out var w) && !w.Disponivel;
    }

    /// <summary>Por que o dia é de não trabalho forçado (<see cref="ForcedOff"/>), para avisos e diagnósticos.</summary>
    public string ForcedOffReason(EmployeeInfo e, int day)
    {
        var date = Start.AddDays(day);
        var unavailable = e.Availability.TryGetValue(date.DayOfWeek, out var w) && !w.Disponivel;
        if (day < 0 || day >= Days) return unavailable ? "indisponível na disponibilidade semanal" : "loja fechada";
        if (IsClosed(day)) return "loja fechada";
        if (unavailable) return "indisponível na disponibilidade semanal";
        var open = Shifts.Where(t => ShiftAllowed(e, t) && OpenAt(day, t)).ToList();
        if (open.Count > 0 && !open.Any(t => AvailableAt(e, day, t))) return $"fora da disponibilidade semanal ({AvailabilityText(e, day)})";
        return "nenhum turno possível no dia (perfil, expediente ou regras de jornada)";
    }

    /// <summary>
    /// Classificação do dia para max_folgas_seguidas (<see cref="DayOffKind"/>), a MESMA no motor e no avaliador.
    /// <paramref name="edgeUnknownIsRest"/>: dia de borda sem escala vizinha — no motor é decisão livre (pode ser folga:
    /// true); no avaliador não se sabe (interrompe: false).
    /// </summary>
    public DayOffKind DayOffKindOf(EmployeeInfo e, int day, bool edgeUnknownIsRest)
    {
        if (day < 0 || day >= Days)
            return !RestOutside(e, day) ? DayOffKind.Interrupts : ForcedOff(e, day) ? DayOffKind.Neutral : DayOffKind.Rest;
        if (e.Busy.ContainsKey(day) || NonWorkable(e, day)) return DayOffKind.Interrupts;
        if (!InPeriod(day) && !e.EdgeRest.Contains(day) && !(edgeUnknownIsRest && e.EdgeUnknown.Contains(day))) return DayOffKind.Interrupts;
        return ForcedOff(e, day) ? DayOffKind.Neutral : DayOffKind.Rest;
    }

    /// <summary>
    /// Número de folgas da semana para a pessoa: o do regime (<paramref name="regime"/>) ou, se maior, o de dias normais da
    /// semana com não trabalho FORÇADO (<see cref="ForcedOff"/>: loja fechada, disponibilidade semanal, sem turno possível)
    /// — esses dias são folga na conta da semana e a pessoa trabalha todos os outros; a semana nunca fica impossível por
    /// um dia que ela não pode trabalhar. Motor e avaliador usam esta conta.
    /// </summary>
    public int WeekRestDays(EmployeeInfo e, int week, int regime) =>
        Math.Max(regime, NormalDays(e, week).Count(d => ForcedOff(e, d)));

    /// <summary>Primeiro e último dia (índices) considerados nas sequências de folgas: o horizonte e a emenda com as folgas registradas.</summary>
    public (int From, int To) DaysOffRange => (-RestWindow - 1, Days + RestWindow);

    /// <summary>A janela de folgas [<paramref name="first"/>, <paramref name="last"/>] é desta escala (toca o período).</summary>
    public bool TouchesPeriod(int first, int last) => first <= LastDay && last >= FirstDay;
}

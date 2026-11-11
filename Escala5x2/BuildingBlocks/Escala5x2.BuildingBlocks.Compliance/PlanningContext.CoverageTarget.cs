using Escala5x2.BuildingBlocks.Contracts.Planning;

namespace Escala5x2.BuildingBlocks.Compliance;

/// <summary>
/// COBERTURA COMO META POR QUADRO INSUFICIENTE (<see cref="Contracts.Planning.OperationSnapshot.CoberturaComoMetaQuadroInsuficiente"/>,
/// padrão ligado; só com operação). Decisões:
/// <list type="bullet">
/// <item><b>Quando</b>: o mínimo de cobertura do setor (cobertura mínima, abertura/fechamento — contando que quem está em
/// intervalo não cobre) é impossível com o quadro. Duas provas, no motor: (a) CONTAGEM antes do CP-SAT — por semana, os
/// turnos que o expediente pede com a cobertura dos intervalos (07:00–22:00 com intervalo de 1h: 3 turnos/dia = 21 na
/// semana) contra os que o quadro faz (3 pessoas no 5x2 = 15; 4 = 20), e as demais provas de capacidade (faixa sem gente
/// possível, perfil, rodízio de domingos); (b) o CP-SAT com a cobertura rígida PROVA que não há escala (perfis,
/// interjornada, regra de domingos, células manuais podem impedir o que a contagem não vê): a geração é refeita com a
/// cobertura como meta nos setores envolvidos. Sem solução no tempo não troca (a primeira escala de um mês pode demorar:
/// trocar antes deixaria como meta um setor que o quadro cobre).</item>
/// <item><b>Só o dia</b>: quando a falta é de um dia (menos gente do que turnos por ausências/folgas manuais, num setor que
/// o quadro cobre nos outros dias), só as faixas desse dia viram meta (<see cref="CoverageTargetDays"/>) — o resto do mês
/// continua rígido e o motor não perde qualidade no mês inteiro.</item>
/// <item><b>O que muda</b>: só a cobertura do setor (mínimos e reforço de pico) vira meta, como no setor de 2 pessoas
/// (<see cref="SoftCoverage"/>): o motor maximiza o tempo coberto com as mesmas prioridades e a escala sai. Regras legais
/// (interjornada, jornada, intervalos, dias seguidos, folgas do regime, domingos), ausências, loja fechada e perfil de
/// jornada (com o rodízio, se ligado) continuam rígidos. Setor que o quadro cobre continua com a cobertura rígida.</item>
/// <item><b>Registro</b>: os setores vão no resultado do motor e ficam gravados na fotografia da escala
/// (<see cref="Contracts.Planning.PlanningSnapshot.CoberturaMetaSetores"/>, <see cref="Contracts.Planning.PlanningSnapshot.CoberturaMetaDias"/>): o avaliador aponta a faixa descoberta como
/// aviso (severidade baixa, "cobertura como meta"), como o motor, também nas edições seguintes.</item>
/// <item>Opção desligada: nada disso — inviável com o diagnóstico (a contagem diz direto quantos turnos faltam).</item>
/// </list>
/// </summary>
public sealed partial class PlanningContext
{
    private HashSet<int>? _coverageTarget;

    /// <summary>A operação permite gerar com a cobertura como meta quando o quadro não cobre o mínimo (sem operação: não).</summary>
    public bool CoverageTargetEnabled => Operation?.CoberturaComoMetaQuadroInsuficiente ?? false;

    /// <summary>
    /// Setores (índices) com a cobertura como meta por quadro insuficiente: os gravados na fotografia (resultado de uma
    /// geração anterior, para o avaliador) mais os marcados pelo motor nesta geração (<see cref="UseCoverageTarget"/>).
    /// </summary>
    public IReadOnlySet<int> CoverageTargetSectors => _coverageTarget ??= Enumerable.Range(0, Sectors.Length)
        .Where(s => Snapshot.CoberturaMetaSetores?.Contains(Sectors[s].SectorId) == true).ToHashSet();

    /// <summary>Marca o setor com a cobertura como meta (decisão do motor). Devolve false se já era.</summary>
    public bool UseCoverageTarget(int sector)
    {
        _ = CoverageTargetSectors;
        return _coverageTarget!.Add(sector);
    }

    /// <summary>Desfaz a marca (o setor saiu todo coberto: a cobertura volta a ser rígida no avaliador).</summary>
    public void RemoveCoverageTarget(int sector)
    {
        _ = CoverageTargetSectors;
        _coverageTarget!.Remove(sector);
    }

    /// <summary>Ids dos setores com a cobertura como meta por quadro insuficiente (para o resultado do motor; null = nenhum).</summary>
    public IReadOnlyList<Guid>? CoverageTargetSectorIds() =>
        CoverageTargetSectors.Count == 0 ? null : CoverageTargetSectors.Order().Select(s => Sectors[s].SectorId).ToList();

    private HashSet<(int Sector, int Day)>? _coverageTargetDays;

    /// <summary>
    /// Dias (setor × índice do horizonte) com a cobertura como meta: o dia tem menos gente do que turnos (ausências, folgas
    /// manuais, disponibilidade, outro setor) num setor que o quadro cobre nos outros dias — a meta fica só no dia, o resto
    /// do mês continua com a cobertura rígida. Os gravados na fotografia mais os marcados pelo motor nesta geração.
    /// </summary>
    public IReadOnlySet<(int Sector, int Day)> CoverageTargetDays => _coverageTargetDays ??= (Snapshot.CoberturaMetaDias ?? [])
        .Select(x => (Sector: Array.FindIndex(Sectors, s => s.SectorId == x.SectorId), Day: DayIndex(x.Date)))
        .Where(x => x.Sector >= 0 && x.Day >= 0 && x.Day < Days).ToHashSet();

    /// <summary>Marca o dia do setor com a cobertura como meta (decisão do motor).</summary>
    public void UseCoverageTargetDay(int sector, int day)
    {
        _ = CoverageTargetDays;
        _coverageTargetDays!.Add((sector, day));
    }

    /// <summary>Desfaz a marca (o dia saiu todo coberto).</summary>
    public void RemoveCoverageTargetDay(int sector, int day)
    {
        _ = CoverageTargetDays;
        _coverageTargetDays!.Remove((sector, day));
    }

    /// <summary>Dias com a cobertura como meta (para o resultado do motor; null = nenhum).</summary>
    public IReadOnlyList<CoberturaMetaDia>? CoverageTargetDayList() =>
        CoverageTargetDays.Count == 0 ? null
            : CoverageTargetDays.OrderBy(x => x.Day).ThenBy(x => x.Sector).Select(x => new CoberturaMetaDia(Sectors[x.Sector].SectorId, Dates[x.Day])).ToList();

    /// <summary>A faixa tem a cobertura como meta (setor de 2 pessoas, setor ou dia com quadro insuficiente).</summary>
    public bool TargetCoverage(CoverageSlot slot) => SoftCoverage(slot.Sector) || CoverageTargetDays.Contains((slot.Sector, slot.Day));
}

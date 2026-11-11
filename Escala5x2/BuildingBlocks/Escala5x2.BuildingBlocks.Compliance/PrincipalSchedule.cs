namespace Escala5x2.BuildingBlocks.Compliance;

/// <summary>Escala vista pela regra da principal: setor (null = escala antiga por loja), período e se é a principal.</summary>
public sealed record PrincipalCandidate(Guid Id, Guid? SectorId, DateOnly Start, DateOnly End, bool Principal);

/// <summary>
/// Regra da escala PRINCIPAL: para um mesmo setor, num mesmo dia, no máximo uma escala principal. Marcar uma escala como
/// principal desmarca as outras principais do mesmo setor cujo período se sobrepõe ao dela (inteiras: a escala é a
/// unidade). Escala antiga por loja (sem setor) nunca é principal.
/// </summary>
public static class PrincipalSchedule
{
    public static bool Overlaps(DateOnly start, DateOnly end, DateOnly otherStart, DateOnly otherEnd) => start <= otherEnd && end >= otherStart;

    /// <summary>Outras escalas do mesmo setor com período sobreposto ao de <paramref name="target"/> (qualquer marcação).</summary>
    public static IEnumerable<PrincipalCandidate> Overlapping(PrincipalCandidate target, IEnumerable<PrincipalCandidate> others) =>
        others.Where(o => o.Id != target.Id && o.SectorId is not null && o.SectorId == target.SectorId
                          && Overlaps(target.Start, target.End, o.Start, o.End));

    /// <summary>Principais que deixam de ser quando <paramref name="target"/> vira a principal.</summary>
    public static List<Guid> ToUnmark(PrincipalCandidate target, IEnumerable<PrincipalCandidate> others) =>
        target.SectorId is null ? [] : Overlapping(target, others).Where(o => o.Principal).Select(o => o.Id).ToList();

    /// <summary>
    /// Marca <paramref name="target"/> como principal no conjunto (que inclui ou não o alvo) e devolve o novo conjunto
    /// e as escalas desmarcadas.
    /// </summary>
    public static (List<PrincipalCandidate> Schedules, List<Guid> Unmarked) Mark(PrincipalCandidate target, IEnumerable<PrincipalCandidate> schedules)
    {
        if (target.SectorId is null) throw new InvalidOperationException("Escala antiga por loja não pode ser a principal.");
        var all = schedules.Where(s => s.Id != target.Id).ToList();
        var unmarked = ToUnmark(target, all).ToHashSet();
        var result = all.Select(s => unmarked.Contains(s.Id) ? s with { Principal = false } : s).Append(target with { Principal = true }).ToList();
        return (result, unmarked.ToList());
    }

    /// <summary>A regra vale: nenhum setor tem duas principais no mesmo dia.</summary>
    public static bool IsConsistent(IEnumerable<PrincipalCandidate> schedules)
    {
        var principals = schedules.Where(s => s.Principal && s.SectorId is not null).ToList();
        return !principals.Any(p => Overlapping(p, principals).Any());
    }
}

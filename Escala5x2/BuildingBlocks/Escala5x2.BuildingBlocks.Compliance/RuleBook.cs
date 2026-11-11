using Escala5x2.BuildingBlocks.Contracts.Compliance;

namespace Escala5x2.BuildingBlocks.Compliance;

/// <summary>Valor de regra aplicado num dia (minutos, dias, semanas...), com a forma de aplicação e a chave de origem.</summary>
public readonly record struct RuleValue(int Value, bool Rigid, string Key);

/// <summary>
/// Regras da fotografia resolvidas por dia do horizonte: para cada dia vale a linha ATIVA cuja vigência contém
/// o dia (sem vigência = vale sempre; se mais de uma, a de início mais recente). Linha bloqueada não é aplicada.
/// </summary>
public sealed class RuleBook
{
    private readonly Dictionary<string, ComplianceRuleSnapshot?[]> _active = new(StringComparer.Ordinal);
    private readonly DateOnly[] _dates;

    public IReadOnlyList<ComplianceRuleSnapshot> All { get; }

    public RuleBook(IEnumerable<ComplianceRuleSnapshot> rules, DateOnly[] dates)
    {
        _dates = dates;
        // Só linhas que tocam o horizonte interessam (a fotografia já vem filtrada; aqui é garantia).
        All = rules.Where(r => dates.Length == 0 || dates.Any(r.Contem)).ToList();
        foreach (var group in All.GroupBy(r => r.Chave))
        {
            var perDay = new ComplianceRuleSnapshot?[dates.Length];
            for (var d = 0; d < dates.Length; d++)
            {
                perDay[d] = group
                    .Where(r => r.Status == RuleStatus.Ativo && r.Contem(dates[d]))
                    .OrderByDescending(r => r.VigenciaInicio ?? DateOnly.MinValue)
                    .ThenByDescending(r => r.RuleId)
                    .FirstOrDefault();
            }
            _active[group.Key] = perDay;
        }
    }

    public IEnumerable<string> Keys => _active.Keys;

    /// <summary>Linha ativa da chave no dia (índice do horizonte), ou null quando a regra não se aplica.</summary>
    public ComplianceRuleSnapshot? Active(string key, int day) =>
        _active.TryGetValue(key, out var perDay) && day >= 0 && day < perDay.Length ? perDay[day] : null;

    /// <summary>Valor inteiro da regra no dia (horas são convertidas em minutos pelo chamador quando preciso).</summary>
    public RuleValue? Value(string key, int day) =>
        Active(key, day) is { } r ? new RuleValue((int)Math.Round(r.Valor, MidpointRounding.AwayFromZero), r.Aplicacao == RuleApplication.Rigida, key) : null;

    /// <summary>Valor em minutos de uma regra em horas (carga semanal, folga isolada).</summary>
    public RuleValue? HoursAsMinutes(string key, int day) =>
        Active(key, day) is { } r ? new RuleValue((int)Math.Round(r.Valor * 60m, MidpointRounding.AwayFromZero), r.Aplicacao == RuleApplication.Rigida, key) : null;

    /// <summary>A chave existe na fotografia, mas nenhum dia do horizonte tem linha ativa (bloqueada no período).</summary>
    public bool BlockedInHorizon(string key) =>
        _active.TryGetValue(key, out var perDay) && perDay.All(r => r is null) && All.Any(r => r.Chave == key && r.Status == RuleStatus.Bloqueado);

    /// <summary>Linha que representa a chave nas telas (a primeira ativa do horizonte, ou qualquer uma).</summary>
    public ComplianceRuleSnapshot? Representative(string key) =>
        _active.TryGetValue(key, out var perDay)
            ? perDay.FirstOrDefault(r => r is not null) ?? All.FirstOrDefault(r => r.Chave == key)
            : null;

    public int Days => _dates.Length;
}

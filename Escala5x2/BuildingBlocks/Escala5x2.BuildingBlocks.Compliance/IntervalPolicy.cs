using Escala5x2.BuildingBlocks.Contracts.Compliance;

namespace Escala5x2.BuildingBlocks.Compliance;

/// <summary>Resultado de uma verificação de intervalo numa célula (Detail sem a base legal; ver <see cref="Text"/>).</summary>
public sealed record IntervalCheck(string Key, bool Rigid, bool Ok, string Detail, string BaseLegal)
{
    public string Text => $"{Detail} — {BaseLegal}";
}

/// <summary>
/// Janela do intervalo de uma célula pelas regras ATIVAS do dia (minutos absolutos a partir da meia-noite do dia
/// da célula; turno que vira o dia passa de 1440). Legal = só regras rígidas; recomendada = rígidas e preferenciais.
/// </summary>
public sealed record IntervalWindow(
    int MinMinutes, int MaxMinutes, int? DefaultMinutes, bool Required,
    int? LegalEarliest, int? LegalLatest, int? RecommendedEarliest, int? RecommendedLatest);

/// <summary>
/// Regras de intervalo de refeição resolvidas para um dia (linhas ATIVAS do cadastro). Mesma lógica no motor
/// (duração do intervalo do turno), no avaliador (por célula), no posicionamento automático e na visão do dia.
/// <list type="bullet">
/// <item>Jornada líquida (duração − intervalo) acima de intervalo_obrigatorio_acima_minutos (360): intervalo obrigatório
/// (CLT art. 71, caput); sem intervalo conta só essa violação; com intervalo valem mínimo, padrão e máximo.</item>
/// <item>Acima de 4h (fixo: condição do §1º) e até o limite acima: intervalo_jornada_4a6h_minutos (15).</item>
/// <item>intervalo_maximo_minutos: qualquer intervalo concedido (o teto de 2h do caput; abaixo de 6h, política).</item>
/// <item>Posição (só com início informado): intervalo_inicio_minimo_apos_entrada_minutos, intervalo_fim_minimo_antes_saida_minutos
/// e jornada_continua_maxima_minutos (trecho contínuo antes/depois do intervalo).</item>
/// <item>Regra bloqueada (ou ausente na fotografia, ex.: escalas antigas) não é verificada; sem a regra de obrigatoriedade o
/// limite de 6h da CLT continua sendo a condição de aplicação do mínimo/padrão.</item>
/// </list>
/// Intervalo fracionado não existe no modelo (um início e uma duração por célula); intervalo fora da jornada é recusado na API.
/// </summary>
public sealed class IntervalPolicy
{
    /// <summary>CLT art. 71, caput: trabalho contínuo acima de 6h (usado quando a regra não está ativa).</summary>
    public const int DefaultThresholdMinutes = 360;
    /// <summary>CLT art. 71, §1º: o intervalo de 15 min vale para jornada acima de 4h (condição de aplicação, não parâmetro).</summary>
    public const int ShortJourneyFromMinutes = 240;

    /// <summary>Chaves das regras de intervalo (as de posição dependem do início do intervalo).</summary>
    public static readonly string[] DurationKeys =
        [RuleKeys.IntervaloObrigatorioAcima, RuleKeys.IntervaloMinimo, RuleKeys.IntervaloMaximo, RuleKeys.IntervaloPadrao, RuleKeys.IntervaloJornada4a6h];
    public static readonly string[] PositionKeys =
        [RuleKeys.IntervaloInicioMinimoAposEntrada, RuleKeys.IntervaloFimMinimoAntesSaida, RuleKeys.JornadaContinuaMaxima];
    public static readonly string[] AllKeys = [.. DurationKeys, .. PositionKeys];

    /// <summary>Base legal usada quando a linha da fotografia não traz a sua (fotografias antigas).</summary>
    public static readonly IReadOnlyDictionary<string, string> DefaultBaseLegal = new Dictionary<string, string>
    {
        [RuleKeys.IntervaloObrigatorioAcima] = "CLT art. 71, caput",
        [RuleKeys.IntervaloMinimo] = "CLT art. 71, caput",
        [RuleKeys.IntervaloMaximo] = "CLT art. 71, caput (salvo acordo escrito ou norma coletiva)",
        [RuleKeys.IntervaloPadrao] = "Política interna",
        [RuleKeys.IntervaloJornada4a6h] = "CLT art. 71, §1º",
        [RuleKeys.IntervaloInicioMinimoAposEntrada] = "Política interna (TST: intervalo no início da jornada não cumpre a finalidade do art. 71 da CLT)",
        [RuleKeys.IntervaloFimMinimoAntesSaida] = "Política interna (TST: intervalo no fim da jornada não cumpre a finalidade do art. 71 da CLT)",
        [RuleKeys.JornadaContinuaMaxima] = "CLT art. 71, caput (trabalho contínuo acima de 6h exige intervalo)",
    };

    private readonly Dictionary<string, (RuleValue Rule, string BaseLegal)> _rules = new(StringComparer.Ordinal);

    public IntervalPolicy(RuleBook rules, int day)
    {
        foreach (var key in AllKeys)
        {
            if (rules.Value(key, day) is not { } v) continue;
            var baseLegal = rules.Active(key, day)?.BaseLegal;
            _rules[key] = (v, string.IsNullOrWhiteSpace(baseLegal) ? DefaultBaseLegal.GetValueOrDefault(key) ?? "Política interna" : baseLegal.Trim());
        }
    }

    public RuleValue? Rule(string key) => _rules.TryGetValue(key, out var r) ? r.Rule : null;

    public string BaseLegal(string key) => _rules.TryGetValue(key, out var r) ? r.BaseLegal : DefaultBaseLegal.GetValueOrDefault(key) ?? "Política interna";

    /// <summary>Jornada líquida acima deste valor exige intervalo (regra ativa ou 6h da CLT).</summary>
    public int Threshold => Rule(RuleKeys.IntervaloObrigatorioAcima)?.Value ?? DefaultThresholdMinutes;

    private static string H(int minutes) => minutes % 60 == 0 ? $"{minutes / 60}h" : $"{minutes / 60}h{minutes % 60:00}";

    private IntervalCheck Check(string key, bool ok, string detail)
    {
        var (rule, baseLegal) = _rules[key];
        return new IntervalCheck(key, rule.Rigid, ok, detail, baseLegal);
    }

    /// <summary>
    /// Verificações de intervalo de uma célula: <paramref name="start"/>/<paramref name="endAbs"/> em minutos do dia
    /// (fim pode passar de 1440), <paramref name="breakStart"/> absoluto (null = não posicionado) e a duração efetiva.
    /// Cada item é uma unidade de verificação do avaliador (Ok = conforme).
    /// </summary>
    public IEnumerable<IntervalCheck> Checks(int start, int endAbs, int? breakStart, int breakMinutes)
    {
        var duration = endAbs - start;
        var b = Math.Max(0, breakMinutes);
        var worked = duration - b;
        var threshold = Threshold;

        if (worked > threshold)
        {
            if (_rules.ContainsKey(RuleKeys.IntervaloObrigatorioAcima))
            {
                yield return Check(RuleKeys.IntervaloObrigatorioAcima, b > 0,
                    $"Jornada de {H(worked)} sem intervalo; acima de {H(threshold)} o intervalo de refeição é obrigatório");
            }
            if (b > 0 || !_rules.ContainsKey(RuleKeys.IntervaloObrigatorioAcima))
            {
                if (Rule(RuleKeys.IntervaloMinimo) is { } min)
                    yield return Check(RuleKeys.IntervaloMinimo, b >= min.Value,
                        $"Intervalo de {b} min em jornada de {H(worked)}; mínimo {min.Value} min");
                if (Rule(RuleKeys.IntervaloPadrao) is { } std)
                    yield return Check(RuleKeys.IntervaloPadrao, b == std.Value,
                        $"Intervalo de {b} min em jornada de {H(worked)}; padrão da empresa {std.Value} min");
            }
        }
        else if (worked > ShortJourneyFromMinutes && Rule(RuleKeys.IntervaloJornada4a6h) is { } shortRule)
        {
            yield return Check(RuleKeys.IntervaloJornada4a6h, b >= shortRule.Value,
                $"Intervalo de {b} min em jornada de {H(worked)}; jornada acima de 4h exige pelo menos {shortRule.Value} min");
        }

        if (b > 0 && Rule(RuleKeys.IntervaloMaximo) is { } max)
            yield return Check(RuleKeys.IntervaloMaximo, b <= max.Value, $"Intervalo de {b} min; máximo {max.Value} min");

        if (b == 0)
        {
            // Sem intervalo, a jornada inteira é um trecho contínuo (acima do limite já conta a obrigatoriedade)
            if (worked <= threshold && worked > ShortJourneyFromMinutes && Rule(RuleKeys.JornadaContinuaMaxima) is { } cont0)
                yield return Check(RuleKeys.JornadaContinuaMaxima, duration <= cont0.Value,
                    $"Trecho de {H(duration)} de trabalho contínuo sem intervalo; máximo {H(cont0.Value)}");
            yield break;
        }
        if (breakStart is not { } bs || bs < start || bs + b > endAbs) yield break;

        var before = bs - start;
        var after = endAbs - bs - b;
        if (Rule(RuleKeys.IntervaloInicioMinimoAposEntrada) is { } first)
            yield return Check(RuleKeys.IntervaloInicioMinimoAposEntrada, before >= first.Value,
                $"Intervalo começa {before} min após a entrada; mínimo {first.Value} min");
        if (Rule(RuleKeys.IntervaloFimMinimoAntesSaida) is { } last)
            yield return Check(RuleKeys.IntervaloFimMinimoAntesSaida, after >= last.Value,
                $"Intervalo termina {after} min antes da saída; mínimo {last.Value} min");
        if (Rule(RuleKeys.JornadaContinuaMaxima) is { } cont)
        {
            var longest = Math.Max(before, after);
            yield return Check(RuleKeys.JornadaContinuaMaxima, longest <= cont.Value,
                $"Trecho de {H(longest)} de trabalho contínuo {(before >= after ? "antes" : "depois")} do intervalo; máximo {H(cont.Value)}");
        }
    }

    /// <summary>Posição conforme às regras de posição (só rígidas, ou todas).</summary>
    public bool PositionOk(int start, int endAbs, int breakStart, int breakMinutes, bool rigidOnly) =>
        breakStart >= start && breakStart + breakMinutes <= endAbs &&
        Checks(start, endAbs, breakStart, breakMinutes)
            .Where(c => PositionKeys.Contains(c.Key) && (!rigidOnly || c.Rigid))
            .All(c => c.Ok);

    /// <summary>Durações e janelas de início permitidas (varredura de 5 em 5 minutos).</summary>
    public IntervalWindow Window(int start, int endAbs, int breakMinutes)
    {
        const int step = 5;
        var duration = endAbs - start;
        var okDurations = new List<int>();
        for (var b = 0; b <= Math.Min(duration - step, 240); b += step)
        {
            if (Checks(start, endAbs, null, b).Where(c => c.Rigid && DurationKeys.Contains(c.Key)).All(c => c.Ok)) okDurations.Add(b);
        }
        var required = !okDurations.Contains(0);
        var minD = okDurations.Count > 0 ? okDurations.Min() : 0;
        var maxD = okDurations.Count > 0 ? okDurations.Max() : 0;

        int? legalFrom = null, legalTo = null, recFrom = null, recTo = null;
        if (breakMinutes > 0)
        {
            for (var bs = start; bs + breakMinutes <= endAbs; bs += step)
            {
                if (PositionOk(start, endAbs, bs, breakMinutes, rigidOnly: true))
                {
                    legalFrom ??= bs;
                    legalTo = bs;
                }
                if (PositionOk(start, endAbs, bs, breakMinutes, rigidOnly: false))
                {
                    recFrom ??= bs;
                    recTo = bs;
                }
            }
        }
        return new IntervalWindow(minD, maxD, Rule(RuleKeys.IntervaloPadrao)?.Value, required, legalFrom, legalTo, recFrom, recTo);
    }
}

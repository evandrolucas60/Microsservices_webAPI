namespace Dimensionamento.Api.Calculo;

/// <summary>
/// Fórmulas de fila usadas no dimensionamento por hora (atendimento no caixa como fila M/M/N).
/// <list type="bullet">
/// <item>Carga (intensidade de tráfego, em Erlangs): A = λ · TMA / 3600, com λ = documentos (chegadas) por hora e
/// TMA = tempo médio de atendimento por documento, em segundos. A é o número médio de atendentes ocupados.</item>
/// <item>Por utilização alvo ρ: N = ⌈A / ρ⌉ (cada atendente fica ocupado no máximo ρ do tempo).</item>
/// <item>Por nível de serviço (Erlang C): menor N &gt; A com P(espera ≤ T) = 1 − C(N, A) · e^(−(N − A)·T/TMA) ≥ meta.</item>
/// </list>
/// </summary>
public static class FilaErlang
{
    /// <summary>Tolerância para arredondar para cima sem transformar 3,0000000001 em 4.</summary>
    private const double Eps = 1e-9;

    /// <summary>Carga em Erlangs: A = λ · TMA / 3600.</summary>
    public static double Carga(double chegadasPorHora, double tmaSegundos) =>
        chegadasPorHora <= 0 || tmaSegundos <= 0 ? 0 : chegadasPorHora * tmaSegundos / 3600.0;

    /// <summary>Atendentes pela utilização alvo: N = ⌈A / ρ⌉ (0 quando não há carga).</summary>
    public static int PorUtilizacao(double carga, double utilizacao)
    {
        if (carga <= 0) return 0;
        if (utilizacao is <= 0 or > 1) throw new ArgumentOutOfRangeException(nameof(utilizacao), "A utilização alvo deve estar entre 0 e 1.");
        return (int)Math.Ceiling(carga / utilizacao - Eps);
    }

    /// <summary>
    /// Erlang B (probabilidade de bloqueio) pela recorrência estável B(0) = 1; B(k) = A·B(k−1) / (k + A·B(k−1)).
    /// </summary>
    public static double ErlangB(int atendentes, double carga)
    {
        if (atendentes < 0) throw new ArgumentOutOfRangeException(nameof(atendentes));
        var b = 1.0;
        for (var k = 1; k <= atendentes; k++) b = carga * b / (k + carga * b);
        return b;
    }

    /// <summary>
    /// Erlang C: probabilidade de o cliente esperar (todos os N atendentes ocupados).
    /// C(N, A) = N·B / (N − A·(1 − B)), com B = Erlang B(N, A). Com N ≤ A a fila cresce sem limite: C = 1.
    /// </summary>
    public static double ErlangC(int atendentes, double carga)
    {
        if (carga <= 0) return 0;
        if (atendentes <= carga) return 1;
        var b = ErlangB(atendentes, carga);
        return atendentes * b / (atendentes - carga * (1 - b));
    }

    /// <summary>Nível de serviço: P(espera ≤ T) = 1 − C(N, A) · e^(−(N − A)·T / TMA). Com N ≤ A: 0.</summary>
    public static double NivelServico(int atendentes, double carga, double tmaSegundos, double tempoAlvoSegundos)
    {
        if (carga <= 0) return 1;
        if (atendentes <= carga) return 0;
        return 1 - ErlangC(atendentes, carga) * Math.Exp(-(atendentes - carga) * tempoAlvoSegundos / tmaSegundos);
    }

    /// <summary>Espera média (s): C(N, A) · TMA / (N − A). Com N ≤ A: infinita.</summary>
    public static double EsperaMedia(int atendentes, double carga, double tmaSegundos)
    {
        if (carga <= 0) return 0;
        if (atendentes <= carga) return double.PositiveInfinity;
        return ErlangC(atendentes, carga) * tmaSegundos / (atendentes - carga);
    }

    /// <summary>Menor N com nível de serviço ≥ meta (0 quando não há carga).</summary>
    public static int PorNivelServico(double carga, double tmaSegundos, double tempoAlvoSegundos, double meta, int maximo = 1000)
    {
        if (carga <= 0) return 0;
        if (meta is <= 0 or >= 1) throw new ArgumentOutOfRangeException(nameof(meta), "A meta do nível de serviço deve estar entre 0 e 1 (exclusive).");
        for (var n = (int)Math.Floor(carga) + 1; n <= maximo; n++)
        {
            if (NivelServico(n, carga, tmaSegundos, tempoAlvoSegundos) >= meta - Eps) return n;
        }
        return maximo;
    }

    /// <summary>Arredonda para cima tolerando erro de ponto flutuante.</summary>
    public static int Teto(double valor) => valor <= 0 ? 0 : (int)Math.Ceiling(valor - Eps);
}

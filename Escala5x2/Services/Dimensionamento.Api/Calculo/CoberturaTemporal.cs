using System.Diagnostics;
using Google.OrTools.Sat;

namespace Dimensionamento.Api.Calculo;

/// <summary>
/// Entrada do modelo de cobertura temporal. Dias 0..6 = segunda..domingo; faixas de <see cref="Faixa"/> minutos a partir
/// da abertura. <see cref="Necessidade"/>[d][t] = pessoas trabalhando (fora do intervalo) exigidas na faixa;
/// <see cref="Minimo"/>[d][t] = parte rígida (cobertura mínima/abertura/fechamento), sempre ≤ necessidade.
/// </summary>
public sealed record ModeloCobertura
{
    public required int Abertura { get; init; }
    public required int Fechamento { get; init; }
    public required int Faixa { get; init; }
    public required IReadOnlyList<TurnoTipo> Turnos { get; init; }
    public required int[][] Necessidade { get; init; }
    public required int[][] Minimo { get; init; }
    /// <summary>Dias de trabalho por pessoa na semana (6 no 6x1, 5 no 5x2).</summary>
    public required int DiasTrabalho { get; init; }
    /// <summary>Folga em 1 domingo a cada N semanas: no máximo (N−1)/N do quadro trabalha no domingo. Null = sem limite.</summary>
    public int? JanelaDomingo { get; init; }
    /// <summary>Descanso mínimo entre jornadas (min). 0 = não considera.</summary>
    public int Interjornada { get; init; }
    /// <summary>Fração das faixas do expediente em que a necessidade deve ser atendida (1 = todas). O mínimo é sempre rígido.</summary>
    public double Meta { get; init; } = 1.0;
    /// <summary>Modo distribuição: quadro fixo (pessoas disponíveis) — minimiza o déficit em vez do quadro.</summary>
    public int? QuadroFixo { get; init; }
    /// <summary>Modo distribuição: pessoas disponíveis em cada dia (descontadas as ausências).</summary>
    public int[]? CapacidadeDia { get; init; }
    public double TempoLimite { get; init; } = 20;
    public int Workers { get; init; } = 8;
    /// <summary>Solução inicial (pessoas por dia e turno-tipo), ex.: a da meta de 100% ao resolver uma meta menor.</summary>
    public int[][]? Dica { get; init; }

    public int Faixas => (Fechamento - Abertura) / Faixa;
}

/// <summary>
/// Resultado: <see cref="Status"/> Otima (ótimo provado), Viavel (solução sem prova de ótimo), Inviavel (provado que
/// não existe), SemSolucaoNoTempo (não concluiu no tempo — NÃO é inviável), Indisponivel (OR-Tools não carregou) ou Falha.
/// <see cref="Uso"/>[d][s] = pessoas no turno-tipo s no dia d; <see cref="Cobertura"/>[d][t] = pessoas trabalhando na faixa.
/// </summary>
public sealed record ResultadoCobertura(
    string Status, string Mensagem, int? Pessoas, double Segundos, int[][]? Cobertura, int[][]? Uso, int? LimiteInferior,
    int? DeficitFaixas = null, int? DeficitMinimoFaixas = null)
{
    public bool TemSolucao => Status is StatusSolucao.Otima or StatusSolucao.Viavel;
}

public static class StatusSolucao
{
    public const string Otima = "Otima";
    public const string Viavel = "Viavel";
    public const string Inviavel = "Inviavel";
    public const string SemSolucaoNoTempo = "SemSolucaoNoTempo";
    public const string Indisponivel = "Indisponivel";
    public const string Falha = "Falha";
}

/// <summary>
/// Cobertura temporal por programação inteira (OR-Tools CP-SAT), agregada por turno-tipo e dia:
/// <code>
/// variáveis: x[d][s] ∈ ℕ (pessoas no turno-tipo s no dia d), N ∈ ℕ (pessoas na escala)
/// cobertura: Σ_{s trabalha na faixa t} x[d][s] ≥ necessidade[d][t]          (toda faixa de todo dia)
/// regime:    w[d] = Σ_s x[d][s] ≤ N;  Σ_d w[d] ≤ D·N                           (cada pessoa trabalha D dias: 6x1 → 6, 5x2 → 5)
/// domingo:   J·w[dom] ≤ (J − 1)·N                                             (folga em 1 domingo a cada J semanas)
/// interjornada (necessária, agregada): para cada horário T de saída,
///            Σ_{s sai ≥ T} x[d][s] + Σ_{s entra &lt; T + inter − 24h} x[d+1][s] ≤ N
/// objetivo:  min N (e, empatado, o menor total de turnos — menos excedente)
/// </code>
/// Com Σw ≤ D·N e w[d] ≤ N sempre existe uma distribuição de folgas com exatamente D dias por pessoa (completa-se com
/// turnos excedentes em dias abaixo de N). Limitações: a interjornada e o rodízio de domingos entram como condições
/// agregadas necessárias; a escala individual (motor) pode precisar de algum ajuste fino.
/// </summary>
public static class CoberturaTemporal
{
    public const string MensagemOrTools =
        "O otimizador (Google OR-Tools) não pôde ser carregado neste servidor. No Windows, instale o Microsoft Visual C++ " +
        "Redistributable (x64) e reinicie o serviço. O dimensionamento por carga de trabalho continua disponível.";

    public static ResultadoCobertura Resolver(ModeloCobertura m)
    {
        try
        {
            return ResolverInterno(m);
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or BadImageFormatException or EntryPointNotFoundException)
        {
            return new ResultadoCobertura(StatusSolucao.Indisponivel, $"{MensagemOrTools} ({ex.GetType().Name}: {ex.GetBaseException().Message})",
                null, 0, null, null, null);
        }
    }

    private sealed record Grupo(int[] Turnos, List<int> Faixas);

    private static ResultadoCobertura ResolverInterno(ModeloCobertura m)
    {
        var relogio = Stopwatch.StartNew();
        var faixas = m.Faixas;
        var turnos = m.Turnos;
        var distribuicao = m.QuadroFixo is not null;

        // Faixas com o mesmo conjunto de turnos que as cobrem viram uma restrição só (com a maior necessidade)
        var grupos = new Dictionary<string, Grupo>();
        var grupoDaFaixa = new Grupo[faixas];
        for (var t = 0; t < faixas; t++)
        {
            var de = m.Abertura + t * m.Faixa;
            var cobrem = Enumerable.Range(0, turnos.Count).Where(s => turnos[s].Trabalhando(de, de + m.Faixa)).ToArray();
            var chave = string.Join(',', cobrem);
            if (!grupos.TryGetValue(chave, out var g)) grupos[chave] = g = new Grupo(cobrem, []);
            g.Faixas.Add(t);
            grupoDaFaixa[t] = g;
        }

        if (!distribuicao)
        {
            for (var d = 0; d < 7; d++)
            for (var t = 0; t < faixas; t++)
            {
                var exigido = m.Meta >= 1 ? m.Necessidade[d][t] : m.Minimo[d][t];
                if (exigido > 0 && grupoDaFaixa[t].Turnos.Length == 0)
                {
                    var de = m.Abertura + t * m.Faixa;
                    return new ResultadoCobertura(StatusSolucao.Inviavel,
                        $"Nenhum turno permitido cobre a faixa {Hm(de)}–{Hm(de + m.Faixa)} ({CurvaDemanda.Semana[d]}): ajuste os turnos, o intervalo ou o horário.",
                        null, relogio.Elapsed.TotalSeconds, null, null, null);
                }
            }
        }

        if (distribuicao)
        {
            var dist = Montar(m, grupos.Values, quadroFixo: m.QuadroFixo);
            dist.Model.Minimize(dist.Objetivo!);
            var sd = Novo(m.TempoLimite, m.Workers);
            var st = sd.Solve(dist.Model);
            if (st is not (CpSolverStatus.Optimal or CpSolverStatus.Feasible)) return SemSolucao(st, m.TempoLimite, relogio.Elapsed.TotalSeconds);
            var (usoD, cobD) = Ler(sd, dist.X, grupoDaFaixa, faixas);
            var defFaixas = 0;
            var defMinFaixas = 0;
            for (var d = 0; d < 7; d++)
            for (var t = 0; t < faixas; t++)
            {
                if (cobD[d][t] < m.Necessidade[d][t]) defFaixas++;
                if (cobD[d][t] < m.Minimo[d][t]) defMinFaixas++;
            }
            var otimaD = st == CpSolverStatus.Optimal;
            return new ResultadoCobertura(otimaD ? StatusSolucao.Otima : StatusSolucao.Viavel,
                otimaD ? "Melhor distribuição possível com o quadro disponível (comprovada)." : "Melhor distribuição encontrada no tempo limite (sem prova de ótimo).",
                m.QuadroFixo, relogio.Elapsed.TotalSeconds, cobD, usoD, null, defFaixas, defMinFaixas);
        }

        // Etapa 1: menor quadro N (prova de otimalidade rápida, sem o desempate)
        var etapa1 = Montar(m, grupos.Values, quadroFixo: null);
        etapa1.Model.Minimize(etapa1.N!);
        Dica(etapa1, m.Dica, turnos.Count);
        var s1 = Novo(m.TempoLimite * 0.8, m.Workers);
        var status = s1.Solve(etapa1.Model);
        if (status is not (CpSolverStatus.Optimal or CpSolverStatus.Feasible)) return SemSolucao(status, m.TempoLimite, relogio.Elapsed.TotalSeconds);
        var pessoas = (int)s1.Value(etapa1.N!);
        var limite = Math.Min(pessoas, Math.Max(0, (int)Math.Ceiling(s1.BestObjectiveBound - 1e-6)));
        var (uso, cob) = Ler(s1, etapa1.X, grupoDaFaixa, faixas);

        // Etapa 2: com N fixo, o menor total de turnos (menos excedente fora do pico)
        var etapa2 = Montar(m, grupos.Values, quadroFixo: pessoas);
        etapa2.Model.Minimize(LinearExpr.Sum(etapa2.X.SelectMany(dia => dia)));
        Dica(etapa2, uso, turnos.Count);
        var s2 = Novo(Math.Clamp(m.TempoLimite * 0.2, 1, 5), m.Workers);
        var status2 = s2.Solve(etapa2.Model);
        if (status2 is CpSolverStatus.Optimal or CpSolverStatus.Feasible) (uso, cob) = Ler(s2, etapa2.X, grupoDaFaixa, faixas);

        var otima = status == CpSolverStatus.Optimal;
        return new ResultadoCobertura(otima ? StatusSolucao.Otima : StatusSolucao.Viavel,
            otima
                ? "Quadro mínimo comprovado (ótimo) para os turnos e o regime informados."
                : $"Melhor quadro encontrado no tempo limite; o mínimo possível é pelo menos {limite}.",
            pessoas, relogio.Elapsed.TotalSeconds, cob, uso, limite);
    }

    private sealed record Montado(CpModel Model, IntVar[][] X, IntVar? N, LinearExpr? Objetivo);

    /// <summary>
    /// Monta o modelo. <paramref name="quadroFixo"/> null = N livre (minimizar quadro); com valor = N fixo (etapa 2 ou
    /// distribuição dos disponíveis, quando o objetivo é o déficit ponderado).
    /// </summary>
    private static Montado Montar(ModeloCobertura m, IEnumerable<Grupo> grupos, int? quadroFixo)
    {
        var turnos = m.Turnos;
        var faixas = m.Faixas;
        var distribuicao = m.QuadroFixo is not null;
        var maxNec = m.Necessidade.Max(dia => dia.Length == 0 ? 0 : dia.Max());
        var model = new CpModel();
        var x = new IntVar[7][];
        var w = new LinearExpr[7];
        var limiteN = Math.Max(1, maxNec) * 7 * 4 + 10;
        IntVar? nVar = null;
        LinearExpr nExpr;
        if (quadroFixo is { } q)
        {
            nExpr = LinearExpr.Constant(q);
        }
        else
        {
            var lb = m.Meta >= 1 ? maxNec : m.Minimo.Max(dia => dia.Length == 0 ? 0 : dia.Max());
            nVar = model.NewIntVar(lb, limiteN, "N");
            nExpr = nVar;
        }

        for (var d = 0; d < 7; d++)
        {
            var maxDia = m.Necessidade[d].Length == 0 ? 0 : m.Necessidade[d].Max();
            var cap = distribuicao ? Math.Min(quadroFixo!.Value, m.CapacidadeDia?[d] ?? quadroFixo.Value) : maxDia;
            x[d] = new IntVar[turnos.Count];
            for (var s = 0; s < turnos.Count; s++) x[d][s] = model.NewIntVar(0, Math.Max(0, Math.Min(cap, maxDia)), $"x_{d}_{s}");
            w[d] = LinearExpr.Sum(x[d]);
            if (distribuicao) model.Add(w[d] <= Math.Max(0, cap));
            else model.Add(w[d] <= nExpr);
        }
        model.Add(LinearExpr.Sum(w) <= nExpr * m.DiasTrabalho);
        if (m.JanelaDomingo is { } janela && janela >= 2)
            model.Add(w[6] * janela <= nExpr * (janela - 1));

        // Interjornada agregada: quem sai tarde no dia d não pode entrar cedo no dia d+1 (semana cíclica)
        if (m.Interjornada > 0)
        {
            var saidas = turnos.Select(t => t.Fim).Distinct().Order().ToList();
            for (var d = 0; d < 7; d++)
            {
                var seguinte = (d + 1) % 7;
                foreach (var corte in saidas)
                {
                    var tarde = Enumerable.Range(0, turnos.Count).Where(s => turnos[s].Fim >= corte).ToList();
                    var cedo = Enumerable.Range(0, turnos.Count).Where(s => turnos[s].Inicio + 1440 - corte < m.Interjornada).ToList();
                    if (tarde.Count == 0 || cedo.Count == 0) continue;
                    model.Add(LinearExpr.Sum(tarde.Select(s => x[d][s])) + LinearExpr.Sum(cedo.Select(s => x[seguinte][s])) <= nExpr);
                }
            }
        }

        var deficits = new List<(IntVar Var, int Peso)>();
        var deficitsMinimo = new List<(IntVar Var, int Peso)>();
        var cobertas = new List<(BoolVar Var, int Faixas)>();
        var faixasSempreCobertas = 0;
        foreach (var g in grupos)
        {
            var cobertura = g.Turnos.Length == 0 ? null : g.Turnos;
            for (var d = 0; d < 7; d++)
            {
                var nec = g.Faixas.Max(t => m.Necessidade[d][t]);
                var min = g.Faixas.Max(t => m.Minimo[d][t]);
                var soma = cobertura is null ? LinearExpr.Constant(0) : LinearExpr.Sum(cobertura.Select(s => x[d][s]));
                if (distribuicao)
                {
                    if (nec <= 0) continue;
                    var def = model.NewIntVar(0, nec, $"def_{d}");
                    model.Add(soma + def >= nec);
                    deficits.Add((def, g.Faixas.Count));
                    if (min > 0)
                    {
                        var defMin = model.NewIntVar(0, min, $"defmin_{d}");
                        model.Add(soma + defMin >= min);
                        deficitsMinimo.Add((defMin, g.Faixas.Count));
                    }
                }
                else if (m.Meta >= 1 || nec <= min)
                {
                    if (nec > 0) model.Add(soma >= nec);
                    faixasSempreCobertas += g.Faixas.Count;
                }
                else
                {
                    if (min > 0) model.Add(soma >= min);
                    var c = model.NewBoolVar($"c_{d}");
                    model.Add(soma >= min + (nec - min) * c);
                    cobertas.Add((c, g.Faixas.Count));
                }
            }
        }
        if (!distribuicao && m.Meta < 1)
        {
            var exigidas = (int)Math.Ceiling(m.Meta * 7 * faixas - 1e-9);
            model.Add(LinearExpr.Sum(cobertas.Select(c => c.Var * c.Faixas)) >= exigidas - faixasSempreCobertas);
        }

        // Distribuição: o mínimo (rígido na escala) pesa muito mais que o reforço; empate: menos turnos excedentes
        LinearExpr? objetivo = distribuicao
            ? LinearExpr.Sum(deficitsMinimo.Select(v => v.Var * (v.Peso * 1000))) + LinearExpr.Sum(deficits.Select(v => v.Var * (v.Peso * 10))) +
              LinearExpr.Sum(x.SelectMany(dia => dia))
            : null;
        return new Montado(model, x, nVar, objetivo);
    }

    private static void Dica(Montado montado, int[][]? dica, int turnos)
    {
        if (dica is not { Length: 7 } || dica.Any(d => d.Length != turnos)) return;
        for (var d = 0; d < 7; d++)
        for (var s = 0; s < turnos; s++) montado.Model.AddHint(montado.X[d][s], dica[d][s]);
    }

    private static (int[][] Uso, int[][] Cobertura) Ler(CpSolver solver, IntVar[][] x, Grupo[] grupoDaFaixa, int faixas)
    {
        var uso = new int[7][];
        var cob = new int[7][];
        for (var d = 0; d < 7; d++)
        {
            uso[d] = x[d].Select(v => (int)solver.Value(v)).ToArray();
            cob[d] = new int[faixas];
            for (var t = 0; t < faixas; t++) cob[d][t] = grupoDaFaixa[t].Turnos.Sum(s => uso[d][s]);
        }
        return (uso, cob);
    }

    private static CpSolver Novo(double segundos, int workers) => new()
    {
        StringParameters = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"max_time_in_seconds:{Math.Max(1, segundos):F1} num_workers:{Math.Max(1, workers)} random_seed:42")
    };

    private static ResultadoCobertura SemSolucao(CpSolverStatus status, double tempo, double segundos) => status switch
    {
        CpSolverStatus.Infeasible => new ResultadoCobertura(StatusSolucao.Inviavel,
            "Provado que não existe quadro que cubra a necessidade com os turnos e o regime informados (ex.: faixa sem turno possível, " +
            "interjornada ou rodízio de domingos impossíveis com esses turnos).", null, segundos, null, null, null),
        CpSolverStatus.Unknown => new ResultadoCobertura(StatusSolucao.SemSolucaoNoTempo,
            $"Não foi possível concluir em {tempo:0} s (isso não significa inviável). Tente com mais tempo.", null, segundos, null, null, null),
        _ => new ResultadoCobertura(StatusSolucao.Falha, $"O otimizador recusou o modelo ({status}).", null, segundos, null, null, null),
    };

    public static string Hm(int minutos) => $"{minutos / 60 % 24:00}:{minutos % 60:00}";
}

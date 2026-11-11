using System.Globalization;

namespace Dimensionamento.Api.Calculo;

/// <summary>
/// Dimensionamento do quadro (cálculo puro, sem acesso a outros serviços):
/// <list type="number">
/// <item><b>Necessidade por hora</b>: λ (doc/h) → carga A = λ·TMA/3600 → N = ⌈A/ρ⌉ (utilização alvo) e, se pedido,
/// o menor N do Erlang C que atinge o nível de serviço → necessário = max(cobertura mínima, calculado) no expediente.</item>
/// <item><b>Carga total de trabalho</b>: horas-pessoa/semana = Σ necessário × fração da hora no expediente;
/// quadro = ⌈horas-pessoa ÷ (horas por colaborador/semana × (1 − shrinkage))⌉. Ignora QUANDO as horas acontecem.</item>
/// <item><b>Cobertura temporal</b>: menor quadro que, com os turnos permitidos e o regime (dias de trabalho por semana),
/// cobre a necessidade de cada faixa de cada dia (<see cref="CoberturaTemporal"/>); quadro = ⌈N ÷ (1 − shrinkage)⌉.
/// Como os turnos têm duração fixa e os picos são curtos, sobra gente fora do pico: temporal ≥ carga.</item>
/// </list>
/// </summary>
public static class Dimensionador
{
    public static readonly string[] RotuloDia = ["Seg", "Ter", "Qua", "Qui", "Sex", "Sáb", "Dom"];

    /// <summary>Necessidade por dia e hora para um conjunto de parâmetros (TMA, utilização, cobertura mínima).</summary>
    public sealed record Necessidade(
        double[] Fracao, int[] MinimoHora, double[][] Carga, int[][] PorUtilizacao, int[][]? PorNivelServico, int[][] Calculado,
        int[][] Final, double HorasPessoa, double HorasPessoaDemanda, double HorasCargaPura)
    {
        public int Pico => Final.Max(d => d.Max());
        public int HorasComMinimo { get; init; }
    }

    public static Necessidade CalcularNecessidade(EntradaCalculo e, double tma, double utilizacao, int coberturaMinima)
    {
        var minAbertura = coberturaMinima + Math.Max(0, e.MinimoAbertura - e.CoberturaMinima);
        var minFechamento = coberturaMinima + Math.Max(0, e.MinimoFechamento - e.CoberturaMinima);
        var fracao = new double[24];
        var minimo = new int[24];
        for (var h = 0; h < 24; h++)
        {
            var sobreposto = Math.Min(h * 60 + 60, e.Fechamento) - Math.Max(h * 60, e.Abertura);
            fracao[h] = Math.Max(0, sobreposto) / 60.0;
            if (fracao[h] <= 0) continue;
            minimo[h] = coberturaMinima;
            if (h == e.Abertura / 60) minimo[h] = Math.Max(minimo[h], minAbertura);
            if (h == (e.Fechamento - 1) / 60) minimo[h] = Math.Max(minimo[h], minFechamento);
        }

        var sl = e.NivelServicoMeta is { } meta && e.NivelServicoTempoSegundos is { } tempo ? (meta, tempo) : ((double, double)?)null;
        var carga = new double[7][];
        var porUtil = new int[7][];
        var porSl = sl is null ? null : new int[7][];
        var calc = new int[7][];
        var final = new int[7][];
        double hp = 0, hpDemanda = 0, hpCarga = 0;
        var horasComMinimo = 0;
        for (var d = 0; d < 7; d++)
        {
            carga[d] = new double[24];
            porUtil[d] = new int[24];
            if (porSl is not null) porSl[d] = new int[24];
            calc[d] = new int[24];
            final[d] = new int[24];
            for (var h = 0; h < 24; h++)
            {
                var a = FilaErlang.Carga(e.Demanda[d][h], tma);
                carga[d][h] = a;
                porUtil[d][h] = FilaErlang.PorUtilizacao(a, utilizacao);
                var n = porUtil[d][h];
                if (porSl is not null)
                {
                    porSl[d][h] = FilaErlang.PorNivelServico(a, tma, sl!.Value.Item2, sl.Value.Item1);
                    n = Math.Max(n, porSl[d][h]);
                }
                calc[d][h] = n;
                if (fracao[h] <= 0) continue;
                final[d][h] = Math.Max(minimo[h], n);
                // Pico manual da loja: a quantidade cadastrada substitui a necessidade calculada (nunca abaixo do mínimo) e o
                // adicional soma, como no motor da escala
                var pico = e.PicoManual?[d][h] ?? 0;
                var adicional = e.PicoAdicional?[d][h] ?? 0;
                if (pico > 0 || adicional > 0) final[d][h] = Math.Max(minimo[h], pico > 0 ? pico : final[d][h]) + adicional;
                if (n < minimo[h]) horasComMinimo++;
                hp += final[d][h] * fracao[h];
                hpDemanda += n * fracao[h];
                hpCarga += a * fracao[h];
            }
        }
        return new Necessidade(fracao, minimo, carga, porUtil, porSl, calc, final, hp, hpDemanda, hpCarga) { HorasComMinimo = horasComMinimo };
    }

    /// <summary>Horas pagas por turno: (duração − intervalo); com turnos do cadastro, a média deles.</summary>
    public static double HorasPagasPorTurno(EntradaCalculo e) =>
        e.ModoTurnos == ModoTurnos.Cadastrados && e.TurnosCadastrados is { Count: > 0 } c
            ? c.Average(t => t.Fim - t.Inicio - t.IntervaloMinutos) / 60.0
            : (e.DuracaoTurno - e.Intervalo) / 60.0;

    /// <summary>Horas trabalháveis por colaborador/semana: o menor entre a carga semanal e dias × horas pagas do turno.</summary>
    public static double HorasPorColaborador(EntradaCalculo e) => Math.Min(e.HorasSemanais, e.DiasTrabalho * HorasPagasPorTurno(e));

    public static (int EmEscala, int Quadro, double Fracionario) QuadroPorCarga(EntradaCalculo e, double horasPessoa)
    {
        var horas = HorasPorColaborador(e);
        var produtivas = horas * (1 - e.Shrinkage);
        return (FilaErlang.Teto(horasPessoa / horas), FilaErlang.Teto(horasPessoa / produtivas), horasPessoa / produtivas);
    }

    public static int QuadroComShrinkage(int pessoasEmEscala, double shrinkage) => FilaErlang.Teto(pessoasEmEscala / (1 - shrinkage));

    public static List<TurnoTipo> Turnos(EntradaCalculo e) =>
        GeradorTurnos.Gerar(e.Abertura, e.Fechamento, e.DuracaoTurno, e.Intervalo, e.Incremento, e.ModoTurnos, e.RegrasIntervalo,
            e.FlexMaisCedo, e.FlexMaisTarde, e.TurnosCadastrados);

    public static int Faixa(EntradaCalculo e, IEnumerable<TurnoTipo> turnos) =>
        GeradorTurnos.Mdc(turnos.SelectMany(t => new[] { t.Inicio, t.Fim, t.IntervaloInicio, t.IntervaloFim })
            .Concat([e.Abertura, e.Fechamento, 60]));

    public static ModeloCobertura Modelo(EntradaCalculo e, Necessidade n, IReadOnlyList<TurnoTipo> turnos, int faixa, double meta, double tempo, int workers)
    {
        var faixas = (e.Fechamento - e.Abertura) / faixa;
        var nec = new int[7][];
        var min = new int[7][];
        for (var d = 0; d < 7; d++)
        {
            nec[d] = new int[faixas];
            min[d] = new int[faixas];
            for (var t = 0; t < faixas; t++)
            {
                var h = (e.Abertura + t * faixa) / 60;
                nec[d][t] = n.Final[d][h];
                min[d][t] = Math.Min(n.MinimoHora[h], nec[d][t]);
            }
        }
        return new ModeloCobertura
        {
            Abertura = e.Abertura, Fechamento = e.Fechamento, Faixa = faixa, Turnos = turnos, Necessidade = nec, Minimo = min,
            DiasTrabalho = e.DiasTrabalho, JanelaDomingo = e.JanelaDomingo, Interjornada = e.Interjornada, Meta = meta,
            TempoLimite = tempo, Workers = workers
        };
    }

    public static ResultadoCalculo Calcular(EntradaCalculo e)
    {
        Validar(e);
        var avisos = new List<string>();
        var n = CalcularNecessidade(e, e.TmaSegundos, e.Utilizacao, e.CoberturaMinima);
        var turnos = Turnos(e);
        var faixa = Faixa(e, turnos);
        var faixas = (e.Fechamento - e.Abertura) / faixa;

        // ---------- Carga total de trabalho ----------
        var (emEscalaCarga, quadroCarga, fracionario) = QuadroPorCarga(e, n.HorasPessoa);
        var horasColab = HorasPorColaborador(e);
        var pagas = HorasPagasPorTurno(e);
        if (e.DiasTrabalho * pagas + 1e-9 < e.HorasSemanais)
            avisos.Add($"Com turnos de {Num(pagas)} h pagas e {e.DiasTrabalho} dias por semana cada pessoa trabalha {Num(e.DiasTrabalho * pagas)} h, " +
                       $"abaixo da carga semanal de {Num(e.HorasSemanais)} h: o cálculo por carga usa {Num(horasColab)} h.");
        var carga = new ResumoCarga(R(n.HorasPessoa), R(n.HorasPessoaDemanda), R(n.HorasCargaPura), R(n.HorasPessoa - n.HorasPessoaDemanda),
            e.HorasSemanais, R(pagas), e.DiasTrabalho, R(horasColab), R(horasColab * (1 - e.Shrinkage)), e.Shrinkage, emEscalaCarga, quadroCarga,
            R(fracionario),
            $"⌈{Num(n.HorasPessoa)} h-pessoa ÷ ({Num(horasColab)} h × (1 − {Pct(e.Shrinkage)}))⌉ = ⌈{Num(fracionario)}⌉ = {quadroCarga}");

        // ---------- Cobertura temporal ----------
        var principal = CoberturaTemporal.Resolver(Modelo(e, n, turnos, faixa, 1.0, e.TempoLimite, e.Workers));
        var temporal = ResumirTemporal(e, n, turnos, faixa, principal);
        if (principal.Status == StatusSolucao.Indisponivel) avisos.Add(principal.Mensagem);

        // ---------- Disponíveis (melhor distribuição possível do quadro atual) ----------
        int[][]? disponivelHora = null;
        ResumoDisponibilidade disponibilidade;
        if (e.Disponibilidade is { } disp)
        {
            int[][] cobDisp;
            string status;
            string? mensagem = null;
            if (disp.Ativos <= 0)
            {
                cobDisp = Enumerable.Range(0, 7).Select(_ => new int[faixas]).ToArray();
                status = StatusSolucao.Otima;
                mensagem = "Nenhum colaborador ativo no setor/loja.";
            }
            else
            {
                var modelo = Modelo(e, n, turnos, faixa, 1.0, e.TempoLimite, e.Workers) with
                {
                    QuadroFixo = disp.Ativos,
                    CapacidadeDia = disp.CapacidadeDia,
                };
                var r = CoberturaTemporal.Resolver(modelo);
                status = r.Status;
                mensagem = r.Mensagem;
                cobDisp = r.Cobertura ?? Enumerable.Range(0, 7).Select(_ => new int[faixas]).ToArray();
            }
            var temCobertura = status is StatusSolucao.Otima or StatusSolucao.Viavel;
            disponivelHora = temCobertura ? PorHora(e, cobDisp, faixa) : null;
            var (naoCobertas, deficitHp, abaixoMin, janelas) = temCobertura ? Deficits(e, n, cobDisp, faixa) : (0, 0, 0, []);
            var expediente = 7 * (e.Fechamento - e.Abertura) / 60.0;
            disponibilidade = new ResumoDisponibilidade("Cadastro de colaboradores (Employee.Api)", mensagem, disp.Ativos, disp.CapacidadeDia,
                R(disp.CapacidadeDia.Average()), status,
                temCobertura ? R(naoCobertas) : null, R(expediente), temCobertura ? R(1 - naoCobertas / expediente) : null,
                temCobertura ? R(deficitHp) : null, temCobertura ? abaixoMin : null, janelas);
        }
        else
        {
            disponibilidade = new ResumoDisponibilidade("Indisponível", "Colaboradores não consultados.", null, null, null, null, null, null, null, null, null, []);
        }

        // ---------- Escala escolhida (presentes por hora) ----------
        ResumoEscala? escala = null;
        if (e.PresentesEscala is { } pres)
        {
            double naoCobertas = 0, deficit = 0;
            for (var d = 0; d < 7; d++)
            for (var h = 0; h < 24; h++)
            {
                if (n.Fracao[h] <= 0) continue;
                var falta = n.Final[d][h] - pres[d][h];
                if (falta > 1e-9)
                {
                    naoCobertas += n.Fracao[h];
                    deficit += falta * n.Fracao[h];
                }
            }
            var expediente = 7 * (e.Fechamento - e.Abertura) / 60.0;
            escala = new ResumoEscala(Guid.Empty, null, string.Empty, 0, R(naoCobertas), R(deficit), R(1 - naoCobertas / expediente));
        }

        // ---------- Contratações para a meta ----------
        var metaTexto = e.MetaCobertura >= 1
            ? "100% das horas do expediente com a necessidade atendida"
            : $"{Pct(e.MetaCobertura)} das horas do expediente com a necessidade atendida (cobertura mínima sempre)";
        if (e.NivelServicoMeta is { } nsMeta && e.NivelServicoTempoSegundos is { } nsTempo)
            metaTexto += $"; necessidade com nível de serviço de {Pct(nsMeta)} em até {nsTempo:0} s";
        var resultadoMeta = e.MetaCobertura >= 1
            ? principal
            : CoberturaTemporal.Resolver(Modelo(e, n, turnos, faixa, e.MetaCobertura, e.TempoLimite, e.Workers) with { Dica = principal.Uso });
        int? quadroMeta = resultadoMeta.Pessoas is { } pm ? QuadroComShrinkage(pm, e.Shrinkage) : null;
        var ativos = e.Disponibilidade?.Ativos;
        var contratacao = new ResumoContratacao(e.MetaCobertura, metaTexto, ativos, resultadoMeta.Status, resultadoMeta.Mensagem,
            resultadoMeta.Pessoas, quadroMeta,
            quadroMeta is { } qm && ativos is { } at ? Math.Max(0, qm - at) : null,
            quadroCarga, ativos is { } at2 ? Math.Max(0, quadroCarga - at2) : null);

        // ---------- Sensibilidade ----------
        var sensibilidade = Sensibilidade(e, n, principal, turnos, faixa);

        // ---------- Tabelas ----------
        var coberturaHora = principal.Cobertura is { } cob ? PorHora(e, cob, faixa) : null;
        var porHora = new List<LinhaHora>();
        for (var h = 0; h < 24; h++)
        {
            var dentro = n.Fracao[h] > 0;
            if (!dentro && Enumerable.Range(0, 7).All(d => e.Demanda[d][h] <= 0)) continue;
            double Media(Func<int, double> f) => R(Enumerable.Range(0, 7).Average(f));
            double? disponivel = disponivelHora is null || !dentro ? null : Media(d => disponivelHora[d][h]);
            double? presentes = e.PresentesEscala is null || !dentro ? null : Media(d => e.PresentesEscala[d][h]);
            var referencia = disponivelHora ?? (e.PresentesEscala is null ? null : e.PresentesEscala.Select(dia => dia.Select(v => (int)Math.Floor(v + 1e-9)).ToArray()).ToArray());
            double? deficit = referencia is null || !dentro ? null : Media(d => Math.Max(0, n.Final[d][h] - referencia[d][h]));
            var diasDeficit = referencia is null || !dentro ? 0 : Enumerable.Range(0, 7).Count(d => referencia[d][h] < n.Final[d][h]);
            var cargaMedia = Enumerable.Range(0, 7).Average(d => n.Carga[d][h]);
            double? utilPrev = null, nsPrev = null;
            if (disponivel is { } dv && dentro && cargaMedia > 0)
            {
                utilPrev = dv > 0 ? R(Math.Min(9.99, cargaMedia / dv)) : null;
                if (e.NivelServicoTempoSegundos is { } tt)
                    nsPrev = R(Enumerable.Range(0, 7).Average(d => FilaErlang.NivelServico(disponivelHora![d][h], n.Carga[d][h], e.TmaSegundos, tt)));
            }
            porHora.Add(new LinhaHora(h, $"{h:00}h", dentro, R(n.Fracao[h]),
                Math.Round(Enumerable.Range(0, 7).Average(d => e.Demanda[d][h]), 3), Media(d => n.Carga[d][h]), Media(d => n.PorUtilizacao[d][h]),
                n.PorNivelServico is null ? null : Media(d => n.PorNivelServico[d][h]),
                Media(d => n.Calculado[d][h]), n.MinimoHora[h], Media(d => n.Final[d][h]),
                Enumerable.Range(0, 7).Max(d => n.Final[d][h]),
                dentro && Enumerable.Range(0, 7).All(d => n.Calculado[d][h] < n.MinimoHora[h]),
                coberturaHora is null || !dentro ? null : Media(d => coberturaHora[d][h]),
                disponivel, presentes, deficit, diasDeficit, utilPrev, nsPrev));
        }

        var porDia = Enumerable.Range(0, 7).Select(d => new LinhaDia(
            CurvaDemanda.Semana[d].ToString(), RotuloDia[d], R(e.Demanda[d].Sum()),
            R(Enumerable.Range(0, 24).Sum(h => n.Final[d][h] * n.Fracao[h])), n.Final[d].Max(),
            principal.Uso is { } uso ? uso[d].Sum() : null,
            e.Disponibilidade?.CapacidadeDia[d],
            n.Final[d], coberturaHora?[d], disponivelHora?[d])).ToList();

        var formulas = new List<string>
        {
            $"Carga por hora (Erlangs): A = λ × TMA ÷ 3600 — ex.: {Num(porHora.FirstOrDefault(p => p.NoExpediente)?.Demanda ?? 0)} doc/h × {e.TmaSegundos:0} s ÷ 3600.",
            $"Necessários pela utilização alvo: N = ⌈A ÷ ρ⌉, com ρ = {Pct(e.Utilizacao)} (cada operador ocupado no máximo {Pct(e.Utilizacao)} do tempo).",
            e.NivelServicoMeta is { } m2 && e.NivelServicoTempoSegundos is { } t2
                ? $"Nível de serviço (Erlang C): menor N com P(espera ≤ {t2:0} s) = 1 − C(N, A)·e^(−(N − A)·{t2:0}/TMA) ≥ {Pct(m2)}; vale o maior entre os dois critérios."
                : "Nível de serviço (Erlang C): não pedido neste cenário.",
            $"Necessário na hora = max(cobertura mínima {e.CoberturaMinima} — abertura {e.MinimoAbertura}, fechamento {e.MinimoFechamento} —, calculado), só no expediente {CoberturaTemporal.Hm(e.Abertura)}–{CoberturaTemporal.Hm(e.Fechamento)}.",
            $"Carga total: horas-pessoa/semana = Σ necessário × fração da hora no expediente = {Num(n.HorasPessoa)} h; quadro = ⌈horas-pessoa ÷ (horas por colaborador × (1 − shrinkage))⌉.",
            "Cobertura temporal: programação inteira (CP-SAT) — menor N tal que, em cada faixa de cada dia, as pessoas trabalhando (fora do intervalo) nos turnos permitidos ≥ necessário; " +
            $"cada pessoa trabalha {e.DiasTrabalho} dias/semana, no máximo {(e.JanelaDomingo is { } j ? $"{j - 1}/{j}" : "todo")} do quadro no domingo e interjornada de {e.Interjornada / 60.0:0.#} h; quadro = ⌈N ÷ (1 − shrinkage)⌉.",
            "Disponíveis: melhor distribuição possível dos colaboradores ativos (descontadas as ausências de cada dia) nos mesmos turnos — o que falta é o déficit por hora.",
        };

        return new ResultadoCalculo(porHora, porDia, carga, temporal, disponibilidade, escala, contratacao, sensibilidade, formulas, avisos);
    }

    private static ResumoTemporal ResumirTemporal(EntradaCalculo e, Necessidade n, IReadOnlyList<TurnoTipo> turnos, int faixa, ResultadoCobertura r)
    {
        var formula = r.Pessoas is { } p
            ? $"⌈{p} pessoas em escala ÷ (1 − {Pct(e.Shrinkage)})⌉ = {QuadroComShrinkage(p, e.Shrinkage)}"
            : "Sem solução de cobertura temporal.";
        if (!r.TemSolucao || r.Uso is null || r.Cobertura is null)
            return new ResumoTemporal(r.Status, r.Mensagem, null, null, r.LimiteInferior, R(r.Segundos), null, null, null, faixa, turnos.Count, [], formula, []);

        double excedente = 0;
        var faixas = (e.Fechamento - e.Abertura) / faixa;
        for (var d = 0; d < 7; d++)
        for (var t = 0; t < faixas; t++)
        {
            var h = (e.Abertura + t * faixa) / 60;
            excedente += Math.Max(0, r.Cobertura[d][t] - n.Final[d][h]) * faixa / 60.0;
        }
        var pagasSemana = Enumerable.Range(0, 7).Sum(d => Enumerable.Range(0, turnos.Count).Sum(s => r.Uso[d][s] * turnos[s].MinutosPagos)) / 60.0;
        var sugeridos = Enumerable.Range(0, turnos.Count)
            .Where(s => Enumerable.Range(0, 7).Any(d => r.Uso[d][s] > 0))
            .GroupBy(s => (turnos[s].Inicio, turnos[s].Fim, turnos[s].Tipo, turnos[s].Codigo))
            .OrderBy(g => g.Key.Inicio).ThenBy(g => g.Key.Fim)
            .Select(g =>
            {
                var porDia = Enumerable.Range(0, 7).Select(d => g.Sum(s => r.Uso[d][s])).ToArray();
                var intervalos = g.OrderBy(s => turnos[s].IntervaloInicio)
                    .Select(s => new IntervaloTurno(CoberturaTemporal.Hm(turnos[s].IntervaloInicio), CoberturaTemporal.Hm(turnos[s].IntervaloFim),
                        Enumerable.Range(0, 7).Select(d => r.Uso[d][s]).ToArray()))
                    .ToList();
                return new TurnoSugerido(CoberturaTemporal.Hm(g.Key.Inicio), CoberturaTemporal.Hm(g.Key.Fim), g.Key.Tipo, g.Key.Codigo,
                    turnos[g.First()].MinutosPagos, porDia, R(porDia.Average()), intervalos);
            })
            .ToList();
        return new ResumoTemporal(r.Status, r.Mensagem, r.Pessoas, QuadroComShrinkage(r.Pessoas!.Value, e.Shrinkage), r.LimiteInferior, R(r.Segundos),
            r.Uso.Select(d => d.Sum()).ToArray(), R(excedente), R(pagasSemana), faixa, turnos.Count, sugeridos, formula,
            Limitantes(e, turnos, r.Uso, r.Pessoas.Value));
    }

    /// <summary>
    /// O que define o quadro da solução: turnos da semana ÷ dias de trabalho, o dia mais cheio, o domingo (só (J−1)/J do
    /// quadro trabalha) e a interjornada (quem fecha num dia não abre no seguinte).
    /// </summary>
    private static List<Limitante> Limitantes(EntradaCalculo e, IReadOnlyList<TurnoTipo> turnos, int[][] uso, int pessoas)
    {
        var w = uso.Select(d => d.Sum()).ToArray();
        var lista = new List<Limitante>
        {
            new("regime", $"{w.Sum()} turnos na semana ÷ {e.DiasTrabalho} dias de trabalho por pessoa", FilaErlang.Teto(w.Sum() / (double)e.DiasTrabalho), false),
            new("dia", $"dia mais cheio ({RotuloDia[Array.IndexOf(w, w.Max())]}: {w.Max()} turnos)", w.Max(), false),
        };
        if (e.JanelaDomingo is { } j && j >= 2)
            lista.Add(new("domingo", $"domingo: {w[6]} turnos, mas só {j - 1} de cada {j} pessoas trabalham no domingo (folga em 1 domingo a cada {j} semanas)",
                FilaErlang.Teto(w[6] * j / (double)(j - 1)), false));
        if (e.Interjornada > 0)
        {
            var pior = 0;
            var texto = string.Empty;
            foreach (var corte in turnos.Select(t => t.Fim).Distinct())
            for (var d = 0; d < 7; d++)
            {
                var s = (d + 1) % 7;
                var tarde = Enumerable.Range(0, turnos.Count).Where(i => turnos[i].Fim >= corte).Sum(i => uso[d][i]);
                var cedo = Enumerable.Range(0, turnos.Count).Where(i => turnos[i].Inicio + 1440 - corte < e.Interjornada).Sum(i => uso[s][i]);
                if (cedo == 0 || tarde + cedo <= pior) continue;
                pior = tarde + cedo;
                texto = $"interjornada: {tarde} saem às {CoberturaTemporal.Hm(corte)} ou depois ({RotuloDia[d]}) e {cedo} entram cedo no dia seguinte ({RotuloDia[s]})";
            }
            if (pior > 0) lista.Add(new("interjornada", texto, pior, false));
        }
        var maior = lista.Max(l => l.Pessoas);
        return lista.Select(l => l with { Determinante = l.Pessoas == maior && maior == pessoas }).ToList();
    }

    /// <summary>Pessoas trabalhando por hora: o menor valor entre as faixas da hora (a hora só está coberta se todas estão).</summary>
    public static int[][] PorHora(EntradaCalculo e, int[][] cobertura, int faixa)
    {
        var r = new int[7][];
        for (var d = 0; d < 7; d++)
        {
            r[d] = new int[24];
            for (var h = 0; h < 24; h++)
            {
                var de = Math.Max(h * 60, e.Abertura);
                var ate = Math.Min(h * 60 + 60, e.Fechamento);
                if (ate <= de) continue;
                var menor = int.MaxValue;
                for (var m = de; m < ate; m += faixa) menor = Math.Min(menor, cobertura[d][(m - e.Abertura) / faixa]);
                r[d][h] = menor == int.MaxValue ? 0 : menor;
            }
        }
        return r;
    }

    private static (double NaoCobertas, double DeficitHp, int AbaixoMinimo, List<string> Janelas) Deficits(EntradaCalculo e, Necessidade n, int[][] cob, int faixa)
    {
        double naoCobertas = 0, deficit = 0;
        var abaixo = 0;
        var janelas = new List<string>();
        var faixas = (e.Fechamento - e.Abertura) / faixa;
        for (var d = 0; d < 7; d++)
        {
            int? inicio = null;
            var maior = 0;
            for (var t = 0; t <= faixas; t++)
            {
                var falta = 0;
                if (t < faixas)
                {
                    var h = (e.Abertura + t * faixa) / 60;
                    falta = Math.Max(0, n.Final[d][h] - cob[d][t]);
                    if (falta > 0)
                    {
                        naoCobertas += faixa / 60.0;
                        deficit += falta * faixa / 60.0;
                    }
                    if (cob[d][t] < n.MinimoHora[h]) abaixo++;
                }
                if (falta > 0)
                {
                    inicio ??= t;
                    maior = Math.Max(maior, falta);
                }
                else if (inicio is { } i)
                {
                    janelas.Add($"{RotuloDia[d]} {CoberturaTemporal.Hm(e.Abertura + i * faixa)}–{CoberturaTemporal.Hm(e.Abertura + t * faixa)}: faltam até {maior}");
                    inicio = null;
                    maior = 0;
                }
            }
        }
        return (naoCobertas, deficit, abaixo, janelas.Take(40).ToList());
    }

    private static ResumoSensibilidade Sensibilidade(EntradaCalculo e, Necessidade atual, ResultadoCobertura principal, IReadOnlyList<TurnoTipo> turnos, int faixa)
    {
        var tmas = (e.SensibilidadeTma is { Count: > 0 } lt ? lt : new[] { 0.75, 0.875, 1.0, 1.125, 1.25 }.Select(f => Math.Round(e.TmaSegundos * f)).ToList())
            .Where(v => v > 0).Distinct().Order().ToList();
        var utils = (e.SensibilidadeUtilizacao is { Count: > 0 } lu ? lu : new[] { 0.70, 0.75, 0.80, 0.85, 0.90 })
            .Where(v => v is > 0 and <= 1).Distinct().Order().ToList();
        var coberturas = (e.SensibilidadeCobertura is { Count: > 0 } lc ? lc
                : Enumerable.Range(e.CoberturaMinima - 1, 4).ToList())
            .Where(v => v >= 0).Distinct().Order().ToList();

        var casos = new List<(string Parametro, double Valor, string Rotulo, bool Atual, Necessidade N)>();
        casos.AddRange(tmas.Select(t => ("TMA", t, $"{t:0} s", Math.Abs(t - e.TmaSegundos) < 1e-9,
            CalcularNecessidade(e, t, e.Utilizacao, e.CoberturaMinima))));
        casos.AddRange(utils.Select(u => ("Utilizacao", u, Pct(u), Math.Abs(u - e.Utilizacao) < 1e-9,
            CalcularNecessidade(e, e.TmaSegundos, u, e.CoberturaMinima))));
        casos.AddRange(coberturas.Select(c => ("CoberturaMinima", (double)c, $"{c} pessoa(s)", c == e.CoberturaMinima,
            CalcularNecessidade(e, e.TmaSegundos, e.Utilizacao, c))));

        var resultados = new ResultadoCobertura?[casos.Count];
        if (e.SensibilidadeTemporal && principal.Status != StatusSolucao.Indisponivel)
        {
            var paralelo = Math.Max(1, e.Paralelo);
            var workers = Math.Max(1, e.Workers / paralelo);
            // Mesma necessidade do cenário principal: reaproveita o resultado
            var assinaturaAtual = Assinatura(atual);
            Parallel.For(0, casos.Count, new ParallelOptions { MaxDegreeOfParallelism = paralelo }, i =>
            {
                resultados[i] = Assinatura(casos[i].N) == assinaturaAtual
                    ? principal
                    : CoberturaTemporal.Resolver(Modelo(e, casos[i].N, turnos, faixa, 1.0, e.TempoLimiteSensibilidade, workers));
            });
        }

        LinhaSensibilidade Linha(int i)
        {
            var c = casos[i];
            var r = resultados[i];
            return new LinhaSensibilidade(c.Parametro, c.Valor, c.Rotulo, c.Atual, R(c.N.HorasPessoa), c.N.Pico, QuadroPorCarga(e, c.N.HorasPessoa).Quadro,
                r?.Pessoas is { } p ? QuadroComShrinkage(p, e.Shrinkage) : null, r?.Status, c.N.HorasComMinimo);
        }

        var linhas = Enumerable.Range(0, casos.Count).Select(Linha).ToList();
        var grade = new GradeSensibilidade(tmas, utils,
            tmas.Select(t => utils.Select(u => QuadroPorCarga(e, CalcularNecessidade(e, t, u, e.CoberturaMinima).HorasPessoa).Quadro).ToArray()).ToArray(),
            tmas.Select(t => utils.Select(u => R(CalcularNecessidade(e, t, u, e.CoberturaMinima).HorasPessoa)).ToArray()).ToArray());
        return new ResumoSensibilidade(
            linhas.Where(l => l.Parametro == "TMA").ToList(),
            linhas.Where(l => l.Parametro == "Utilizacao").ToList(),
            linhas.Where(l => l.Parametro == "CoberturaMinima").ToList(),
            grade);
    }

    private static string Assinatura(Necessidade n) => string.Join('|', n.Final.Select(d => string.Join(',', d)));

    private static void Validar(EntradaCalculo e)
    {
        if (e.Demanda.Length != 7 || e.Demanda.Any(d => d.Length != 24)) throw new ArgumentException("A demanda precisa ter 7 dias × 24 horas.");
        if (e.TmaSegundos <= 0) throw new ArgumentException("O TMA deve ser maior que zero.");
        if (e.Utilizacao is <= 0 or > 1) throw new ArgumentException("A utilização alvo deve estar entre 1% e 100%.");
        if (e.Shrinkage is < 0 or >= 0.9) throw new ArgumentException("O shrinkage deve estar entre 0% e 90%.");
        if (e.Fechamento <= e.Abertura) throw new ArgumentException("O fechamento precisa ser depois da abertura no mesmo dia.");
        if (e.DiasTrabalho is < 1 or > 7) throw new ArgumentException("Dias de trabalho por semana entre 1 e 7.");
        if (e.CoberturaMinima < 0) throw new ArgumentException("A cobertura mínima não pode ser negativa.");
        if (e.MetaCobertura is <= 0 or > 1) throw new ArgumentException("A meta de horas cobertas deve estar entre 1% e 100%.");
        if (e.HorasSemanais <= 0) throw new ArgumentException("As horas por semana devem ser maiores que zero.");
    }

    private static double R(double v) => Math.Round(v, 2);
    private static string Num(double v) => v.ToString("0.##", CultureInfo.GetCultureInfo("pt-BR"));
    private static string Pct(double v) => (v * 100).ToString("0.#", CultureInfo.GetCultureInfo("pt-BR")) + "%";
}

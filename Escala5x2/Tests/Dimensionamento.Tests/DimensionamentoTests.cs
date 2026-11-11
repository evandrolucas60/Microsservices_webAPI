using Dimensionamento.Api.Calculo;
using Dimensionamento.Api.Endpoints;
using Xunit;
using Xunit.Abstractions;

namespace Dimensionamento.Tests;

/// <summary>
/// Cálculo completo com a demanda real da 28-ASABRANCA. O CP-SAT (Google OR-Tools) precisa do Microsoft Visual C++
/// Redistributable (x64) no Windows: rode com a pasta das DLLs no PATH se o servidor não o tiver instalado.
/// </summary>
public class DimensionamentoTests(ITestOutputHelper output)
{
    private static readonly double[][] Asabranca = CurvaDemanda.Matriz(
        CurvaDemanda.LerCsv(DimensionamentoEndpoints.LerExemploCsv(), 27).Curva());

    private static EntradaCalculo Base() => new()
    {
        Demanda = Asabranca,
        TmaSegundos = 120,
        Utilizacao = 0.85,
        Shrinkage = 0.10,
        CoberturaMinima = 2,
        MinimoAbertura = 2,
        MinimoFechamento = 2,
        Abertura = 7 * 60,
        Fechamento = 22 * 60,
        HorasSemanais = 44,
        DiasTrabalho = 6,
        DuracaoTurno = 500,
        Intervalo = 60,
        Incremento = 30,
        JanelaDomingo = 3,
        Interjornada = 660,
        SensibilidadeTemporal = false,
        TempoLimite = 30,
        Workers = 8,
    };

    [Fact]
    public void Necessidade_por_hora_da_asabranca_com_tma_120_e_85_por_cento()
    {
        var n = Dimensionador.CalcularNecessidade(Base(), 120, 0.85, 2);
        // ⌈λ·120/3600/0,85⌉: 7h 85,1 → 3,34 → 4 · 11h 163,0 → 6,39 → 7 · 17h 170,7 → 6,69 → 7 · 21h 77,6 → 3,04 → 4
        var esperado = new Dictionary<int, int>
        {
            [7] = 4, [8] = 5, [9] = 6, [10] = 7, [11] = 7, [12] = 7, [13] = 6, [14] = 6,
            [15] = 7, [16] = 7, [17] = 7, [18] = 7, [19] = 6, [20] = 5, [21] = 4,
        };
        foreach (var (h, v) in esperado) Assert.Equal(v, n.Final[0][h]);
        // Fora do expediente (22h, 23h, 6h) não entra, mesmo com documentos
        Assert.Equal(0, n.Final[0][22]);
        Assert.Equal(0, n.Final[0][6]);
        Assert.Equal(esperado.Values.Sum() * 7, n.HorasPessoa, 6);
        output.WriteLine($"Horas-pessoa/semana: {n.HorasPessoa}; carga pura {n.HorasCargaPura:F1} h");
    }

    [Fact]
    public void Cobertura_minima_2_vale_nas_horas_de_baixa_demanda()
    {
        // TMA 30 s: 7h 85,1 → 0,71 Erlang → 1 pelo cálculo; o mínimo de 2 prevalece
        var com2 = Dimensionador.CalcularNecessidade(Base(), 30, 0.85, 2);
        var com1 = Dimensionador.CalcularNecessidade(Base(), 30, 0.85, 1);
        Assert.Equal(1, com2.Calculado[0][7]);
        Assert.Equal(2, com2.Final[0][7]);
        Assert.Equal(1, com1.Final[0][7]);
        Assert.True(com2.HorasComMinimo > 0);
        Assert.True(com2.HorasPessoa > com1.HorasPessoa);
        // No pico (17h, 1,42 Erlang → 2) os dois coincidem
        Assert.Equal(com1.Final[0][17], com2.Final[0][17]);
        // Mínimo de abertura/fechamento maior que a cobertura: só a primeira e a última hora
        var e = Base() with { MinimoAbertura = 3, MinimoFechamento = 3 };
        var n = Dimensionador.CalcularNecessidade(e, 30, 0.85, 2);
        Assert.Equal(3, n.Final[0][7]);
        Assert.Equal(3, n.Final[0][21]);
        Assert.Equal(2, n.Final[0][8]);
    }

    [Fact]
    public void Pico_manual_substitui_a_necessidade_da_hora_e_o_adicional_soma()
    {
        // Sábado (índice 5) 11h–14h com 9 pessoas; segunda 07h com +1; um pico de data fora do período não entra
        var picos = new List<Dimensionamento.Api.Clients.PicoLoja>
        {
            new(null, DayOfWeek.Saturday, null, new TimeOnly(11, 0), new TimeOnly(14, 0), 9, null, "Pico manual 11:00–14:00 de sábado"),
            new(null, DayOfWeek.Monday, null, new TimeOnly(7, 0), new TimeOnly(8, 0), null, 1, "Pico manual 07:00–08:00 de segunda"),
            new(null, null, new DateOnly(2027, 2, 1), new TimeOnly(9, 0), new TimeOnly(10, 0), 20, null, "fora do período"),
        };
        var (qtd, adic) = Dimensionamento.Api.Services.SimulacaoService.PicosPorHora(picos, null, new DateOnly(2026, 11, 9), new DateOnly(2026, 11, 15));
        var n = Dimensionador.CalcularNecessidade(Base() with { PicoManual = qtd, PicoAdicional = adic }, 120, 0.85, 2);
        var semPico = Dimensionador.CalcularNecessidade(Base(), 120, 0.85, 2);

        Assert.Equal([9, 9, 9], new[] { n.Final[5][11], n.Final[5][12], n.Final[5][13] });
        Assert.Equal(semPico.Final[5][14], n.Final[5][14]);
        Assert.Equal(semPico.Final[0][7] + 1, n.Final[0][7]);
        Assert.Equal(semPico.Final[0][9], n.Final[0][9]); // pico de data fora do período
    }

    [Fact]
    public void Turnos_fixos_e_flexiveis_respeitam_o_expediente_e_a_janela_do_intervalo()
    {
        var regras = new RegrasIntervalo(60, 60, 360);
        var fixos = GeradorTurnos.Gerar(420, 1320, 500, 60, 30, ModoTurnos.Fixos, regras);
        Assert.Equal([420, 820], fixos.Select(t => t.Inicio).Distinct().Order().ToArray()); // 07:00 e 13:40
        Assert.All(fixos, t =>
        {
            Assert.True(t.IntervaloInicio - t.Inicio >= 60);
            Assert.True(t.Fim - t.IntervaloFim >= 60);
            Assert.True(t.IntervaloInicio - t.Inicio <= 360 && t.Fim - t.IntervaloFim <= 360);
        });
        var flex = GeradorTurnos.Gerar(420, 1320, 500, 60, 30, ModoTurnos.Operacao, regras);
        var entradas = flex.Select(t => t.Inicio).Distinct().Order().ToList();
        Assert.Equal(420, entradas[0]);
        Assert.Equal(820, entradas[^1]);
        Assert.Contains(450, entradas); // 07:30
        Assert.All(flex, t => Assert.True(t.Fim <= 1320));
        Assert.Equal("Abertura", flex.First(t => t.Inicio == 420).Tipo);
        Assert.Equal("Fechamento", flex.First(t => t.Inicio == 820).Tipo);
        Assert.Equal("Flexivel", flex.First(t => t.Inicio == 450).Tipo);
    }

    [Fact]
    public void Cobertura_temporal_e_maior_ou_igual_a_carga_e_turnos_fixos_exigem_mais()
    {
        var flex = Dimensionador.Calcular(Base());
        var fixos = Dimensionador.Calcular(Base() with { ModoTurnos = ModoTurnos.Fixos });
        output.WriteLine($"Carga: {flex.Carga.HorasPessoaSemana} h-pessoa → {flex.Carga.PessoasEmEscala} em escala / {flex.Carga.QuadroMinimo} com shrinkage");
        output.WriteLine($"Temporal flexível: {flex.Temporal.Status} {flex.Temporal.PessoasEmEscala} em escala / {flex.Temporal.QuadroMinimo} ({flex.Temporal.Segundos} s)");
        output.WriteLine($"Temporal fixos: {fixos.Temporal.Status} {fixos.Temporal.PessoasEmEscala} em escala / {fixos.Temporal.QuadroMinimo} ({fixos.Temporal.Segundos} s)");
        foreach (var t in flex.Temporal.Turnos) output.WriteLine($"  {t.Entrada}–{t.Saida} {t.Tipo}: {string.Join(' ', t.PorDia)}");
        Assert.True(flex.Temporal.Status is StatusSolucao.Otima or StatusSolucao.Viavel, flex.Temporal.Mensagem);
        Assert.True(fixos.Temporal.Status is StatusSolucao.Otima or StatusSolucao.Viavel, fixos.Temporal.Mensagem);
        Assert.True(flex.Temporal.PessoasEmEscala >= flex.Carga.PessoasEmEscala);
        // Picos (10h–12h59 e 15h–18h59) não cabem em só dois turnos fixos: sobra gente no vale → mais pessoas
        Assert.True(fixos.Temporal.PessoasEmEscala > flex.Carga.PessoasEmEscala);
        Assert.True(fixos.Temporal.PessoasEmEscala >= flex.Temporal.PessoasEmEscala);
        Assert.True(fixos.Temporal.ExcedenteHorasPessoa > 0);
        // Demanda de domingo igual à dos outros dias: o rodízio de domingos (só 2/3 do quadro no domingo) define o quadro
        foreach (var l in flex.Temporal.Limitantes) output.WriteLine($"  [{(l.Determinante ? "x" : " ")}] {l.Pessoas}: {l.Descricao}");
        Assert.Contains(flex.Temporal.Limitantes, l => l.Chave == "domingo" && l.Determinante);
        var semDomingo = Dimensionador.Calcular(Base() with { JanelaDomingo = null });
        output.WriteLine($"Sem rodízio de domingos: {semDomingo.Temporal.PessoasEmEscala}");
        Assert.True(semDomingo.Temporal.PessoasEmEscala < flex.Temporal.PessoasEmEscala);
        // A cobertura temporal atende cada hora
        Assert.All(flex.PorHora.Where(h => h.NoExpediente), h => Assert.True(h.CoberturaTemporal >= h.NecessarioFinal - 1e-9, $"{h.Rotulo}"));
    }

    [Fact]
    public void Temporal_maior_que_carga_quando_o_pico_e_curto()
    {
        // 2 pessoas o dia todo e 6 só das 11h às 13h: por carga ~ (2×15 + 4×2)×7 ÷ 44; temporal precisa de turnos inteiros no pico
        var demanda = Enumerable.Range(0, 7).Select(_ => new double[24]).ToArray();
        for (var d = 0; d < 7; d++)
        {
            for (var h = 7; h < 22; h++) demanda[d][h] = 30;   // 1 Erlang com TMA 120 → 2 com o mínimo
            demanda[d][11] = demanda[d][12] = 170;             // 5,67 Erlang → 6 com 100%
        }
        var e = Base() with { Demanda = demanda, Utilizacao = 1.0, Shrinkage = 0 };
        var r = Dimensionador.Calcular(e);
        output.WriteLine($"Carga {r.Carga.PessoasEmEscala} × temporal {r.Temporal.PessoasEmEscala}");
        Assert.Equal(StatusSolucao.Otima, r.Temporal.Status);
        Assert.True(r.Temporal.PessoasEmEscala > r.Carga.PessoasEmEscala);
    }

    [Fact]
    public void Disponiveis_deficit_e_contratacoes_para_a_meta()
    {
        var e = Base() with
        {
            Disponibilidade = new DisponibilidadeEntrada(8, [8, 8, 8, 8, 8, 7, 8]),
            Shrinkage = 0,
        };
        var relogio = System.Diagnostics.Stopwatch.StartNew();
        var r = Dimensionador.Calcular(e);
        output.WriteLine($"[{relogio.Elapsed.TotalSeconds:F1} s; distribuição {r.Disponibilidade.Status}] Disponíveis 8: não cobertas {r.Disponibilidade.HorasNaoCobertas} h de {r.Disponibilidade.HorasExpedienteSemana}; " +
                         $"meta {r.Contratacao.QuadroMeta} → contratar {r.Contratacao.ContratarCobertura} (carga: {r.Contratacao.ContratarCarga})");
        Assert.True(r.Disponibilidade.HorasNaoCobertas > 0);
        Assert.NotEmpty(r.Disponibilidade.Faixas);
        Assert.Equal(r.Temporal.QuadroMinimo, r.Contratacao.QuadroMeta);
        Assert.Equal(r.Temporal.QuadroMinimo - 8, r.Contratacao.ContratarCobertura);
        Assert.Contains(r.PorHora, h => h.NoExpediente && h.Deficit > 0);

        // Meta de 90% das horas: exige no máximo o mesmo quadro
        var r90 = Dimensionador.Calcular(e with { MetaCobertura = 0.9 });
        output.WriteLine($"Meta 90%: {r90.Contratacao.Status} {r90.Contratacao.PessoasEmEscalaMeta} → contratar {r90.Contratacao.ContratarCobertura}");
        Assert.True(r90.Contratacao.PessoasEmEscalaMeta <= r.Temporal.PessoasEmEscala);

        // Com gente suficiente não há o que contratar
        var sobra = Dimensionador.Calcular(e with { Disponibilidade = new DisponibilidadeEntrada(40, [40, 40, 40, 40, 40, 40, 40]) });
        Assert.Equal(0, sobra.Contratacao.ContratarCobertura);
        Assert.Equal(0, sobra.Disponibilidade.HorasNaoCobertas);
    }

    [Fact]
    public void Sensibilidade_tma_utilizacao_e_cobertura_minima()
    {
        // Tempo folgado: com a máquina carregada (outros testes do motor em paralelo) 10 s não bastavam
        var r = Dimensionador.Calcular(Base() with { SensibilidadeTemporal = true, TempoLimiteSensibilidade = 30, Paralelo = 3 });
        foreach (var l in r.Sensibilidade.Tma.Concat(r.Sensibilidade.Utilizacao).Concat(r.Sensibilidade.CoberturaMinima))
            output.WriteLine($"{l.Parametro} {l.Rotulo}: {l.HorasPessoaSemana} h → carga {l.QuadroCarga} / cobertura {l.QuadroCobertura} ({l.StatusCobertura})");
        // TMA maior → mais horas-pessoa; utilização maior → menos
        Assert.True(r.Sensibilidade.Tma.Zip(r.Sensibilidade.Tma.Skip(1)).All(p => p.First.HorasPessoaSemana <= p.Second.HorasPessoaSemana));
        Assert.True(r.Sensibilidade.Utilizacao.Zip(r.Sensibilidade.Utilizacao.Skip(1)).All(p => p.First.HorasPessoaSemana >= p.Second.HorasPessoaSemana));
        Assert.Contains(r.Sensibilidade.Tma, l => l.Atual);
        // Cobertura temporal ≥ carga em todo cenário resolvido (SemSolucaoNoTempo não tem quadro e não é falha do cálculo)
        var resolvidas = r.Sensibilidade.Tma.Concat(r.Sensibilidade.Utilizacao)
            .Where(l => l.StatusCobertura is StatusSolucao.Otima or StatusSolucao.Viavel).ToList();
        Assert.NotEmpty(resolvidas);
        Assert.All(resolvidas, l => Assert.True(l.QuadroCobertura >= l.QuadroCarga));
        Assert.Equal(5, r.Sensibilidade.Grade.QuadroCarga.Length);
    }

    [Fact]
    public void Interjornada_impede_fechar_e_abrir_no_dia_seguinte()
    {
        // Domingo: 3 no fechamento (até 22:00); segunda: 3 na abertura (07:00) — 9 h de descanso não bastam (11 h):
        // quem fecha no domingo não abre na segunda ⇒ 6 pessoas; sem a interjornada 3 bastariam
        var turnos = new List<TurnoTipo> { new(420, 920, 420, 0, "Abertura"), new(820, 1320, 820, 0, "Fechamento") };
        var nec = Enumerable.Range(0, 7).Select(_ => new int[45]).ToArray();
        for (var t = 0; t < 45; t++)
        {
            if (420 + t * 20 >= 920) nec[6][t] = 3;
            if (420 + t * 20 < 820) nec[0][t] = 3;
        }
        var modelo = new ModeloCobertura
        {
            Abertura = 420, Fechamento = 1320, Faixa = 20, Turnos = turnos, Necessidade = nec, Minimo = nec,
            DiasTrabalho = 6, Interjornada = 660, TempoLimite = 10,
        };
        var com = CoberturaTemporal.Resolver(modelo);
        var sem = CoberturaTemporal.Resolver(modelo with { Interjornada = 0 });
        output.WriteLine($"com interjornada {com.Pessoas} · sem {sem.Pessoas}");
        Assert.Equal(StatusSolucao.Otima, com.Status);
        Assert.Equal(6, com.Pessoas);
        Assert.Equal(3, sem.Pessoas);
    }
}

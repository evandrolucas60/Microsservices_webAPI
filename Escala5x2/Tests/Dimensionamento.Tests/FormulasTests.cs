using Dimensionamento.Api.Calculo;
using Dimensionamento.Api.Endpoints;
using Xunit;

namespace Dimensionamento.Tests;

public class FormulasTests
{
    private static Escala5x2.BuildingBlocks.Contracts.Compliance.ComplianceRuleSnapshot Regra(int id, string chave, decimal valor, string unidade,
        DateOnly? de = null, DateOnly? ate = null) =>
        new(id, "jornada", chave, valor, unidade, chave, de, ate, Escala5x2.BuildingBlocks.Contracts.Compliance.RuleStatus.Ativo,
            Escala5x2.BuildingBlocks.Contracts.Compliance.RuleApplication.Rigida);

    [Fact]
    public void Turnos_automaticos_usam_a_mesma_jornada_da_escala()
    {
        // 44h até 15/12/2026 (6x1, limiar 44h) e 42h depois (5x2), limite diário 8h, compensação do 5x2, intervalo 60
        List<Escala5x2.BuildingBlocks.Contracts.Compliance.ComplianceRuleSnapshot> regras =
        [
            Regra(1, Escala5x2.BuildingBlocks.Contracts.Compliance.RuleKeys.CargaHorariaSemanal, 44, "horas", null, new DateOnly(2026, 12, 15)),
            Regra(2, Escala5x2.BuildingBlocks.Contracts.Compliance.RuleKeys.CargaHorariaSemanal, 42, "horas", new DateOnly(2026, 12, 16)),
            Regra(3, Escala5x2.BuildingBlocks.Contracts.Compliance.RuleKeys.LimiteDiario, 480, "minutos"),
            Regra(4, Escala5x2.BuildingBlocks.Contracts.Compliance.RuleKeys.HoraExtraMaxDiaria, 120, "minutos"),
            Regra(5, Escala5x2.BuildingBlocks.Contracts.Compliance.RuleKeys.CargaMinimaRegime6x1, 44, "horas"),
            Regra(6, Escala5x2.BuildingBlocks.Contracts.Compliance.RuleKeys.FolgasSemana6x1, 1, "dias"),
            Regra(7, Escala5x2.BuildingBlocks.Contracts.Compliance.RuleKeys.FolgasSemana5x2, 2, "dias"),
            Regra(8, Escala5x2.BuildingBlocks.Contracts.Compliance.RuleKeys.CompensacaoJornada5x2, 1, "booleano"),
            Regra(9, Escala5x2.BuildingBlocks.Contracts.Compliance.RuleKeys.IntervaloPadrao, 60, "minutos"),
        ];
        var jan = Dimensionamento.Api.Services.SimulacaoService.JornadaAutomatica(new TimeOnly(7, 0), new TimeOnly(22, 0), 30, regras,
            new DateOnly(2027, 1, 1), new DateOnly(2027, 1, 31), false);
        Assert.Equal((504, 60), (jan!.Value.Journey.Paid, jan.Value.Journey.Break));
        Assert.Contains("42h ÷ 5 dias", jan.Value.Base);
        var nov = Dimensionamento.Api.Services.SimulacaoService.JornadaAutomatica(new TimeOnly(7, 0), new TimeOnly(22, 0), 30, regras,
            new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), false);
        Assert.Equal((440, 60), (nov!.Value.Journey.Paid, nov.Value.Journey.Break));
    }

    [Fact]
    public void Carga_em_erlangs_e_lambda_vezes_tma_sobre_3600()
    {
        Assert.Equal(5.69, FilaErlang.Carga(170.7, 120), 6);
        Assert.Equal(0, FilaErlang.Carga(0, 120));
        // 360 doc/h × 100 s = 10 Erlangs
        Assert.Equal(10, FilaErlang.Carga(360, 100), 9);
    }

    [Fact]
    public void Utilizacao_alvo_arredonda_para_cima_sem_erro_de_ponto_flutuante()
    {
        Assert.Equal(7, FilaErlang.PorUtilizacao(5.69, 0.85));   // 6,69 → 7
        Assert.Equal(3, FilaErlang.PorUtilizacao(3.0, 1.0));     // exato: não vira 4
        Assert.Equal(3, FilaErlang.PorUtilizacao(2.55, 0.85));   // 2,55/0,85 = 3,0000000000000004 em ponto flutuante → 3
        Assert.Equal(4, FilaErlang.PorUtilizacao(2.56, 0.85));
        Assert.Equal(0, FilaErlang.PorUtilizacao(0, 0.85));
    }

    [Fact]
    public void Erlang_B_e_C_com_valores_de_referencia()
    {
        // M/M/2 com A = 1 (ρ = 0,5): B = 0,2 e P(espera) = 1/3
        Assert.Equal(0.2, FilaErlang.ErlangB(2, 1), 9);
        Assert.Equal(1.0 / 3, FilaErlang.ErlangC(2, 1), 9);
        // M/M/3 com A = 2: B = 4/19 e C = 4/9
        Assert.Equal(4.0 / 19, FilaErlang.ErlangB(3, 2), 9);
        Assert.Equal(4.0 / 9, FilaErlang.ErlangC(3, 2), 9);
        // M/M/1: C = ρ
        Assert.Equal(0.5, FilaErlang.ErlangC(1, 0.5), 9);
        // Tabela clássica (A = 10 Erlangs): N = 11 → 0,6821; N = 12 → 0,4494; N = 13 → 0,2853
        Assert.Equal(0.6821, FilaErlang.ErlangC(11, 10), 4);
        Assert.Equal(0.4494, FilaErlang.ErlangC(12, 10), 4);
        Assert.Equal(0.2853, FilaErlang.ErlangC(13, 10), 4);
        // Sem folga (N ≤ A): todos esperam
        Assert.Equal(1, FilaErlang.ErlangC(10, 10));
    }

    [Fact]
    public void Nivel_de_servico_pelo_erlang_c()
    {
        // M/M/1, ρ = 0,5, T = TMA: 1 − 0,5·e^(−0,5)
        Assert.Equal(1 - 0.5 * Math.Exp(-0.5), FilaErlang.NivelServico(1, 0.5, 60, 60), 9);
        // T = 0: só quem não espera
        Assert.Equal(2.0 / 3, FilaErlang.NivelServico(2, 1, 60, 0), 9);
        // Menor N com P(espera ≤ 0) ≥ 50% para A = 2: N = 3 (C = 0,444)
        Assert.Equal(3, FilaErlang.PorNivelServico(2, 60, 0, 0.5));
        // A = 10, TMA 180 s, 80% em até 20 s: 12 → 64%; 13 → 1 − 0,2853·e^(−3·20/180) = 79,6%; 14 → 89% ⇒ N = 14
        var n = FilaErlang.PorNivelServico(10, 180, 20, 0.8);
        Assert.Equal(14, n);
        Assert.True(FilaErlang.NivelServico(13, 10, 180, 20) < 0.8);
        Assert.True(FilaErlang.NivelServico(14, 10, 180, 20) >= 0.8);
        // Espera média M/M/2, A = 1, TMA 60: C·TMA/(N−A) = (1/3)·60/1 = 20 s
        Assert.Equal(20, FilaErlang.EsperaMedia(2, 1, 60), 9);
    }

    [Fact]
    public void Importacao_do_csv_da_28_asabranca_confere_com_o_relatorio()
    {
        var csv = DimensionamentoEndpoints.LerExemploCsv();
        var r = CurvaDemanda.LerCsv(csv, 27);
        Assert.Equal(56414, r.TotalDocumentos);
        Assert.Equal(463, r.Linhas);
        Assert.Empty(r.Avisos);
        var esperado = new Dictionary<int, double>
        {
            [7] = 85.1, [8] = 113.9, [9] = 138.3, [10] = 154.6, [11] = 163.0, [12] = 157.3, [13] = 131.9, [14] = 137.0,
            [15] = 159.7, [16] = 166.9, [17] = 170.7, [18] = 161.1, [19] = 132.0, [20] = 107.1, [21] = 77.6, [22] = 27.8,
            [23] = 4.1, [6] = 1.3,
        };
        foreach (var (hora, media) in esperado) Assert.Equal(media, Math.Round(r.MediaPorHora[hora], 1));
        Assert.Equal(56414, r.Checkouts.Sum(c => c.Documentos));
    }

    [Fact]
    public void Csv_aceita_cabecalho_separadores_e_numeros_no_padrao_brasileiro()
    {
        var r = CurvaDemanda.LerCsv("caixa,hora,dctos\n001,07,1.200\n002,7h,\"300\"\n003,08:00,10,5\n\nxx", 3);
        Assert.Equal(1500, r.TotalPorHora[7]);
        Assert.Equal(10, r.TotalPorHora[8]); // "10,5" com vírgula decimal e separador vírgula → campos a mais: usa a 3ª coluna (10)
        Assert.Equal(500, r.MediaPorHora[7]);
        Assert.Throws<FormatException>(() => CurvaDemanda.LerCsv("checkout;hora;dctos\n", 27));
        Assert.Throws<FormatException>(() => CurvaDemanda.LerCsv("001;7;10", 0));
    }

    [Fact]
    public void Curva_por_dia_da_semana_e_fatores()
    {
        var curva = new List<PontoCurva> { new(10, 100), new(10, 50, DayOfWeek.Sunday) };
        var m = CurvaDemanda.Matriz(curva, [1, 1, 1, 1, 1.2, 1.5, 1]);
        Assert.Equal(100, m[0][10]);
        Assert.Equal(120, m[4][10], 9);
        Assert.Equal(150, m[5][10], 9);
        Assert.Equal(50, m[6][10]);  // ponto específico do domingo prevalece
        Assert.Equal(0, m[0][11]);
        Assert.NotNull(CurvaDemanda.Validar([new(10, 1), new(10, 2)]));
        Assert.Null(CurvaDemanda.Validar(curva));
    }
}

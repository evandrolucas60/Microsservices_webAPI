namespace Dimensionamento.Api.Calculo;

/// <summary>Pessoas disponíveis no período: ativas no setor/loja e, por dia (seg..dom), as não ausentes.</summary>
public sealed record DisponibilidadeEntrada(int Ativos, int[] CapacidadeDia);

/// <summary>Parâmetros já resolvidos (cenário + operação da loja + regras + padrões) para o cálculo puro.</summary>
public sealed record EntradaCalculo
{
    /// <summary>[dia 0..6 = seg..dom][hora 0..23] documentos (chegadas) por hora.</summary>
    public required double[][] Demanda { get; init; }
    public required double TmaSegundos { get; init; }
    public required double Utilizacao { get; init; }
    public double? NivelServicoMeta { get; init; }
    public double? NivelServicoTempoSegundos { get; init; }
    public double Shrinkage { get; init; }
    public int CoberturaMinima { get; init; } = 2;
    public int MinimoAbertura { get; init; } = 2;
    public int MinimoFechamento { get; init; } = 2;
    public int Abertura { get; init; } = 7 * 60;
    public int Fechamento { get; init; } = 22 * 60;
    public double HorasSemanais { get; init; } = 44;
    public int DiasTrabalho { get; init; } = 6;
    public int DuracaoTurno { get; init; } = 500;
    public int Intervalo { get; init; } = 60;
    public int Incremento { get; init; } = 30;
    public int? FlexMaisCedo { get; init; }
    public int? FlexMaisTarde { get; init; }
    public string ModoTurnos { get; init; } = Calculo.ModoTurnos.Operacao;
    public IReadOnlyList<TurnoCadastrado>? TurnosCadastrados { get; init; }
    public RegrasIntervalo RegrasIntervalo { get; init; } = new();
    public int? JanelaDomingo { get; init; } = 3;
    public int Interjornada { get; init; } = 660;
    public double MetaCobertura { get; init; } = 1.0;
    /// <summary>
    /// [dia 0..6 = seg..dom][hora] pessoas desejadas pelos picos manuais da loja (0 = sem pico): na hora, substitui a
    /// necessidade calculada pela curva (nunca abaixo da cobertura mínima). null = sem picos manuais.
    /// </summary>
    public int[][]? PicoManual { get; init; }
    /// <summary>[dia][hora] pessoas a mais pedidas pelos picos manuais (somadas à necessidade da hora). null = sem.</summary>
    public int[][]? PicoAdicional { get; init; }
    public DisponibilidadeEntrada? Disponibilidade { get; init; }
    /// <summary>[dia][hora] presentes (média, fora do intervalo) numa escala escolhida; null = sem escala.</summary>
    public double[][]? PresentesEscala { get; init; }
    public IReadOnlyList<double>? SensibilidadeTma { get; init; }
    public IReadOnlyList<double>? SensibilidadeUtilizacao { get; init; }
    public IReadOnlyList<int>? SensibilidadeCobertura { get; init; }
    public bool SensibilidadeTemporal { get; init; } = true;
    public double TempoLimite { get; init; } = 20;
    public double TempoLimiteSensibilidade { get; init; } = 6;
    public int Workers { get; init; } = 8;
    public int Paralelo { get; init; } = 3;
}

public sealed record LinhaHora(
    int Hora, string Rotulo, bool NoExpediente, double Fracao,
    double Demanda, double Carga, double NecessariosUtilizacao, double? NecessariosNivelServico,
    double NecessarioCalculado, int Minimo, double NecessarioFinal, int NecessarioPico, bool MinimoPrevalece,
    double? CoberturaTemporal, double? Disponivel, double? PresentesEscala, double? Deficit, int DiasComDeficit,
    double? UtilizacaoPrevista, double? NivelServicoPrevisto);

public sealed record LinhaDia(
    string Dia, string Rotulo, double Documentos, double HorasPessoa, int Pico, int? PessoasCobertura, int? Disponiveis,
    int[] Necessidade, int[]? Cobertura, int[]? Disponivel);

public sealed record ResumoCarga(
    double HorasPessoaSemana, double HorasPessoaDemanda, double HorasCargaPura, double HorasMinimoAcrescentadas,
    double HorasSemanaisRegra, double HorasPagasPorTurno, int DiasTrabalho, double HorasPorColaborador, double HorasProdutivasColaborador,
    double Shrinkage, int PessoasEmEscala, int QuadroMinimo, double QuadroFracionario, string Formula);

public sealed record IntervaloTurno(string Inicio, string Fim, int[] PorDia);

public sealed record TurnoSugerido(string Entrada, string Saida, string Tipo, string? Codigo, int MinutosPagos, int[] PorDia, double MediaDia,
    IReadOnlyList<IntervaloTurno> Intervalos);

/// <summary>Quantas pessoas cada condição do regime exige com os turnos da solução (a maior define o quadro).</summary>
public sealed record Limitante(string Chave, string Descricao, int Pessoas, bool Determinante);

public sealed record ResumoTemporal(
    string Status, string Mensagem, int? PessoasEmEscala, int? QuadroMinimo, int? LimiteInferior, double Segundos,
    int[]? PessoasPorDia, double? ExcedenteHorasPessoa, double? HorasPagasSemana, int FaixaMinutos, int TurnosCandidatos,
    IReadOnlyList<TurnoSugerido> Turnos, string Formula, IReadOnlyList<Limitante> Limitantes);

public sealed record ResumoDisponibilidade(
    string Fonte, string? Mensagem, int? Ativos, int[]? DisponiveisPorDia, double? DisponiveisMedia,
    string? Status, double? HorasNaoCobertas, double? HorasExpedienteSemana, double? PercentualHorasCobertas,
    double? DeficitHorasPessoa, int? FaixasAbaixoMinimo, IReadOnlyList<string> Faixas);

public sealed record ResumoEscala(
    Guid EscalaId, string? Titulo, string Periodo, int DiasConsiderados, double HorasNaoCobertas, double DeficitHorasPessoa,
    double PercentualHorasCobertas);

public sealed record ResumoContratacao(
    double Meta, string MetaDescricao, int? Ativos, string Status, string? Mensagem,
    int? PessoasEmEscalaMeta, int? QuadroMeta, int? ContratarCobertura, int QuadroCarga, int? ContratarCarga);

public sealed record LinhaSensibilidade(
    string Parametro, double Valor, string Rotulo, bool Atual, double HorasPessoaSemana, int PicoNecessario, int QuadroCarga,
    int? QuadroCobertura, string? StatusCobertura, int HorasComMinimo);

public sealed record GradeSensibilidade(IReadOnlyList<double> Tmas, IReadOnlyList<double> Utilizacoes, int[][] QuadroCarga, double[][] HorasPessoa);

public sealed record ResumoSensibilidade(
    IReadOnlyList<LinhaSensibilidade> Tma, IReadOnlyList<LinhaSensibilidade> Utilizacao, IReadOnlyList<LinhaSensibilidade> CoberturaMinima,
    GradeSensibilidade Grade);

public sealed record ResultadoCalculo(
    IReadOnlyList<LinhaHora> PorHora, IReadOnlyList<LinhaDia> PorDia, ResumoCarga Carga, ResumoTemporal Temporal,
    ResumoDisponibilidade Disponibilidade, ResumoEscala? Escala, ResumoContratacao Contratacao, ResumoSensibilidade Sensibilidade,
    IReadOnlyList<string> Formulas, IReadOnlyList<string> Avisos);

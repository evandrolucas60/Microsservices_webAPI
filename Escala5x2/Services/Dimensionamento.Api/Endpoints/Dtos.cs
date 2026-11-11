using Dimensionamento.Api.Calculo;
using Dimensionamento.Api.Clients;

namespace Dimensionamento.Api.Endpoints;

/// <summary>
/// Cenário da simulação. Campos vazios (null) são preenchidos pela operação da loja (Store.Api), pelas regras de
/// conformidade vigentes (Compliance.Api) ou pelos padrões (07:00–22:00, cobertura mínima 2, turno 8h20 com 1 h de intervalo).
/// Utilização, shrinkage, metas: frações (0,85 = 85%). Horários "HH:mm".
/// </summary>
public sealed record SimulacaoRequest
{
    public Guid StoreId { get; init; }
    public Guid? SectorId { get; init; }
    /// <summary>Perfil de demanda salvo; ou <see cref="Curva"/> digitada na hora.</summary>
    public Guid? PerfilDemandaId { get; init; }
    public List<PontoCurva>? Curva { get; init; }
    public List<double>? FatoresDiaSemana { get; init; }
    public double TmaSegundos { get; init; } = 120;
    public double Utilizacao { get; init; } = 0.85;
    public double? NivelServicoMeta { get; init; }
    public double? NivelServicoTempoSegundos { get; init; }
    public double Shrinkage { get; init; }
    public int? CoberturaMinima { get; init; }
    public int? MinimoAbertura { get; init; }
    public int? MinimoFechamento { get; init; }
    public string? Abertura { get; init; }
    public string? Fechamento { get; init; }
    public double? HorasSemanais { get; init; }
    public int? DiasTrabalhoSemana { get; init; }
    public int? DuracaoTurnoMinutos { get; init; }
    public int? IntervaloMinutos { get; init; }
    public int? IncrementoFlexivelMinutos { get; init; }
    public string? EntradaFlexivelMaisCedo { get; init; }
    public string? EntradaFlexivelMaisTarde { get; init; }
    /// <summary>Operacao (abertura, fechamento e flexíveis), Fixos (só abertura e fechamento) ou Cadastrados (turnos da loja).</summary>
    public string? ModoTurnos { get; init; }
    /// <summary>Fração das horas do expediente que devem ter a necessidade atendida (1 = 100%).</summary>
    public double? MetaCobertura { get; init; }
    /// <summary>Escala existente para comparar presentes por hora × necessidade.</summary>
    public Guid? EscalaId { get; init; }
    /// <summary>Período das ausências consideradas nos disponíveis (padrão: a próxima semana, segunda a domingo).</summary>
    public DateOnly? PeriodoInicio { get; init; }
    public DateOnly? PeriodoFim { get; init; }
    public List<double>? SensibilidadeTma { get; init; }
    public List<double>? SensibilidadeUtilizacao { get; init; }
    public List<int>? SensibilidadeCobertura { get; init; }
    public bool? SensibilidadeTemporal { get; init; }
    public double? TempoLimiteSegundos { get; init; }
}

/// <summary>Valor usado no cálculo e de onde veio (cenário, operação da loja, regra, padrão).</summary>
public sealed record ParametroUsado(string Chave, string Nome, string Valor, string Origem);

/// <summary>Parâmetros resolvidos (formato do formulário), para preencher a tela.</summary>
public sealed record ParametrosResolvidos(
    string Abertura, string Fechamento, int CoberturaMinima, int MinimoAbertura, int MinimoFechamento,
    double HorasSemanais, int DiasTrabalhoSemana, string Regime, int DuracaoTurnoMinutos, int IntervaloMinutos, int IncrementoFlexivelMinutos,
    string? EntradaFlexivelMaisCedo, string? EntradaFlexivelMaisTarde, string ModoTurnos, int? JanelaDomingoSemanas, int InterjornadaMinutos,
    DateOnly PeriodoInicio, DateOnly PeriodoFim);

public sealed record PerfilResumo(Guid Id, string Nome, string Origem, int? Dias, double TotalDocumentos, double MediaDia);

public sealed record LojaResumo(Guid Id, string Codigo, string Nome, IReadOnlyList<SetorLoja> Setores, bool TemOperacao);

public sealed record SimulacaoResponse(
    ParametrosResolvidos Parametros, IReadOnlyList<ParametroUsado> Origens, PerfilResumo? Perfil, LojaResumo? Loja,
    ResultadoCalculo Resultado, IReadOnlyList<string> Avisos, IReadOnlyDictionary<string, string?> Servicos, DateTime CalculadoEm)
{
    /// <summary>Resumo gravado com o cenário (comparação).</summary>
    public ResumoCenario Resumo() => new(
        Resultado.Carga.QuadroMinimo, Resultado.Temporal.QuadroMinimo, Resultado.Temporal.PessoasEmEscala, Resultado.Temporal.Status,
        Resultado.Carga.HorasPessoaSemana, Resultado.PorHora.Where(h => h.NoExpediente).Select(h => h.NecessarioPico).DefaultIfEmpty(0).Max(),
        Resultado.Disponibilidade.Ativos, Resultado.Disponibilidade.HorasNaoCobertas, Resultado.Contratacao.ContratarCobertura,
        Resultado.Contratacao.ContratarCarga, Parametros.Regime, Parametros.ModoTurnos, CalculadoEm);
}

public sealed record ResumoCenario(
    int QuadroCarga, int? QuadroCobertura, int? PessoasEmEscala, string StatusCobertura, double HorasPessoaSemana, int PicoNecessario,
    int? Ativos, double? HorasNaoCobertas, int? ContratarCobertura, int? ContratarCarga, string Regime, string ModoTurnos, DateTime CalculadoEm);

// ---------- Perfis de demanda ----------

public sealed record PerfilRequest(Guid StoreId, Guid? SectorId, string Nome, string? Observacao, List<PontoCurva> Curva, List<double>? FatoresDiaSemana);

public sealed record PerfilUpdateRequest(string Nome, string? Observacao, Guid? SectorId, List<PontoCurva> Curva, List<double>? FatoresDiaSemana);

/// <summary>CSV checkout;hora;dctos (texto) com o total do período e o nº de dias (média = total ÷ dias).</summary>
public sealed record ImportarCsvRequest(Guid StoreId, Guid? SectorId, string Nome, string Conteudo, int Dias,
    DateOnly? PeriodoInicio, DateOnly? PeriodoFim, string? Observacao);

public sealed record ExemploRequest(Guid StoreId, Guid? SectorId);

public sealed record PerfilListItem(Guid Id, Guid StoreId, Guid? SectorId, string Nome, string Origem, string? Observacao,
    DateOnly? PeriodoInicio, DateOnly? PeriodoFim, int? Dias, double TotalDocumentos, double MediaDia, int HorasComDemanda, bool PorDiaSemana,
    string CriadoPor, DateTime CriadoEm, string? AtualizadoPor, DateTime? AtualizadoEm, double[] MediaPorHora);

public sealed record PerfilDetalhe(PerfilListItem Perfil, IReadOnlyList<PontoCurva> Curva, IReadOnlyList<double>? FatoresDiaSemana,
    IReadOnlyList<TotalCheckout>? Checkouts, double[] MediaPorHora);

public sealed record ImportacaoResponse(PerfilDetalhe Perfil, int Linhas, double TotalDocumentos, IReadOnlyList<string> Avisos);

// ---------- Cenários ----------

public sealed record CenarioRequest(string Nome, string? Descricao, SimulacaoRequest Parametros);

public sealed record CenarioListItem(Guid Id, Guid StoreId, Guid? SectorId, Guid? PerfilDemandaId, string Nome, string? Descricao,
    ResumoCenario? Resumo, SimulacaoRequest Parametros, string CriadoPor, DateTime CriadoEm, string? AtualizadoPor, DateTime? AtualizadoEm);

public sealed record CenarioDetalhe(CenarioListItem Cenario, SimulacaoResponse? Resultado);

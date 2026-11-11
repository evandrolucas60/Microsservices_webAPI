namespace Escala5x2.BuildingBlocks.Contracts.Compliance;

/// <summary>
/// Resultado do avaliador de conformidade de uma escala.
/// <paramref name="Percentual"/> = verificações conformes / verificações totais × 100 (1 casa decimal);
/// cada regra conta as próprias unidades de verificação (ver ComplianceEvaluator).
/// </summary>
public sealed record ComplianceResult(
    double Percentual,
    int TotalVerificacoes,
    int TotalViolacoes,
    IReadOnlyList<RuleComplianceDto> PorRegra,
    IReadOnlyList<ComplianceViolationDto> Violacoes,
    DateTime AvaliadaEm,
    // Informativo (não entra no percentual): horas e dias por colaborador × semana. Avaliações gravadas antes não têm (null).
    IReadOnlyList<WeeklyHoursDto>? HorasSemanais = null,
    // Regime (6x1/5x2) de cada semana civil do horizonte. null = avaliação antiga ou fotografia sem o modelo de regime.
    IReadOnlyList<WeekRegimeDto>? RegimePorSemana = null);

/// <summary>
/// Regime da semana civil: "6x1" quando a carga semanal vigente (<paramref name="CargaHoras"/>; na semana de virada,
/// a mais restritiva) é ≥ carga_minima_regime_6x1_horas, senão "5x2".
/// <paramref name="Folgas"/>: número exato de folgas da semana (folgas do regime, nunca abaixo de folgas_minimas_semana;
/// null = regra do regime bloqueada). <paramref name="DiasConsecutivosMax"/>: máximo de dias seguidos do regime
/// (null = bloqueada). <paramref name="CargaMinima6x1Horas"/>: limiar usado (null = bloqueado: toda semana é 5x2).
/// </summary>
public sealed record WeekRegimeDto(
    DateOnly InicioSemana,
    string Regime,
    decimal? CargaHoras,
    int? Folgas,
    int? DiasConsecutivosMax,
    decimal? CargaMinima6x1Horas,
    // Semana 6x1 com folga_extra_domingo_6x1 ativa: quem folga no domingo pode ter 1 folga a mais
    bool FolgaExtraDomingo = false);

/// <summary>
/// Horas da semana civil de um colaborador comparadas com a carga semanal (informativo, não é violação).
/// <paramref name="MinutosTrabalhados"/>: minutos pagos (duração − intervalo) no setor e em outros setores;
/// <paramref name="CargaSemanalMinutos"/>: carga_horaria_semanal vigente (null = sem controle de jornada ou regra bloqueada);
/// <paramref name="Folgas"/>: dias sem trabalho que não são feriado sem autorização nem ausência (<paramref name="Feriados"/>, <paramref name="Ausencias"/>).
/// </summary>
public sealed record WeeklyHoursDto(
    Guid EmployeeId,
    string EmployeeName,
    DateOnly Semana,
    bool ControleJornada,
    int DiasTrabalhados,
    int Folgas,
    int Feriados,
    int Ausencias,
    int MinutosTrabalhados,
    int? CargaSemanalMinutos,
    // "6x1" | "5x2" (null = fotografia sem o modelo de regime)
    string? Regime = null,
    // Semana parcial na ponta do mês: dias da semana que são do período da escala e os minutos pagos neles (o resto da
    // semana é da escala vizinha). null = avaliação antiga (semana inteira na escala)
    int? DiasNoPeriodo = null,
    int? MinutosNoPeriodo = null);

public sealed record RuleComplianceDto(
    string Chave,
    string Descricao,
    string Categoria,
    int Verificacoes,
    int Violacoes,
    double Percentual,
    bool Bloqueada,
    // Rigida | Preferencial | null (verificação estrutural, fora do cadastro)
    string? Aplicacao,
    // O que é uma verificação desta regra (ex.: "colaborador × semana")
    string Unidade);

/// <summary>Severidade: "alta" (regra rígida), "media" (cobertura rígida) ou "baixa" (regra preferencial).</summary>
public sealed record ComplianceViolationDto(
    string Id,
    string Chave,
    string RegraDescricao,
    string Severidade,
    Guid? EmployeeId,
    string? EmployeeName,
    IReadOnlyList<DateOnly> Dates,
    string Detalhe);

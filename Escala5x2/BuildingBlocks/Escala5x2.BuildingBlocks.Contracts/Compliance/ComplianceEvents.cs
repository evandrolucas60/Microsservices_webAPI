namespace Escala5x2.BuildingBlocks.Contracts.Compliance;

/// <summary>Evento das antigas versões de regra legal (LegalRuleVersion). Mantido só para ler a auditoria antiga.</summary>
public sealed record LegalRuleVersionPublished(
    Guid RuleVersionId, string Code, DateOnly ValidFrom, string LegalBasis, string By, DateTime OccurredAt);

/// <summary>
/// Regra de conformidade criada, alterada, bloqueada ou desbloqueada (histórico fica na auditoria).
/// <paramref name="Acao"/>: criada | alterada | bloqueada | desbloqueada.
/// <paramref name="ForaDoLimiteLegal"/>: o valor ficou abaixo do piso / acima do teto legal (ou a regra com
/// limite legal foi bloqueada); <paramref name="ConfirmadoPeloAdmin"/> registra a confirmação explícita.
/// </summary>
public sealed record ComplianceRuleChanged(
    int RuleId,
    string Chave,
    string Acao,
    decimal? ValorAnterior,
    decimal Valor,
    string? StatusAnterior,
    string Status,
    string Aplicacao,
    DateOnly? VigenciaInicio,
    DateOnly? VigenciaFim,
    bool ForaDoLimiteLegal,
    bool ConfirmadoPeloAdmin,
    string? Motivo,
    string By,
    DateTime OccurredAt);

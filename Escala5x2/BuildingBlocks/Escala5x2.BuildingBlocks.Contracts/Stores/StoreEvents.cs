namespace Escala5x2.BuildingBlocks.Contracts.Stores;

public sealed record StoreCreated(Guid StoreId, string Code, string Name, string By, DateTime OccurredAt);

public sealed record StorePlanningChanged(Guid StoreId, string What, string By, DateTime OccurredAt);

/// <summary>
/// Feriado criado, alterado ou excluído (histórico na auditoria). <paramref name="Acao"/>: criado | alterado | excluido.
/// Os campos descrevem o feriado depois da ação (na exclusão, como estava); <paramref name="Anterior"/> traz o
/// resumo de antes numa alteração ("07/09 Independência, Nacional, recorrente").
/// </summary>
public sealed record HolidayChanged(
    Guid HolidayId,
    string Acao,
    string Abrangencia,
    string? Uf,
    string? Municipio,
    DateOnly Date,
    string Name,
    bool Recorrente,
    bool WorkAuthorized,
    string? AuthorizationRef,
    string? Anterior,
    string By,
    DateTime OccurredAt);

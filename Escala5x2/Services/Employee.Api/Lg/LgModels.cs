using System.Text.Json.Nodes;
using Escala5x2.BuildingBlocks.Shared.Lg;

namespace Employee.Api.Lg;

// LgItem, LgIntegracaoException e LgOptions ficam no núcleo compartilhado (Escala5x2.BuildingBlocks.Shared.Lg).

/// <summary>
/// ContratoDeTrabalhoParcial da LG com os campos usados pela sincronização. <see cref="Json"/> guarda o registro
/// COMPLETO (todos os elementos, inclusive os que a Escala não usa ou que a LG venha a acrescentar), para o "Visualizar".
/// </summary>
public sealed record LgContrato(
    string Matricula,
    string? Nome,
    LgItem? Empresa,
    LgItem? Cargo,
    LgItem? CentroDeCusto,
    LgItem? Situacao,
    LgItem? OrgaoResponsavel,
    DateOnly? DataAdmissao,
    DateOnly? DataInicioSituacaoAtual,
    JsonObject Json)
{
    public int? EmpresaCodigo => int.TryParse(Empresa?.Codigo, out var c) ? c : null;
    public int? SituacaoCodigo => int.TryParse(Situacao?.Codigo, out var c) ? c : null;
    /// <summary>OrgaoResponsavel.Codigo (setor padrão do colaborador); null = órgão vazio/nulo na LG.</summary>
    public int? OrgaoCodigo => int.TryParse(OrgaoResponsavel?.Codigo, out var c) ? c : null;
    /// <summary>OrgaoResponsavel.Status (0 = ativo na LG).</summary>
    public int? OrgaoStatus =>
        Json["OrgaoResponsavel"] is JsonObject o && o["Status"] is JsonValue v && v.TryGetValue<string>(out var s) && int.TryParse(s, out var st) ? st : null;
}

/// <summary>Uma página da resposta (ConsultarListaHistoricosVigenteNaDataPorDemandaResult).</summary>
public sealed record LgPagina(IReadOnlyList<LgContrato> Contratos, int NumeroDaPagina, int QuantidadePorPagina, int TotalDePaginas, int TotalGeral);

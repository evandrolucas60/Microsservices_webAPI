namespace Escala5x2.BuildingBlocks.Contracts.Stores;

/// <summary>
/// OrgaoResponsavel da LG (ContratoDeTrabalhoParcial) que vira setor da loja: <paramref name="Codigo"/> (ex.: 2),
/// <paramref name="Descricao"/> (ex.: "FRENTE DE LOJA") e <paramref name="Status"/> (0 = ativo na LG).
/// </summary>
public sealed record LgOrgaoSetor(int Codigo, string? Descricao, int? Status);

/// <summary>
/// Pedido do Employee.Api ao Store.Api (POST /internal/stores/{storeId}/sectors/garantir-lg): garante um setor da loja
/// para cada órgão responsável da LG, antes de gravar os colaboradores. <paramref name="Por"/>: quem iniciou.
/// </summary>
public sealed record GarantirSetoresLgRequest(IReadOnlyList<LgOrgaoSetor> Orgaos, string? Por);

/// <summary>
/// Setor ligado a um órgão da LG. <paramref name="Acao"/>: criado | adotado (setor manual com o mesmo nome recebeu o
/// código LG) | renomeado (descrição mudou na LG) | existente.
/// </summary>
public sealed record LgSetorGarantido(int Codigo, Guid SectorId, string Nome, string Acao);

/// <summary>
/// Resposta do garantir-lg: <paramref name="Setores"/> = código do órgão → SectorId (todos os órgãos com setor);
/// contagens, o detalhe por órgão e avisos (descrição mudou num setor manual, nomes repetidos na LG...).
/// </summary>
public sealed record GarantirSetoresLgResponse(
    Guid StoreId, IReadOnlyDictionary<int, Guid> Setores, int Criados, int Adotados, int Renomeados,
    IReadOnlyList<LgSetorGarantido> Detalhes, IReadOnlyList<string> Avisos);

/// <summary>
/// Auditoria: setor criado, adotado, renomeado, inativado ou reativado pela integração LG (<paramref name="Acao"/>:
/// criado | adotado | renomeado | inativado | reativado). <paramref name="Anterior"/>: nome antes de uma renomeação.
/// </summary>
public sealed record SectorLgChanged(Guid StoreId, Guid SectorId, string Nome, int LgOrgaoCodigo, string? LgOrgaoDescricao, string Acao,
    string? Anterior, string By, DateTime OccurredAt);

/// <summary>
/// Órgão responsável do catálogo da LG que não virou setor. <paramref name="Motivo"/>: sem habilitação para as empresas
/// de LG:Empresas, sem descrição, sem código ou Status ≠ 0 (inativo na LG).
/// </summary>
public sealed record LgOrgaoIgnorado(int? Codigo, string? Descricao, string Motivo);

/// <summary>Setores de uma loja na sincronização do catálogo de órgãos responsáveis.</summary>
public sealed record LgSetoresLojaResumo(
    Guid StoreId, string Code, string Name, int Criados, int Adotados, int Renomeados, int Inativados, int Reativados,
    int SetoresAtivosLg, IReadOnlyList<string> Alteracoes, IReadOnlyList<string> Avisos);

/// <summary>
/// Resumo da sincronização do catálogo de setores (ConsultarListaPorDemanda / OrgaoResponsavel): resposta do status
/// GET /api/stores/sincronizar-setores-lg/status e parte do resumo das lojas. <paramref name="Status"/>: Concluida | Falha
/// | NaoConfigurada. <paramref name="Orgaos"/>: órgãos únicos lidos; <paramref name="Elegiveis"/>: Status 0 e habilitados.
/// </summary>
public sealed record LgSetoresResumo(
    string Status, DateTime IniciadaEm, DateTime ConcluidaEm, string Por, IReadOnlyList<int> Empresas, int Paginas, int TotalLg,
    int Orgaos, int Elegiveis, int Lojas, int Criados, int Adotados, int Renomeados, int Inativados, int Reativados, int Ignorados,
    IReadOnlyList<LgOrgaoIgnorado> OrgaosIgnorados, IReadOnlyList<LgSetoresLojaResumo> PorLoja, IReadOnlyList<string> Avisos,
    string? Mensagem);

/// <summary>Auditoria: sincronização do catálogo de setores concluída (ou com falha) — só contagens.</summary>
public sealed record StoresSectorsLgSyncCompleted(
    string Status, IReadOnlyList<int> Empresas, int Paginas, int TotalLg, int Orgaos, int Elegiveis, int Lojas, int Criados,
    int Adotados, int Renomeados, int Inativados, int Reativados, int Ignorados, IReadOnlyList<string> OrgaosIgnorados,
    string? Mensagem, string By, DateTime StartedAt, DateTime OccurredAt);

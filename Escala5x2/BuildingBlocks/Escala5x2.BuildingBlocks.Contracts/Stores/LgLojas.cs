namespace Escala5x2.BuildingBlocks.Contracts.Stores;

/// <summary>Centro de custo da LG que não virou loja (fora do padrão, encerrado sem loja, sem número...).</summary>
public sealed record LgCentroNaoLoja(string Codigo, string? Descricao, string Motivo);

/// <summary>
/// Conflito: mais de um centro de custo para o mesmo código de loja (ex.: varejo e atacarejo "LOJA 05"), ou loja já
/// vinculada a outro centro de custo. Nada é criado/alterado para esses centros até o mapeamento manual
/// (LG:Lojas:Mapeamento { "codigoCentroDeCusto": "codigoLoja" }).
/// </summary>
public sealed record LgConflitoLoja(string CodigoLoja, IReadOnlyList<string> CentrosDeCusto, string Mensagem);

/// <summary>Loja criada/atualizada/inativada/reativada pela sincronização (<paramref name="Acao"/>), com o que mudou.</summary>
public sealed record LgLojaAlterada(Guid StoreId, string Code, string Name, string CentroDeCusto, string Acao, string? Detalhe);

/// <summary>
/// Resumo de uma sincronização de lojas com a LG (ConsultarListaCentroDeCustoCompletoPorDemanda): resposta do
/// POST /internal/stores/sincronizar-lg e "última execução" do GET /api/stores/sincronizar-lg/status.
/// <paramref name="Status"/>: Concluida | Falha. <paramref name="Setores"/>: catálogo de setores (órgãos responsáveis)
/// garantido em todas as lojas logo depois das lojas (null = não executado: lojas com falha ou outra sincronização de
/// setores em andamento).
/// </summary>
public sealed record LgLojasResumo(
    string Status, DateTime IniciadaEm, DateTime ConcluidaEm, string Por, IReadOnlyList<int> Empresas, int Paginas, int TotalLg,
    int Lojas, int Criadas, int Atualizadas, int Inalteradas, int Inativadas, int Reativadas, int Ignorados,
    IReadOnlyList<LgLojaAlterada> Alteracoes, IReadOnlyList<LgCentroNaoLoja> CentrosIgnorados, IReadOnlyList<LgConflitoLoja> Conflitos,
    IReadOnlyList<string> Avisos, string? Mensagem, LgSetoresResumo? Setores = null);

/// <summary>Auditoria: sincronização de lojas concluída (ou com falha) — só contagens e centros de custo.</summary>
public sealed record StoresLgSyncCompleted(
    string Status, IReadOnlyList<int> Empresas, int Paginas, int TotalLg, int Lojas, int Criadas, int Atualizadas, int Inalteradas,
    int Inativadas, int Reativadas, int Ignorados, IReadOnlyList<string> CentrosIgnorados, IReadOnlyList<string> Conflitos,
    string? Mensagem, string By, DateTime StartedAt, DateTime OccurredAt);

/// <summary>
/// Auditoria: loja criada, atualizada, inativada ou reativada pela LG (<paramref name="Acao"/>: criada | atualizada |
/// inativada | reativada). <paramref name="Anterior"/>: resumo de antes ("01 - CABO, Varejo, ativa").
/// </summary>
public sealed record StoreLgChanged(Guid StoreId, string Code, string Name, string CentroDeCusto, string Acao, string? Anterior,
    string By, DateTime OccurredAt);

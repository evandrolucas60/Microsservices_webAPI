using Escala5x2.BuildingBlocks.Contracts.Stores;
using Escala5x2.BuildingBlocks.Shared.Demo;
using Escala5x2.BuildingBlocks.Shared.Lg;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Store.Api.Data;
using Store.Api.Domain;

namespace Store.Api.Lg;

/// <summary>
/// Catálogo de órgãos responsáveis já filtrado (uma vez por sincronização, vale para todas as lojas).
/// <see cref="Elegiveis"/>: Status 0, habilitados para uma empresa de LG:Empresas e com descrição (viram setor);
/// <see cref="CodigosAtivos"/>: Status 0 e habilitados (com ou sem descrição — o setor existente desses códigos não é
/// inativado); <see cref="Ignorados"/>: o resto, com o motivo.
/// </summary>
public sealed record LgCatalogoSetores(
    IReadOnlyList<LgOrgaoSetor> Elegiveis, IReadOnlySet<int> CodigosAtivos, IReadOnlyList<LgOrgaoIgnorado> Ignorados);

/// <summary>
/// Setores de todas as lojas a partir do catálogo de órgãos responsáveis da EMPRESA (ConsultarListaPorDemanda): o órgão
/// não é da loja, então cada loja ativa (exceto a DEMO) recebe um setor por órgão ativo e habilitado. Criação, adoção de
/// setor manual pelo nome, renomeação e corrida: <see cref="LgSetoresRegras.Garantir"/> (o mesmo do garantir-lg). Além
/// disso, o setor criado pela LG (<see cref="SectorOrigem.Lg"/>) cujo órgão ficou com Status ≠ 0, perdeu a habilitação
/// ou sumiu do catálogo é INATIVADO (nunca apagado) e é reativado se o órgão voltar. Setor manual (inclusive adotado)
/// nunca é inativado pela LG.
/// </summary>
public static class LgSetoresCatalogoRegras
{
    /// <summary>Filtra o catálogo: Status 0 (nulo = 0) e ListaHabilitacaoPorEmpresas com alguma de <paramref name="empresas"/>.</summary>
    public static LgCatalogoSetores Filtrar(IEnumerable<LgOrgaoResponsavel> orgaos, IReadOnlyCollection<int> empresas)
    {
        var elegiveis = new List<LgOrgaoSetor>();
        var ativos = new HashSet<int>();
        var ignorados = new List<LgOrgaoIgnorado>();
        foreach (var o in orgaos)
        {
            if (o.Codigo is not { } codigo)
            {
                ignorados.Add(new LgOrgaoIgnorado(null, o.Descricao, "sem código numérico"));
                continue;
            }
            if (o.Status is { } st && st != 0)
            {
                ignorados.Add(new LgOrgaoIgnorado(codigo, o.Descricao, $"inativo na LG (Status {st})"));
                continue;
            }
            if (!o.EmpresasHabilitadas.Any(empresas.Contains))
            {
                ignorados.Add(new LgOrgaoIgnorado(codigo, o.Descricao, $"sem habilitação para a(s) empresa(s) {string.Join(", ", empresas)}"));
                continue;
            }
            ativos.Add(codigo);
            if (LgSetoresRegras.Chave(o.Descricao).Length == 0)
            {
                ignorados.Add(new LgOrgaoIgnorado(codigo, o.Descricao, "sem descrição (nenhum setor criado)"));
                continue;
            }
            elegiveis.Add(new LgOrgaoSetor(codigo, o.Descricao, 0));
        }
        return new LgCatalogoSetores(elegiveis, ativos, ignorados);
    }

    /// <summary>Lojas que recebem o catálogo: ativas e não DEMO.</summary>
    public static bool LojaAlvo(Domain.Store s) =>
        s.Active && s.Id != DemoData.StoreId && !string.Equals(s.Code, "DEMO", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Aplica o catálogo nos setores (rastreados) de uma loja: garante os elegíveis e ajusta ativo/inativo dos setores
    /// criados pela LG. Idempotente: a segunda vez com o mesmo catálogo não muda nada.
    /// </summary>
    public static LgSetoresResultado AplicarNaLoja(LgCatalogoSetores catalogo, List<Sector> setores, Guid storeId, DateTime agora)
    {
        var r = LgSetoresRegras.Garantir(catalogo.Elegiveis, setores, storeId, agora);
        var garantidos = r.Mapa.Values.ToHashSet();
        foreach (var s in setores.Where(s => s.Origem == SectorOrigem.Lg && s.LgOrgaoCodigo is not null).ToList())
        {
            var ativo = garantidos.Contains(s.Id) || catalogo.CodigosAtivos.Contains(s.LgOrgaoCodigo!.Value);
            if (ativo && !s.Active)
            {
                s.Active = true;
                r.Alteracoes.Add((s, "reativado", null));
                r.Mudou = true;
            }
            else if (!ativo && s.Active)
            {
                s.Active = false;
                r.Alteracoes.Add((s, "inativado", null));
                r.Mudou = true;
            }
        }
        return r;
    }
}

/// <summary>
/// Sincroniza o catálogo de setores: lê o catálogo inteiro na LG (todas as páginas de todas as empresas) ANTES de gravar
/// (falha na LG → nada muda) e aplica loja a loja (<see cref="LgSetoresCatalogoRegras"/>). Catálogo sem nenhum órgão ativo
/// e habilitado não altera nada (proteção contra inativar tudo por uma resposta vazia).
/// </summary>
public sealed class LgSetoresSincronizador(
    StoreDbContext db, LgOrgaosResponsaveisClient lg, LgSetoresService setores, IOptionsMonitor<LgOptions> options,
    ILogger<LgSetoresSincronizador> logger)
{
    public async Task<LgSetoresResumo> SincronizarAsync(string por, Action<string, int, int>? progresso, CancellationToken ct)
    {
        var o = options.CurrentValue;
        var inicio = DateTime.UtcNow;
        if (!o.ConfiguradaPara(LgOperacao.OrgaosResponsaveis))
            throw new LgIntegracaoException(o.MensagemNaoConfigurada(LgOperacao.OrgaosResponsaveis));

        var consulta = await lg.ConsultarTodosAsync(progresso, ct);
        var catalogo = LgSetoresCatalogoRegras.Filtrar(consulta.Orgaos, consulta.Empresas);
        var avisos = new List<string>();
        var porLoja = new List<LgSetoresLojaResumo>();

        if (catalogo.CodigosAtivos.Count == 0)
        {
            avisos.Add("A LG não devolveu nenhum órgão responsável ativo e habilitado para as empresas de LG:Empresas: nenhum setor foi alterado.");
        }
        else
        {
            var agora = DateTime.UtcNow;
            var lojas = (await db.Stores.AsNoTracking().OrderBy(s => s.Code).ToListAsync(ct)).Where(LgSetoresCatalogoRegras.LojaAlvo).ToList();
            for (var i = 0; i < lojas.Count; i++)
            {
                var loja = lojas[i];
                progresso?.Invoke($"Gravando os setores (loja {loja.Code})", i + 1, lojas.Count);
                var r = await setores.AplicarAsync(loja.Id, lista => LgSetoresCatalogoRegras.AplicarNaLoja(catalogo, lista, loja.Id, agora), por,
                    "Setores do catálogo de órgãos da LG", agora, ct);
                var ativosLg = await db.Sectors.CountAsync(s => s.StoreId == loja.Id && s.Active && s.LgOrgaoCodigo != null, ct);
                porLoja.Add(new LgSetoresLojaResumo(loja.Id, loja.Code, loja.Name, r.Criados, r.Adotados, r.Renomeados, r.Inativados,
                    r.Reativados, ativosLg, r.Alteracoes.Select(a => $"{a.Setor.Name} ({a.Acao})").ToList(), r.Avisos));
                // Uma loja por vez no rastreador (são ~60 setores por loja)
                db.ChangeTracker.Clear();
            }
            if (lojas.Count == 0) avisos.Add("Nenhuma loja ativa para receber os setores: rode antes a sincronização de lojas.");
        }

        var resumo = new LgSetoresResumo("Concluida", inicio, DateTime.UtcNow, por, consulta.Empresas, consulta.Paginas, consulta.TotalLg,
            consulta.Orgaos.Count, catalogo.Elegiveis.Count, porLoja.Count,
            porLoja.Sum(l => l.Criados), porLoja.Sum(l => l.Adotados), porLoja.Sum(l => l.Renomeados),
            porLoja.Sum(l => l.Inativados), porLoja.Sum(l => l.Reativados), catalogo.Ignorados.Count,
            catalogo.Ignorados, porLoja, avisos, null);
        logger.LogInformation(
            "LG: setores sincronizados por {Por}: {Orgaos} órgão(s), {Elegiveis} elegível(is), {Lojas} loja(s), {Criados} criado(s), {Adotados} adotado(s), {Renomeados} renomeado(s), {Inativados} inativado(s), {Reativados} reativado(s), {Ignorados} ignorado(s)",
            por, resumo.Orgaos, resumo.Elegiveis, resumo.Lojas, resumo.Criados, resumo.Adotados, resumo.Renomeados, resumo.Inativados,
            resumo.Reativados, resumo.Ignorados);
        await setores.PublicarAsync(Evento(resumo), ct);
        return resumo;
    }

    /// <summary>Evento de auditoria do resumo (contagens e órgãos ignorados).</summary>
    public static StoresSectorsLgSyncCompleted Evento(LgSetoresResumo r) =>
        new(r.Status, r.Empresas, r.Paginas, r.TotalLg, r.Orgaos, r.Elegiveis, r.Lojas, r.Criados, r.Adotados, r.Renomeados,
            r.Inativados, r.Reativados, r.Ignorados, r.OrgaosIgnorados.Select(x => $"{x.Codigo} {x.Descricao} ({x.Motivo})").ToList(),
            r.Mensagem, r.Por, r.IniciadaEm, r.ConcluidaEm);
}

/// <summary>Status consultável da sincronização de setores (GET /api/stores/sincronizar-setores-lg/status).</summary>
public sealed record LgSetoresStatusResponse(
    bool Configurada, string? MensagemConfiguracao, string? SincronizacaoDiaria, bool EmAndamento, string? Etapa, int PaginaAtual,
    int TotalPaginas, DateTime? IniciadaEm, string? IniciadaPor, LgSetoresResumo? UltimaExecucao);

/// <summary>
/// Executa uma sincronização de setores por vez (mesmo padrão do <see cref="LgLojasRunner"/>): o POST da tela devolve na
/// hora e a tela consulta o status; a sincronização de lojas (botão, diária e chamada interna do Employee.Api) chama
/// <see cref="ExecutarAsync"/> logo depois das lojas e aguarda. Status e última execução em memória; o histórico
/// permanente fica na auditoria (StoresSectorsLgSyncCompleted, SectorLgChanged).
/// </summary>
public sealed class LgSetoresRunner(
    IServiceScopeFactory scopes, IOptionsMonitor<LgOptions> options, IHostApplicationLifetime lifetime, ILogger<LgSetoresRunner> logger)
{
    private readonly Lock _lock = new();
    private bool _emAndamento;
    private string? _etapa;
    private int _pagina, _totalPaginas;
    private DateTime? _iniciadaEm;
    private string? _por;
    private LgSetoresResumo? _ultima;

    public bool Configurada => options.CurrentValue.ConfiguradaPara(LgOperacao.OrgaosResponsaveis);

    public LgSetoresStatusResponse Status()
    {
        var o = options.CurrentValue;
        var configurada = o.ConfiguradaPara(LgOperacao.OrgaosResponsaveis);
        lock (_lock)
        {
            return new LgSetoresStatusResponse(configurada, configurada ? null : o.MensagemNaoConfigurada(LgOperacao.OrgaosResponsaveis),
                o.HoraDiaria(o.Lojas.SincronizacaoDiaria)?.ToString("HH:mm"), _emAndamento, _etapa, _pagina, _totalPaginas, _iniciadaEm, _por,
                _ultima);
        }
    }

    /// <summary>Inicia em segundo plano; devolve a mensagem de recusa (não configurada / já em andamento) ou null.</summary>
    public string? TryIniciar(string por)
    {
        var o = options.CurrentValue;
        if (!o.ConfiguradaPara(LgOperacao.OrgaosResponsaveis)) return o.MensagemNaoConfigurada(LgOperacao.OrgaosResponsaveis);
        if (!Reservar(por)) return "Já existe uma sincronização de setores com a LG em andamento. Acompanhe pelo status.";
        _ = Task.Run(() => ExecutarReservadaAsync(por, null, lifetime.ApplicationStopping));
        return null;
    }

    /// <summary>Executa e aguarda (depois das lojas). null = outra já estava em andamento.</summary>
    public async Task<LgSetoresResumo?> ExecutarAsync(string por, Action<string, int, int>? progresso, CancellationToken ct)
    {
        if (!Reservar(por)) return null;
        return await ExecutarReservadaAsync(por, progresso, ct);
    }

    private bool Reservar(string por)
    {
        lock (_lock)
        {
            if (_emAndamento) return false;
            _emAndamento = true;
            _etapa = "Iniciando";
            _pagina = _totalPaginas = 0;
            _iniciadaEm = DateTime.UtcNow;
            _por = por;
            return true;
        }
    }

    private async Task<LgSetoresResumo> ExecutarReservadaAsync(string por, Action<string, int, int>? progresso, CancellationToken ct)
    {
        var inicio = DateTime.UtcNow;
        LgSetoresResumo resumo;
        await using var scope = scopes.CreateAsyncScope();
        var sincronizador = scope.ServiceProvider.GetRequiredService<LgSetoresSincronizador>();
        try
        {
            resumo = await sincronizador.SincronizarAsync(por, (e, p, t) =>
            {
                Progresso(e, p, t);
                progresso?.Invoke(e, p, t);
            }, ct);
        }
        catch (Exception ex)
        {
            var mensagem = ex switch
            {
                LgIntegracaoException lg => $"{lg.Message} Nenhum setor foi alterado.",
                OperationCanceledException => "Sincronização de setores interrompida (serviço parando ou chamada cancelada). Setores já gravados em outras lojas ficam (a operação é idempotente).",
                _ => $"Erro inesperado na sincronização de setores: {ex.GetType().Name}: {ex.Message}. Setores já gravados em outras lojas ficam."
            };
            if (ex is LgIntegracaoException or OperationCanceledException)
                logger.LogWarning("LG: sincronização de setores iniciada por {Por} falhou: {Mensagem}", por, mensagem);
            else
                logger.LogError(ex, "LG: sincronização de setores iniciada por {Por} falhou", por);
            resumo = new LgSetoresResumo("Falha", inicio, DateTime.UtcNow, por, options.CurrentValue.EmpresasEfetivas.ToList(), _totalPaginas, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, [], [], [], mensagem);
            if (ex is not OperationCanceledException)
                await scope.ServiceProvider.GetRequiredService<LgSetoresService>()
                    .PublicarAsync(LgSetoresSincronizador.Evento(resumo), CancellationToken.None);
        }
        lock (_lock)
        {
            _ultima = resumo;
            _emAndamento = false;
            _etapa = null;
        }
        return resumo;
    }

    private void Progresso(string etapa, int pagina, int total)
    {
        lock (_lock)
        {
            _etapa = etapa;
            _pagina = pagina;
            _totalPaginas = total;
        }
    }
}

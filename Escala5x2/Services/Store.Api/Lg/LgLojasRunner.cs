using Escala5x2.BuildingBlocks.Contracts.Stores;
using Escala5x2.BuildingBlocks.Shared.Lg;
using Microsoft.Extensions.Options;

namespace Store.Api.Lg;

/// <summary>Status consultável da sincronização de lojas (GET /api/stores/sincronizar-lg/status).</summary>
public sealed record LgLojasStatusResponse(
    bool Configurada, string? MensagemConfiguracao, string? SincronizacaoDiaria, string Padrao, int Mapeamentos,
    bool EmAndamento, string? Etapa, int PaginaAtual, int TotalPaginas, DateTime? IniciadaEm, string? IniciadaPor,
    LgLojasResumo? UltimaExecucao);

/// <summary>
/// Executa uma sincronização de lojas por vez (mesmo padrão do LgSincronizacaoRunner do Employee.Api): o POST da tela
/// devolve na hora e a tela consulta o status; a chamada interna (Employee.Api) e a diária aguardam o fim. Status e
/// última execução em memória; o histórico permanente fica na auditoria (StoresLgSyncCompleted, StoreLgChanged).
/// </summary>
public sealed class LgLojasRunner(
    IServiceScopeFactory scopes, IOptionsMonitor<LgOptions> options, IHostApplicationLifetime lifetime, LgSetoresRunner setores,
    ILogger<LgLojasRunner> logger)
{
    private readonly Lock _lock = new();
    private bool _emAndamento;
    private string? _etapa;
    private int _pagina, _totalPaginas;
    private DateTime? _iniciadaEm;
    private string? _por;
    private LgLojasResumo? _ultima;

    public LgLojasStatusResponse Status()
    {
        var o = options.CurrentValue;
        var configurada = o.ConfiguradaPara(LgOperacao.CentrosDeCusto);
        lock (_lock)
        {
            return new LgLojasStatusResponse(configurada, configurada ? null : o.MensagemNaoConfigurada(LgOperacao.CentrosDeCusto),
                o.HoraDiaria(o.Lojas.SincronizacaoDiaria)?.ToString("HH:mm"), o.Lojas.PadraoEfetivo, o.Lojas.Mapeamento.Count,
                _emAndamento, _etapa, _pagina, _totalPaginas, _iniciadaEm, _por, _ultima);
        }
    }

    /// <summary>Inicia em segundo plano; devolve a mensagem de recusa (não configurada / já em andamento) ou null.</summary>
    public string? TryIniciar(string por)
    {
        var o = options.CurrentValue;
        if (!o.ConfiguradaPara(LgOperacao.CentrosDeCusto)) return o.MensagemNaoConfigurada(LgOperacao.CentrosDeCusto);
        if (!Reservar(por)) return "Já existe uma sincronização de lojas com a LG em andamento. Acompanhe pelo status.";
        _ = Task.Run(() => ExecutarReservadaAsync(por, lifetime.ApplicationStopping));
        return null;
    }

    /// <summary>Executa e aguarda (diária e chamada interna). null = outra já estava em andamento.</summary>
    public async Task<LgLojasResumo?> ExecutarAsync(string por, CancellationToken ct)
    {
        if (!Reservar(por)) return null;
        return await ExecutarReservadaAsync(por, ct);
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

    private async Task<LgLojasResumo> ExecutarReservadaAsync(string por, CancellationToken ct)
    {
        var inicio = DateTime.UtcNow;
        LgLojasResumo resumo;
        await using var scope = scopes.CreateAsyncScope();
        var sincronizador = scope.ServiceProvider.GetRequiredService<LgLojasSincronizador>();
        try
        {
            resumo = await sincronizador.SincronizarAsync(por, Progresso, ct);
        }
        catch (Exception ex)
        {
            var mensagem = ex switch
            {
                LgIntegracaoException lg => $"{lg.Message} Nenhuma loja foi alterada.",
                OperationCanceledException => "Sincronização de lojas interrompida (serviço parando ou chamada cancelada). Nenhuma loja foi alterada.",
                _ => $"Erro inesperado na sincronização de lojas: {ex.GetType().Name}: {ex.Message}"
            };
            if (ex is LgIntegracaoException or OperationCanceledException)
                logger.LogWarning("LG: sincronização de lojas iniciada por {Por} falhou: {Mensagem}", por, mensagem);
            else
                logger.LogError(ex, "LG: sincronização de lojas iniciada por {Por} falhou", por);
            resumo = new LgLojasResumo("Falha", inicio, DateTime.UtcNow, por, options.CurrentValue.EmpresasEfetivas.ToList(), _totalPaginas, 0,
                0, 0, 0, 0, 0, 0, 0, [], [], [], [], mensagem);
            if (ex is not OperationCanceledException)
                await sincronizador.PublicarAsync(LgLojasSincronizador.Evento(resumo), CancellationToken.None);
        }
        if (resumo.Status == "Concluida") resumo = await SincronizarSetoresAsync(resumo, por, ct);
        lock (_lock)
        {
            _ultima = resumo;
            _emAndamento = false;
            _etapa = null;
        }
        return resumo;
    }

    /// <summary>
    /// Ordem lojas → setores: com as lojas gravadas, garante o catálogo de setores (órgãos responsáveis) em todas as lojas
    /// ativas. Sem a operação configurada ou com outra sincronização de setores em andamento, só aviso (as lojas ficam).
    /// </summary>
    private async Task<LgLojasResumo> SincronizarSetoresAsync(LgLojasResumo resumo, string por, CancellationToken ct)
    {
        if (!setores.Configurada)
            return resumo with
            {
                Avisos = [.. resumo.Avisos, $"Setores não sincronizados: {options.CurrentValue.MensagemNaoConfigurada(LgOperacao.OrgaosResponsaveis)}"]
            };
        var s = await setores.ExecutarAsync(por, (e, p, t) => Progresso($"Setores: {e}", p, t), ct);
        if (s is null)
            return resumo with
            {
                Avisos = [.. resumo.Avisos, "Setores não sincronizados agora: já existe uma sincronização de setores em andamento."]
            };
        return s.Status == "Concluida"
            ? resumo with { Setores = s }
            : resumo with { Setores = s, Avisos = [.. resumo.Avisos, $"Lojas sincronizadas, mas os setores falharam: {s.Mensagem}"] };
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

/// <summary>
/// Sincronização diária automática das lojas (e, logo depois, do catálogo de setores) (LG:Lojas:SincronizacaoDiaria = "HH:mm" no fuso LG:FusoHorario). Desligada
/// por padrão. Agende ANTES da dos colaboradores (LG:SincronizacaoDiaria no Employee.Api). Lê a configuração a cada ciclo.
/// </summary>
public sealed class LgLojasSincronizacaoDiaria(LgLojasRunner runner, IOptionsMonitor<LgOptions> options, ILogger<LgLojasSincronizacaoDiaria> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var o = options.CurrentValue;
            if (o.HoraDiaria(o.Lojas.SincronizacaoDiaria) is not { } hora || !o.ConfiguradaPara(LgOperacao.CentrosDeCusto))
            {
                // Desligada (ou sem configuração): confere de novo em 15 min
                await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
                continue;
            }
            var fuso = o.Fuso();
            var agoraLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, fuso);
            var proxima = agoraLocal.Date + hora.ToTimeSpan();
            if (proxima <= agoraLocal) proxima = proxima.AddDays(1);
            var espera = proxima - agoraLocal;
            // Acorda no máximo a cada 15 min para perceber mudança de configuração
            if (espera > TimeSpan.FromMinutes(15))
            {
                await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
                continue;
            }
            await Task.Delay(espera, stoppingToken);
            logger.LogInformation("LG: sincronização diária de lojas das {Hora}", hora.ToString("HH:mm"));
            if (await runner.ExecutarAsync("Sincronização diária de lojas (LG)", stoppingToken) is null)
                logger.LogInformation("LG: sincronização diária de lojas pulada (outra em andamento)");
        }
    }
}

using Escala5x2.BuildingBlocks.Shared.Lg;
using Microsoft.Extensions.Options;

namespace Employee.Api.Lg;

/// <summary>Status consultável da sincronização (GET /api/employees/sincronizar-lg/status).</summary>
public sealed record LgStatusResponse(
    bool Configurada, string? MensagemConfiguracao, string? SincronizacaoDiaria,
    bool EmAndamento, string? Etapa, int PaginaAtual, int TotalPaginas, DateTime? IniciadaEm, string? IniciadaPor,
    LgResumo? UltimaExecucao);

/// <summary>
/// Executa uma sincronização por vez, em segundo plano (POST devolve na hora; a tela consulta o status). O status e a
/// última execução ficam em memória (o histórico permanente fica na auditoria: EmployeesLgSyncCompleted).
/// </summary>
public sealed class LgSincronizacaoRunner(
    IServiceScopeFactory scopes, IOptionsMonitor<LgOptions> options, IHostApplicationLifetime lifetime, ILogger<LgSincronizacaoRunner> logger)
{
    private readonly Lock _lock = new();
    private bool _emAndamento;
    private string? _etapa;
    private int _pagina, _totalPaginas;
    private DateTime? _iniciadaEm;
    private string? _por;
    private LgResumo? _ultima;

    public LgStatusResponse Status()
    {
        var o = options.CurrentValue;
        lock (_lock)
        {
            return new LgStatusResponse(o.Configurada, o.Configurada ? null : o.MensagemNaoConfigurada(),
                o.HoraDiaria()?.ToString("HH:mm"), _emAndamento, _etapa, _pagina, _totalPaginas, _iniciadaEm, _por, _ultima);
        }
    }

    /// <summary>Inicia em segundo plano; devolve a mensagem de recusa (não configurada / já em andamento) ou null.</summary>
    public string? TryIniciar(string por)
    {
        var o = options.CurrentValue;
        if (!o.Configurada) return o.MensagemNaoConfigurada();
        if (!Reservar(por)) return "Já existe uma sincronização com a LG em andamento. Acompanhe pelo status.";
        _ = Task.Run(() => ExecutarReservadaAsync(por, lifetime.ApplicationStopping));
        return null;
    }

    /// <summary>Executa e aguarda (sincronização diária). false = outra já estava em andamento.</summary>
    public async Task<bool> ExecutarAsync(string por, CancellationToken ct)
    {
        if (!Reservar(por)) return false;
        await ExecutarReservadaAsync(por, ct);
        return true;
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

    private async Task ExecutarReservadaAsync(string por, CancellationToken ct)
    {
        var inicio = DateTime.UtcNow;
        LgResumo resumo;
        await using var scope = scopes.CreateAsyncScope();
        var sincronizador = scope.ServiceProvider.GetRequiredService<LgSincronizador>();
        try
        {
            resumo = await sincronizador.SincronizarAsync(por, Progresso, ct);
        }
        catch (Exception ex)
        {
            var mensagem = ex switch
            {
                LgIntegracaoException lg => lg.Message,
                OperationCanceledException => "Sincronização interrompida (serviço parando).",
                _ => $"Erro inesperado na sincronização: {ex.GetType().Name}: {ex.Message}"
            };
            if (ex is LgIntegracaoException or OperationCanceledException)
                logger.LogWarning("LG: sincronização iniciada por {Por} falhou: {Mensagem}", por, mensagem);
            else
                logger.LogError(ex, "LG: sincronização iniciada por {Por} falhou", por);
            var o = options.CurrentValue;
            resumo = new LgResumo("Falha", o.Hoje(inicio), inicio, DateTime.UtcNow, por, _totalPaginas, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1,
                [], [], [mensagem], mensagem);
            if (ex is not OperationCanceledException)
                await sincronizador.PublicarAsync(LgSincronizador.Evento(resumo), CancellationToken.None);
        }
        lock (_lock)
        {
            _ultima = resumo;
            _emAndamento = false;
            _etapa = null;
        }
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
/// Sincronização diária automática (LG:SincronizacaoDiaria = "HH:mm" no fuso LG:FusoHorario). Desligada por padrão
/// (vazio). Lê a configuração a cada ciclo: ligar/desligar não exige reiniciar o serviço.
/// </summary>
public sealed class LgSincronizacaoDiaria(LgSincronizacaoRunner runner, IOptionsMonitor<LgOptions> options, ILogger<LgSincronizacaoDiaria> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var o = options.CurrentValue;
            if (o.HoraDiaria() is not { } hora || !o.Configurada)
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
            logger.LogInformation("LG: sincronização diária das {Hora}", hora.ToString("HH:mm"));
            if (!await runner.ExecutarAsync("Sincronização diária (LG)", stoppingToken))
                logger.LogInformation("LG: sincronização diária pulada (outra em andamento)");
        }
    }
}

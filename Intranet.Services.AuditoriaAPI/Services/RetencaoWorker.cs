using Intranet.Services.AuditoriaAPI.Data;
using Microsoft.EntityFrameworkCore;

namespace Intranet.Services.AuditoriaAPI.Services;

/// <summary>
/// Retenção: uma vez por dia apaga erros e acessos mais antigos que Auditoria:RetencaoDias
/// (padrão 90; zero ou negativo desativa a limpeza).
/// </summary>
public class RetencaoWorker : BackgroundService
{
    private static readonly TimeSpan Intervalo = TimeSpan.FromDays(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RetencaoWorker> _logger;

    public RetencaoWorker(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<RetencaoWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Espera a aplicação subir (migrations) antes da primeira limpeza
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                await LimparAsync(stoppingToken);
                await Task.Delay(Intervalo, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Encerrando a aplicação
        }
    }

    private async Task LimparAsync(CancellationToken ct)
    {
        var dias = _configuration.GetValue("Auditoria:RetencaoDias", 90);
        if (dias <= 0) return;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var limite = DateTime.UtcNow.AddDays(-dias);

            var erros = await db.Erros.Where(e => e.Em < limite).ExecuteDeleteAsync(ct);
            var acessos = await db.Acessos.Where(a => a.Em < limite).ExecuteDeleteAsync(ct);

            if (erros + acessos > 0)
                _logger.LogInformation("Retenção da auditoria ({Dias} dias): {Erros} erro(s) e {Acessos} acesso(s) apagados", dias, erros, acessos);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Falha na limpeza de retenção da auditoria; nova tentativa no próximo ciclo");
        }
    }
}

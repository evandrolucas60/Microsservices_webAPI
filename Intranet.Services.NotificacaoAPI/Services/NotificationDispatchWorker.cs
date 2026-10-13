using Intranet.Services.NotificacaoAPI.Data;
using Intranet.Services.NotificacaoAPI.Models;
using Microsoft.EntityFrameworkCore;

namespace Intranet.Services.NotificacaoAPI.Services;

/// <summary>
/// Processa as notificações pendentes gravadas no banco (fila persistente: nada se perde
/// se o serviço reiniciar). Até <see cref="MaxAttempts"/> tentativas por notificação.
/// </summary>
public class NotificationDispatchWorker : BackgroundService
{
    public const int MaxAttempts = 3;
    private const int BatchSize = 50;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NotificationDispatchWorker> _logger;

    public NotificationDispatchWorker(IServiceScopeFactory scopeFactory, ILogger<NotificationDispatchWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var (processed, sent) = await ProcessBatchAsync(stoppingToken);
                // Lote cheio com envios bem-sucedidos: ainda há fila, segue sem esperar.
                // Se tudo falhou (ex.: SMTP fora do ar), espera o intervalo antes de tentar de novo.
                if (processed == BatchSize && sent > 0) continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao processar a fila de notificações");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<(int Processed, int Sent)> ProcessBatchAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var senders = scope.ServiceProvider.GetServices<INotificationSender>().ToList();

        var pending = await db.notifications
            .Where(n => n.Status == NotificationStatus.Pending)
            .OrderBy(n => n.NotificationId)
            .Take(BatchSize)
            .ToListAsync(ct);

        var sent = 0;
        foreach (var notification in pending)
        {
            var sender = senders.FirstOrDefault(s => s.CanSend(notification));
            notification.Attempts++;
            try
            {
                if (sender is null) throw new InvalidOperationException($"Sem canal de envio para {notification.GetType().Name}.");

                await sender.SendAsync(notification, ct);
                notification.Status = NotificationStatus.Sent;
                notification.SentAt = DateTime.UtcNow;
                notification.LastError = null;
                sent++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                notification.Attempts--;
                throw;
            }
            catch (Exception ex)
            {
                notification.LastError = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
                if (notification.Attempts >= MaxAttempts)
                {
                    notification.Status = NotificationStatus.Failed;
                }
                _logger.LogWarning(ex, "Falha ao enviar a notificação {Id} (tentativa {Attempt})", notification.NotificationId, notification.Attempts);
            }

            await db.SaveChangesAsync(ct);
        }

        return (pending.Count, sent);
    }
}

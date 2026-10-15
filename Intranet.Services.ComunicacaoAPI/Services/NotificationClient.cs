using BuildingBlocks.Shared.Messaging;

namespace Intranet.Services.ComunicacaoAPI.Services;

/// <summary>
/// Notificações (message) publicadas no RabbitMQ para o NotificacaoAPI. Assíncrono: falhas não
/// interrompem a operação principal; ficam apenas registradas em log.
/// </summary>
public class NotificationClient
{
    private readonly INotificacaoPublisher _publisher;

    public NotificationClient(INotificacaoPublisher publisher)
    {
        _publisher = publisher;
    }

    public Task NotifyAsync(
        IEnumerable<Guid> recipientMemberIds,
        string content,
        string link,
        string? bearerToken,
        bool sendEmail = false,
        CancellationToken ct = default) =>
        _publisher.NotificarAsync(recipientMemberIds, content, "message", link, sendEmail, ct);
}
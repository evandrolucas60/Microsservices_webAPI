using BuildingBlocks.Shared.Messaging;

namespace Intranet.Services.ConteudoAPI.Services;

public interface INotificationClient
{
    /// <summary>
    /// Notifica membros (in-app) publicando no RabbitMQ; o NotificacaoAPI grava a notificação.
    /// Falhas não interrompem a operação principal. Categorias: "post", "comment", "article".
    /// </summary>
    Task NotifyAsync(IEnumerable<Guid> recipientMemberIds, string content, string category, string link, string? bearerToken, CancellationToken ct = default);
}

/// <summary>"Post/Comment creates Notification" (diagrama de classes), via mensageria assíncrona.</summary>
public class NotificationClient : INotificationClient
{
    private readonly INotificacaoPublisher _publisher;

    public NotificationClient(INotificacaoPublisher publisher)
    {
        _publisher = publisher;
    }

    public Task NotifyAsync(IEnumerable<Guid> recipientMemberIds, string content, string category, string link, string? bearerToken, CancellationToken ct = default) =>
        _publisher.NotificarAsync(recipientMemberIds, content, category, link, enviarEmail: false, ct);
}

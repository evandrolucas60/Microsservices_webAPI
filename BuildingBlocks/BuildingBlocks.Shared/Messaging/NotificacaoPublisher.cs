using BuildingBlocks.Contracts.Notificacoes;
using MassTransit;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BuildingBlocks.Shared.Messaging;

/// <summary>
/// Publica pedidos de notificação no RabbitMQ (o NotificacaoAPI consome e grava/envia).
/// Assíncrono: a operação principal não espera o NotificacaoAPI; falhas ao publicar só geram log.
/// </summary>
public interface INotificacaoPublisher
{
    Task NotificarAsync(IEnumerable<Guid> destinatarios, string conteudo, string categoria, string? link, bool enviarEmail = false, CancellationToken ct = default);
}

public class NotificacaoPublisher : INotificacaoPublisher
{
    private readonly IPublishEndpoint _publish;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<NotificacaoPublisher> _logger;

    public NotificacaoPublisher(IPublishEndpoint publish, IHostEnvironment environment, ILogger<NotificacaoPublisher> logger)
    {
        _publish = publish;
        _environment = environment;
        _logger = logger;
    }

    public async Task NotificarAsync(IEnumerable<Guid> destinatarios, string conteudo, string categoria, string? link, bool enviarEmail = false, CancellationToken ct = default)
    {
        var ids = destinatarios.Where(id => id != Guid.Empty).Distinct().ToArray();
        if (ids.Length == 0) return;

        try
        {
            await _publish.Publish(new NotificacaoSolicitada
            {
                DestinatariosMemberIds = ids,
                Conteudo = conteudo,
                Categoria = categoria,
                Link = link,
                EnviarEmail = enviarEmail,
                ServicoOrigem = _environment.ApplicationName,
                SolicitadaEm = DateTime.UtcNow,
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A notificação é um aviso: não desfaz a curtida/comentário/mensagem que a originou
            _logger.LogWarning(ex, "Não foi possível publicar a notificação ({Categoria}, {Link})", categoria, link);
        }
    }
}

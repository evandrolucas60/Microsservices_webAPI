using BuildingBlocks.Contracts.Notificacoes;
using Intranet.Services.ConteudoAPI.Models.DTOs;
using MassTransit;

namespace Intranet.Services.ConteudoAPI.Services;

public interface INewsletterClient
{
    /// <summary>Publica o pedido de newsletter no RabbitMQ. Retorna false se não foi possível publicar.</summary>
    Task<bool> QueueArticleAsync(ArticleNewsletterRequest request, string? bearerToken, CancellationToken ct = default);
}

/// <summary>
/// Newsletter assíncrona: publica NewsletterArtigoSolicitada (o NotificacaoAPI consome e envia).
/// Falhas não interrompem a publicação do artigo: ficam em log e o artigo fica com NewsletterQueuedAt = null.
/// </summary>
public class NewsletterClient : INewsletterClient
{
    private readonly IPublishEndpoint _publish;
    private readonly ILogger<NewsletterClient> _logger;

    public NewsletterClient(IPublishEndpoint publish, ILogger<NewsletterClient> logger)
    {
        _publish = publish;
        _logger = logger;
    }

    public async Task<bool> QueueArticleAsync(ArticleNewsletterRequest request, string? bearerToken, CancellationToken ct = default)
    {
        try
        {
            await _publish.Publish(new NewsletterArtigoSolicitada
            {
                ArtigoId = request.ArticleId,
                Tipo = request.Type,
                Titulo = request.Title,
                Resumo = request.Summary,
                CapaUrl = request.CoverImageUrl,
                AutorMemberId = request.AuthorMemberId,
            }, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Não foi possível publicar a newsletter do artigo {ArticleId}", request.ArticleId);
            return false;
        }
    }
}

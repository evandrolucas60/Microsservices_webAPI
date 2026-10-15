using BuildingBlocks.Contracts.Notificacoes;
using BuildingBlocks.Shared.Messaging;
using Intranet.Services.NotificacaoAPI.Data;
using Intranet.Services.NotificacaoAPI.Models;
using Intranet.Services.NotificacaoAPI.Services;
using MassTransit;

namespace Intranet.Services.NotificacaoAPI.Consumers;

/// <summary>
/// Pedido de notificação vindo de outro serviço (RabbitMQ): grava as notificações in-app e,
/// se pedido, as de e-mail (enviadas depois pelo NotificationDispatchWorker).
/// Substitui o antigo POST api/notifications protegido por chave de serviço.
/// </summary>
public class NotificacaoSolicitadaConsumer : IConsumer<NotificacaoSolicitada>
{
    private const int MaxRecipients = 500;

    private readonly AppDbContext _db;
    private readonly IIdentidadeGateway _identidade;
    private readonly ILogger<NotificacaoSolicitadaConsumer> _logger;

    public NotificacaoSolicitadaConsumer(AppDbContext db, IIdentidadeGateway identidade, ILogger<NotificacaoSolicitadaConsumer> logger)
    {
        _db = db;
        _identidade = identidade;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<NotificacaoSolicitada> context)
    {
        var message = context.Message;
        var ct = context.CancellationToken;

        // Mensagens inválidas são descartadas com log (repetir não as tornaria válidas)
        var recipients = message.DestinatariosMemberIds.Where(id => id != Guid.Empty).Distinct().Take(MaxRecipients).ToList();
        var content = message.Conteudo?.Trim() ?? string.Empty;
        var link = message.Link?.Trim();
        if (recipients.Count == 0 || content.Length == 0 || !NotificationCategories.IsValid(message.Categoria))
        {
            _logger.LogWarning("Notificação descartada (dados inválidos) vinda de {Origem}: categoria {Categoria}", message.ServicoOrigem, message.Categoria);
            return;
        }
        // Link apenas relativo ao portal (evita redirecionar para sites externos)
        if (!string.IsNullOrEmpty(link) && (!link.StartsWith('/') || link.StartsWith("//") || link.Length > 300))
        {
            _logger.LogWarning("Link de notificação inválido descartado ({Origem}): {Link}", message.ServicoOrigem, link);
            link = null;
        }
        if (content.Length > 500) content = content[..500];

        var now = DateTime.UtcNow;
        foreach (var memberId in recipients)
        {
            _db.pushNotifications.Add(new PushNotification
            {
                RecipientMemberId = memberId,
                Content = content,
                Category = message.Categoria,
                Link = link,
                CreatedOn = now,
                Status = NotificationStatus.Sent,
                SentAt = now,
            });
        }

        var emails = 0;
        if (message.EnviarEmail)
        {
            try
            {
                foreach (var recipient in await _identidade.ObterDestinatariosEmailAsync(recipients, ct))
                {
                    _db.emailNotifications.Add(new EmailNotification
                    {
                        RecipientMemberId = recipient.MemberId,
                        Email = recipient.Email.Trim(),
                        Content = content,
                        Category = message.Categoria,
                        Link = link,
                        CreatedOn = now,
                        Status = NotificationStatus.Pending,
                    });
                    emails++;
                }
            }
            catch (Exception ex)
            {
                // As notificações in-app são gravadas mesmo assim
                _logger.LogWarning(ex, "Não foi possível obter os e-mails dos destinatários; apenas notificações in-app serão criadas.");
            }
        }

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Notificação de {Origem} gravada: {InApp} in-app, {Emails} e-mail(s) ({Categoria})",
            message.ServicoOrigem, recipients.Count, emails, message.Categoria);
    }
}

/// <summary>
/// Artigo publicado: busca os destinatários no IdentidadeAPI (RabbitMQ) e coloca a newsletter na fila de envio.
/// Substitui o antigo POST api/newsletter/articles.
/// </summary>
public class NewsletterArtigoSolicitadaConsumer : IConsumer<NewsletterArtigoSolicitada>
{
    private readonly IIdentidadeGateway _identidade;
    private readonly NewsletterQueue _queue;
    private readonly ILogger<NewsletterArtigoSolicitadaConsumer> _logger;

    public NewsletterArtigoSolicitadaConsumer(IIdentidadeGateway identidade, NewsletterQueue queue, ILogger<NewsletterArtigoSolicitadaConsumer> logger)
    {
        _identidade = identidade;
        _queue = queue;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<NewsletterArtigoSolicitada> context)
    {
        var message = context.Message;
        if (message.ArtigoId <= 0 || string.IsNullOrWhiteSpace(message.Titulo))
        {
            _logger.LogWarning("Newsletter descartada: artigo inválido ({ArtigoId})", message.ArtigoId);
            return;
        }

        // Falha aqui (IdentidadeAPI fora do ar) gera nova tentativa pelo MassTransit
        var recipients = (await _identidade.ObterDestinatariosEmailAsync(null, context.CancellationToken))
            .Where(r => !string.IsNullOrWhiteSpace(r.Email) && r.Email.Contains('@'))
            .GroupBy(r => r.Email.Trim().ToLowerInvariant())
            .Select(g => new Recipient { MemberId = g.First().MemberId, Nome = g.First().Nome, Email = g.First().Email.Trim() })
            .ToList();

        var article = new ArticleNewsletterRequest
        {
            ArticleId = message.ArtigoId,
            Type = message.Tipo,
            Title = message.Titulo,
            Summary = message.Resumo,
            CoverImageUrl = message.CapaUrl,
            AuthorMemberId = message.AutorMemberId,
        };
        var authorName = recipients.FirstOrDefault(r => r.MemberId == message.AutorMemberId)?.Nome;

        await _queue.EnqueueAsync(new NewsletterJob(article, authorName, recipients), context.CancellationToken);
        _logger.LogInformation("Newsletter do artigo {ArtigoId} enfileirada para {Count} colaboradores.", message.ArtigoId, recipients.Count);
    }
}

using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;
using Intranet.Services.NotificacaoAPI.Models;
using Microsoft.Extensions.Options;

namespace Intranet.Services.NotificacaoAPI.Services;

/// <summary>
/// Consome a fila e envia um e-mail por colaborador (sem expor a lista em Cc/Bcc).
/// Com Newsletter:Enabled=false ou Smtp:Host vazio, apenas registra o que seria enviado.
/// </summary>
public class NewsletterWorker : BackgroundService
{
    private readonly NewsletterQueue _queue;
    private readonly IOptionsMonitor<SmtpOptions> _smtp;
    private readonly IOptionsMonitor<NewsletterOptions> _newsletter;
    private readonly ILogger<NewsletterWorker> _logger;

    public NewsletterWorker(
        NewsletterQueue queue,
        IOptionsMonitor<SmtpOptions> smtp,
        IOptionsMonitor<NewsletterOptions> newsletter,
        ILogger<NewsletterWorker> logger)
    {
        _queue = queue;
        _smtp = smtp;
        _newsletter = newsletter;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await SendAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao processar a newsletter do artigo {ArticleId}", job.Article.ArticleId);
            }
        }
    }

    private async Task SendAsync(NewsletterJob job, CancellationToken ct)
    {
        var smtp = _smtp.CurrentValue;
        var options = _newsletter.CurrentValue;
        var article = job.Article;

        if (!options.Enabled || string.IsNullOrWhiteSpace(smtp.Host))
        {
            _logger.LogWarning(
                "Newsletter desativada (Newsletter:Enabled={Enabled}, Smtp:Host='{Host}'): o artigo {ArticleId} seria enviado a {Count} colaboradores.",
                options.Enabled, smtp.Host, article.ArticleId, job.Recipients.Count);
            return;
        }

        using var client = new SmtpClient(smtp.Host, smtp.Port)
        {
            EnableSsl = smtp.EnableSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Credentials = string.IsNullOrWhiteSpace(smtp.User) ? null : new NetworkCredential(smtp.User, smtp.Password),
        };

        var from = new MailAddress(smtp.FromAddress, smtp.FromName, Encoding.UTF8);
        var subject = NewsletterTemplate.Subject(article);
        var plain = NewsletterTemplate.PlainText(article, job.AuthorName, options.PortalUrl);

        int sent = 0, failed = 0;
        foreach (var recipient in job.Recipients)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var message = new MailMessage
                {
                    From = from,
                    Subject = subject,
                    SubjectEncoding = Encoding.UTF8,
                    BodyEncoding = Encoding.UTF8,
                    Body = plain,
                    IsBodyHtml = false,
                };
                message.To.Add(new MailAddress(recipient.Email, recipient.Nome ?? string.Empty, Encoding.UTF8));
                message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(
                    NewsletterTemplate.Html(article, job.AuthorName, recipient.Nome, options.PortalUrl),
                    Encoding.UTF8,
                    MediaTypeNames.Text.Html));

                await client.SendMailAsync(message, ct);
                sent++;
            }
            catch (Exception ex) when (ex is SmtpException or FormatException or InvalidOperationException or ArgumentException)
            {
                failed++;
                _logger.LogWarning(ex, "Newsletter do artigo {ArticleId}: falha ao enviar para {Email}", article.ArticleId, recipient.Email);
            }

            if (options.DelayBetweenEmailsMs > 0)
            {
                await Task.Delay(options.DelayBetweenEmailsMs, ct);
            }
        }

        _logger.LogInformation("Newsletter do artigo {ArticleId}: {Sent} enviados, {Failed} falhas.", article.ArticleId, sent, failed);
    }
}

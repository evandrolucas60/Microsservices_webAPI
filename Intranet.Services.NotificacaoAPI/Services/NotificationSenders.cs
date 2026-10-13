using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;
using Intranet.Services.NotificacaoAPI.Models;
using Microsoft.Extensions.Options;

namespace Intranet.Services.NotificacaoAPI.Services;

/// <summary>
/// send() do diagrama: cada especialização de Notification tem o seu canal de envio.
/// Retorna true quando entregue; lança exceção em falha (o worker registra e tenta de novo).
/// </summary>
public interface INotificationSender
{
    bool CanSend(Notification notification);
    Task SendAsync(Notification notification, CancellationToken ct);
}

/// <summary>
/// PushNotification: notificação in-app. Ela é "entregue" ao ser gravada (o frontend lê pela API);
/// o envio para dispositivo móvel (PhoneNumber) não está implementado.
/// </summary>
public class PushNotificationSender : INotificationSender
{
    public bool CanSend(Notification notification) => notification is PushNotification;

    public Task SendAsync(Notification notification, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>EmailNotification: envio por SMTP (configuração "Smtp").</summary>
public class EmailNotificationSender : INotificationSender
{
    private readonly IOptionsMonitor<SmtpOptions> _smtp;
    private readonly IOptionsMonitor<NewsletterOptions> _options;
    private readonly ILogger<EmailNotificationSender> _logger;

    public EmailNotificationSender(
        IOptionsMonitor<SmtpOptions> smtp,
        IOptionsMonitor<NewsletterOptions> options,
        ILogger<EmailNotificationSender> logger)
    {
        _smtp = smtp;
        _options = options;
        _logger = logger;
    }

    public bool CanSend(Notification notification) => notification is EmailNotification;

    public async Task SendAsync(Notification notification, CancellationToken ct)
    {
        var email = (EmailNotification)notification;
        var smtp = _smtp.CurrentValue;
        var options = _options.CurrentValue;

        // Sem SMTP configurado (desenvolvimento): registra e considera entregue
        if (!options.Enabled || string.IsNullOrWhiteSpace(smtp.Host))
        {
            _logger.LogInformation("E-mail desativado: notificação {Id} para {Email} não enviada ({Content}).",
                email.NotificationId, email.Email, email.Content);
            return;
        }

        var link = string.IsNullOrWhiteSpace(email.Link) ? options.PortalUrl : $"{options.PortalUrl.TrimEnd('/')}{email.Link}";
        string E(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);

        var html = $"""
            <!doctype html>
            <html lang="pt-BR">
            <body style="margin:0;padding:24px;background:#f3f4f6;font-family:Arial,Helvetica,sans-serif;color:#111827">
              <table role="presentation" width="600" cellpadding="0" cellspacing="0" style="max-width:600px;width:100%;margin:0 auto;background:#ffffff;border-radius:8px">
                <tr><td style="padding:24px 20px">
                  <p style="margin:0 0 16px;font-size:15px;line-height:1.5">{E(email.Content)}</p>
                  <a href="{E(link)}" style="display:inline-block;background:#0d6efd;color:#ffffff;text-decoration:none;padding:10px 18px;border-radius:6px;font-weight:bold">Abrir na Arco-Net</a>
                </td></tr>
              </table>
            </body>
            </html>
            """;

        using var client = new SmtpClient(smtp.Host, smtp.Port)
        {
            EnableSsl = smtp.EnableSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Credentials = string.IsNullOrWhiteSpace(smtp.User) ? null : new NetworkCredential(smtp.User, smtp.Password),
        };

        using var message = new MailMessage
        {
            From = new MailAddress(smtp.FromAddress, smtp.FromName, Encoding.UTF8),
            // Subject não aceita quebras de linha
            Subject = $"[Arco-Net] {email.Content.ReplaceLineEndings(" ")}".Truncate(150),
            SubjectEncoding = Encoding.UTF8,
            BodyEncoding = Encoding.UTF8,
            Body = $"{email.Content}\n\n{link}\n",
            IsBodyHtml = false,
        };
        message.To.Add(new MailAddress(email.Email));
        message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(html, Encoding.UTF8, MediaTypeNames.Text.Html));

        await client.SendMailAsync(message, ct);
    }
}

internal static class StringExtensions
{
    public static string Truncate(this string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
}

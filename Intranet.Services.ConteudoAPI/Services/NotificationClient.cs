using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Intranet.Services.ConteudoAPI.Services;

public interface INotificationClient
{
    /// <summary>
    /// Notifica membros (in-app) via NotificacaoAPI. Falhas não interrompem a operação principal.
    /// Categorias: "post", "comment", "article".
    /// </summary>
    Task NotifyAsync(IEnumerable<Guid> recipientMemberIds, string content, string category, string link, string? bearerToken, CancellationToken ct = default);
}

/// <summary>"Post/Comment creates Notification" (diagrama de classes).</summary>
public class NotificationClient : INotificationClient
{
    public const string HttpClientName = NewsletterClient.HttpClientName; // mesmo serviço (NotificacaoAPI)

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<NotificationClient> _logger;

    public NotificationClient(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<NotificationClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task NotifyAsync(IEnumerable<Guid> recipientMemberIds, string content, string category, string link, string? bearerToken, CancellationToken ct = default)
    {
        var recipients = recipientMemberIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (recipients.Count == 0) return;

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/notifications")
            {
                Content = JsonContent.Create(new
                {
                    RecipientMemberIds = recipients,
                    Content = content,
                    Category = category,
                    Link = link,
                    SendEmail = false,
                }),
            };
            if (!string.IsNullOrWhiteSpace(bearerToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            }
            var key = _configuration["Notificacao:ServiceKey"];
            if (string.IsNullOrWhiteSpace(key)) key = _configuration["Newsletter:ServiceKey"];
            request.Headers.Add(NewsletterClient.ServiceKeyHeader, key ?? string.Empty);

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("NotificacaoAPI recusou a notificação ({Link}): HTTP {Status}", link, (int)response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Não foi possível contatar o NotificacaoAPI ({Link})", link);
        }
    }
}

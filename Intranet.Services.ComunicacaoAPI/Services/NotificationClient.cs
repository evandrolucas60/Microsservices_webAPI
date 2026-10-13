using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Intranet.Services.ComunicacaoAPI.Services;

/// <summary>
/// "Message creates Notification" (diagrama): pede ao NotificacaoAPI a notificação dos membros.
/// Falhas não interrompem a operação principal; ficam apenas registradas em log.
/// </summary>
public class NotificationClient
{
    public const string HttpClientName = "Notificacao";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<NotificationClient> _logger;

    public NotificationClient(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<NotificationClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task NotifyAsync(
        IEnumerable<Guid> recipientMemberIds,
        string content,
        string link,
        string? bearerToken,
        bool sendEmail = false,
        CancellationToken ct = default)
    {
        var recipients = recipientMemberIds.Distinct().ToList();
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
                    Category = "message",
                    Link = link,
                    SendEmail = sendEmail,
                }),
            };
            if (!string.IsNullOrWhiteSpace(bearerToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            }
            request.Headers.Add("X-Api-Key", _configuration["Notificacao:ServiceKey"] ?? string.Empty);

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

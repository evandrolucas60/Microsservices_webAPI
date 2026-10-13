using System.Net.Http.Headers;
using System.Net.Http.Json;
using Intranet.Services.ConteudoAPI.Models.DTOs;

namespace Intranet.Services.ConteudoAPI.Services;

public interface INewsletterClient
{
    /// <summary>Pede ao NotificacaoAPI o envio da newsletter. Retorna false se não foi aceito.</summary>
    Task<bool> QueueArticleAsync(ArticleNewsletterRequest request, string? bearerToken, CancellationToken ct = default);
}

/// <summary>
/// Cliente HTTP do NotificacaoAPI. Falhas não interrompem a publicação do artigo:
/// são registradas em log e o artigo fica com NewsletterQueuedAt = null.
/// </summary>
public class NewsletterClient : INewsletterClient
{
    public const string HttpClientName = "Notificacao";

    public const string ServiceKeyHeader = "X-Api-Key";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<NewsletterClient> _logger;

    public NewsletterClient(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<NewsletterClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<bool> QueueArticleAsync(ArticleNewsletterRequest request, string? bearerToken, CancellationToken ct = default)
    {
        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);

            using var message = new HttpRequestMessage(HttpMethod.Post, "api/newsletter/articles")
            {
                Content = JsonContent.Create(request),
            };
            if (!string.IsNullOrWhiteSpace(bearerToken))
            {
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            }
            // Credencial de serviço: só o ConteudoAPI pode disparar newsletters
            message.Headers.Add(ServiceKeyHeader, _configuration["Newsletter:ServiceKey"] ?? string.Empty);

            using var response = await client.SendAsync(message, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("NotificacaoAPI recusou a newsletter do artigo {ArticleId}: HTTP {Status}",
                    request.ArticleId, (int)response.StatusCode);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Não foi possível contatar o NotificacaoAPI para o artigo {ArticleId}", request.ArticleId);
            return false;
        }
    }
}

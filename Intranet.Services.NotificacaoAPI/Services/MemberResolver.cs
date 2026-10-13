using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace Intranet.Services.NotificacaoAPI.Services;

/// <summary>
/// Resolve o MemberId do usuário do token (IdentidadeAPI api/Colaboradores/me),
/// usado para listar apenas as notificações do próprio colaborador. Resultado em cache.
/// </summary>
public class MemberResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<MemberResolver> _logger;

    public MemberResolver(IHttpClientFactory httpClientFactory, IMemoryCache cache, ILogger<MemberResolver> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
    }

    private sealed class MeResult
    {
        public Guid MemberId { get; set; }
    }

    private sealed class Envelope
    {
        public MeResult? Result { get; set; }
    }

    public async Task<Guid?> GetMemberIdAsync(string userId, string? bearerToken, CancellationToken ct = default)
    {
        var cacheKey = $"member:{userId}";
        if (_cache.TryGetValue(cacheKey, out Guid cached)) return cached;

        try
        {
            var client = _httpClientFactory.CreateClient(RecipientsClient.HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/Colaboradores/me");
            if (!string.IsNullOrWhiteSpace(bearerToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            }

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var envelope = await JsonSerializer.DeserializeAsync<Envelope>(stream, JsonOptions, ct);
            var memberId = envelope?.Result?.MemberId;
            if (memberId is null || memberId == Guid.Empty) return null;

            _cache.Set(cacheKey, memberId.Value, TimeSpan.FromMinutes(30));
            return memberId;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao consultar o IdentidadeAPI para o usuário {UserId}", userId);
            return null;
        }
    }
}

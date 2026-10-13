using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace Intranet.Services.ConteudoAPI.Services;

public interface IMemberResolver
{
    /// <summary>MemberId do usuário do token (via IdentidadeAPI), ou null se não houver membro.</summary>
    Task<Guid?> GetMemberIdAsync(string userId, string? bearerToken, CancellationToken ct = default);

    /// <summary>Nome do membro (para textos de notificação), ou null se indisponível.</summary>
    Task<string?> GetNameAsync(Guid memberId, string? bearerToken, CancellationToken ct = default);
}

/// <summary>
/// Resolve a autoria (MemberId) a partir do token, em vez de confiar no valor enviado
/// pelo cliente. O resultado fica em cache por usuário para evitar uma chamada por requisição.
/// </summary>
public class MemberResolver : IMemberResolver
{
    public const string HttpClientName = "Identidade";

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

    private sealed class LookupItem
    {
        public Guid MemberId { get; set; }
        public string? Nome { get; set; }
    }

    private sealed class LookupEnvelope
    {
        public List<LookupItem>? Result { get; set; }
    }

    public async Task<string?> GetNameAsync(Guid memberId, string? bearerToken, CancellationToken ct = default)
    {
        var cacheKey = $"member-name:{memberId}";
        if (_cache.TryGetValue(cacheKey, out string? cached)) return cached;

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/Colaboradores/lookup")
            {
                Content = System.Net.Http.Json.JsonContent.Create(new[] { memberId }),
            };
            if (!string.IsNullOrWhiteSpace(bearerToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            }

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var envelope = await JsonSerializer.DeserializeAsync<LookupEnvelope>(stream, JsonOptions, ct);
            var name = envelope?.Result?.FirstOrDefault(m => m.MemberId == memberId)?.Nome;
            if (!string.IsNullOrWhiteSpace(name)) _cache.Set(cacheKey, name, TimeSpan.FromMinutes(30));
            return name;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao consultar o nome do membro {MemberId}", memberId);
            return null;
        }
    }

    public async Task<Guid?> GetMemberIdAsync(string userId, string? bearerToken, CancellationToken ct = default)
    {
        var cacheKey = $"member:{userId}";
        if (_cache.TryGetValue(cacheKey, out Guid cached)) return cached;

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/Colaboradores/me");
            if (!string.IsNullOrWhiteSpace(bearerToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            }

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("IdentidadeAPI não retornou o membro do usuário {UserId}: HTTP {Status}", userId, (int)response.StatusCode);
                return null;
            }

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

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace Intranet.Services.ComunicacaoAPI.Services;

/// <summary>Membro retornado pelo IdentidadeAPI.</summary>
public record MemberInfo(Guid MemberId, string? Nome, string? Foto);

/// <summary>
/// Acesso ao IdentidadeAPI: membro do token (autoria vem sempre do token, nunca do cliente)
/// e validação de MemberIds informados (destinatários).
/// </summary>
public class IdentidadeClient
{
    public const string HttpClientName = "Identidade";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<IdentidadeClient> _logger;

    public IdentidadeClient(IHttpClientFactory httpClientFactory, IMemoryCache cache, ILogger<IdentidadeClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
    }

    private sealed class Envelope<T>
    {
        public T? Result { get; set; }
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string url, string? bearerToken)
    {
        var request = new HttpRequestMessage(method, url);
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }
        return request;
    }

    /// <summary>MemberId do usuário do token, ou null se não houver membro.</summary>
    public async Task<Guid?> GetMemberIdAsync(string userId, string? bearerToken, CancellationToken ct = default)
    {
        var cacheKey = $"member:{userId}";
        if (_cache.TryGetValue(cacheKey, out Guid cached)) return cached;

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var request = NewRequest(HttpMethod.Get, "api/Colaboradores/me", bearerToken);
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;

            var envelope = await response.Content.ReadFromJsonAsync<Envelope<MemberInfo>>(JsonOptions, ct);
            var memberId = envelope?.Result?.MemberId;
            if (memberId is null || memberId == Guid.Empty) return null;

            _cache.Set(cacheKey, memberId.Value, TimeSpan.FromMinutes(30));
            return memberId;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao consultar o IdentidadeAPI (me) para o usuário {UserId}", userId);
            return null;
        }
    }

    /// <summary>Membros existentes entre os ids informados (ids inexistentes ficam de fora).</summary>
    public async Task<IReadOnlyDictionary<Guid, MemberInfo>> LookupAsync(IEnumerable<Guid> memberIds, string? bearerToken, CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = NewRequest(HttpMethod.Post, "api/Colaboradores/lookup", bearerToken);
        request.Content = JsonContent.Create(memberIds.Distinct().ToList());

        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var envelope = await response.Content.ReadFromJsonAsync<Envelope<List<MemberInfo>>>(JsonOptions, ct);
        return (envelope?.Result ?? new List<MemberInfo>()).ToDictionary(m => m.MemberId);
    }
}

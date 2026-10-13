using System.Net.Http.Headers;
using System.Text.Json;
using Intranet.Services.NotificacaoAPI.Models;

namespace Intranet.Services.NotificacaoAPI.Services;

/// <summary>Busca no IdentidadeAPI os colaboradores com e-mail cadastrado.</summary>
public class RecipientsClient
{
    public const string HttpClientName = "Identidade";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;

    public RecipientsClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    private sealed class ResponseEnvelope
    {
        public List<Recipient>? Result { get; set; }
        public bool IsSuccess { get; set; }
        public string? Message { get; set; }
    }

    public async Task<IReadOnlyList<Recipient>> GetAllAsync(string? bearerToken, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/Colaboradores/destinatarios");
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var envelope = await JsonSerializer.DeserializeAsync<ResponseEnvelope>(stream, JsonOptions, ct);

        if (envelope is null || !envelope.IsSuccess || envelope.Result is null)
        {
            throw new InvalidOperationException($"IdentidadeAPI não retornou destinatários: {envelope?.Message}");
        }

        // Remove e-mails vazios/duplicados
        return envelope.Result
            .Where(r => !string.IsNullOrWhiteSpace(r.Email) && r.Email.Contains('@'))
            .GroupBy(r => r.Email.Trim().ToLowerInvariant())
            .Select(g => g.First())
            .ToList();
    }
}

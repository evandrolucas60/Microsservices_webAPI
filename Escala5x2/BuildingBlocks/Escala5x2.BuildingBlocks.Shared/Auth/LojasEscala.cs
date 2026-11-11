using System.Net.Http.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Escala5x2.BuildingBlocks.Shared.Auth;

/// <summary>
/// Loja cadastrada no Store.Api da Escala 5x2 (resposta de /internal/stores). <paramref name="LgCentroDeCustoCodigo"/>:
/// centro de custo da LG da loja (sincronização de lojas); <paramref name="Active"/>: false = centro de custo encerrado na LG.
/// </summary>
public sealed record LojaEscala(Guid Id, string Code, string Name, string? LgCentroDeCustoCodigo = null, bool Active = true);

/// <summary>
/// Lojas da Escala 5x2, usadas para converter a loja do perfil da intranet ("04 - PRAZERES") em StoreId.
/// O Store.Api consulta o próprio banco; os demais serviços chamam GET /internal/stores do Store.Api.
/// </summary>
public interface ILojasEscala
{
    /// <summary>
    /// Lojas cadastradas; null quando não foi possível consultar (o resultado não é guardado em cache).
    /// Só lança exceção (OperationCanceledException) quando a requisição do cliente foi abortada (<paramref name="ct"/>).
    /// </summary>
    Task<IReadOnlyList<LojaEscala>?> ObterLojasAsync(CancellationToken ct = default);

    /// <summary>Como <see cref="ObterLojasAsync"/>, mas sem cache (sincronização com a LG logo depois da de lojas).</summary>
    Task<IReadOnlyList<LojaEscala>?> ObterLojasAtualizadasAsync(CancellationToken ct = default) => ObterLojasAsync(ct);
}

/// <summary>
/// Lojas pelo endpoint interno do Store.Api (X-Internal-Key = Internal:ApiKey, via <c>InternalKeyHandler</c>),
/// com cache de 5 minutos. Store.Api sem responder (timeout do HttpClient, 5 s): aviso uma vez e nova
/// tentativa só depois de 30 s.
/// </summary>
public sealed class LojasEscalaHttpClient(HttpClient http, IMemoryCache cache, ILogger<LojasEscalaHttpClient> logger) : ILojasEscala
{
    private const string CacheKey = "escala5x2:lojas";
    private const string ChaveIndisponivel = "escala5x2:lojas-indisponivel";
    private static readonly TimeSpan DuracaoFalha = TimeSpan.FromSeconds(30);

    public Task<IReadOnlyList<LojaEscala>?> ObterLojasAtualizadasAsync(CancellationToken ct = default)
    {
        cache.Remove(CacheKey);
        cache.Remove(ChaveIndisponivel);
        return ObterLojasAsync(ct);
    }

    public async Task<IReadOnlyList<LojaEscala>?> ObterLojasAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(CacheKey, out IReadOnlyList<LojaEscala>? cached) && cached is not null) return cached;
        if (cache.TryGetValue(ChaveIndisponivel, out _)) return null;

        try
        {
            using var response = await http.GetAsync("/internal/stores", ct);
            response.EnsureSuccessStatusCode();
            IReadOnlyList<LojaEscala> lojas = await response.Content.ReadFromJsonAsync<List<LojaEscala>>(ct) ?? [];
            cache.Set(CacheKey, lojas, TimeSpan.FromMinutes(5));
            return lojas;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            cache.Set(ChaveIndisponivel, true, DuracaoFalha);
            // Sem a pilha: é indisponibilidade de outro serviço, não erro deste
            logger.LogWarning("Não foi possível consultar as lojas da Escala 5x2 no Store.Api ({Motivo}); nova tentativa em {Segundos} s",
                ex is OperationCanceledException ? $"sem resposta em {http.Timeout.TotalSeconds:0} s" : $"{ex.GetType().Name}: {ex.Message}",
                DuracaoFalha.TotalSeconds);
            return null;
        }
    }
}

public static class CodigoLoja
{
    /// <summary>
    /// Código normalizado para comparar a loja do perfil com o Code do Store.Api:
    /// numérico → sem zeros à esquerda ("04 - PRAZERES" → "4", "04" → "4");
    /// com letras → em maiúsculas, sem espaços nas pontas ("CD" → "CD", "cd" → "CD", "DEMO" → "DEMO").
    /// </summary>
    public static string? Normalizar(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return null;
        var limpo = texto.Trim();
        var digitos = new string(limpo.TakeWhile(char.IsDigit).ToArray());
        if (digitos.Length == 0) return limpo.Split(" - ", 2)[0].Trim().ToUpperInvariant();
        var semZeros = digitos.TrimStart('0');
        return semZeros.Length == 0 ? "0" : semZeros;
    }

    /// <summary>
    /// Código da loja do perfil: o prefixo antes de " - " ("04 - PRAZERES" → "04") ou a própria loja quando
    /// ela não tem descrição ("CD" → "CD", o Centro de Distribuição). Cadastre no Store.Api uma loja com esse Code.
    /// </summary>
    public static string? DaLoja(string? loja)
    {
        if (string.IsNullOrWhiteSpace(loja)) return null;
        var prefixo = loja.Split(" - ", 2)[0].Trim();
        return prefixo.Length > 0 ? prefixo : null;
    }
}

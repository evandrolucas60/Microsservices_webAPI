using BuildingBlocks.Contracts.Administracao;
using MassTransit;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace BuildingBlocks.Shared.Security;

/// <summary>
/// Usuários com bloqueio vigente (definido pelo superAdmin no AuthAPI), para esconder das listagens de
/// colaboradores e do perfil público. Consulta o AuthAPI pelo RabbitMQ e guarda a lista em cache curto;
/// o evento RestricoesUsuarioAlteradas (RestricoesUsuarioAlteradasConsumer) atualiza a lista na hora.
/// Se o AuthAPI não responder, não filtra ninguém (registrando aviso) para não derrubar a página.
/// </summary>
public interface IUsuariosBloqueadosService
{
    /// <summary>Ids (AspNetUsers.Id = Member.UserId) dos usuários bloqueados agora.</summary>
    Task<IReadOnlyCollection<string>> ObterAsync(CancellationToken ct = default);

    Task<bool> EstaBloqueadoAsync(string? userId, CancellationToken ct = default);
}

public class UsuariosBloqueadosService : IUsuariosBloqueadosService
{
    private const string CacheKey = "usuarios-bloqueados";
    private static readonly TimeSpan Duracao = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DuracaoFalha = TimeSpan.FromSeconds(30);

    // Uma consulta ao AuthAPI por vez quando o cache expira (evita rajada de requisições iguais)
    private static readonly SemaphoreSlim Carregando = new(1, 1);
    private static readonly object Trava = new();

    private readonly IRequestClient<ObterUsuariosBloqueados> _client;
    private readonly IMemoryCache _cache;
    private readonly ILogger<UsuariosBloqueadosService> _logger;

    public UsuariosBloqueadosService(IRequestClient<ObterUsuariosBloqueados> client, IMemoryCache cache, ILogger<UsuariosBloqueadosService> logger)
    {
        _client = client;
        _cache = cache;
        _logger = logger;
    }

    // Lista imutável (cada alteração gera uma cópia) + expiração absoluta, mantida ao aplicar eventos
    private sealed record Lista(IReadOnlyDictionary<string, DateTime?> Usuarios, DateTimeOffset Expira);

    public async Task<IReadOnlyCollection<string>> ObterAsync(CancellationToken ct = default)
    {
        var lista = await ObterListaAsync(ct);
        var agora = DateTime.UtcNow;
        // Bloqueio com prazo vencido deixa de valer sem precisar de novo evento
        return lista.Usuarios
            .Where(u => u.Value is not { } ate || ate > agora)
            .Select(u => u.Key)
            .ToArray();
    }

    public async Task<bool> EstaBloqueadoAsync(string? userId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(userId)) return false;
        var lista = await ObterListaAsync(ct);
        return lista.Usuarios.TryGetValue(userId, out var ate) && (ate is not { } fim || fim > DateTime.UtcNow);
    }

    private async Task<Lista> ObterListaAsync(CancellationToken ct)
    {
        if (_cache.TryGetValue(CacheKey, out Lista? cached) && cached is not null) return cached;

        await Carregando.WaitAsync(ct);
        try
        {
            // Outra requisição pode ter carregado enquanto esta esperava
            if (_cache.TryGetValue(CacheKey, out cached) && cached is not null) return cached;

            try
            {
                var response = await _client.GetResponse<UsuariosBloqueadosResposta>(new ObterUsuariosBloqueados(), ct);
                var usuarios = response.Message.Usuarios
                    .Where(u => !string.IsNullOrEmpty(u.UserId))
                    .GroupBy(u => u.UserId, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First().BloqueadoAte, StringComparer.OrdinalIgnoreCase);
                return Guardar(_cache, new Lista(usuarios, DateTimeOffset.UtcNow.Add(Duracao)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Não foi possível consultar os usuários bloqueados; listagens sem filtro de bloqueio");
                // Cache curto da falha para não repetir a espera a cada requisição
                return Guardar(_cache, new Lista(
                    new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase),
                    DateTimeOffset.UtcNow.Add(DuracaoFalha)));
            }
        }
        finally
        {
            Carregando.Release();
        }
    }

    private static Lista Guardar(IMemoryCache cache, Lista lista)
    {
        lock (Trava)
        {
            cache.Set(CacheKey, lista, lista.Expira);
        }
        return lista;
    }

    /// <summary>
    /// Usado pelo consumer do evento de alteração: inclui/retira o usuário da lista em cache.
    /// Sem lista em cache não faz nada (a próxima consulta já traz o estado atual do AuthAPI).
    /// </summary>
    internal static void Atualizar(IMemoryCache cache, string userId, bool bloqueado, DateTime? bloqueadoAte)
    {
        if (string.IsNullOrEmpty(userId)) return;

        lock (Trava)
        {
            if (!cache.TryGetValue(CacheKey, out Lista? atual) || atual is null) return;

            var usuarios = new Dictionary<string, DateTime?>(atual.Usuarios, StringComparer.OrdinalIgnoreCase);
            if (bloqueado) usuarios[userId] = bloqueadoAte;
            else usuarios.Remove(userId);

            cache.Set(CacheKey, atual with { Usuarios = usuarios }, atual.Expira);
        }
    }
}

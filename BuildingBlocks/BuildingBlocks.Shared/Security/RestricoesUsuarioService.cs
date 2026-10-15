using BuildingBlocks.Contracts.Administracao;
using MassTransit;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace BuildingBlocks.Shared.Security;

/// <summary>
/// Bloqueio e restrições do usuário (definidos pelo superAdmin no AuthAPI). Consulta o AuthAPI pelo
/// RabbitMQ e guarda em cache; o evento RestricoesUsuarioAlteradas atualiza o cache na hora.
/// Se o AuthAPI não responder, libera o acesso (registrando aviso) para não derrubar o portal.
/// </summary>
public interface IRestricoesUsuarioService
{
    Task<RestricoesUsuarioResposta> ObterAsync(string userId, CancellationToken ct = default);
}

public class RestricoesUsuarioService : IRestricoesUsuarioService
{
    private static readonly TimeSpan Duracao = TimeSpan.FromMinutes(5);

    private readonly IRequestClient<ObterRestricoesUsuario> _client;
    private readonly IMemoryCache _cache;
    private readonly ILogger<RestricoesUsuarioService> _logger;

    public RestricoesUsuarioService(IRequestClient<ObterRestricoesUsuario> client, IMemoryCache cache, ILogger<RestricoesUsuarioService> logger)
    {
        _client = client;
        _cache = cache;
        _logger = logger;
    }

    internal static string CacheKey(string userId) => $"restricoes:{userId}";

    /// <summary>Usado pelo consumer do evento de alteração.</summary>
    internal static void Atualizar(IMemoryCache cache, RestricoesUsuarioResposta restricoes) =>
        cache.Set(CacheKey(restricoes.UserId), restricoes, Duracao);

    public async Task<RestricoesUsuarioResposta> ObterAsync(string userId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(CacheKey(userId), out RestricoesUsuarioResposta? cached) && cached is not null)
            return Vigente(cached);

        try
        {
            var response = await _client.GetResponse<RestricoesUsuarioResposta>(new ObterRestricoesUsuario { UserId = userId }, ct);
            Atualizar(_cache, response.Message);
            return Vigente(response.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Não foi possível consultar as restrições do usuário {UserId}; acesso liberado", userId);
            // Cache curto da liberação para não repetir a espera a cada requisição
            var liberado = new RestricoesUsuarioResposta { UserId = userId };
            _cache.Set(CacheKey(userId), liberado, TimeSpan.FromSeconds(30));
            return liberado;
        }
    }

    // Bloqueio com prazo vencido deixa de valer sem precisar de novo evento
    private static RestricoesUsuarioResposta Vigente(RestricoesUsuarioResposta r) =>
        r.Bloqueado && r.BloqueadoAte is { } ate && ate <= DateTime.UtcNow ? r with { Bloqueado = false } : r;
}

/// <summary>Atualiza o cache local de restrições quando o superAdmin altera um usuário.</summary>
public class RestricoesUsuarioAlteradasConsumer : IConsumer<RestricoesUsuarioAlteradas>
{
    private readonly IMemoryCache _cache;

    public RestricoesUsuarioAlteradasConsumer(IMemoryCache cache) => _cache = cache;

    public Task Consume(ConsumeContext<RestricoesUsuarioAlteradas> context)
    {
        var m = context.Message;
        RestricoesUsuarioService.Atualizar(_cache, new RestricoesUsuarioResposta
        {
            UserId = m.UserId,
            Bloqueado = m.Bloqueado,
            BloqueadoAte = m.BloqueadoAte,
            Servicos = m.Servicos,
        });
        return Task.CompletedTask;
    }
}

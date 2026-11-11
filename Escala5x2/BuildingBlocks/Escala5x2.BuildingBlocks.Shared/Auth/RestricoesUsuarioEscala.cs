using BuildingBlocks.Contracts.Administracao;
using BuildingBlocks.Shared.Security;
using Escala5x2.BuildingBlocks.Shared.Messaging;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Escala5x2.BuildingBlocks.Shared.Auth;

/// <summary>
/// Bloqueio de usuários nos serviços 5x2: o mesmo <see cref="RestricoesUsuarioService"/> da intranet, com um limite
/// próprio de espera. Com o RabbitMQ inacessível o MassTransit fica tentando enviar a requisição e o RequestTimeout
/// não vale (a requisição HTTP ficaria pendurada até o cliente/gateway desistir). Estourado o limite, libera o acesso
/// (mesma regra da intranet quando o AuthAPI não responde), avisa uma vez e não espera de novo por 30 s; restrições
/// já em cache (inclusive as atualizadas pelo evento RestricoesUsuarioAlteradas) continuam valendo.
/// </summary>
public sealed class RestricoesUsuarioEscala(
    RestricoesUsuarioService intranet,
    IMemoryCache cache,
    IConfiguration configuration,
    ILogger<RestricoesUsuarioEscala> logger) : IRestricoesUsuarioService
{
    private static readonly TimeSpan DuracaoFalha = TimeSpan.FromSeconds(30);

    // Espera máxima pelo AuthAPI (RabbitMq:RequestTimeoutSeconds, padrão 5 s)
    private readonly RabbitMqOptions _rabbitMq = RabbitMqOptions.From(configuration);

    public async Task<RestricoesUsuarioResposta> ObterAsync(string userId, CancellationToken ct = default)
    {
        // RabbitMQ sem conexão há pouco (aqui ou na consulta da loja): libera sem esperar, salvo restrição já em cache
        if (cache.TryGetValue(RabbitMqOptions.ChaveSemConexao, out _) && !cache.TryGetValue(RestricoesUsuarioService.CacheKey(userId), out _))
            return new RestricoesUsuarioResposta { UserId = userId };

        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(_rabbitMq.LimiteEspera);
        try
        {
            return await intranet.ObterAsync(userId, limite.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Só o nosso limite estourou (as demais falhas o serviço da intranet já trata e libera)
            cache.Set(RabbitMqOptions.ChaveSemConexao, true, DuracaoFalha);
            logger.LogWarning(
                "AuthAPI não respondeu à consulta de bloqueio do usuário {UserId} em {Segundos:0} s (RabbitMQ sem conexão); acesso liberado pelos próximos {Falha} s",
                userId, _rabbitMq.RequestTimeout.TotalSeconds, DuracaoFalha.TotalSeconds);
            return new RestricoesUsuarioResposta { UserId = userId };
        }
    }
}

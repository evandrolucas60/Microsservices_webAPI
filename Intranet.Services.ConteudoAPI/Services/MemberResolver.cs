using BuildingBlocks.Shared.Messaging;

namespace Intranet.Services.ConteudoAPI.Services;

public interface IMemberResolver
{
    /// <summary>MemberId do usuário do token (IdentidadeAPI via RabbitMQ), ou null se não houver membro.</summary>
    Task<Guid?> GetMemberIdAsync(string userId, string? bearerToken, CancellationToken ct = default);

    /// <summary>Nome do membro (para textos de notificação), ou null se indisponível.</summary>
    Task<string?> GetNameAsync(Guid memberId, string? bearerToken, CancellationToken ct = default);
}

/// <summary>
/// Resolve a autoria (MemberId) a partir do token, em vez de confiar no valor enviado
/// pelo cliente. Consulta o IdentidadeAPI pelo RabbitMQ (requisição/resposta, com cache).
/// </summary>
public class MemberResolver : IMemberResolver
{
    private readonly IIdentidadeGateway _identidade;
    private readonly ILogger<MemberResolver> _logger;

    public MemberResolver(IIdentidadeGateway identidade, ILogger<MemberResolver> logger)
    {
        _identidade = identidade;
        _logger = logger;
    }

    public async Task<Guid?> GetMemberIdAsync(string userId, string? bearerToken, CancellationToken ct = default) =>
        (await _identidade.ObterMembroPorUsuarioAsync(userId, ct))?.MemberId;

    public async Task<string?> GetNameAsync(Guid memberId, string? bearerToken, CancellationToken ct = default)
    {
        try
        {
            var membros = await _identidade.ObterMembrosAsync(new[] { memberId }, ct);
            return membros.TryGetValue(memberId, out var membro) ? membro.Nome : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // O nome só enfeita o texto da notificação
            _logger.LogWarning(ex, "Não foi possível obter o nome do membro {MemberId}", memberId);
            return null;
        }
    }
}

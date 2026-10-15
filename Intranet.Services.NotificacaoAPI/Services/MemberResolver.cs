using BuildingBlocks.Shared.Messaging;

namespace Intranet.Services.NotificacaoAPI.Services;

/// <summary>
/// Resolve o MemberId do usuário do token (consulta ao IdentidadeAPI pelo RabbitMQ, com cache),
/// usado para listar apenas as notificações do próprio colaborador.
/// </summary>
public class MemberResolver
{
    private readonly IIdentidadeGateway _identidade;

    public MemberResolver(IIdentidadeGateway identidade)
    {
        _identidade = identidade;
    }

    public async Task<Guid?> GetMemberIdAsync(string userId, CancellationToken ct = default) =>
        (await _identidade.ObterMembroPorUsuarioAsync(userId, ct))?.MemberId;
}

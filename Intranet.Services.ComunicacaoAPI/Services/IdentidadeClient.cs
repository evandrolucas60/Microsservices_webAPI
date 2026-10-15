using BuildingBlocks.Shared.Messaging;

namespace Intranet.Services.ComunicacaoAPI.Services;

/// <summary>Membro retornado pelo IdentidadeAPI.</summary>
public record MemberInfo(Guid MemberId, string? Nome, string? Foto);

/// <summary>
/// Acesso ao IdentidadeAPI pelo RabbitMQ (requisição/resposta do MassTransit): membro do token
/// (autoria vem sempre do token, nunca do cliente) e validação de MemberIds informados.
/// Lança ServicoIndisponivelException (503) se o IdentidadeAPI ou o broker não responderem.
/// </summary>
public class IdentidadeClient
{
    private readonly IIdentidadeGateway _identidade;

    public IdentidadeClient(IIdentidadeGateway identidade)
    {
        _identidade = identidade;
    }

    /// <summary>MemberId do usuário do token, ou null se não houver membro.</summary>
    public async Task<Guid?> GetMemberIdAsync(string userId, string? bearerToken, CancellationToken ct = default) =>
        (await _identidade.ObterMembroPorUsuarioAsync(userId, ct))?.MemberId;

    /// <summary>Membros existentes entre os ids informados (ids inexistentes ficam de fora).</summary>
    public async Task<IReadOnlyDictionary<Guid, MemberInfo>> LookupAsync(IEnumerable<Guid> memberIds, string? bearerToken, CancellationToken ct = default)
    {
        var membros = await _identidade.ObterMembrosAsync(memberIds, ct);
        return membros.Values.ToDictionary(m => m.MemberId, m => new MemberInfo(m.MemberId, m.Nome, m.Foto));
    }
}
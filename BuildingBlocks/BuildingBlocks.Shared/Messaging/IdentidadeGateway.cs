using BuildingBlocks.Contracts.Identidade;
using BuildingBlocks.Shared.Exceptions;
using MassTransit;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace BuildingBlocks.Shared.Messaging;

/// <summary>
/// Consultas ao IdentidadeAPI pelo RabbitMQ (requisição/resposta do MassTransit).
/// Lança <see cref="ServicoIndisponivelException"/> quando o IdentidadeAPI ou o broker não respondem.
/// </summary>
public interface IIdentidadeGateway
{
    /// <summary>Membro do usuário do token (cache de 5 minutos); null se o usuário não tiver membro.</summary>
    Task<MembroPorUsuarioResposta?> ObterMembroPorUsuarioAsync(string userId, CancellationToken ct = default);

    /// <summary>Nome/foto dos membros existentes entre os ids informados.</summary>
    Task<IReadOnlyDictionary<Guid, MembroResumo>> ObterMembrosAsync(IEnumerable<Guid> memberIds, CancellationToken ct = default);

    Task<IReadOnlyList<ColaboradorResumo>> ObterColaboradoresPorDepartamentoAsync(string departamento, CancellationToken ct = default);

    /// <summary>E-mails dos membros informados (vazio = todos os colaboradores com e-mail).</summary>
    Task<IReadOnlyList<DestinatarioEmail>> ObterDestinatariosEmailAsync(IEnumerable<Guid>? memberIds = null, CancellationToken ct = default);
}

public class IdentidadeGateway : IIdentidadeGateway
{
    private const string Indisponivel = "O serviço de colaboradores não respondeu. Tente novamente em instantes.";

    private readonly IRequestClient<ObterMembroPorUsuario> _membroPorUsuario;
    private readonly IRequestClient<ObterMembros> _membros;
    private readonly IRequestClient<ObterColaboradoresPorDepartamento> _colaboradores;
    private readonly IRequestClient<ObterDestinatariosEmail> _destinatarios;
    private readonly IMemoryCache _cache;
    private readonly ILogger<IdentidadeGateway> _logger;

    public IdentidadeGateway(
        IRequestClient<ObterMembroPorUsuario> membroPorUsuario,
        IRequestClient<ObterMembros> membros,
        IRequestClient<ObterColaboradoresPorDepartamento> colaboradores,
        IRequestClient<ObterDestinatariosEmail> destinatarios,
        IMemoryCache cache,
        ILogger<IdentidadeGateway> logger)
    {
        _membroPorUsuario = membroPorUsuario;
        _membros = membros;
        _colaboradores = colaboradores;
        _destinatarios = destinatarios;
        _cache = cache;
        _logger = logger;
    }

    private async Task<T> RequestAsync<TRequest, T>(IRequestClient<TRequest> client, TRequest request, CancellationToken ct)
        where TRequest : class
        where T : class
    {
        try
        {
            var response = await client.GetResponse<T>(request, ct);
            return response.Message;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is RequestTimeoutException or RequestFaultException or MassTransitException)
        {
            _logger.LogWarning(ex, "Falha na requisição {Request} ao IdentidadeAPI", typeof(TRequest).Name);
            throw new ServicoIndisponivelException(Indisponivel, ex);
        }
    }

    public async Task<MembroPorUsuarioResposta?> ObterMembroPorUsuarioAsync(string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) return null;

        var cacheKey = $"identidade:membro:{userId}";
        if (_cache.TryGetValue(cacheKey, out MembroPorUsuarioResposta? cached)) return cached;

        var resposta = await RequestAsync<ObterMembroPorUsuario, MembroPorUsuarioResposta>(
            _membroPorUsuario, new ObterMembroPorUsuario { UserId = userId }, ct);
        if (!resposta.Encontrado || resposta.MemberId == Guid.Empty) return null;

        // Cache curto: setor/nome mudam pouco, mas a mudança deve valer em poucos minutos
        _cache.Set(cacheKey, resposta, TimeSpan.FromMinutes(5));
        return resposta;
    }

    public async Task<IReadOnlyDictionary<Guid, MembroResumo>> ObterMembrosAsync(IEnumerable<Guid> memberIds, CancellationToken ct = default)
    {
        var ids = memberIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        if (ids.Length == 0) return new Dictionary<Guid, MembroResumo>();

        var resposta = await RequestAsync<ObterMembros, MembrosResposta>(_membros, new ObterMembros { MemberIds = ids }, ct);
        return resposta.Membros.GroupBy(m => m.MemberId).ToDictionary(g => g.Key, g => g.First());
    }

    public async Task<IReadOnlyList<ColaboradorResumo>> ObterColaboradoresPorDepartamentoAsync(string departamento, CancellationToken ct = default)
    {
        var resposta = await RequestAsync<ObterColaboradoresPorDepartamento, ColaboradoresResposta>(
            _colaboradores, new ObterColaboradoresPorDepartamento { Departamento = departamento }, ct);
        return resposta.Colaboradores;
    }

    public async Task<IReadOnlyList<DestinatarioEmail>> ObterDestinatariosEmailAsync(IEnumerable<Guid>? memberIds = null, CancellationToken ct = default)
    {
        var ids = memberIds?.Where(id => id != Guid.Empty).Distinct().ToArray() ?? Array.Empty<Guid>();
        var resposta = await RequestAsync<ObterDestinatariosEmail, DestinatariosEmailResposta>(
            _destinatarios, new ObterDestinatariosEmail { MemberIds = ids }, ct);
        return resposta.Destinatarios;
    }
}

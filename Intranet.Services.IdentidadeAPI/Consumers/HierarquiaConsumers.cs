using BuildingBlocks.Contracts.Identidade;
using BuildingBlocks.Shared.Exceptions;
using Intranet.Services.IdentidadeAPI.Service.IService;
using MassTransit;

namespace Intranet.Services.IdentidadeAPI.Consumers;

// Consultas de hierarquia (gestor direto) feitas por outros serviços pelo RabbitMQ,
// base dos fluxos de aprovação (ex.: o gestor aprova a solicitação do liderado).

/// <summary>Gestores do membro, do direto até o topo.</summary>
public class ObterCadeiaGestoresConsumer : IConsumer<ObterCadeiaGestores>
{
    private readonly IHierarquiaService _hierarquia;

    public ObterCadeiaGestoresConsumer(IHierarquiaService hierarquia) => _hierarquia = hierarquia;

    public async Task Consume(ConsumeContext<ObterCadeiaGestores> context)
    {
        var memberId = context.Message.MemberId;
        List<Guid> ids;
        try
        {
            ids = memberId == Guid.Empty
                ? throw new NotFoundException("Colaborador não encontrado.")
                : (await _hierarquia.ObterCadeiaIdsAsync(memberId, context.CancellationToken)).Ids;
        }
        catch (NotFoundException)
        {
            await context.RespondAsync(new CadeiaGestoresResposta { MemberId = memberId, Encontrado = false });
            return;
        }

        var resumos = await _hierarquia.ObterResumosAsync(ids, context.CancellationToken);
        var gestores = ids
            .Where(resumos.ContainsKey)
            .Select(id => new MembroResumo { MemberId = id, Nome = resumos[id].Nome, Foto = resumos[id].Foto })
            .ToArray();

        await context.RespondAsync(new CadeiaGestoresResposta { MemberId = memberId, Encontrado = true, Gestores = gestores });
    }
}

/// <summary>Se GestorMemberId é gestor (direto ou acima) de MemberId.</summary>
public class VerificarGestorConsumer : IConsumer<VerificarGestor>
{
    private readonly IHierarquiaService _hierarquia;

    public VerificarGestorConsumer(IHierarquiaService hierarquia) => _hierarquia = hierarquia;

    public async Task Consume(ConsumeContext<VerificarGestor> context)
    {
        var (gestorId, memberId) = (context.Message.GestorMemberId, context.Message.MemberId);
        if (gestorId == Guid.Empty || memberId == Guid.Empty || gestorId == memberId)
        {
            await context.RespondAsync(new VerificarGestorResposta());
            return;
        }

        List<Guid> cadeia;
        try
        {
            cadeia = (await _hierarquia.ObterCadeiaIdsAsync(memberId, context.CancellationToken)).Ids;
        }
        catch (NotFoundException)
        {
            cadeia = new List<Guid>();
        }

        await context.RespondAsync(new VerificarGestorResposta
        {
            EhGestorDireto = cadeia.Count > 0 && cadeia[0] == gestorId,
            EhGestorNaCadeia = cadeia.Contains(gestorId),
        });
    }
}

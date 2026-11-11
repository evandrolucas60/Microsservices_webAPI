using BuildingBlocks.Contracts.Administracao;
using Intranet.Services.AuthAPI.Data;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Intranet.Services.AuthAPI.Consumers;

/// <summary>
/// Requisição/resposta (RabbitMQ): usuários com bloqueio vigente (mesmo critério de
/// AdminUsuariosService.EstaBloqueado). O IdentidadeAPI usa para esconder os bloqueados dos ramais e do perfil.
/// </summary>
public class ObterUsuariosBloqueadosConsumer : IConsumer<ObterUsuariosBloqueados>
{
    private readonly AppDbContext _db;

    public ObterUsuariosBloqueadosConsumer(AppDbContext db) => _db = db;

    public async Task Consume(ConsumeContext<ObterUsuariosBloqueados> context)
    {
        var agora = DateTimeOffset.UtcNow;

        var bloqueados = await _db.Users.AsNoTracking()
            .Where(u => u.LockoutEnabled && u.LockoutEnd != null && u.LockoutEnd > agora)
            .Select(u => new { u.Id, u.LockoutEnd })
            .ToListAsync(context.CancellationToken);

        await context.RespondAsync(new UsuariosBloqueadosResposta
        {
            // MaxValue = bloqueio indeterminado (como em AdminUsuariosService.BloqueadoAte)
            Usuarios = bloqueados
                .Select(u => new UsuarioBloqueado
                {
                    UserId = u.Id,
                    BloqueadoAte = u.LockoutEnd!.Value.Year < 9999 ? u.LockoutEnd.Value.UtcDateTime : null,
                })
                .ToArray(),
        });
    }
}

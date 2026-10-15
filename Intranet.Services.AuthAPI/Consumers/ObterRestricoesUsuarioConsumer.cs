using BuildingBlocks.Contracts.Administracao;
using Intranet.Services.AuthAPI.Data;
using Intranet.Services.AuthAPI.Service;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Intranet.Services.AuthAPI.Consumers;

/// <summary>
/// Requisição/resposta (RabbitMQ): os demais serviços consultam se o usuário está bloqueado e
/// quais serviços estão restritos (UserAccessMiddleware e [RequerServico]).
/// </summary>
public class ObterRestricoesUsuarioConsumer : IConsumer<ObterRestricoesUsuario>
{
    private readonly AppDbContext _db;

    public ObterRestricoesUsuarioConsumer(AppDbContext db) => _db = db;

    public async Task Consume(ConsumeContext<ObterRestricoesUsuario> context)
    {
        var userId = context.Message.UserId;
        var ct = context.CancellationToken;

        var user = string.IsNullOrWhiteSpace(userId)
            ? null
            : await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);

        var servicos = user is null
            ? null
            : await _db.UserRestrictions.AsNoTracking()
                .Where(r => r.UserId == userId)
                .Select(r => r.Servicos)
                .FirstOrDefaultAsync(ct);

        // Usuário inexistente: sem bloqueio nem restrições (o token já não seria renovado)
        await context.RespondAsync(new RestricoesUsuarioResposta
        {
            UserId = userId,
            Bloqueado = user is not null && AdminUsuariosService.EstaBloqueado(user),
            BloqueadoAte = user is null ? null : AdminUsuariosService.BloqueadoAte(user),
            Servicos = servicos ?? Array.Empty<string>(),
        });
    }
}

using BuildingBlocks.Contracts.Identidade;
using Intranet.Services.IdentidadeAPI.Data;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Intranet.Services.IdentidadeAPI.Consumers;

/// <summary>
/// Painel de gestão (AuthAPI): colaborador de cada conta informada (UserId), com foto, setor, cargo,
/// gestor direto e último acesso. Contas sem membro ficam fora da resposta. Até 500 ids por consulta.
/// </summary>
public class ObterMembrosPorUsuariosConsumer : IConsumer<ObterMembrosPorUsuarios>
{
    private const int MaxIds = 500;
    private readonly AppDbContext _db;

    public ObterMembrosPorUsuariosConsumer(AppDbContext db) => _db = db;

    public async Task Consume(ConsumeContext<ObterMembrosPorUsuarios> context)
    {
        var ids = (context.Message.UserIds ?? Array.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct()
            .Take(MaxIds)
            .ToList();

        var itens = ids.Count == 0
            ? new List<MembroDeUsuario>()
            : await (from m in _db.Members.AsNoTracking()
                     where m.UserId != null && ids.Contains(m.UserId)
                     join p in _db.Profiles.AsNoTracking() on m.MemberId equals p.MemberId into profiles
                     from p in profiles.DefaultIfEmpty()
                     join w in _db.Works.AsNoTracking() on m.MemberId equals w.MemberId into works
                     from w in works.DefaultIfEmpty()
                     select new MembroDeUsuario
                     {
                         UserId = m.UserId!,
                         MemberId = m.MemberId,
                         Nome = m.Name,
                         Foto = p != null ? p.ProfilePicture : null,
                         Departamento = w != null ? w.Department : null,
                         Cargo = w != null ? w.Title : null,
                         GestorMemberId = m.GestorMemberId,
                         UltimoAcesso = m.LastLoginAt,
                     })
                .ToListAsync(context.CancellationToken);

        await context.RespondAsync(new MembrosPorUsuariosResposta
        {
            Itens = itens.GroupBy(i => i.UserId).Select(g => g.First()).ToArray(),
        });
    }
}

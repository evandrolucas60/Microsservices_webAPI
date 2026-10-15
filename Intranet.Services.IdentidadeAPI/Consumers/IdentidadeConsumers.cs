using BuildingBlocks.Contracts.Identidade;
using Intranet.Services.IdentidadeAPI.Data;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Intranet.Services.IdentidadeAPI.Consumers;

// Respostas às consultas dos outros serviços pelo RabbitMQ (requisição/resposta do MassTransit).
// Substituem as chamadas HTTP diretas a api/Colaboradores/me, lookup, ramais e destinatarios.

/// <summary>Membro do usuário autenticado (claim "id"), com setor.</summary>
public class ObterMembroPorUsuarioConsumer : IConsumer<ObterMembroPorUsuario>
{
    private readonly AppDbContext _db;

    public ObterMembroPorUsuarioConsumer(AppDbContext db) => _db = db;

    public async Task Consume(ConsumeContext<ObterMembroPorUsuario> context)
    {
        var userId = context.Message.UserId;
        var membro = string.IsNullOrWhiteSpace(userId)
            ? null
            : await (from m in _db.Members.AsNoTracking()
                     where m.UserId == userId
                     join w in _db.Works.AsNoTracking() on m.MemberId equals w.MemberId into works
                     from w in works.DefaultIfEmpty()
                     select new { m.MemberId, m.Name, Departamento = w != null ? w.Department : null })
                .FirstOrDefaultAsync(context.CancellationToken);

        await context.RespondAsync(membro is null
            ? new MembroPorUsuarioResposta { Encontrado = false }
            : new MembroPorUsuarioResposta { Encontrado = true, MemberId = membro.MemberId, Nome = membro.Name, Departamento = membro.Departamento });
    }
}

/// <summary>Nome e foto dos membros (validação de destinatários, nomes em notificações).</summary>
public class ObterMembrosConsumer : IConsumer<ObterMembros>
{
    private const int MaxIds = 500;
    private readonly AppDbContext _db;

    public ObterMembrosConsumer(AppDbContext db) => _db = db;

    public async Task Consume(ConsumeContext<ObterMembros> context)
    {
        var ids = context.Message.MemberIds.Where(id => id != Guid.Empty).Distinct().Take(MaxIds).ToList();
        var membros = ids.Count == 0
            ? new List<MembroResumo>()
            : await (from m in _db.Members.AsNoTracking()
                     where ids.Contains(m.MemberId)
                     join p in _db.Profiles.AsNoTracking() on m.MemberId equals p.MemberId into profiles
                     from p in profiles.DefaultIfEmpty()
                     select new MembroResumo { MemberId = m.MemberId, Nome = m.Name, Foto = p != null ? p.ProfilePicture : null })
                .ToListAsync(context.CancellationToken);

        await context.RespondAsync(new MembrosResposta { Membros = membros.GroupBy(m => m.MemberId).Select(g => g.First()).ToArray() });
    }
}

/// <summary>Colaboradores de um setor (ex.: T.I para a escala).</summary>
public class ObterColaboradoresPorDepartamentoConsumer : IConsumer<ObterColaboradoresPorDepartamento>
{
    private readonly AppDbContext _db;

    public ObterColaboradoresPorDepartamentoConsumer(AppDbContext db) => _db = db;

    public async Task Consume(ConsumeContext<ObterColaboradoresPorDepartamento> context)
    {
        var departamento = context.Message.Departamento?.Trim() ?? string.Empty;
        var colaboradores = departamento.Length == 0
            ? new List<ColaboradorResumo>()
            : await (from w in _db.Works.AsNoTracking()
                     where w.Department == departamento
                     join m in _db.Members.AsNoTracking() on w.MemberId equals m.MemberId
                     join p in _db.Profiles.AsNoTracking() on m.MemberId equals p.MemberId into profiles
                     from p in profiles.DefaultIfEmpty()
                     orderby m.Name
                     select new ColaboradorResumo
                     {
                         MemberId = m.MemberId,
                         Nome = m.Name,
                         Email = m.Email,
                         Cargo = w.Title,
                         Departamento = w.Department,
                         Foto = p != null ? p.ProfilePicture : null,
                     })
                .ToListAsync(context.CancellationToken);

        await context.RespondAsync(new ColaboradoresResposta { Colaboradores = colaboradores.GroupBy(c => c.MemberId).Select(g => g.First()).ToArray() });
    }
}

/// <summary>E-mails dos colaboradores (newsletter e notificações por e-mail). Sem ids: todos.</summary>
public class ObterDestinatariosEmailConsumer : IConsumer<ObterDestinatariosEmail>
{
    private readonly AppDbContext _db;

    public ObterDestinatariosEmailConsumer(AppDbContext db) => _db = db;

    public async Task Consume(ConsumeContext<ObterDestinatariosEmail> context)
    {
        var ids = context.Message.MemberIds.Where(id => id != Guid.Empty).Distinct().ToList();
        var query = _db.Members.AsNoTracking().Where(m => m.Email != null && m.Email != "");
        if (ids.Count > 0) query = query.Where(m => ids.Contains(m.MemberId));

        var destinatarios = await query
            .OrderBy(m => m.Name)
            .Select(m => new DestinatarioEmail { MemberId = m.MemberId, Email = m.Email!, Nome = m.Name })
            .ToListAsync(context.CancellationToken);

        await context.RespondAsync(new DestinatariosEmailResposta { Destinatarios = destinatarios.ToArray() });
    }
}

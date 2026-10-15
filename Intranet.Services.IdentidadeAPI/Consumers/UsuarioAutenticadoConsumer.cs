using BuildingBlocks.Contracts.Autenticacao;
using Intranet.Services.IdentidadeAPI.Data;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Intranet.Services.IdentidadeAPI.Consumers;

/// <summary>
/// Login no portal (evento do AuthAPI): grava o último acesso do colaborador.
/// Mensagens fora de ordem não voltam o horário para trás.
/// </summary>
public class UsuarioAutenticadoConsumer : IConsumer<UsuarioAutenticado>
{
    private readonly AppDbContext _db;
    private readonly ILogger<UsuarioAutenticadoConsumer> _logger;

    public UsuarioAutenticadoConsumer(AppDbContext db, ILogger<UsuarioAutenticadoConsumer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<UsuarioAutenticado> context)
    {
        var userId = context.Message.UserId;
        if (string.IsNullOrWhiteSpace(userId)) return;

        var em = DateTime.SpecifyKind(context.Message.AutenticadoEm, DateTimeKind.Utc);
        var atualizados = await _db.Members
            .Where(m => m.UserId == userId && (m.LastLoginAt == null || m.LastLoginAt < em))
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LastLoginAt, em), context.CancellationToken);

        if (atualizados == 0)
            _logger.LogDebug("Login de {UserId} sem membro correspondente (ou evento antigo)", userId);
    }
}

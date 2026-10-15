using BuildingBlocks.Contracts.Administracao;
using Intranet.Services.AuditoriaAPI.Data;
using Intranet.Services.AuditoriaAPI.Models;
using MassTransit;

namespace Intranet.Services.AuditoriaAPI.Consumers;

/// <summary>Erro 5xx publicado pelo ExceptionHandlingMiddleware de qualquer serviço.</summary>
public class ErroRegistradoConsumer : IConsumer<ErroRegistrado>
{
    private readonly AppDbContext _db;

    public ErroRegistradoConsumer(AppDbContext db) => _db = db;

    public async Task Consume(ConsumeContext<ErroRegistrado> context)
    {
        var m = context.Message;
        _db.Erros.Add(new ErroLog
        {
            Servico = Limitar(m.Servico, 100) ?? "desconhecido",
            UserId = Limitar(m.UserId, 100),
            Metodo = Limitar(m.Metodo, 10) ?? string.Empty,
            Caminho = Limitar(m.Caminho, 500) ?? string.Empty,
            Status = m.Status,
            Mensagem = Limitar(m.Mensagem, 1000) ?? string.Empty,
            Excecao = Limitar(m.Excecao, 8000),
            CorrelationId = Limitar(m.CorrelationId, 100),
            Em = Utc(m.Em),
        });
        await _db.SaveChangesAsync(context.CancellationToken);
    }

    internal static string? Limitar(string? valor, int max)
    {
        if (string.IsNullOrWhiteSpace(valor)) return null;
        valor = valor.Trim();
        return valor.Length > max ? valor[..max] : valor;
    }

    internal static DateTime Utc(DateTime em) => em == default
        ? DateTime.UtcNow
        : em.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(em, DateTimeKind.Utc) : em.ToUniversalTime();
}

/// <summary>Tentativa de login publicada pelo AuthAPI.</summary>
public class AcessoRegistradoConsumer : IConsumer<AcessoRegistrado>
{
    private readonly AppDbContext _db;

    public AcessoRegistradoConsumer(AppDbContext db) => _db = db;

    public async Task Consume(ConsumeContext<AcessoRegistrado> context)
    {
        var m = context.Message;
        _db.Acessos.Add(new AcessoLog
        {
            UserId = ErroRegistradoConsumer.Limitar(m.UserId, 100),
            UserName = ErroRegistradoConsumer.Limitar(m.UserName, 256),
            Sucesso = m.Sucesso,
            Motivo = ErroRegistradoConsumer.Limitar(m.Motivo, 200),
            Ip = ErroRegistradoConsumer.Limitar(m.Ip, 64),
            Em = ErroRegistradoConsumer.Utc(m.Em),
        });
        await _db.SaveChangesAsync(context.CancellationToken);
    }
}

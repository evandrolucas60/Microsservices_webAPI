using BuildingBlocks.Shared.Security;
using Intranet.Services.AuditoriaAPI.Data;
using Intranet.Services.AuditoriaAPI.Models.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Intranet.Services.AuditoriaAPI.Controllers;

/// <summary>Painel de gestão: tentativas de login (sucesso e falha) e métricas de acesso.</summary>
[Route("api/admin/acessos")]
[ApiController]
[Authorize(Policy = AdminPolicies.SuperAdmin)]
public class AcessosController : ControllerBase
{
    private readonly AppDbContext _db;

    public AcessosController(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>Acessos mais recentes primeiro.</summary>
    [HttpGet]
    public async Task<ActionResult> Listar(
        [FromQuery] string? userId,
        [FromQuery] bool? sucesso,
        [FromQuery] DateTime? de,
        [FromQuery] DateTime? ate,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        (pageNumber, pageSize) = Consultas.Normalizar(pageNumber, pageSize);

        var query = _db.Acessos.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(userId)) query = query.Where(a => a.UserId == userId.Trim());
        if (sucesso is { } s) query = query.Where(a => a.Sucesso == s);
        if (de is { } inicio)
        {
            var utc = Consultas.ParaUtc(inicio);
            query = query.Where(a => a.Em >= utc);
        }
        if (ate is { } fim)
        {
            var limite = Consultas.LimiteAte(fim);
            query = query.Where(a => a.Em < limite);
        }

        var total = await query.CountAsync(ct);
        var itens = await query
            .OrderByDescending(a => a.Em)
            .ThenByDescending(a => a.Id)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new AcessoDto
            {
                Id = a.Id,
                UserId = a.UserId,
                UserName = a.UserName,
                Sucesso = a.Sucesso,
                Motivo = a.Motivo,
                Ip = a.Ip,
                Em = a.Em,
            })
            .ToListAsync(ct);

        Consultas.EscreverPaginacao(Response, total, pageNumber, pageSize);
        return Ok(new ResponseDto { Result = itens });
    }

    /// <summary>
    /// Métricas dos últimos "dias" dias (padrão 30): logins com sucesso, usuários únicos e falhas,
    /// no total e por dia (UTC).
    /// </summary>
    [HttpGet("metricas")]
    public async Task<ActionResult> Metricas([FromQuery] int dias = 30, CancellationToken ct = default)
    {
        dias = Consultas.NormalizarDias(dias);
        var inicio = Consultas.InicioJanela(dias);
        var query = _db.Acessos.AsNoTracking().Where(a => a.Em >= inicio);
        var sucessos = query.Where(a => a.Sucesso);

        var totalLogins = await sucessos.CountAsync(ct);
        var falhas = await query.CountAsync(a => !a.Sucesso, ct);
        var usuariosUnicos = await sucessos
            .Where(a => a.UserId != null)
            .Select(a => a.UserId)
            .Distinct()
            .CountAsync(ct);

        var contagens = await query
            .GroupBy(a => a.Em.Date)
            .Select(g => new
            {
                Dia = g.Key,
                Logins = g.Count(a => a.Sucesso),
                Falhas = g.Count(a => !a.Sucesso),
            })
            .ToListAsync(ct);

        var unicosPorDia = await sucessos
            .Where(a => a.UserId != null)
            .Select(a => new { Dia = a.Em.Date, a.UserId })
            .Distinct()
            .GroupBy(x => x.Dia)
            .Select(g => new { Dia = g.Key, Total = g.Count() })
            .ToListAsync(ct);

        var contagensMapa = contagens.ToDictionary(x => Consultas.FormatarDia(x.Dia));
        var unicosMapa = unicosPorDia.ToDictionary(x => Consultas.FormatarDia(x.Dia), x => x.Total);

        return Ok(new ResponseDto
        {
            Result = new AcessosMetricasDto
            {
                TotalLogins = totalLogins,
                UsuariosUnicos = usuariosUnicos,
                Falhas = falhas,
                // Todos os dias da janela, inclusive os sem acesso
                PorDia = Consultas.Dias(inicio, dias)
                    .Select(d => Consultas.FormatarDia(d))
                    .Select(d => new AcessosPorDiaDto
                    {
                        Dia = d,
                        Logins = contagensMapa.TryGetValue(d, out var c) ? c.Logins : 0,
                        Falhas = c?.Falhas ?? 0,
                        UsuariosUnicos = unicosMapa.GetValueOrDefault(d),
                    })
                    .ToList(),
            },
        });
    }
}

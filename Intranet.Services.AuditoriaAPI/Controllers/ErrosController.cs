using BuildingBlocks.Shared.Exceptions;
using BuildingBlocks.Shared.Security;
using Intranet.Services.AuditoriaAPI.Data;
using Intranet.Services.AuditoriaAPI.Models.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Intranet.Services.AuditoriaAPI.Controllers;

/// <summary>Painel de gestão: erros (5xx) registrados pelos serviços, por usuário/serviço/período.</summary>
[Route("api/admin/erros")]
[ApiController]
[Authorize(Policy = AdminPolicies.SuperAdmin)]
public class ErrosController : ControllerBase
{
    private const int TopUsuarios = 10;

    private readonly AppDbContext _db;

    public ErrosController(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>Erros mais recentes primeiro.</summary>
    [HttpGet]
    public async Task<ActionResult> Listar(
        [FromQuery] string? userId,
        [FromQuery] string? servico,
        [FromQuery] DateTime? de,
        [FromQuery] DateTime? ate,
        [FromQuery] int? status,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        (pageNumber, pageSize) = Consultas.Normalizar(pageNumber, pageSize);

        var query = _db.Erros.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(userId)) query = query.Where(e => e.UserId == userId.Trim());
        if (!string.IsNullOrWhiteSpace(servico))
        {
            var padrao = $"%{servico.Trim()}%";
            query = query.Where(e => EF.Functions.ILike(e.Servico, padrao));
        }
        if (de is { } inicio)
        {
            var utc = Consultas.ParaUtc(inicio);
            query = query.Where(e => e.Em >= utc);
        }
        if (ate is { } fim)
        {
            var limite = Consultas.LimiteAte(fim);
            query = query.Where(e => e.Em < limite);
        }
        if (status is { } codigo) query = query.Where(e => e.Status == codigo);

        var total = await query.CountAsync(ct);
        var itens = await query
            .OrderByDescending(e => e.Em)
            .ThenByDescending(e => e.Id)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(e => new ErroDto
            {
                Id = e.Id,
                Servico = e.Servico,
                UserId = e.UserId,
                Metodo = e.Metodo,
                Caminho = e.Caminho,
                Status = e.Status,
                Mensagem = e.Mensagem,
                CorrelationId = e.CorrelationId,
                Em = e.Em,
            })
            .ToListAsync(ct);

        Consultas.EscreverPaginacao(Response, total, pageNumber, pageSize);
        return Ok(new ResponseDto { Result = itens });
    }

    [HttpGet("{id:long}")]
    public async Task<ActionResult> Obter(long id, CancellationToken ct)
    {
        var erro = await _db.Erros.AsNoTracking()
            .Where(e => e.Id == id)
            .Select(e => new ErroDetalheDto
            {
                Id = e.Id,
                Servico = e.Servico,
                UserId = e.UserId,
                Metodo = e.Metodo,
                Caminho = e.Caminho,
                Status = e.Status,
                Mensagem = e.Mensagem,
                CorrelationId = e.CorrelationId,
                Em = e.Em,
                Excecao = e.Excecao,
            })
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("Erro não encontrado.");

        return Ok(new ResponseDto { Result = erro });
    }

    /// <summary>Resumo dos últimos "dias" dias (padrão 30): total, por serviço, por dia e usuários mais afetados.</summary>
    [HttpGet("resumo")]
    public async Task<ActionResult> Resumo([FromQuery] int dias = 30, CancellationToken ct = default)
    {
        dias = Consultas.NormalizarDias(dias);
        var inicio = Consultas.InicioJanela(dias);
        var query = _db.Erros.AsNoTracking().Where(e => e.Em >= inicio);

        var total = await query.CountAsync(ct);

        var porServico = await query
            .GroupBy(e => e.Servico)
            .Select(g => new ErrosPorServicoDto { Servico = g.Key, Total = g.Count() })
            .OrderByDescending(x => x.Total)
            .ToListAsync(ct);

        var porDiaDb = await query
            .GroupBy(e => e.Em.Date)
            .Select(g => new { Dia = g.Key, Total = g.Count() })
            .ToListAsync(ct);
        var porDiaMapa = porDiaDb.ToDictionary(x => Consultas.FormatarDia(x.Dia), x => x.Total);

        var topUsuarios = await query
            .Where(e => e.UserId != null)
            .GroupBy(e => e.UserId!)
            .Select(g => new ErrosPorUsuarioDto { UserId = g.Key, Total = g.Count() })
            .OrderByDescending(x => x.Total)
            .Take(TopUsuarios)
            .ToListAsync(ct);

        return Ok(new ResponseDto
        {
            Result = new ErrosResumoDto
            {
                Total = total,
                PorServico = porServico,
                // Todos os dias da janela, inclusive os sem erros (gráfico contínuo)
                PorDia = Consultas.Dias(inicio, dias)
                    .Select(d => Consultas.FormatarDia(d))
                    .Select(d => new ErrosPorDiaDto { Dia = d, Total = porDiaMapa.GetValueOrDefault(d) })
                    .ToList(),
                TopUsuarios = topUsuarios,
            },
        });
    }
}

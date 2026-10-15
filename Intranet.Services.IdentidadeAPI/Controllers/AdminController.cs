using System.Text.Json;
using BuildingBlocks.Shared.Exceptions;
using BuildingBlocks.Shared.Security;
using Intranet.Services.IdentidadeAPI.Data;
using Intranet.Services.IdentidadeAPI.Models.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Intranet.Services.IdentidadeAPI.Controllers;

/// <summary>
/// Painel de gestão (superAdmin): certificados enviados pelos colaboradores e métricas de cadastro.
/// O download do certificado continua em GET api/Certificados/{id}/arquivo.
/// </summary>
[Route("api/admin")]
[ApiController]
[Authorize(Policy = AdminPolicies.SuperAdmin)]
public class AdminController : ControllerBase
{
    private const int TamanhoPaginaPadrao = 20;
    private const int TamanhoPaginaMaximo = 100;

    private readonly AppDbContext _db;
    private readonly ILogger<AdminController> _logger;

    public AdminController(AppDbContext db, ILogger<AdminController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>Certificados (somente metadados, sem o PDF), do mais recente para o mais antigo.</summary>
    [HttpGet("arquivos")]
    public async Task<ActionResult> Arquivos([FromQuery] int pageNumber = 1, [FromQuery] int pageSize = TamanhoPaginaPadrao, CancellationToken ct = default)
    {
        (pageNumber, pageSize) = NormalizarPaginacao(pageNumber, pageSize);

        var query = _db.Certificates.AsNoTracking();
        var total = await query.CountAsync(ct);

        var itens = await query
            .OrderByDescending(c => c.CreatedAt)
            .ThenBy(c => c.CertificateId)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            // Projeção sem o conteúdo (bytea)
            .Select(c => new
            {
                Id = c.CertificateId,
                c.MemberId,
                Titulo = c.Title,
                NomeArquivo = c.FileName,
                Tamanho = c.FileSize,
                CriadoEm = c.CreatedAt,
            })
            .ToListAsync(ct);

        EscreverPaginacao(total, pageNumber, pageSize);
        return Ok(new ResponseDto { Result = itens });
    }

    /// <summary>Exclui um certificado de qualquer colaborador.</summary>
    [HttpDelete("arquivos/{id:guid}")]
    public async Task<ActionResult> ExcluirArquivo(Guid id, CancellationToken ct)
    {
        // Exclui sem carregar o conteúdo do arquivo
        var removidos = await _db.Certificates.Where(c => c.CertificateId == id).ExecuteDeleteAsync(ct);
        if (removidos == 0) throw new NotFoundException("Certificado não encontrado.");

        _logger.LogInformation("Certificado {CertificateId} excluído pela administração (usuário {UserId})", id, User.GetUserId());
        return Ok(new ResponseDto { Message = "Certificado excluído." });
    }

    /// <summary>Colaboradores cadastrados, com setor e com gestor direto definidos, e total por setor.</summary>
    [HttpGet("metricas")]
    public async Task<ActionResult> Metricas(CancellationToken ct)
    {
        var totalColaboradores = await _db.Members.AsNoTracking().CountAsync(ct);
        var comGestor = await _db.Members.AsNoTracking().CountAsync(m => m.GestorMemberId != null, ct);

        var setores = await (from m in _db.Members.AsNoTracking()
                             join w in _db.Works.AsNoTracking() on m.MemberId equals w.MemberId
                             where w.Department != null && w.Department.Trim() != ""
                             group m by w.Department.Trim() into g
                             select new { Setor = g.Key, Total = g.Count() })
            .ToListAsync(ct);

        var porSetor = setores
            .OrderByDescending(s => s.Total)
            .ThenBy(s => s.Setor)
            .ToList();

        return Ok(new ResponseDto
        {
            Result = new
            {
                TotalColaboradores = totalColaboradores,
                ComSetor = porSetor.Sum(s => s.Total),
                ComGestor = comGestor,
                SemGestor = totalColaboradores - comGestor,
                PorSetor = porSetor,
            },
        });
    }

    private static (int PageNumber, int PageSize) NormalizarPaginacao(int pageNumber, int pageSize) =>
        (Math.Max(pageNumber, 1), pageSize < 1 ? TamanhoPaginaPadrao : Math.Min(pageSize, TamanhoPaginaMaximo));

    private void EscreverPaginacao(int total, int pageNumber, int pageSize)
    {
        var totalPages = (int)Math.Ceiling(total / (double)pageSize);
        Response.Headers["X-Pagination"] = JsonSerializer.Serialize(new
        {
            TotalCount = total,
            PageSize = pageSize,
            CurrentPage = pageNumber,
            TotalPages = totalPages,
            HasNext = pageNumber < totalPages,
            HasPrevious = pageNumber > 1,
        });
    }
}

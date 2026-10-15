using System.Text.Json;
using BuildingBlocks.Shared.Security;
using Intranet.Services.DocumentoAPI.Data;
using Intranet.Services.DocumentoAPI.Models.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Intranet.Services.DocumentoAPI.Controllers;

/// <summary>
/// Painel de gestão (superAdmin): arquivos das lojas do GC. Visualizar/baixar continua em
/// GET api/lojas/arquivos/{id}.
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

    private static ResponseDto Falha(string message) => new() { IsSuccess = false, Message = message };

    /// <summary>
    /// Arquivos de todas as lojas (somente metadados), do mais recente para o mais antigo.
    /// busca: parte do nome do arquivo ou da loja (sem diferenciar maiúsculas/minúsculas).
    /// </summary>
    [HttpGet("arquivos")]
    public async Task<ActionResult> Arquivos(
        [FromQuery] string? busca,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = TamanhoPaginaPadrao,
        CancellationToken ct = default)
    {
        pageNumber = Math.Max(pageNumber, 1);
        pageSize = pageSize < 1 ? TamanhoPaginaPadrao : Math.Min(pageSize, TamanhoPaginaMaximo);

        var query = _db.lojaArquivos.AsNoTracking();
        var termo = busca?.Trim();
        if (!string.IsNullOrEmpty(termo))
        {
            var padrao = $"%{EscaparLike(termo)}%";
            query = query.Where(a => EF.Functions.ILike(a.NomeArquivo, padrao, "\\")
                                     || EF.Functions.ILike(a.Loja.Nome, padrao, "\\"));
        }

        var total = await query.CountAsync(ct);
        var itens = await query
            .OrderByDescending(a => a.CriadoEm)
            .ThenByDescending(a => a.LojaArquivoId)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            // Projeção sem o conteúdo (bytea)
            .Select(a => new
            {
                Id = a.LojaArquivoId,
                a.LojaId,
                LojaNome = a.Loja.Nome,
                a.Secao,
                a.Tipo,
                Nome = a.NomeArquivo,
                a.ContentType,
                a.Tamanho,
                a.EnviadoPor,
                a.CriadoEm,
            })
            .ToListAsync(ct);

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

        return Ok(new ResponseDto { Result = itens });
    }

    /// <summary>Remove um arquivo de qualquer loja.</summary>
    [HttpDelete("arquivos/{id:int}")]
    public async Task<ActionResult> RemoverArquivo(int id, CancellationToken ct)
    {
        var lojaId = await _db.lojaArquivos.AsNoTracking()
            .Where(a => a.LojaArquivoId == id)
            .Select(a => (int?)a.LojaId)
            .FirstOrDefaultAsync(ct);
        if (lojaId is null) return NotFound(Falha("Arquivo não encontrado."));

        // Exclusão direta no banco, sem carregar o conteúdo
        await _db.lojaArquivos.Where(a => a.LojaArquivoId == id).ExecuteDeleteAsync(ct);
        await _db.lojas.Where(l => l.LojaId == lojaId.Value)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.AtualizadoEm, DateTime.UtcNow), ct);

        _logger.LogInformation("Arquivo {ArquivoId} da loja {LojaId} removido pela administração (usuário {UserId})",
            id, lojaId, User.GetUserId());
        return Ok(new ResponseDto { Message = "Arquivo removido." });
    }

    // Curingas do LIKE digitados pelo usuário são tratados como texto
    private static string EscaparLike(string valor) =>
        valor.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}

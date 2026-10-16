using System.Text.Json;
using BuildingBlocks.Shared.Security;
using Intranet.Services.GerenciamentoCategoriaAPI.Data;
using Intranet.Services.GerenciamentoCategoriaAPI.Models.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Intranet.Services.GerenciamentoCategoriaAPI.Controllers;

/// <summary>
/// Painel de gestão (superAdmin): arquivos dos itens das categorias do GC. Visualizar/baixar continua em
/// GET api/categorias/itens/{id}/arquivo.
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
    /// Arquivos de todas as categorias (somente metadados), do mais recente para o mais antigo.
    /// busca: parte do nome do arquivo, do título do item ou do nome da categoria (sem diferenciar maiúsculas/minúsculas).
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

        var query = _db.categoriaItens.AsNoTracking();
        var termo = busca?.Trim();
        if (!string.IsNullOrEmpty(termo))
        {
            var padrao = $"%{EscaparLike(termo)}%";
            query = query.Where(i => EF.Functions.ILike(i.NomeArquivo, padrao, "\\")
                                     || EF.Functions.ILike(i.Titulo, padrao, "\\")
                                     || EF.Functions.ILike(i.Categoria.Nome, padrao, "\\"));
        }

        var total = await query.CountAsync(ct);
        var itens = await query
            .OrderByDescending(i => i.EnviadoEm)
            .ThenByDescending(i => i.CategoriaItemId)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            // Projeção sem o conteúdo (bytea)
            .Select(i => new
            {
                Id = i.CategoriaItemId,
                i.CategoriaId,
                CategoriaNome = i.Categoria.Nome,
                i.Titulo,
                i.Secao,
                i.Tipo,
                Nome = i.NomeArquivo,
                i.ContentType,
                i.Tamanho,
                i.EnviadoPor,
                CriadoEm = i.EnviadoEm,
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

    /// <summary>Remove o item (e o seu arquivo) de qualquer categoria.</summary>
    [HttpDelete("arquivos/{id:int}")]
    public async Task<ActionResult> RemoverArquivo(int id, CancellationToken ct)
    {
        var categoriaId = await _db.categoriaItens.AsNoTracking()
            .Where(i => i.CategoriaItemId == id)
            .Select(i => (int?)i.CategoriaId)
            .FirstOrDefaultAsync(ct);
        if (categoriaId is null) return NotFound(Falha("Arquivo não encontrado."));

        // Exclusão direta no banco, sem carregar o conteúdo
        await _db.categoriaItens.Where(i => i.CategoriaItemId == id).ExecuteDeleteAsync(ct);
        await _db.categorias.Where(c => c.CategoriaId == categoriaId.Value)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.AtualizadoEm, DateTime.UtcNow), ct);

        _logger.LogInformation("Item {ItemId} da categoria {CategoriaId} removido pela administração (usuário {UserId})",
            id, categoriaId, User.GetUserId());
        return Ok(new ResponseDto { Message = "Arquivo removido." });
    }

    // Curingas do LIKE digitados pelo usuário são tratados como texto
    private static string EscaparLike(string valor) =>
        valor.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}

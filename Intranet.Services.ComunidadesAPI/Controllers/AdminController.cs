using System.Globalization;
using System.Text.Json;
using BuildingBlocks.Shared.Security;
using Intranet.Services.ComunidadesAPI.Data;
using Intranet.Services.ComunidadesAPI.Models.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Intranet.Services.ComunidadesAPI.Controllers;

/// <summary>Arquivo (imagem) de grupo listado no painel de gestão. Nunca contém os bytes.</summary>
public class AdminArquivoGrupoDto
{
    /// <summary>"post-{postId}" (imagem da publicação) ou "capa-{groupId}" (capa do grupo).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>"post" ou "capa".</summary>
    public string Tipo { get; set; } = string.Empty;
    public int GrupoId { get; set; }
    public string GrupoNome { get; set; } = string.Empty;
    public string Nome { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long Tamanho { get; set; }

    /// <summary>Autor da publicação; null para capas (quem trocou a capa não é registrado).</summary>
    public Guid? EnviadoPorMemberId { get; set; }
    public DateTime CriadoEm { get; set; }
}

/// <summary>
/// Painel de gestão (superAdmin): imagens dos grupos (publicações e capas) e métricas de uso.
/// </summary>
[Route("api/admin")]
[ApiController]
[Authorize(Policy = AdminPolicies.SuperAdmin)]
public class AdminController : ControllerBase
{
    private const int TamanhoPaginaPadrao = 20;
    private const int TamanhoPaginaMaximo = 100;
    private const string TipoPost = "post";
    private const string TipoCapa = "capa";

    private readonly AppDbContext _db;
    private readonly ILogger<AdminController> _logger;

    public AdminController(AppDbContext db, ILogger<AdminController> logger)
    {
        _db = db;
        _logger = logger;
    }

    private static ResponseDto Falha(string message) => new() { IsSuccess = false, Message = message };

    // ---------------------------------------------------------------- arquivos

    /// <summary>
    /// Imagens das publicações e capas dos grupos (somente metadados), das mais recentes para as mais antigas.
    /// tipo: "post", "capa" ou vazio (ambos).
    /// </summary>
    [HttpGet("arquivos")]
    public async Task<ActionResult> Arquivos(
        [FromQuery] string? tipo,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = TamanhoPaginaPadrao,
        CancellationToken ct = default)
    {
        pageNumber = Math.Max(pageNumber, 1);
        pageSize = pageSize < 1 ? TamanhoPaginaPadrao : Math.Min(pageSize, TamanhoPaginaMaximo);

        var filtro = tipo?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(filtro)) filtro = null;
        if (filtro is not null and not TipoPost and not TipoCapa)
            return BadRequest(Falha("Tipo inválido. Use \"post\" ou \"capa\"."));

        var incluirPosts = filtro is null or TipoPost;
        var incluirCapas = filtro is null or TipoCapa;

        // Duas tabelas: cada uma devolve no máximo as primeiras (página × tamanho) linhas,
        // a junção e o recorte da página são feitos aqui. Tamanho = length(bytea), sem ler a imagem.
        var limite = pageNumber * pageSize;
        var total = 0;
        var linhas = new List<AdminArquivoGrupoDto>();

        if (incluirPosts)
        {
            var posts = _db.groupPostImages.AsNoTracking();
            total += await posts.CountAsync(ct);
            var itens = await posts
                .OrderByDescending(i => i.Post.CreatedAt)
                .ThenByDescending(i => i.GroupPostId)
                .Take(limite)
                .Select(i => new
                {
                    i.GroupPostId,
                    i.Post.GroupId,
                    GrupoNome = i.Post.Group.Name,
                    i.ContentType,
                    Tamanho = (long)i.Data.Length,
                    i.Post.MemberId,
                    i.Post.CreatedAt,
                })
                .ToListAsync(ct);

            linhas.AddRange(itens.Select(i => new AdminArquivoGrupoDto
            {
                Id = $"{TipoPost}-{i.GroupPostId}",
                Tipo = TipoPost,
                GrupoId = i.GroupId,
                GrupoNome = i.GrupoNome,
                Nome = $"publicacao-{i.GroupPostId}{Extensao(i.ContentType)}",
                ContentType = i.ContentType,
                Tamanho = i.Tamanho,
                EnviadoPorMemberId = i.MemberId,
                CriadoEm = i.CreatedAt,
            }));
        }

        if (incluirCapas)
        {
            var capas = _db.groupCovers.AsNoTracking();
            total += await capas.CountAsync(ct);
            var itens = await capas
                .OrderByDescending(c => c.UpdatedAt)
                .ThenByDescending(c => c.GroupId)
                .Take(limite)
                .Select(c => new
                {
                    c.GroupId,
                    GrupoNome = c.Group.Name,
                    c.ContentType,
                    Tamanho = (long)c.Data.Length,
                    c.UpdatedAt,
                })
                .ToListAsync(ct);

            linhas.AddRange(itens.Select(c => new AdminArquivoGrupoDto
            {
                Id = $"{TipoCapa}-{c.GroupId}",
                Tipo = TipoCapa,
                GrupoId = c.GroupId,
                GrupoNome = c.GrupoNome,
                Nome = $"capa-grupo-{c.GroupId}{Extensao(c.ContentType)}",
                ContentType = c.ContentType,
                Tamanho = c.Tamanho,
                EnviadoPorMemberId = null,
                CriadoEm = c.UpdatedAt,
            }));
        }

        var pagina = linhas
            .OrderByDescending(a => a.CriadoEm)
            .ThenBy(a => a.Id, StringComparer.Ordinal)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        EscreverPaginacao(total, pageNumber, pageSize);
        return Ok(new ResponseDto { Result = pagina });
    }

    /// <summary>Bytes da imagem (pré-visualização no painel).</summary>
    [HttpGet("arquivos/{id}/conteudo")]
    public async Task<ActionResult> Conteudo(string id, CancellationToken ct)
    {
        if (!TentarLerId(id, out var tipo, out var chave)) return NotFound(Falha("Arquivo não encontrado."));

        var imagem = tipo == TipoPost
            ? await _db.groupPostImages.AsNoTracking()
                .Where(i => i.GroupPostId == chave)
                .Select(i => new { i.Data, i.ContentType })
                .FirstOrDefaultAsync(ct)
            : await _db.groupCovers.AsNoTracking()
                .Where(c => c.GroupId == chave)
                .Select(c => new { c.Data, c.ContentType })
                .FirstOrDefaultAsync(ct);
        if (imagem is null) return NotFound(Falha("Arquivo não encontrado."));

        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers.CacheControl = "private, no-store";
        return File(imagem.Data, imagem.ContentType);
    }

    /// <summary>
    /// Remove a imagem da publicação ou a capa do grupo. Publicação sem texto (só a imagem)
    /// é excluída inteira, com curtidas e comentários, para não ficar vazia no feed.
    /// </summary>
    [HttpDelete("arquivos/{id}")]
    public async Task<ActionResult> Remover(string id, CancellationToken ct)
    {
        if (!TentarLerId(id, out var tipo, out var chave)) return NotFound(Falha("Arquivo não encontrado."));

        string mensagem;
        if (tipo == TipoPost)
        {
            var post = await _db.groupPosts.AsNoTracking()
                .Where(p => p.GroupPostId == chave && p.Image != null)
                .Select(p => new { p.Text })
                .FirstOrDefaultAsync(ct);
            if (post is null) return NotFound(Falha("Arquivo não encontrado."));

            if (string.IsNullOrWhiteSpace(post.Text))
            {
                await _db.groupPosts.Where(p => p.GroupPostId == chave).ExecuteDeleteAsync(ct);
                mensagem = "Imagem removida. A publicação não tinha texto e também foi excluída.";
            }
            else
            {
                await _db.groupPostImages.Where(i => i.GroupPostId == chave).ExecuteDeleteAsync(ct);
                mensagem = "Imagem da publicação removida.";
            }
        }
        else
        {
            var removidas = await _db.groupCovers.Where(c => c.GroupId == chave).ExecuteDeleteAsync(ct);
            if (removidas == 0) return NotFound(Falha("Arquivo não encontrado."));

            await _db.groups.Where(g => g.GroupId == chave)
                .ExecuteUpdateAsync(s => s.SetProperty(g => g.CoverUpdatedAt, (DateTime?)null), ct);
            mensagem = "Capa do grupo removida.";
        }

        _logger.LogInformation("Arquivo de grupo {ArquivoId} removido pela administração (usuário {UserId})", id, User.GetUserId());
        return Ok(new ResponseDto { Message = mensagem });
    }

    // ---------------------------------------------------------------- métricas

    /// <summary>Total de grupos e participações; publicações e comentários em grupos por dia (UTC).</summary>
    [HttpGet("metricas")]
    public async Task<ActionResult> Metricas([FromQuery] int dias = 30, CancellationToken ct = default)
    {
        dias = Math.Clamp(dias, 1, 365);
        var inicio = DateTime.UtcNow.Date.AddDays(-(dias - 1));
        var inicioUtc = DateTime.SpecifyKind(inicio, DateTimeKind.Utc);

        var totalGrupos = await _db.groups.AsNoTracking().CountAsync(ct);
        var totalParticipacoes = await _db.groupMembers.AsNoTracking().CountAsync(ct);

        // Só as datas (sem conteúdo); o agrupamento por dia UTC é feito aqui
        var posts = await _db.groupPosts.AsNoTracking()
            .Where(p => p.CreatedAt >= inicioUtc)
            .Select(p => p.CreatedAt)
            .ToListAsync(ct);
        var comentarios = await _db.groupPostComments.AsNoTracking()
            .Where(c => c.CreatedAt >= inicioUtc)
            .Select(c => c.CreatedAt)
            .ToListAsync(ct);

        var postsPorDia = ContarPorDia(posts);
        var comentariosPorDia = ContarPorDia(comentarios);

        var porDia = Enumerable.Range(0, dias)
            .Select(i => inicio.AddDays(i))
            .Select(d => new
            {
                Dia = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                PostsGrupo = postsPorDia.GetValueOrDefault(d),
                ComentariosGrupo = comentariosPorDia.GetValueOrDefault(d),
            })
            .ToList();

        return Ok(new ResponseDto
        {
            Result = new
            {
                TotalGrupos = totalGrupos,
                TotalParticipacoes = totalParticipacoes,
                PorDia = porDia,
            },
        });
    }

    // ---------------------------------------------------------------- apoio

    private static Dictionary<DateTime, int> ContarPorDia(IEnumerable<DateTime> datas) =>
        datas
            .Select(d => (d.Kind == DateTimeKind.Local ? d.ToUniversalTime() : d).Date)
            .GroupBy(d => d)
            .ToDictionary(g => g.Key, g => g.Count());

    /// <summary>Lê "post-{id}" ou "capa-{id}".</summary>
    private static bool TentarLerId(string? id, out string tipo, out int chave)
    {
        tipo = string.Empty;
        chave = 0;
        if (string.IsNullOrWhiteSpace(id)) return false;

        var partes = id.Trim().Split('-', 2);
        if (partes.Length != 2) return false;

        tipo = partes[0].ToLowerInvariant();
        return tipo is TipoPost or TipoCapa
               && int.TryParse(partes[1], NumberStyles.None, CultureInfo.InvariantCulture, out chave)
               && chave > 0;
    }

    private static string Extensao(string? contentType) => contentType?.ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/png" => ".png",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        _ => string.Empty,
    };

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

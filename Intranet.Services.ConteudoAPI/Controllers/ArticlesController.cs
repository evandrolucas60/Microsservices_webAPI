using System.Text.Json;
using System.Text.RegularExpressions;
using Intranet.Services.ConteudoAPI.Data;
using Intranet.Services.ConteudoAPI.Models;
using Intranet.Services.ConteudoAPI.Models.DTOs;
using Intranet.Services.ConteudoAPI.Services;
using BuildingBlocks.Contracts.Administracao;
using BuildingBlocks.Shared.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Intranet.Services.ConteudoAPI.Controllers;

/// <summary>
/// Seção Artigos: artigos, vídeos e informativos publicados pelos colaboradores,
/// com comentários e curtidas. Cada artigo novo dispara a newsletter (NotificacaoAPI).
/// A autoria é sempre a do token (claim "id"); edição/exclusão só pelo autor.
/// </summary>
[Route("api/articles")]
[ApiController]
[Authorize]
public class ArticlesController : ContentControllerBase
{
    private const int MaxPageSize = 50;
    private static readonly string[] VideoHosts =
    {
        "youtube.com", "www.youtube.com", "m.youtube.com", "youtu.be",
        "vimeo.com", "www.vimeo.com", "player.vimeo.com",
    };

    private readonly AppDbContext _db;
    private readonly INewsletterClient _newsletter;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ArticlesController> _logger;

    public ArticlesController(AppDbContext db, INewsletterClient newsletter, IMemberResolver members, INotificationClient notifications, IConfiguration configuration, ILogger<ArticlesController> logger)
        : base(members, notifications)
    {
        _db = db;
        _newsletter = newsletter;
        _configuration = configuration;
        _logger = logger;
    }

    // ---------------------------------------------------------------- Artigos

    /// <summary>Lista paginada (mais recentes primeiro). Filtros: type, search. Leitura pública.</summary>
    [AllowAnonymous]
    [HttpGet]
    public async Task<ActionResult> List(
        [FromQuery] string? type,
        [FromQuery] string? search,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 12)
    {
        pageNumber = Math.Max(pageNumber, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = _db.articles.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(type))
        {
            if (!ArticleTypes.IsValid(type)) return BadRequest(Fail("Tipo inválido."));
            query = query.Where(a => a.Type == type);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var termo = $"%{search.Trim()}%";
            query = query.Where(a => EF.Functions.ILike(a.Title, termo) || EF.Functions.ILike(a.Summary ?? "", termo));
        }

        var total = await query.CountAsync();
        var totalPages = (int)Math.Ceiling(total / (double)pageSize);

        var items = await query
            .OrderByDescending(a => a.CreatedAt)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new ArticleListItemDto
            {
                ArticleId = a.ArticleId,
                Type = a.Type,
                Title = a.Title,
                Summary = a.Summary,
                CoverImageUrl = a.CoverImageUrl,
                VideoUrl = a.VideoUrl,
                MemberId = a.MemberId,
                CreatedAt = a.CreatedAt,
                TotalLikes = a.Likes.Count,
                TotalComments = a.Comments.Count,
            })
            .ToListAsync();

        Response.Headers["X-Pagination"] = JsonSerializer.Serialize(new
        {
            TotalCount = total,
            PageSize = pageSize,
            CurrentPage = pageNumber,
            TotalPages = totalPages,
            HasNext = pageNumber < totalPages,
            HasPrevious = pageNumber > 1,
        });

        return Ok(new ResponseDto { Result = items });
    }

    /// <summary>Leitura pública; sem token, likedByMe e isAuthor vêm como false.</summary>
    [AllowAnonymous]
    [HttpGet("{id:int}")]
    public async Task<ActionResult> Get(int id)
    {
        var userId = CurrentUserId;

        var dto = await _db.articles.AsNoTracking()
            .Where(a => a.ArticleId == id)
            .Select(a => new ArticleDetailDto
            {
                ArticleId = a.ArticleId,
                Type = a.Type,
                Title = a.Title,
                Summary = a.Summary,
                Content = a.Content,
                CoverImageUrl = a.CoverImageUrl,
                VideoUrl = a.VideoUrl,
                MemberId = a.MemberId,
                CreatedAt = a.CreatedAt,
                UpdatedAt = a.UpdatedAt,
                TotalLikes = a.Likes.Count,
                TotalComments = a.Comments.Count,
                LikedByMe = userId != null && a.Likes.Any(l => l.UserId == userId),
                IsAuthor = userId != null && a.AuthorUserId == userId,
            })
            .FirstOrDefaultAsync();

        if (dto is null) return NotFound(Fail("Artigo não encontrado."));
        return Ok(new ResponseDto { Result = dto });
    }

    /// <summary>Publica um artigo e solicita o envio da newsletter.</summary>
    [HttpPost]
    [RequerServico(ServicosRestringiveis.Artigos)]
    public async Task<ActionResult> Create([FromBody] ArticleUpsertDto dto)
    {
        var userId = CurrentUserId;
        if (userId is null) return Unauthorized();

        var error = Validate(dto);
        if (error is not null) return BadRequest(Fail(error));

        // Autoria exibida vem do token, não do MemberId enviado pelo cliente
        var memberId = await CurrentMemberIdAsync();
        if (memberId is null) return BadRequest(Fail(PerfilNaoEncontrado));

        var article = new Article
        {
            AuthorUserId = userId,
            MemberId = memberId.Value,
            CreatedAt = DateTime.UtcNow,
        };
        Apply(dto, article);

        _db.articles.Add(article);
        await _db.SaveChangesAsync();

        // Newsletter: o NotificacaoAPI busca os destinatários e envia em segundo plano
        var queued = await _newsletter.QueueArticleAsync(new ArticleNewsletterRequest
        {
            ArticleId = article.ArticleId,
            Type = article.Type,
            Title = article.Title,
            Summary = article.Summary,
            CoverImageUrl = article.CoverImageUrl,
            AuthorMemberId = article.MemberId,
        }, BearerToken, HttpContext.RequestAborted);

        if (queued)
        {
            article.NewsletterQueuedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        _logger.LogInformation("Artigo {ArticleId} publicado por {UserId} (newsletter: {Queued})",
            article.ArticleId, userId, queued);

        return Ok(new ResponseDto
        {
            Result = new { article.ArticleId, NewsletterQueued = queued },
            Message = queued
                ? "Artigo publicado. A newsletter será enviada aos colaboradores."
                : "Artigo publicado, mas não foi possível agendar a newsletter.",
        });
    }

    [HttpPut("{id:int}")]
    [RequerServico(ServicosRestringiveis.Artigos)]
    public async Task<ActionResult> Update(int id, [FromBody] ArticleUpsertDto dto)
    {
        var article = await _db.articles.FirstOrDefaultAsync(a => a.ArticleId == id);
        if (article is null) return NotFound(Fail("Artigo não encontrado."));
        if (article.AuthorUserId != CurrentUserId) return Forbid();

        var error = Validate(dto);
        if (error is not null) return BadRequest(Fail(error));

        Apply(dto, article);
        article.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new ResponseDto { Result = new { article.ArticleId } });
    }

    [HttpDelete("{id:int}")]
    public async Task<ActionResult> Delete(int id)
    {
        var article = await _db.articles.FirstOrDefaultAsync(a => a.ArticleId == id);
        if (article is null) return NotFound(Fail("Artigo não encontrado."));
        if (article.AuthorUserId != CurrentUserId) return Forbid();

        _db.articles.Remove(article); // comentários e curtidas em cascata
        await _db.SaveChangesAsync();
        return Ok(new ResponseDto());
    }

    // ------------------------------------------------------------- Curtidas

    /// <summary>Curte/descurte o artigo para o usuário do token.</summary>
    [HttpPost("{id:int}/like")]
    [RequerServico(ServicosRestringiveis.Curtidas)]
    public async Task<ActionResult> ToggleLike(int id)
    {
        var userId = CurrentUserId;
        if (userId is null) return Unauthorized();
        var article = await _db.articles.AsNoTracking()
            .Where(a => a.ArticleId == id)
            .Select(a => new { a.MemberId, a.Title })
            .FirstOrDefaultAsync();
        if (article is null) return NotFound(Fail("Artigo não encontrado."));

        var like = await _db.articleLikes.FirstOrDefaultAsync(l => l.ArticleId == id && l.UserId == userId);
        var liked = like is null;
        if (liked)
        {
            _db.articleLikes.Add(new ArticleLike { ArticleId = id, UserId = userId, CreatedAt = DateTime.UtcNow });
        }
        else
        {
            _db.articleLikes.Remove(like!);
        }

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Duplo clique simultâneo: o índice único já garante uma curtida; segue com o estado atual
            liked = false;
        }

        var result = new ArticleLikeResultDto
        {
            TotalLikes = await _db.articleLikes.CountAsync(l => l.ArticleId == id),
            LikedByMe = await _db.articleLikes.AnyAsync(l => l.ArticleId == id && l.UserId == userId),
        };

        if (liked)
        {
            var memberId = await CurrentMemberIdAsync();
            if (memberId is not null)
            {
                await NotifyAuthorAsync(article.MemberId, memberId.Value,
                    name => $"{name} curtiu seu artigo \"{article.Title}\".", "article", $"/artigos/{id}");
            }
        }

        return Ok(new ResponseDto { Result = result });
    }

    // ----------------------------------------------------------- Comentários

    /// <summary>Comentários do artigo (leitura pública; sem token, isAuthor vem como false).</summary>
    [AllowAnonymous]
    [HttpGet("{id:int}/comments")]
    public async Task<ActionResult> ListComments(int id, [FromQuery] int pageNumber = 1, [FromQuery] int pageSize = 20)
    {
        pageNumber = Math.Max(pageNumber, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var userId = CurrentUserId;

        var query = _db.articleComments.AsNoTracking().Where(c => c.ArticleId == id);
        var total = await query.CountAsync();
        var totalPages = (int)Math.Ceiling(total / (double)pageSize);

        var items = await query
            // Mais recentes primeiro
            .OrderByDescending(c => c.CreatedAt)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new ArticleCommentDto
            {
                ArticleCommentId = c.ArticleCommentId,
                ArticleId = c.ArticleId,
                MemberId = c.MemberId,
                Text = c.Text,
                CreatedAt = c.CreatedAt,
                IsAuthor = userId != null && c.UserId == userId,
            })
            .ToListAsync();

        Response.Headers["X-Pagination"] = JsonSerializer.Serialize(new
        {
            TotalCount = total,
            PageSize = pageSize,
            CurrentPage = pageNumber,
            TotalPages = totalPages,
            HasNext = pageNumber < totalPages,
            HasPrevious = pageNumber > 1,
        });

        return Ok(new ResponseDto { Result = items });
    }

    [HttpPost("{id:int}/comments")]
    [RequerServico(ServicosRestringiveis.Comentarios)]
    public async Task<ActionResult> AddComment(int id, [FromBody] ArticleCommentCreateDto dto)
    {
        var userId = CurrentUserId;
        if (userId is null) return Unauthorized();

        var text = dto.Text?.Trim() ?? string.Empty;
        if (text.Length == 0) return BadRequest(Fail("O comentário não pode ficar vazio."));
        if (text.Length > 2000) return BadRequest(Fail("O comentário deve ter no máximo 2000 caracteres."));
        var article = await _db.articles.AsNoTracking()
            .Where(a => a.ArticleId == id)
            .Select(a => new { a.MemberId, a.Title })
            .FirstOrDefaultAsync();
        if (article is null) return NotFound(Fail("Artigo não encontrado."));

        var memberId = await CurrentMemberIdAsync();
        if (memberId is null) return BadRequest(Fail(PerfilNaoEncontrado));

        var comment = new ArticleComment
        {
            ArticleId = id,
            MemberId = memberId.Value,
            UserId = userId,
            Text = text,
            CreatedAt = DateTime.UtcNow,
        };
        _db.articleComments.Add(comment);
        await _db.SaveChangesAsync();

        await NotifyAuthorAsync(article.MemberId, memberId.Value,
            name => $"{name} comentou seu artigo \"{article.Title}\".", "article", $"/artigos/{id}");

        return Ok(new ResponseDto
        {
            Result = new ArticleCommentDto
            {
                ArticleCommentId = comment.ArticleCommentId,
                ArticleId = id,
                MemberId = comment.MemberId,
                Text = comment.Text,
                CreatedAt = comment.CreatedAt,
                IsAuthor = true,
            }
        });
    }

    [HttpDelete("{id:int}/comments/{commentId:int}")]
    public async Task<ActionResult> DeleteComment(int id, int commentId)
    {
        var comment = await _db.articleComments.FirstOrDefaultAsync(c => c.ArticleId == id && c.ArticleCommentId == commentId);
        if (comment is null) return NotFound(Fail("Comentário não encontrado."));
        if (comment.UserId != CurrentUserId) return Forbid();

        _db.articleComments.Remove(comment);
        await _db.SaveChangesAsync();
        return Ok(new ResponseDto());
    }

    // -------------------------------------------------------------- Imagens

    /// <summary>
    /// Envia uma imagem do computador (JPG, PNG, GIF ou WebP, até 5 MB) e devolve a URL pública
    /// para usar como capa. A URL é absoluta porque também é usada na newsletter por e-mail.
    /// </summary>
    [HttpPost("images")]
    [RequerServico(ServicosRestringiveis.Artigos)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(ImageFiles.MaxBytes + 64 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = ImageFiles.MaxBytes + 64 * 1024)]
    public async Task<ActionResult> UploadImage(IFormFile? file)
    {
        var userId = CurrentUserId;
        if (userId is null) return Unauthorized();

        var image = await ImageFiles.ReadAsync(file, HttpContext.RequestAborted);
        if (image.Error is not null) return BadRequest(Fail(image.Error));

        var entity = new ArticleImage
        {
            ArticleImageId = Guid.NewGuid(),
            FileName = image.FileName,
            ContentType = image.ContentType!,
            Size = image.Content!.LongLength,
            Content = image.Content,
            UploadedByUserId = userId,
            CreatedAt = DateTime.UtcNow,
        };
        _db.articleImages.Add(entity);
        await _db.SaveChangesAsync();

        // Endereço público do serviço: PublicBaseUrl ou o pedido (atrás do gateway, X-Forwarded-* → gateway + /conteudo)
        var baseUrl = (_configuration["PublicBaseUrl"] ?? $"{Request.Scheme}://{Request.Host}{Request.PathBase}").TrimEnd('/');
        return Ok(new ResponseDto { Result = new { url = $"{baseUrl}/api/articles/images/{entity.ArticleImageId}" } });
    }

    /// <summary>Imagem enviada pelo autor (pública: aparece no portal e nos e-mails da newsletter).</summary>
    [AllowAnonymous]
    [HttpGet("images/{imageId:guid}")]
    public async Task<ActionResult> GetImage(Guid imageId)
    {
        var image = await _db.articleImages.AsNoTracking()
            .Where(i => i.ArticleImageId == imageId)
            .Select(i => new { i.Content, i.ContentType })
            .FirstOrDefaultAsync();
        if (image is null) return NotFound();

        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        return File(image.Content, image.ContentType);
    }

    // -------------------------------------------------------------- Helpers

    private static void Apply(ArticleUpsertDto dto, Article article)
    {
        article.Type = dto.Type;
        article.Title = dto.Title.Trim();
        article.Summary = string.IsNullOrWhiteSpace(dto.Summary) ? null : dto.Summary.Trim();
        article.Content = string.IsNullOrWhiteSpace(dto.Content) ? null : dto.Content;
        article.VideoUrl = string.IsNullOrWhiteSpace(dto.VideoUrl) ? null : dto.VideoUrl.Trim();
        article.CoverImageUrl = string.IsNullOrWhiteSpace(dto.CoverImageUrl) ? null : dto.CoverImageUrl.Trim();
        // MemberId (autoria) não é alterado pelo cliente: é definido na criação a partir do token
    }

    private static string? Validate(ArticleUpsertDto dto)
    {
        if (!ArticleTypes.IsValid(dto.Type)) return "Tipo inválido. Use artigo, video ou informativo.";
        if (string.IsNullOrWhiteSpace(dto.Title)) return "Informe o título.";
        if (dto.Title.Contains('\r') || dto.Title.Contains('\n')) return "O título não pode ter quebras de linha.";
        if (dto.Title.Trim().Length > 200) return "O título deve ter no máximo 200 caracteres.";
        if (dto.Summary?.Trim().Length > 500) return "O resumo deve ter no máximo 500 caracteres.";

        if (!string.IsNullOrWhiteSpace(dto.CoverImageUrl) && !IsHttpUrl(dto.CoverImageUrl, out _))
            return "A imagem de capa deve ser um endereço http(s) válido.";

        if (dto.Type == ArticleTypes.Video)
        {
            if (string.IsNullOrWhiteSpace(dto.VideoUrl)) return "Informe o link do vídeo.";
            if (!IsAllowedVideoUrl(dto.VideoUrl)) return "Use um link do YouTube, do Vimeo ou de um arquivo .mp4/.webm (https).";
        }
        else if (!HasText(dto.Content))
        {
            return "Escreva o conteúdo do artigo.";
        }
        else if (!string.IsNullOrWhiteSpace(dto.VideoUrl) && !IsAllowedVideoUrl(dto.VideoUrl))
        {
            return "Use um link do YouTube, do Vimeo ou de um arquivo .mp4/.webm (https).";
        }

        return null;
    }

    private static bool HasText(string? html) =>
        !string.IsNullOrWhiteSpace(html) &&
        !string.IsNullOrWhiteSpace(Regex.Replace(html, "<[^>]*>|&nbsp;", " "));

    private static bool IsHttpUrl(string value, out Uri? uri) =>
        Uri.TryCreate(value.Trim(), UriKind.Absolute, out uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private static bool IsAllowedVideoUrl(string value)
    {
        if (!IsHttpUrl(value, out var uri) || uri!.Scheme != Uri.UriSchemeHttps) return false;
        if (VideoHosts.Contains(uri.Host.ToLowerInvariant())) return true;
        var path = uri.AbsolutePath.ToLowerInvariant();
        return path.EndsWith(".mp4") || path.EndsWith(".webm");
    }
}

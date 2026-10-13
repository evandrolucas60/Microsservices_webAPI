using Intranet.Services.ComunidadesAPI.Data;
using Intranet.Services.ComunidadesAPI.Models;
using Intranet.Services.ComunidadesAPI.Models.DTOs;
using Intranet.Services.ComunidadesAPI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Intranet.Services.ComunidadesAPI.Controllers;

/// <summary>
/// Publicações do grupo (texto simples + imagem opcional), curtidas e comentários.
/// Tudo aqui é visível apenas para membros do grupo (e Admin global, para moderação).
/// Autor, curtidas e comentários vêm sempre do membro do token. O autor apaga o que é seu;
/// dono, admins do grupo e Admin global apagam qualquer post/comentário.
/// </summary>
[Route("api/groups/{groupId:int}/posts")]
[ApiController]
[Authorize]
public class GroupPostsController : GroupControllerBase
{
    private const int PostMaxLength = 5000;
    private const int CommentMaxLength = 2000;

    public GroupPostsController(AppDbContext db, IdentidadeClient identidade, NotificationClient notifications, ILogger<GroupPostsController> logger)
        : base(db, identidade, notifications, logger)
    {
    }

    /// <summary>Normaliza quebras de linha e remove espaços nas pontas (o texto é exibido como texto simples).</summary>
    private static string CleanText(string? text) =>
        (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    // ------------------------------------------------------------------ Posts

    /// <summary>Feed do grupo, mais recentes primeiro (sem os bytes das imagens).</summary>
    [HttpGet]
    public async Task<ActionResult> List(int groupId, [FromQuery] int pageNumber = 1, [FromQuery] int pageSize = 10, CancellationToken ct = default)
    {
        var access = await GetAccessAsync(groupId, ct);
        if (access is null) return NotFound(Fail(GrupoNaoEncontrado));
        if (!access.CanView) return Forbidden(SomenteMembros);

        pageNumber = Math.Max(pageNumber, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var me = access.MemberId ?? Guid.Empty;

        var query = _db.groupPosts.AsNoTracking().Where(p => p.GroupId == groupId);
        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(p => p.CreatedAt)
            .ThenByDescending(p => p.GroupPostId)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new GroupPostDto
            {
                GroupPostId = p.GroupPostId,
                GroupId = p.GroupId,
                MemberId = p.MemberId,
                Text = p.Text,
                CreatedAt = p.CreatedAt,
                HasImage = p.Image != null,
                TotalLikes = p.Likes.Count,
                TotalComments = p.Comments.Count,
                LikedByMe = p.Likes.Any(l => l.MemberId == me),
            })
            .ToListAsync(ct);

        items.ForEach(p => p.CanDelete = access.CanManage || (access.MemberId.HasValue && p.MemberId == access.MemberId.Value));
        WritePagination(total, pageNumber, pageSize);
        return Ok(new ResponseDto { Result = items });
    }

    /// <summary>Nova publicação (multipart/form-data: "text" e "image" opcional). Avisa os demais membros.</summary>
    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(ImageValidator.MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = ImageValidator.MaxRequestBytes)]
    public async Task<ActionResult> Create(int groupId, [FromForm] GroupPostCreateDto dto, CancellationToken ct)
    {
        var access = await GetAccessAsync(groupId, ct);
        if (access is null) return NotFound(Fail(GrupoNaoEncontrado));
        if (access.MemberId is null) return BadRequest(Fail(PerfilNaoEncontrado));
        if (!access.IsMember) return Forbidden("Apenas membros do grupo podem publicar.");

        var text = CleanText(dto.Text);
        if (text.Length > PostMaxLength) return BadRequest(Fail($"A publicação deve ter no máximo {PostMaxLength} caracteres."));

        ValidatedImage? image = null;
        if (dto.Image is not null)
        {
            var (validated, error) = await ImageValidator.ReadAsync(dto.Image, ct);
            if (validated is null) return BadRequest(Fail(error!));
            image = validated;
        }
        if (text.Length == 0 && image is null) return BadRequest(Fail("Escreva algo ou anexe uma imagem."));

        var post = new GroupPost { GroupId = groupId, MemberId = access.MemberId.Value, Text = text, CreatedAt = DateTime.UtcNow };
        if (image is not null) post.Image = new GroupPostImage { Data = image.Data, ContentType = image.ContentType };

        _db.groupPosts.Add(post);
        await _db.SaveChangesAsync(ct);

        var recipients = await GroupMemberIdsAsync(groupId, access.MemberId, ct);
        var name = await MemberNameAsync(access.MemberId.Value, ct);
        await _notifications.NotifyAsync(
            recipients,
            $"{name ?? "Um colaborador"} publicou no grupo {access.GroupName}.",
            $"/grupos/{groupId}",
            BearerToken,
            ct);

        return Ok(new ResponseDto
        {
            Result = new GroupPostDto
            {
                GroupPostId = post.GroupPostId,
                GroupId = groupId,
                MemberId = post.MemberId,
                Text = post.Text,
                CreatedAt = post.CreatedAt,
                HasImage = image is not null,
                CanDelete = true,
            },
        });
    }

    /// <summary>Exclui a publicação (com imagem, curtidas e comentários): autor, dono, admins ou Admin global.</summary>
    [HttpDelete("{postId:int}")]
    public async Task<ActionResult> Delete(int groupId, int postId, CancellationToken ct)
    {
        var access = await GetAccessAsync(groupId, ct);
        if (access is null) return NotFound(Fail(GrupoNaoEncontrado));
        if (!access.CanView) return Forbidden(SomenteMembros);

        var authorId = await _db.groupPosts.AsNoTracking()
            .Where(p => p.GroupPostId == postId && p.GroupId == groupId)
            .Select(p => (Guid?)p.MemberId)
            .FirstOrDefaultAsync(ct);
        if (authorId is null) return NotFound(Fail("Publicação não encontrada."));

        var isAuthor = access.MemberId.HasValue && authorId == access.MemberId.Value;
        if (!isAuthor && !access.CanManage) return Forbidden("Você só pode excluir as suas publicações.");

        await _db.groupPosts.Where(p => p.GroupPostId == postId && p.GroupId == groupId).ExecuteDeleteAsync(ct);
        return Ok(new ResponseDto());
    }

    /// <summary>Imagem da publicação: apenas para membros (o front usa AuthImage com o token).</summary>
    [HttpGet("{postId:int}/image")]
    public async Task<ActionResult> Image(int groupId, int postId, CancellationToken ct)
    {
        var access = await GetAccessAsync(groupId, ct);
        if (access is null) return NotFound(Fail(GrupoNaoEncontrado));
        if (!access.CanView) return Forbidden(SomenteMembros);

        var image = await _db.groupPostImages.AsNoTracking()
            .Where(i => i.GroupPostId == postId && i.Post.GroupId == groupId)
            .Select(i => new { i.Data, i.ContentType })
            .FirstOrDefaultAsync(ct);
        if (image is null) return NotFound(Fail("Imagem não encontrada."));

        // Conteúdo privado: só o navegador do próprio usuário pode guardar em cache
        Response.Headers.CacheControl = "private, max-age=3600";
        return ImageFile(image.Data, image.ContentType);
    }

    // --------------------------------------------------------------- Curtidas

    /// <summary>Curte/descurte (uma curtida por membro). Retorna o estado atualizado do servidor.</summary>
    [HttpPost("{postId:int}/like")]
    public async Task<ActionResult> ToggleLike(int groupId, int postId, CancellationToken ct)
    {
        var access = await GetAccessAsync(groupId, ct);
        if (access is null) return NotFound(Fail(GrupoNaoEncontrado));
        if (access.MemberId is null) return BadRequest(Fail(PerfilNaoEncontrado));
        if (!access.IsMember) return Forbidden("Apenas membros do grupo podem curtir.");
        var me = access.MemberId.Value;

        var post = await _db.groupPosts.AsNoTracking()
            .Where(p => p.GroupPostId == postId && p.GroupId == groupId)
            .Select(p => new { p.MemberId })
            .FirstOrDefaultAsync(ct);
        if (post is null) return NotFound(Fail("Publicação não encontrada."));

        var existing = await _db.groupPostLikes.FirstOrDefaultAsync(l => l.GroupPostId == postId && l.MemberId == me, ct);
        var liked = existing is null;
        if (existing is null)
        {
            _db.groupPostLikes.Add(new GroupPostLike { GroupPostId = postId, MemberId = me, CreatedAt = DateTime.UtcNow });
        }
        else
        {
            _db.groupPostLikes.Remove(existing);
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Clique duplo: o índice único já garantiu uma curtida (ou ela já foi removida)
            _db.ChangeTracker.Clear();
            liked = await _db.groupPostLikes.AnyAsync(l => l.GroupPostId == postId && l.MemberId == me, ct);
            var current = await _db.groupPostLikes.CountAsync(l => l.GroupPostId == postId, ct);
            return Ok(new ResponseDto { Result = new LikeStateDto { TotalLikes = current, LikedByMe = liked } });
        }

        var totalLikes = await _db.groupPostLikes.CountAsync(l => l.GroupPostId == postId, ct);

        if (liked && post.MemberId != me && await IsGroupMemberAsync(groupId, post.MemberId, ct))
        {
            var name = await MemberNameAsync(me, ct);
            await _notifications.NotifyAsync(
                new[] { post.MemberId },
                $"{name ?? "Um colaborador"} curtiu sua publicação no grupo {access.GroupName}.",
                $"/grupos/{groupId}",
                BearerToken,
                ct);
        }

        return Ok(new ResponseDto { Result = new LikeStateDto { TotalLikes = totalLikes, LikedByMe = liked } });
    }

    // ------------------------------------------------------------ Comentários

    /// <summary>Comentários da publicação, mais recentes primeiro.</summary>
    [HttpGet("{postId:int}/comments")]
    public async Task<ActionResult> Comments(int groupId, int postId, [FromQuery] int pageNumber = 1, [FromQuery] int pageSize = 10, CancellationToken ct = default)
    {
        var access = await GetAccessAsync(groupId, ct);
        if (access is null) return NotFound(Fail(GrupoNaoEncontrado));
        if (!access.CanView) return Forbidden(SomenteMembros);
        if (!await _db.groupPosts.AnyAsync(p => p.GroupPostId == postId && p.GroupId == groupId, ct))
            return NotFound(Fail("Publicação não encontrada."));

        pageNumber = Math.Max(pageNumber, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = _db.groupPostComments.AsNoTracking().Where(c => c.GroupPostId == postId);
        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(c => c.CreatedAt)
            .ThenByDescending(c => c.GroupPostCommentId)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new GroupCommentDto
            {
                GroupPostCommentId = c.GroupPostCommentId,
                GroupPostId = c.GroupPostId,
                MemberId = c.MemberId,
                Text = c.Text,
                CreatedAt = c.CreatedAt,
            })
            .ToListAsync(ct);

        items.ForEach(c => c.CanDelete = access.CanManage || (access.MemberId.HasValue && c.MemberId == access.MemberId.Value));
        WritePagination(total, pageNumber, pageSize);
        return Ok(new ResponseDto { Result = items });
    }

    /// <summary>Novo comentário (texto simples). Avisa o autor da publicação.</summary>
    [HttpPost("{postId:int}/comments")]
    public async Task<ActionResult> AddComment(int groupId, int postId, [FromBody] GroupCommentUpsertDto dto, CancellationToken ct)
    {
        var access = await GetAccessAsync(groupId, ct);
        if (access is null) return NotFound(Fail(GrupoNaoEncontrado));
        if (access.MemberId is null) return BadRequest(Fail(PerfilNaoEncontrado));
        if (!access.IsMember) return Forbidden("Apenas membros do grupo podem comentar.");
        var me = access.MemberId.Value;

        var text = CleanText(dto.Text);
        if (text.Length == 0) return BadRequest(Fail("Escreva o comentário."));
        if (text.Length > CommentMaxLength) return BadRequest(Fail($"O comentário deve ter no máximo {CommentMaxLength} caracteres."));

        var post = await _db.groupPosts.AsNoTracking()
            .Where(p => p.GroupPostId == postId && p.GroupId == groupId)
            .Select(p => new { p.MemberId })
            .FirstOrDefaultAsync(ct);
        if (post is null) return NotFound(Fail("Publicação não encontrada."));

        var comment = new GroupPostComment { GroupPostId = postId, MemberId = me, Text = text, CreatedAt = DateTime.UtcNow };
        _db.groupPostComments.Add(comment);
        await _db.SaveChangesAsync(ct);

        if (post.MemberId != me && await IsGroupMemberAsync(groupId, post.MemberId, ct))
        {
            var name = await MemberNameAsync(me, ct);
            await _notifications.NotifyAsync(
                new[] { post.MemberId },
                $"{name ?? "Um colaborador"} comentou sua publicação no grupo {access.GroupName}.",
                $"/grupos/{groupId}",
                BearerToken,
                ct);
        }

        return Ok(new ResponseDto
        {
            Result = new GroupCommentDto
            {
                GroupPostCommentId = comment.GroupPostCommentId,
                GroupPostId = postId,
                MemberId = me,
                Text = comment.Text,
                CreatedAt = comment.CreatedAt,
                CanDelete = true,
            },
        });
    }

    /// <summary>Exclui o comentário: autor, dono, admins ou Admin global.</summary>
    [HttpDelete("{postId:int}/comments/{commentId:int}")]
    public async Task<ActionResult> DeleteComment(int groupId, int postId, int commentId, CancellationToken ct)
    {
        var access = await GetAccessAsync(groupId, ct);
        if (access is null) return NotFound(Fail(GrupoNaoEncontrado));
        if (!access.CanView) return Forbidden(SomenteMembros);

        var authorId = await _db.groupPostComments.AsNoTracking()
            .Where(c => c.GroupPostCommentId == commentId && c.GroupPostId == postId && c.Post.GroupId == groupId)
            .Select(c => (Guid?)c.MemberId)
            .FirstOrDefaultAsync(ct);
        if (authorId is null) return NotFound(Fail("Comentário não encontrado."));

        var isAuthor = access.MemberId.HasValue && authorId == access.MemberId.Value;
        if (!isAuthor && !access.CanManage) return Forbidden("Você só pode excluir os seus comentários.");

        await _db.groupPostComments.Where(c => c.GroupPostCommentId == commentId).ExecuteDeleteAsync(ct);
        return Ok(new ResponseDto());
    }
}

using Intranet.Services.ConteudoAPI.Data;
using Intranet.Services.ConteudoAPI.Models;
using Intranet.Services.ConteudoAPI.Models.DTOs;
using Intranet.Services.ConteudoAPI.Pagination.Parameters;
using Intranet.Services.ConteudoAPI.Repository.IRepository;
using Intranet.Services.ConteudoAPI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Intranet.Services.ConteudoAPI.Controllers;

/// <summary>
/// Comentários dos posts. Autoria e curtidas vêm do token; o autor do post é notificado
/// de novos comentários e o autor do comentário, de curtidas.
/// </summary>
[Route("api/[controller]")]
[ApiController]
[Authorize]
public class CommentController : ContentControllerBase
{
    private const int MaxTextLength = 2000;

    private readonly IUnityOfWork _uof;
    private readonly AppDbContext _db;

    public CommentController(IUnityOfWork uof, AppDbContext db, IMemberResolver members, INotificationClient notifications)
        : base(members, notifications)
    {
        _uof = uof;
        _db = db;
    }

    [AllowAnonymous]
    [HttpGet("filter/date/pagination/{postId:int}")]
    public async Task<ActionResult> GetCommentsByFilterDate([FromQuery] CommentsParametersFilterData parameters, int postId)
    {
        var comments = await _uof.CommentRepository.GetByPostAsync(parameters, postId, await CurrentMemberIdAsync());

        Response.Headers["X-Pagination"] = JsonSerializer.Serialize(new
        {
            comments.TotalCount,
            comments.PageSize,
            comments.CurrentPage,
            comments.TotalPages,
            comments.HasNext,
            comments.HasPrevious,
        });

        // Antes o resultado era convertido de volta para a entidade e perdia o TotalLikes
        return Ok(new ResponseDto { Result = comments });
    }

    [HttpPost]
    public async Task<ActionResult> Post([FromBody] CommentDto commentDto)
    {
        var memberId = await CurrentMemberIdAsync();
        if (memberId is null) return BadRequest(Fail(PerfilNaoEncontrado));

        var text = commentDto.Text?.Trim() ?? string.Empty;
        if (text.Length == 0) return BadRequest(Fail("O comentário não pode ficar vazio."));
        if (text.Length > MaxTextLength) return BadRequest(Fail($"O comentário deve ter no máximo {MaxTextLength} caracteres."));

        var post = await _db.posts.AsNoTracking().FirstOrDefaultAsync(p => p.PostId == commentDto.PostId);
        if (post is null) return NotFound(Fail("Post não encontrado."));

        // Autor sempre do token (o MemberId do corpo é ignorado)
        var comment = new Comment { PostId = post.PostId, MemberId = memberId.Value, Text = text, CreatedAt = DateTime.UtcNow };
        _uof.CommentRepository.Create(comment);
        await _uof.CommitAsync();

        await NotifyAuthorAsync(post.MemberId, memberId.Value, name => $"{name} comentou seu post.", "comment", $"/post/{post.PostId}");

        return Ok(new ResponseDto
        {
            Result = new CommentDto
            {
                CommentId = comment.CommentId,
                PostId = comment.PostId,
                MemberId = comment.MemberId,
                Text = comment.Text,
                CreatedAt = comment.CreatedAt,
            }
        });
    }

    [HttpPatch("UpdatePartial/{commentId:int}")]
    public async Task<ActionResult> UpdatePartial(int commentId, [FromBody] CommentDto commentDto)
    {
        var memberId = await CurrentMemberIdAsync();
        // Antes buscava por PostId == commentId (editava o comentário errado)
        var comment = await _uof.CommentRepository.GetAsync(c => c.CommentId == commentId);
        if (comment is null) return NotFound(Fail("Comentário não encontrado."));
        if (memberId is null || comment.MemberId != memberId.Value) return Forbid();

        var text = commentDto.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return BadRequest(Fail("O comentário não pode ficar vazio."));
        if (text.Length > MaxTextLength) return BadRequest(Fail($"O comentário deve ter no máximo {MaxTextLength} caracteres."));

        comment.Text = text;
        _uof.CommentRepository.Update(comment);
        await _uof.CommitAsync();
        return Ok(new ResponseDto());
    }

    /// <summary>Curte/descurte o comentário para o colaborador do token e devolve o estado atualizado.</summary>
    [HttpPatch("ToggleLikeAsync/{commentId:int}")]
    public async Task<ActionResult> ToggleLikeAsync(int commentId)
    {
        var memberId = await CurrentMemberIdAsync();
        if (memberId is null) return BadRequest(Fail(PerfilNaoEncontrado));

        var comment = await _db.comments.AsNoTracking().FirstOrDefaultAsync(c => c.CommentId == commentId);
        if (comment is null) return NotFound(Fail("Comentário não encontrado."));

        var existing = await _db.commentLikes.FirstOrDefaultAsync(l => l.CommentId == commentId && l.MemberId == memberId.Value);
        var liked = existing is null;
        if (liked)
        {
            _db.commentLikes.Add(new CommentLike { CommentId = commentId, MemberId = memberId.Value, CreatedAt = DateTime.UtcNow });
        }
        else
        {
            _db.commentLikes.Remove(existing!);
        }

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Clique duplo simultâneo: o índice único (CommentId, MemberId) mantém uma única curtida
            liked = false;
        }

        var state = new LikeStateDto
        {
            TotalLikes = await _db.commentLikes.CountAsync(l => l.CommentId == commentId),
            LikedByMe = await _db.commentLikes.AnyAsync(l => l.CommentId == commentId && l.MemberId == memberId.Value),
        };

        if (liked)
        {
            await NotifyAuthorAsync(comment.MemberId, memberId.Value, name => $"{name} curtiu seu comentário.", "comment", $"/post/{comment.PostId}");
        }

        return Ok(new ResponseDto { Result = state });
    }

    [HttpDelete("{id:int}")]
    public async Task<ActionResult> Delete(int id)
    {
        var memberId = await CurrentMemberIdAsync();
        var comment = await _uof.CommentRepository.GetAsync(c => c.CommentId == id);
        if (comment is null) return NotFound(Fail("Comentário não encontrado."));
        if (memberId is null || comment.MemberId != memberId.Value) return Forbid();

        _uof.CommentRepository.Delete(comment);
        await _uof.CommitAsync();
        return Ok(new ResponseDto());
    }
}

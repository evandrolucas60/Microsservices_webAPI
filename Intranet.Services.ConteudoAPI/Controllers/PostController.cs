using Intranet.Services.ConteudoAPI.Data;
using Intranet.Services.ConteudoAPI.Models;
using Intranet.Services.ConteudoAPI.Models.DTOs;
using Intranet.Services.ConteudoAPI.Pagination;
using Intranet.Services.ConteudoAPI.Pagination.Parameters;
using Intranet.Services.ConteudoAPI.Repository.IRepository;
using Intranet.Services.ConteudoAPI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Intranet.Services.ConteudoAPI.Controllers;

/// <summary>
/// Posts do feed. Autoria e curtidas vêm do token; as listas trazem totalLikes,
/// totalComments e likedByMe. Edição/exclusão apenas pelo autor.
/// </summary>
[Route("api/[controller]")]
[ApiController]
[Authorize]
public class PostController : ContentControllerBase
{
    private const int MaxTextLength = 20000;

    private readonly IUnityOfWork _uof;
    private readonly AppDbContext _db;
    private readonly ILogger<PostController> _logger;

    public PostController(IUnityOfWork uof, AppDbContext db, IMemberResolver members, INotificationClient notifications, ILogger<PostController> logger)
        : base(members, notifications)
    {
        _uof = uof;
        _db = db;
        _logger = logger;
    }

    [HttpGet("{postId:int}")]
    public async Task<ActionResult> GetAsync(int postId)
    {
        var post = await _uof.PostRepository.GetDetailAsync(postId, await CurrentMemberIdAsync());
        if (post is null) return NotFound(Fail("Post não encontrado."));
        return Ok(new ResponseDto { Result = post });
    }

    [HttpGet("GetAllPostByMemberId/{memberId:Guid}")]
    public async Task<ActionResult> GetAllPostByMemberIdAsync(Guid memberId)
    {
        var posts = await _uof.PostRepository.GetAllByMemberIdWithLikesAsync(memberId, await CurrentMemberIdAsync());
        return Ok(new ResponseDto { Result = posts });
    }

    [HttpGet("filter/date/pagination")]
    public async Task<ActionResult> GetPostsByFilterDate([FromQuery] PostsParametersFilterDate parameters)
    {
        var posts = await _uof.PostRepository.GetFeedAsync(parameters, await CurrentMemberIdAsync());
        WritePagination(posts);
        return Ok(new ResponseDto { Result = posts });
    }

    [HttpPost]
    public async Task<ActionResult> Post([FromBody] PostDto postDto)
    {
        var memberId = await CurrentMemberIdAsync();
        if (memberId is null) return BadRequest(Fail(PerfilNaoEncontrado));

        var text = postDto.Text?.Trim() ?? string.Empty;
        if (text.Length == 0) return BadRequest(Fail("Escreva algo antes de publicar."));
        if (text.Length > MaxTextLength) return BadRequest(Fail("O post é longo demais."));

        // Autor sempre do token (o MemberId do corpo é ignorado)
        var post = new Post { MemberId = memberId.Value, Text = text, CreatedAt = DateTime.UtcNow };
        _uof.PostRepository.Create(post);
        await _uof.CommitAsync();

        return Ok(new ResponseDto { Result = await _uof.PostRepository.GetDetailAsync(post.PostId, memberId) });
    }

    [HttpPatch("UpdatePartial/{postId:int}")]
    public async Task<ActionResult> UpdatePartial(int postId, [FromBody] PostDto postDto)
    {
        var memberId = await CurrentMemberIdAsync();
        var post = await _uof.PostRepository.GetAsync(p => p.PostId == postId);
        if (post is null) return NotFound(Fail("Post não encontrado."));
        if (memberId is null || post.MemberId != memberId.Value) return Forbid();

        var text = postDto.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return BadRequest(Fail("O post não pode ficar vazio."));
        if (text.Length > MaxTextLength) return BadRequest(Fail("O post é longo demais."));

        post.Text = text;
        _uof.PostRepository.Update(post);
        await _uof.CommitAsync();

        return Ok(new ResponseDto { Result = await _uof.PostRepository.GetDetailAsync(postId, memberId) });
    }

    /// <summary>
    /// Compartilha o post de outro colaborador na própria timeline (com comentário opcional).
    /// Compartilhar um compartilhamento compartilha o post original; cada um compartilha um post uma vez.
    /// </summary>
    [HttpPost("{postId:int}/share")]
    public async Task<ActionResult> Share(int postId, [FromBody] SharePostDto? dto)
    {
        var memberId = await CurrentMemberIdAsync();
        if (memberId is null) return BadRequest(Fail(PerfilNaoEncontrado));

        var target = await _db.posts.AsNoTracking().FirstOrDefaultAsync(p => p.PostId == postId);
        if (target is null) return NotFound(Fail("Post não encontrado."));

        // Sempre aponta para o original (evita cadeia de compartilhamentos)
        var original = target.SharedFromPostId is null
            ? target
            : await _db.posts.AsNoTracking().FirstOrDefaultAsync(p => p.PostId == target.SharedFromPostId);
        if (original is null) return NotFound(Fail("O post original não está mais disponível."));

        if (original.MemberId == memberId.Value) return BadRequest(Fail("Você não pode compartilhar o seu próprio post."));
        if (await _db.posts.AnyAsync(p => p.MemberId == memberId.Value && p.SharedFromPostId == original.PostId))
            return BadRequest(Fail("Você já compartilhou este post."));

        var text = dto?.Text?.Trim();
        if (text?.Length > MaxTextLength) return BadRequest(Fail("O comentário é longo demais."));

        var share = new Post
        {
            MemberId = memberId.Value,
            Text = string.IsNullOrEmpty(text) ? null : text,
            CreatedAt = DateTime.UtcNow,
            SharedFromPostId = original.PostId,
            SharedFromMemberId = original.MemberId,
        };
        _uof.PostRepository.Create(share);
        await _uof.CommitAsync();

        await NotifyAuthorAsync(original.MemberId, memberId.Value, name => $"{name} compartilhou seu post.", "post", $"/post/{share.PostId}");

        return Ok(new ResponseDto { Result = await _uof.PostRepository.GetDetailAsync(share.PostId, memberId) });
    }

    /// <summary>Curte/descurte o post para o colaborador do token e devolve o estado atualizado.</summary>
    [HttpPatch("ToggleLikeAsync/{postId:int}")]
    public async Task<ActionResult> ToggleLikeAsync(int postId)
    {
        var memberId = await CurrentMemberIdAsync();
        if (memberId is null) return BadRequest(Fail(PerfilNaoEncontrado));

        var post = await _db.posts.AsNoTracking().FirstOrDefaultAsync(p => p.PostId == postId);
        if (post is null) return NotFound(Fail("Post não encontrado."));

        var existing = await _db.postLikes.FirstOrDefaultAsync(l => l.PostId == postId && l.MemberId == memberId.Value);
        var liked = existing is null;
        if (liked)
        {
            _db.postLikes.Add(new PostLike { PostId = postId, MemberId = memberId.Value, CreatedAt = DateTime.UtcNow });
        }
        else
        {
            _db.postLikes.Remove(existing!);
        }

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Clique duplo simultâneo: o índice único (PostId, MemberId) mantém uma única curtida
            liked = false;
        }

        var state = new LikeStateDto
        {
            TotalLikes = await _db.postLikes.CountAsync(l => l.PostId == postId),
            LikedByMe = await _db.postLikes.AnyAsync(l => l.PostId == postId && l.MemberId == memberId.Value),
        };

        if (liked)
        {
            await NotifyAuthorAsync(post.MemberId, memberId.Value, name => $"{name} curtiu seu post.", "post", $"/post/{postId}");
        }

        return Ok(new ResponseDto { Result = state });
    }

    [HttpDelete("{id:int}")]
    public async Task<ActionResult> Delete(int id)
    {
        var memberId = await CurrentMemberIdAsync();
        var post = await _uof.PostRepository.GetAsync(p => p.PostId == id);
        if (post is null) return NotFound(Fail("Post não encontrado."));
        if (memberId is null || post.MemberId != memberId.Value) return Forbid();

        // Comentários do post não têm FK em cascata: remove junto
        var comments = await _db.comments.Where(c => c.PostId == id).ToListAsync();
        _db.comments.RemoveRange(comments);
        _uof.PostRepository.Delete(post);
        await _uof.CommitAsync();

        return Ok(new ResponseDto());
    }

    private void WritePagination<T>(PagedList<T> page) where T : class
    {
        Response.Headers["X-Pagination"] = JsonSerializer.Serialize(new
        {
            page.TotalCount,
            page.PageSize,
            page.CurrentPage,
            page.TotalPages,
            page.HasNext,
            page.HasPrevious,
        });
    }
}

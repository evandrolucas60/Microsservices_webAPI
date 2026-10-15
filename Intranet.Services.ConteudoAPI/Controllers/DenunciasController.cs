using Intranet.Services.ConteudoAPI.Data;
using Intranet.Services.ConteudoAPI.Models;
using Intranet.Services.ConteudoAPI.Models.DTOs;
using Intranet.Services.ConteudoAPI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Intranet.Services.ConteudoAPI.Controllers;

/// <summary>
/// Denúncia de conteúdo pelos colaboradores (post, artigo ou comentário de post).
/// Uma denúncia pendente por colaborador por conteúdo; não é possível denunciar o próprio conteúdo.
/// A análise é feita pelo superAdmin (AdminConteudoController).
/// </summary>
[Route("api/denuncias")]
[ApiController]
[Authorize]
public class DenunciasController : ContentControllerBase
{
    private const int MaxDetalhes = 1000;

    private readonly AppDbContext _db;
    private readonly ILogger<DenunciasController> _logger;

    public DenunciasController(AppDbContext db, IMemberResolver members, INotificationClient notifications, ILogger<DenunciasController> logger)
        : base(members, notifications)
    {
        _db = db;
        _logger = logger;
    }

    [HttpPost]
    public async Task<ActionResult> Create([FromBody] DenunciaCreateDto dto)
    {
        var userId = CurrentUserId;
        if (userId is null) return Unauthorized();

        var tipo = dto.Tipo?.Trim().ToLowerInvariant();
        if (!DenunciaTipos.IsValid(tipo)) return BadRequest(Fail("Tipo de conteúdo inválido."));

        var motivo = dto.Motivo?.Trim().ToLowerInvariant();
        if (!DenunciaMotivos.IsValid(motivo)) return BadRequest(Fail("Escolha o motivo da denúncia."));

        var detalhes = dto.Detalhes?.Trim();
        if (detalhes?.Length > MaxDetalhes) return BadRequest(Fail($"Os detalhes devem ter no máximo {MaxDetalhes} caracteres."));

        var memberId = await CurrentMemberIdAsync();
        if (memberId is null) return BadRequest(Fail(PerfilNaoEncontrado));

        // Autor e resumo do conteúdo (guardados na denúncia)
        Guid autor;
        string? resumo;
        bool proprio;
        switch (tipo)
        {
            case DenunciaTipos.Post:
            {
                var post = await _db.posts.AsNoTracking()
                    .Where(p => p.PostId == dto.AlvoId)
                    .Select(p => new { p.MemberId, p.Text, p.SharedFromMemberId })
                    .FirstOrDefaultAsync();
                if (post is null) return NotFound(Fail("Post não encontrado."));
                autor = post.MemberId;
                resumo = ConteudoResumo.De(post.Text) ?? (post.SharedFromMemberId is null ? null : "(compartilhamento de post)");
                proprio = post.MemberId == memberId.Value;
                break;
            }
            case DenunciaTipos.Artigo:
            {
                var artigo = await _db.articles.AsNoTracking()
                    .Where(a => a.ArticleId == dto.AlvoId)
                    .Select(a => new { a.MemberId, a.AuthorUserId, a.Title })
                    .FirstOrDefaultAsync();
                if (artigo is null) return NotFound(Fail("Artigo não encontrado."));
                autor = artigo.MemberId;
                resumo = ConteudoResumo.De(artigo.Title);
                proprio = artigo.AuthorUserId == userId || artigo.MemberId == memberId.Value;
                break;
            }
            default:
            {
                var comentario = await _db.comments.AsNoTracking()
                    .Where(c => c.CommentId == dto.AlvoId)
                    .Select(c => new { c.MemberId, c.Text })
                    .FirstOrDefaultAsync();
                if (comentario is null) return NotFound(Fail("Comentário não encontrado."));
                autor = comentario.MemberId;
                resumo = ConteudoResumo.De(comentario.Text);
                proprio = comentario.MemberId == memberId.Value;
                break;
            }
        }

        if (proprio) return BadRequest(Fail("Você não pode denunciar o seu próprio conteúdo."));

        if (await _db.denuncias.AnyAsync(d => d.DenuncianteMemberId == memberId.Value && d.Tipo == tipo
                && d.AlvoId == dto.AlvoId && d.Status == DenunciaStatus.Pendente))
            return BadRequest(Fail("Você já denunciou este conteúdo"));

        var denuncia = new Denuncia
        {
            Tipo = tipo!,
            AlvoId = dto.AlvoId,
            AlvoAutorMemberId = autor,
            AlvoResumo = resumo,
            Motivo = motivo!,
            Detalhes = string.IsNullOrEmpty(detalhes) ? null : detalhes,
            DenuncianteMemberId = memberId.Value,
            Status = DenunciaStatus.Pendente,
            CriadaEm = DateTime.UtcNow,
        };
        _db.denuncias.Add(denuncia);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Envio duplicado simultâneo: o índice único filtrado mantém uma denúncia pendente
            return BadRequest(Fail("Você já denunciou este conteúdo"));
        }

        _logger.LogInformation("Denúncia {DenunciaId} criada: {Tipo} {AlvoId} ({Motivo}) por {UserId}",
            denuncia.Id, denuncia.Tipo, denuncia.AlvoId, denuncia.Motivo, userId);

        return Ok(new ResponseDto { Result = new { denuncia.Id }, Message = "Denúncia enviada. Obrigado!" });
    }
}

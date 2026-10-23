using System.Text.Json;
using BuildingBlocks.Shared.Security;
using Intranet.Services.ConteudoAPI.Data;
using Intranet.Services.ConteudoAPI.Models;
using Intranet.Services.ConteudoAPI.Models.DTOs;
using Intranet.Services.ConteudoAPI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Intranet.Services.ConteudoAPI.Controllers;

/// <summary>
/// Painel de gestão (superAdmin): denúncias de conteúdo, métricas de publicações e
/// imagens enviadas para os artigos.
/// </summary>
[Route("api/admin")]
[ApiController]
[Authorize(Policy = AdminPolicies.SuperAdmin)]
public class AdminConteudoController : ContentControllerBase
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;
    private const int MaxObservacao = 1000;
    private const int TopAutores = 10;

    private readonly AppDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AdminConteudoController> _logger;

    public AdminConteudoController(AppDbContext db, IMemberResolver members, INotificationClient notifications, IConfiguration configuration, ILogger<AdminConteudoController> logger)
        : base(members, notifications)
    {
        _db = db;
        _configuration = configuration;
        _logger = logger;
    }

    // ------------------------------------------------------------ Denúncias

    /// <summary>Denúncias (mais recentes primeiro), com filtros de status e tipo.</summary>
    [HttpGet("denuncias")]
    public async Task<ActionResult> ListDenuncias(
        [FromQuery] string? status,
        [FromQuery] string? tipo,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = DefaultPageSize)
    {
        (pageNumber, pageSize) = NormalizePage(pageNumber, pageSize);

        var query = _db.denuncias.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(status))
        {
            status = status.Trim().ToLowerInvariant();
            if (!DenunciaStatus.IsValid(status)) return BadRequest(Fail("Status inválido. Use pendente, resolvida ou descartada."));
            query = query.Where(d => d.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(tipo))
        {
            tipo = tipo.Trim().ToLowerInvariant();
            if (!DenunciaTipos.IsValid(tipo)) return BadRequest(Fail("Tipo inválido. Use post, artigo ou comentario."));
            query = query.Where(d => d.Tipo == tipo);
        }

        var total = await query.CountAsync();
        var itens = await query
            .OrderByDescending(d => d.CriadaEm)
            .ThenByDescending(d => d.Id)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        WritePagination(total, pageNumber, pageSize);
        return Ok(new ResponseDto { Result = await ToDtosAsync(itens) });
    }

    [HttpGet("denuncias/resumo")]
    public async Task<ActionResult> ResumoDenuncias()
    {
        var porStatus = await _db.denuncias.AsNoTracking()
            .GroupBy(d => d.Status)
            .Select(g => new { Status = g.Key, Total = g.Count() })
            .ToListAsync();

        int Total(string s) => porStatus.FirstOrDefault(x => x.Status == s)?.Total ?? 0;

        return Ok(new ResponseDto
        {
            Result = new DenunciasResumoDto
            {
                Pendentes = Total(DenunciaStatus.Pendente),
                Resolvidas = Total(DenunciaStatus.Resolvida),
                Descartadas = Total(DenunciaStatus.Descartada),
            }
        });
    }

    /// <summary>Descarta a denúncia (o conteúdo permanece).</summary>
    [HttpPost("denuncias/{id:int}/descartar")]
    public async Task<ActionResult> Descartar(int id, [FromBody] DenunciaAcaoDto? dto)
    {
        var observacao = dto?.Observacao?.Trim();
        if (observacao?.Length > MaxObservacao) return BadRequest(Fail($"A observação deve ter no máximo {MaxObservacao} caracteres."));

        var denuncia = await _db.denuncias.FirstOrDefaultAsync(d => d.Id == id);
        if (denuncia is null) return NotFound(Fail("Denúncia não encontrada."));
        if (denuncia.Status != DenunciaStatus.Pendente) return BadRequest(Fail("Esta denúncia já foi analisada."));

        denuncia.Status = DenunciaStatus.Descartada;
        denuncia.ResolvidaPorUserId = User.GetUserId();
        denuncia.ResolvidaEm = DateTime.UtcNow;
        denuncia.Resolucao = string.IsNullOrEmpty(observacao) ? null : observacao;
        await _db.SaveChangesAsync();

        _logger.LogInformation("Denúncia {DenunciaId} descartada por {UserId}", id, denuncia.ResolvidaPorUserId);

        return Ok(new ResponseDto { Result = (await ToDtosAsync(new List<Denuncia> { denuncia }))[0], Message = "Denúncia descartada." });
    }

    /// <summary>
    /// Remove o conteúdo denunciado (post com comentários e curtidas, artigo ou comentário),
    /// marca como resolvidas todas as denúncias pendentes desse conteúdo e notifica o autor.
    /// </summary>
    [HttpPost("denuncias/{id:int}/remover-conteudo")]
    public async Task<ActionResult> RemoverConteudo(int id, [FromBody] DenunciaAcaoDto? dto)
    {
        var observacao = dto?.Observacao?.Trim();
        if (observacao?.Length > MaxObservacao) return BadRequest(Fail($"A observação deve ter no máximo {MaxObservacao} caracteres."));

        var denuncia = await _db.denuncias.FirstOrDefaultAsync(d => d.Id == id);
        if (denuncia is null) return NotFound(Fail("Denúncia não encontrada."));

        var removido = false;
        Guid autor = Guid.Empty;
        string categoria;
        string link;
        var comentariosRemovidos = new List<int>();

        switch (denuncia.Tipo)
        {
            case DenunciaTipos.Post:
            {
                categoria = "post";
                link = "/";
                var post = await _db.posts.FirstOrDefaultAsync(p => p.PostId == denuncia.AlvoId);
                if (post is not null)
                {
                    // Comentários do post não têm FK em cascata: remove junto (as curtidas dos
                    // comentários e do post saem em cascata; compartilhamentos ficam "indisponível")
                    var comentarios = await _db.comments.Where(c => c.PostId == post.PostId).ToListAsync();
                    comentariosRemovidos = comentarios.Select(c => c.CommentId).ToList();
                    _db.comments.RemoveRange(comentarios);
                    _db.posts.Remove(post);
                    autor = post.MemberId;
                    removido = true;
                }
                break;
            }
            case DenunciaTipos.Artigo:
            {
                categoria = "article";
                link = "/artigos";
                var artigo = await _db.articles.FirstOrDefaultAsync(a => a.ArticleId == denuncia.AlvoId);
                if (artigo is not null)
                {
                    _db.articles.Remove(artigo); // comentários e curtidas em cascata
                    autor = artigo.MemberId;
                    removido = true;
                }
                break;
            }
            default:
            {
                categoria = "comment";
                link = "/";
                var comentario = await _db.comments.FirstOrDefaultAsync(c => c.CommentId == denuncia.AlvoId);
                if (comentario is not null)
                {
                    _db.comments.Remove(comentario); // curtidas em cascata
                    autor = comentario.MemberId;
                    link = $"/post/{comentario.PostId}";
                    removido = true;
                }
                break;
            }
        }

        // Todas as denúncias pendentes do conteúdo (e dos comentários removidos junto com o post)
        var tipo = denuncia.Tipo;
        var alvoId = denuncia.AlvoId;
        var pendentes = await _db.denuncias
            .Where(d => d.Status == DenunciaStatus.Pendente &&
                ((d.Tipo == tipo && d.AlvoId == alvoId) ||
                 (d.Tipo == DenunciaTipos.Comentario && comentariosRemovidos.Contains(d.AlvoId))))
            .ToListAsync();
        if (!pendentes.Contains(denuncia)) pendentes.Add(denuncia);

        var adminUserId = User.GetUserId();
        var agora = DateTime.UtcNow;
        var resolucao = string.IsNullOrEmpty(observacao) ? "Conteúdo removido pela administração." : observacao;
        foreach (var d in pendentes)
        {
            d.Status = DenunciaStatus.Resolvida;
            d.ResolvidaPorUserId = adminUserId;
            d.ResolvidaEm = agora;
            d.Resolucao = resolucao;
        }

        await _db.SaveChangesAsync();

        _logger.LogInformation("Conteúdo {Tipo} {AlvoId} removido pela administração ({UserId}); {Total} denúncia(s) resolvida(s)",
            tipo, alvoId, adminUserId, pendentes.Count);

        if (removido)
        {
            await NotificarAutorAsync(autor, denuncia, categoria, link);
        }

        return Ok(new ResponseDto
        {
            Result = (await ToDtosAsync(new List<Denuncia> { denuncia }))[0],
            Message = removido
                ? "Conteúdo removido e denúncias resolvidas."
                : "O conteúdo já havia sido removido; as denúncias foram marcadas como resolvidas.",
        });
    }

    // -------------------------------------------------------------- Métricas

    /// <summary>Publicações por dia (UTC) nos últimos N dias, totais do período e autores mais ativos.</summary>
    [HttpGet("metricas")]
    public async Task<ActionResult> Metricas([FromQuery] int dias = 30)
    {
        dias = Math.Clamp(dias, 1, 365);
        var hoje = DateTime.SpecifyKind(DateTime.UtcNow.Date, DateTimeKind.Utc);
        var inicio = hoje.AddDays(-(dias - 1));
        var ct = HttpContext.RequestAborted;

        var posts = await PorDiaAsync(_db.posts.Where(p => p.SharedFromMemberId == null && p.SharedFromPostId == null && p.CreatedAt >= inicio).Select(p => p.CreatedAt), ct);
        var compartilhamentos = await PorDiaAsync(_db.posts.Where(p => (p.SharedFromMemberId != null || p.SharedFromPostId != null) && p.CreatedAt >= inicio).Select(p => p.CreatedAt), ct);
        var artigos = await PorDiaAsync(_db.articles.Where(a => a.CreatedAt >= inicio).Select(a => a.CreatedAt), ct);
        var comentarios = Somar(
            await PorDiaAsync(_db.comments.Where(c => c.CreatedAt >= inicio).Select(c => c.CreatedAt), ct),
            await PorDiaAsync(_db.articleComments.Where(c => c.CreatedAt >= inicio).Select(c => c.CreatedAt), ct));
        var curtidas = Somar(
            await PorDiaAsync(_db.postLikes.Where(l => l.CreatedAt >= inicio).Select(l => l.CreatedAt), ct),
            await PorDiaAsync(_db.commentLikes.Where(l => l.CreatedAt >= inicio).Select(l => l.CreatedAt), ct),
            await PorDiaAsync(_db.articleLikes.Where(l => l.CreatedAt >= inicio).Select(l => l.CreatedAt), ct));

        var dto = new MetricasConteudoDto();
        for (var dia = DateOnly.FromDateTime(inicio); dia <= DateOnly.FromDateTime(hoje); dia = dia.AddDays(1))
        {
            dto.PorDia.Add(new MetricasConteudoDiaDto
            {
                Dia = dia.ToString("yyyy-MM-dd"),
                Posts = posts.GetValueOrDefault(dia),
                Comentarios = comentarios.GetValueOrDefault(dia),
                Curtidas = curtidas.GetValueOrDefault(dia),
                Artigos = artigos.GetValueOrDefault(dia),
                Compartilhamentos = compartilhamentos.GetValueOrDefault(dia),
            });
        }

        dto.Totais = new MetricasConteudoTotaisDto
        {
            Posts = dto.PorDia.Sum(d => d.Posts),
            Comentarios = dto.PorDia.Sum(d => d.Comentarios),
            Curtidas = dto.PorDia.Sum(d => d.Curtidas),
            Artigos = dto.PorDia.Sum(d => d.Artigos),
            Compartilhamentos = dto.PorDia.Sum(d => d.Compartilhamentos),
        };

        // Autores mais ativos no período (posts próprios + artigos + comentários em posts e artigos)
        var postsPorAutor = await PorAutorAsync(_db.posts.Where(p => p.SharedFromMemberId == null && p.SharedFromPostId == null && p.CreatedAt >= inicio).Select(p => p.MemberId), ct);
        var artigosPorAutor = await PorAutorAsync(_db.articles.Where(a => a.CreatedAt >= inicio).Select(a => a.MemberId), ct);
        var comentariosPorAutor = Somar(
            await PorAutorAsync(_db.comments.Where(c => c.CreatedAt >= inicio).Select(c => c.MemberId), ct),
            await PorAutorAsync(_db.articleComments.Where(c => c.CreatedAt >= inicio).Select(c => c.MemberId), ct));

        var top = postsPorAutor.Keys.Concat(artigosPorAutor.Keys).Concat(comentariosPorAutor.Keys)
            .Where(m => m != Guid.Empty)
            .Distinct()
            .Select(m => new MetricasAutorDto
            {
                MemberId = m,
                Posts = postsPorAutor.GetValueOrDefault(m),
                Artigos = artigosPorAutor.GetValueOrDefault(m),
                Comentarios = comentariosPorAutor.GetValueOrDefault(m),
            })
            .OrderByDescending(a => a.Posts + a.Artigos + a.Comentarios)
            .ThenByDescending(a => a.Artigos)
            .Take(TopAutores)
            .ToList();

        if (top.Count > 0)
        {
            var membros = await Members.GetMembersAsync(top.Select(a => a.MemberId), ct);
            foreach (var autor in top)
            {
                autor.Nome = membros.TryGetValue(autor.MemberId, out var m) ? m.Nome : null;
            }
        }

        dto.TopAutores = top;
        return Ok(new ResponseDto { Result = dto });
    }

    // -------------------------------------------------------------- Arquivos

    /// <summary>Imagens enviadas para os artigos (mais recentes primeiro), sem o conteúdo binário.</summary>
    [HttpGet("arquivos")]
    public async Task<ActionResult> ListArquivos([FromQuery] int pageNumber = 1, [FromQuery] int pageSize = DefaultPageSize)
    {
        (pageNumber, pageSize) = NormalizePage(pageNumber, pageSize);

        var total = await _db.articleImages.CountAsync();
        var itens = await _db.articleImages.AsNoTracking()
            .OrderByDescending(i => i.CreatedAt)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(i => new
            {
                i.ArticleImageId,
                i.FileName,
                i.ContentType,
                i.Size,
                i.UploadedByUserId,
                i.CreatedAt,
                // Em uso: algum artigo aponta para a imagem (capa ou no conteúdo)
                EmUso = _db.articles.Any(a =>
                    (a.CoverImageUrl != null && a.CoverImageUrl.Contains(i.ArticleImageId.ToString())) ||
                    (a.Content != null && a.Content.Contains(i.ArticleImageId.ToString()))),
            })
            .ToListAsync();

        // Mesmo endereço público usado no envio da imagem (ArticlesController)
        var baseUrl = (_configuration["PublicBaseUrl"] ?? $"{Request.Scheme}://{Request.Host}{Request.PathBase}").TrimEnd('/');

        WritePagination(total, pageNumber, pageSize);
        return Ok(new ResponseDto
        {
            Result = itens.Select(i => new ArquivoArtigoAdminDto
            {
                Id = i.ArticleImageId,
                Nome = i.FileName,
                ContentType = i.ContentType,
                Tamanho = i.Size,
                EnviadoPorUserId = i.UploadedByUserId,
                CriadoEm = i.CreatedAt,
                Url = $"{baseUrl}/api/articles/images/{i.ArticleImageId}",
                EmUso = i.EmUso,
            }).ToList()
        });
    }

    [HttpDelete("arquivos/{id:guid}")]
    public async Task<ActionResult> DeleteArquivo(Guid id)
    {
        var imagem = await _db.articleImages.FirstOrDefaultAsync(i => i.ArticleImageId == id);
        if (imagem is null) return NotFound(Fail("Arquivo não encontrado."));

        _db.articleImages.Remove(imagem);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Imagem de artigo {ImageId} ({FileName}) excluída pela administração ({UserId})",
            id, imagem.FileName, User.GetUserId());

        return Ok(new ResponseDto { Message = "Arquivo excluído." });
    }

    // -------------------------------------------------------------- Helpers

    /// <summary>Monta os DTOs com existência do alvo, link e total de denúncias do alvo (poucas consultas por página).</summary>
    private async Task<List<DenunciaAdminDto>> ToDtosAsync(List<Denuncia> itens)
    {
        List<int> Ids(string t) => itens.Where(d => d.Tipo == t).Select(d => d.AlvoId).Distinct().ToList();
        var postIds = Ids(DenunciaTipos.Post);
        var artigoIds = Ids(DenunciaTipos.Artigo);
        var comentarioIds = Ids(DenunciaTipos.Comentario);

        var postsExistentes = postIds.Count == 0 ? new HashSet<int>()
            : (await _db.posts.AsNoTracking().Where(p => postIds.Contains(p.PostId)).Select(p => p.PostId).ToListAsync()).ToHashSet();
        var artigosExistentes = artigoIds.Count == 0 ? new HashSet<int>()
            : (await _db.articles.AsNoTracking().Where(a => artigoIds.Contains(a.ArticleId)).Select(a => a.ArticleId).ToListAsync()).ToHashSet();
        var comentariosExistentes = comentarioIds.Count == 0 ? new Dictionary<int, int>()
            : await _db.comments.AsNoTracking().Where(c => comentarioIds.Contains(c.CommentId)).ToDictionaryAsync(c => c.CommentId, c => c.PostId);

        var totais = await _db.denuncias.AsNoTracking()
            .Where(d =>
                (d.Tipo == DenunciaTipos.Post && postIds.Contains(d.AlvoId)) ||
                (d.Tipo == DenunciaTipos.Artigo && artigoIds.Contains(d.AlvoId)) ||
                (d.Tipo == DenunciaTipos.Comentario && comentarioIds.Contains(d.AlvoId)))
            .GroupBy(d => new { d.Tipo, d.AlvoId })
            .Select(g => new { g.Key.Tipo, g.Key.AlvoId, Total = g.Count() })
            .ToListAsync();
        var totalPorAlvo = totais.ToDictionary(t => (t.Tipo, t.AlvoId), t => t.Total);

        return itens.Select(d =>
        {
            bool existe;
            string? link;
            switch (d.Tipo)
            {
                case DenunciaTipos.Post:
                    existe = postsExistentes.Contains(d.AlvoId);
                    link = $"/post/{d.AlvoId}";
                    break;
                case DenunciaTipos.Artigo:
                    existe = artigosExistentes.Contains(d.AlvoId);
                    link = $"/artigos/{d.AlvoId}";
                    break;
                default:
                    existe = comentariosExistentes.TryGetValue(d.AlvoId, out var postId);
                    link = existe ? $"/post/{postId}" : null;
                    break;
            }

            return new DenunciaAdminDto
            {
                Id = d.Id,
                Tipo = d.Tipo,
                AlvoId = d.AlvoId,
                AlvoExiste = existe,
                AlvoResumo = d.AlvoResumo,
                AlvoAutorMemberId = d.AlvoAutorMemberId,
                AlvoLink = link,
                Motivo = d.Motivo,
                Detalhes = d.Detalhes,
                DenuncianteMemberId = d.DenuncianteMemberId,
                Status = d.Status,
                CriadaEm = d.CriadaEm,
                TotalDenunciasDoAlvo = totalPorAlvo.GetValueOrDefault((d.Tipo, d.AlvoId)),
                ResolvidaPorUserId = d.ResolvidaPorUserId,
                ResolvidaEm = d.ResolvidaEm,
                Resolucao = d.Resolucao,
            };
        }).ToList();
    }

    private async Task NotificarAutorAsync(Guid autor, Denuncia denuncia, string categoria, string link)
    {
        if (autor == Guid.Empty) return;

        var tipoNome = denuncia.Tipo switch
        {
            DenunciaTipos.Post => "post",
            DenunciaTipos.Artigo => "artigo",
            _ => "comentário",
        };
        var resumo = ConteudoResumo.De(denuncia.AlvoResumo, 80);
        var mensagem = resumo is null
            ? $"Seu conteúdo foi removido pela administração ({tipoNome})."
            : $"Seu conteúdo foi removido pela administração ({tipoNome}: \"{resumo}\").";

        try
        {
            await Notifications.NotifyAsync(new[] { autor }, mensagem, categoria, link, BearerToken, HttpContext.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A remoção já foi feita; a notificação é apenas informativa
            _logger.LogWarning(ex, "Não foi possível notificar o autor {MemberId} sobre a remoção de conteúdo", autor);
        }
    }

    private static (int PageNumber, int PageSize) NormalizePage(int pageNumber, int pageSize) =>
        (Math.Max(pageNumber, 1), pageSize <= 0 ? DefaultPageSize : Math.Min(pageSize, MaxPageSize));

    private void WritePagination(int total, int pageNumber, int pageSize)
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

    /// <summary>Contagem por dia (UTC) de uma coluna de data.</summary>
    private static async Task<Dictionary<DateOnly, int>> PorDiaAsync(IQueryable<DateTime> datas, CancellationToken ct)
    {
        var porDia = await datas
            .GroupBy(d => d.Date)
            .Select(g => new { Dia = g.Key, Total = g.Count() })
            .ToListAsync(ct);
        return porDia.ToDictionary(x => DateOnly.FromDateTime(x.Dia), x => x.Total);
    }

    private static async Task<Dictionary<Guid, int>> PorAutorAsync(IQueryable<Guid> autores, CancellationToken ct)
    {
        var porAutor = await autores
            .GroupBy(m => m)
            .Select(g => new { MemberId = g.Key, Total = g.Count() })
            .ToListAsync(ct);
        return porAutor.ToDictionary(x => x.MemberId, x => x.Total);
    }

    private static Dictionary<TKey, int> Somar<TKey>(params Dictionary<TKey, int>[] partes) where TKey : notnull
    {
        var soma = new Dictionary<TKey, int>();
        foreach (var parte in partes)
        {
            foreach (var (chave, total) in parte)
            {
                soma[chave] = soma.GetValueOrDefault(chave) + total;
            }
        }
        return soma;
    }
}

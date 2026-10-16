using BuildingBlocks.Contracts.Administracao;
using BuildingBlocks.Shared.Security;
using Intranet.Services.ComunidadesAPI.Data;
using Intranet.Services.ComunidadesAPI.Models;
using Intranet.Services.ComunidadesAPI.Models.DTOs;
using Intranet.Services.ComunidadesAPI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Intranet.Services.ComunidadesAPI.Controllers;

/// <summary>
/// Grupos privados. Quem cria vira dono (owner). Para entrar, o colaborador envia uma
/// solicitação que o dono ou um admin do grupo aprova/recusa. Dono e admins editam, trocam
/// a capa e gerenciam membros; só o dono promove/rebaixa admins e exclui o grupo. O papel
/// global "Admin" mantém todos os poderes. Nome, descrição, capa e total de membros são
/// públicos; membros e solicitações só aparecem para quem participa do grupo.
/// O membro atual vem sempre do token.
/// </summary>
[Route("api/groups")]
[ApiController]
[Authorize]
public class GroupsController : GroupControllerBase
{
    public GroupsController(AppDbContext db, IdentidadeClient identidade, NotificationClient notifications, ILogger<GroupsController> logger)
        : base(db, identidade, notifications, logger)
    {
    }

    private static string? Validate(GroupUpsertDto dto)
    {
        var name = dto.Name?.Trim() ?? string.Empty;
        if (name.Length == 0) return "Informe o nome do grupo.";
        if (name.Length > 100) return "O nome deve ter no máximo 100 caracteres.";
        if (dto.Description?.Trim().Length > 1000) return "A descrição deve ter no máximo 1000 caracteres.";
        return null;
    }

    /// <summary>Completa as permissões calculadas a partir do papel (e do Admin global).</summary>
    private GroupDto Complete(GroupDto dto)
    {
        dto.MyRole = NormalizeRole(dto.MyRole);
        var access = new GroupAccess { Role = dto.MyRole, IsGlobalAdmin = IsAdmin, IsSuperAdmin = IsSuperAdmin };
        dto.IsMember = access.IsMember;
        dto.IsOwner = access.IsOwner;
        dto.CanView = access.CanView;
        dto.CanManage = access.CanManage;
        dto.CanManageAdmins = access.CanManageAdmins;
        dto.CanEdit = access.CanEditGroup;
        if (dto.IsMember) dto.HasPendingRequest = false;
        return dto;
    }

    // ------------------------------------------------------------------ Grupos

    /// <summary>Lista grupos (busca por nome/descrição). mine=true: apenas os meus.</summary>
    [HttpGet]
    public async Task<ActionResult> List(
        [FromQuery] string? search,
        [FromQuery] bool mine = false,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 12,
        CancellationToken ct = default)
    {
        var memberId = await CurrentMemberIdAsync(ct) ?? Guid.Empty;
        if (mine && memberId == Guid.Empty) return BadRequest(Fail(PerfilNaoEncontrado));

        pageNumber = Math.Max(pageNumber, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = _db.groups.AsNoTracking().AsQueryable();
        if (mine) query = query.Where(g => g.Members.Any(m => m.MemberId == memberId));
        if (!string.IsNullOrWhiteSpace(search))
        {
            var termo = $"%{search.Trim()}%";
            query = query.Where(g => EF.Functions.ILike(g.Name, termo) || EF.Functions.ILike(g.Description ?? "", termo));
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderBy(g => g.Name)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(g => new GroupDto
            {
                GroupId = g.GroupId,
                Name = g.Name,
                Description = g.Description,
                TotalMembers = g.Members.Count,
                CreatedByMemberId = g.CreatedByMemberId,
                CreatedAt = g.CreatedAt,
                CoverUpdatedAt = g.CoverUpdatedAt,
                MyRole = g.Members.Where(m => m.MemberId == memberId).Select(m => m.Role).FirstOrDefault(),
                HasPendingRequest = g.JoinRequests.Any(r => r.MemberId == memberId && r.Status == JoinRequestStatus.Pending),
            })
            .ToListAsync(ct);

        items.ForEach(g => Complete(g));
        WritePagination(total, pageNumber, pageSize);
        return Ok(new ResponseDto { Result = items });
    }

    /// <summary>Informações públicas do grupo + papel do usuário (o conteúdo fica nos endpoints de membros/posts).</summary>
    [HttpGet("{id:int}")]
    public async Task<ActionResult> Get(int id, CancellationToken ct)
    {
        var memberId = await CurrentMemberIdAsync(ct) ?? Guid.Empty;

        var group = await _db.groups.AsNoTracking()
            .Where(g => g.GroupId == id)
            .Select(g => new GroupDto
            {
                GroupId = g.GroupId,
                Name = g.Name,
                Description = g.Description,
                TotalMembers = g.Members.Count,
                CreatedByMemberId = g.CreatedByMemberId,
                CreatedAt = g.CreatedAt,
                CoverUpdatedAt = g.CoverUpdatedAt,
                MyRole = g.Members.Where(m => m.MemberId == memberId).Select(m => m.Role).FirstOrDefault(),
                HasPendingRequest = g.JoinRequests.Any(r => r.MemberId == memberId && r.Status == JoinRequestStatus.Pending),
                PendingRequestsCount = g.JoinRequests.Count(r => r.Status == JoinRequestStatus.Pending),
            })
            .FirstOrDefaultAsync(ct);

        if (group is null) return NotFound(Fail(GrupoNaoEncontrado));
        Complete(group);
        if (!group.CanManage) group.PendingRequestsCount = null;
        return Ok(new ResponseDto { Result = group });
    }

    /// <summary>Cria um grupo; quem cria vira dono (owner) e primeiro membro.</summary>
    [HttpPost]
    [RequerServico(ServicosRestringiveis.Grupos)]
    public async Task<ActionResult> Create([FromBody] GroupUpsertDto dto, CancellationToken ct)
    {
        var userId = CurrentUserId;
        if (userId is null) return Unauthorized();

        var error = Validate(dto);
        if (error is not null) return BadRequest(Fail(error));

        var memberId = await CurrentMemberIdAsync(ct);
        if (memberId is null) return BadRequest(Fail(PerfilNaoEncontrado));

        var now = DateTime.UtcNow;
        var group = new Group
        {
            Name = dto.Name.Trim(),
            Description = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim(),
            CreatedByMemberId = memberId.Value,
            CreatedByUserId = userId,
            CreatedAt = now,
        };
        group.Members.Add(new GroupMember { MemberId = memberId.Value, Role = GroupRoles.Owner, JoinedAt = now });

        _db.groups.Add(group);
        await _db.SaveChangesAsync(ct);

        return Ok(new ResponseDto { Result = new { group.GroupId } });
    }

    /// <summary>Edita nome/descrição: somente o dono ou o superAdmin.</summary>
    [HttpPut("{id:int}")]
    public async Task<ActionResult> Update(int id, [FromBody] GroupUpsertDto dto, CancellationToken ct)
    {
        var access = await GetAccessAsync(id, ct);
        if (access is null) return NotFound(Fail(GrupoNaoEncontrado));
        if (!access.CanEditGroup) return Forbidden("Apenas o dono do grupo ou o superAdmin pode editá-lo.");

        var error = Validate(dto);
        if (error is not null) return BadRequest(Fail(error));

        var group = await _db.groups.FirstAsync(g => g.GroupId == id, ct);
        group.Name = dto.Name.Trim();
        group.Description = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim();
        group.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return Ok(new ResponseDto { Result = new { group.GroupId } });
    }

    /// <summary>Exclui o grupo e todo o conteúdo: somente o dono ou o superAdmin.</summary>
    [HttpDelete("{id:int}")]
    public async Task<ActionResult> Delete(int id, CancellationToken ct)
    {
        var access = await GetAccessAsync(id, ct);
        if (access is null) return NotFound(Fail(GrupoNaoEncontrado));
        if (!access.CanEditGroup) return Forbidden("Apenas o dono do grupo ou o superAdmin pode excluí-lo.");

        // Membros, solicitações, capa, posts, imagens, curtidas e comentários em cascata
        await _db.groups.Where(g => g.GroupId == id).ExecuteDeleteAsync(ct);
        return Ok(new ResponseDto());
    }

    // ------------------------------------------------------------------- Capa

    /// <summary>Capa do grupo (pública: aparece na listagem para quem ainda não participa).</summary>
    [HttpGet("{id:int}/cover")]
    [AllowAnonymous]
    public async Task<ActionResult> GetCover(int id, CancellationToken ct)
    {
        var cover = await _db.groupCovers.AsNoTracking()
            .Where(c => c.GroupId == id)
            .Select(c => new { c.Data, c.ContentType, c.UpdatedAt })
            .FirstOrDefaultAsync(ct);
        if (cover is null) return NotFound(Fail("Este grupo não tem capa."));

        var etag = $"\"{cover.UpdatedAt.Ticks}\"";
        Response.Headers.ETag = etag;
        // A URL leva ?v={CoverVersion}: ao trocar a capa a URL muda
        Response.Headers.CacheControl = "public, max-age=86400";
        if (Request.Headers.IfNoneMatch.ToString() == etag)
        {
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return ImageFile(cover.Data, cover.ContentType);
    }

    /// <summary>Envia ou troca a capa (multipart/form-data, campo "file"): dono ou superAdmin.</summary>
    [HttpPut("{id:int}/cover")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(ImageValidator.MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = ImageValidator.MaxRequestBytes)]
    public async Task<ActionResult> UploadCover(int id, IFormFile? file, CancellationToken ct)
    {
        var access = await GetAccessAsync(id, ct);
        if (access is null) return NotFound(Fail(GrupoNaoEncontrado));
        if (!access.CanEditGroup) return Forbidden("Apenas o dono do grupo ou o superAdmin pode trocar a capa.");

        var (image, error) = await ImageValidator.ReadAsync(file, ct);
        if (image is null) return BadRequest(Fail(error!));

        var now = DateTime.UtcNow;
        var cover = await _db.groupCovers.FirstOrDefaultAsync(c => c.GroupId == id, ct);
        if (cover is null)
        {
            _db.groupCovers.Add(new GroupCover { GroupId = id, Data = image.Data, ContentType = image.ContentType, UpdatedAt = now });
        }
        else
        {
            cover.Data = image.Data;
            cover.ContentType = image.ContentType;
            cover.UpdatedAt = now;
        }

        var group = await _db.groups.FirstAsync(g => g.GroupId == id, ct);
        group.CoverUpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        return Ok(new ResponseDto { Result = new { HasCover = true, CoverVersion = now.Ticks } });
    }

    /// <summary>Remove a capa: dono ou superAdmin.</summary>
    [HttpDelete("{id:int}/cover")]
    public async Task<ActionResult> RemoveCover(int id, CancellationToken ct)
    {
        var access = await GetAccessAsync(id, ct);
        if (access is null) return NotFound(Fail(GrupoNaoEncontrado));
        if (!access.CanEditGroup) return Forbidden("Apenas o dono do grupo ou o superAdmin pode remover a capa.");

        await _db.groupCovers.Where(c => c.GroupId == id).ExecuteDeleteAsync(ct);
        await _db.groups.Where(g => g.GroupId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.CoverUpdatedAt, (DateTime?)null), ct);

        return Ok(new ResponseDto());
    }

    // ---------------------------------------------------- Solicitações de acesso

    /// <summary>
    /// O colaborador pede para entrar no grupo (não entra direto). Dono e admins são avisados.
    /// "join" é mantido como sinônimo para clientes antigos.
    /// </summary>
    [HttpPost("{id:int}/requests")]
    [HttpPost("{id:int}/join")]
    [RequerServico(ServicosRestringiveis.Grupos)]
    public async Task<ActionResult> RequestAccess(int id, CancellationToken ct)
    {
        var access = await GetAccessAsync(id, ct);
        if (access is null) return NotFound(Fail(GrupoNaoEncontrado));
        if (access.MemberId is null) return BadRequest(Fail(PerfilNaoEncontrado));
        if (access.IsMember) return BadRequest(Fail("Você já participa deste grupo."));

        var memberId = access.MemberId.Value;
        if (await _db.groupJoinRequests.AnyAsync(r => r.GroupId == id && r.MemberId == memberId && r.Status == JoinRequestStatus.Pending, ct))
            return BadRequest(Fail("Você já tem uma solicitação pendente para este grupo."));

        var request = new GroupJoinRequest { GroupId = id, MemberId = memberId, Status = JoinRequestStatus.Pending, CreatedAt = DateTime.UtcNow };
        _db.groupJoinRequests.Add(request);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Índice único parcial: outra requisição simultânea já criou a solicitação
            return BadRequest(Fail("Você já tem uma solicitação pendente para este grupo."));
        }

        var managers = await GroupMemberIdsAsync(id, memberId, ct, GroupRoles.Owner, GroupRoles.Admin);
        var name = await MemberNameAsync(memberId, ct);
        await _notifications.NotifyAsync(
            managers,
            $"{name ?? "Um colaborador"} pediu para participar do grupo {access.GroupName}.",
            $"/grupos/{id}",
            BearerToken,
            ct);

        return Ok(new ResponseDto { Result = new { request.GroupJoinRequestId } });
    }

    /// <summary>O colaborador cancela a própria solicitação pendente.</summary>
    [HttpDelete("{id:int}/requests/mine")]
    public async Task<ActionResult> CancelMyRequest(int id, CancellationToken ct)
    {
        var memberId = await CurrentMemberIdAsync(ct);
        if (memberId is null) return BadRequest(Fail(PerfilNaoEncontrado));

        var request = await _db.groupJoinRequests
            .FirstOrDefaultAsync(r => r.GroupId == id && r.MemberId == memberId.Value && r.Status == JoinRequestStatus.Pending, ct);
        if (request is null) return NotFound(Fail("Você não tem solicitação pendente para este grupo."));

        request.Status = JoinRequestStatus.Canceled;
        request.DecidedAt = DateTime.UtcNow;
        request.DecidedByMemberId = memberId.Value;
        await _db.SaveChangesAsync(ct);
        return Ok(new ResponseDto());
    }

    /// <summary>Solicitações pendentes (mais antigas primeiro): dono, admins ou Admin global.</summary>
    [HttpGet("{id:int}/requests")]
    public async Task<ActionResult> PendingRequests(int id, [FromQuery] int pageNumber = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        var access = await GetAccessAsync(id, ct);
        if (access is null) return NotFound(Fail(GrupoNaoEncontrado));
        if (!access.CanManage) return Forbidden("Apenas o dono ou um admin do grupo pode ver as solicitações.");

        pageNumber = Math.Max(pageNumber, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = _db.groupJoinRequests.AsNoTracking().Where(r => r.GroupId == id && r.Status == JoinRequestStatus.Pending);
        var total = await query.CountAsync(ct);
        var items = await query
            .OrderBy(r => r.CreatedAt)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(r => new GroupJoinRequestDto { GroupJoinRequestId = r.GroupJoinRequestId, MemberId = r.MemberId, CreatedAt = r.CreatedAt })
            .ToListAsync(ct);

        WritePagination(total, pageNumber, pageSize);
        return Ok(new ResponseDto { Result = items });
    }

    /// <summary>Aprova a solicitação: o colaborador vira membro e é avisado.</summary>
    [HttpPost("{id:int}/requests/{requestId:int}/approve")]
    public Task<ActionResult> ApproveRequest(int id, int requestId, CancellationToken ct) =>
        DecideRequestAsync(id, requestId, approve: true, ct);

    /// <summary>Recusa a solicitação: o colaborador é avisado.</summary>
    [HttpPost("{id:int}/requests/{requestId:int}/reject")]
    public Task<ActionResult> RejectRequest(int id, int requestId, CancellationToken ct) =>
        DecideRequestAsync(id, requestId, approve: false, ct);

    private async Task<ActionResult> DecideRequestAsync(int id, int requestId, bool approve, CancellationToken ct)
    {
        var access = await GetAccessAsync(id, ct);
        if (access is null) return NotFound(Fail(GrupoNaoEncontrado));
        if (!access.CanManage) return Forbidden("Apenas o dono ou um admin do grupo pode responder às solicitações.");

        var request = await _db.groupJoinRequests.FirstOrDefaultAsync(r => r.GroupJoinRequestId == requestId && r.GroupId == id, ct);
        if (request is null) return NotFound(Fail("Solicitação não encontrada."));
        if (request.Status != JoinRequestStatus.Pending) return BadRequest(Fail("Esta solicitação já foi respondida ou cancelada."));

        var now = DateTime.UtcNow;
        request.Status = approve ? JoinRequestStatus.Approved : JoinRequestStatus.Rejected;
        request.DecidedAt = now;
        request.DecidedByMemberId = access.MemberId;

        if (approve && !await IsGroupMemberAsync(id, request.MemberId, ct))
        {
            _db.groupMembers.Add(new GroupMember { GroupId = id, MemberId = request.MemberId, Role = GroupRoles.Member, JoinedAt = now });
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return BadRequest(Fail("Esta solicitação já foi respondida ou cancelada."));
        }
        catch (DbUpdateException)
        {
            return BadRequest(Fail("Não foi possível concluir a ação. Atualize a página e tente novamente."));
        }

        // A recusa avisa apenas o próprio solicitante (sem expor conteúdo do grupo)
        await _notifications.NotifyAsync(
            new[] { request.MemberId },
            approve
                ? $"Sua solicitação para participar do grupo {access.GroupName} foi aprovada."
                : $"Sua solicitação para participar do grupo {access.GroupName} foi recusada.",
            $"/grupos/{id}",
            BearerToken,
            ct);

        return Ok(new ResponseDto());
    }

    // --------------------------------------------------------- Participação

    /// <summary>O colaborador sai do grupo (o último dono não pode sair).</summary>
    [HttpPost("{id:int}/leave")]
    public async Task<ActionResult> Leave(int id, CancellationToken ct)
    {
        var memberId = await CurrentMemberIdAsync(ct);
        if (memberId is null) return BadRequest(Fail(PerfilNaoEncontrado));

        var membership = await _db.groupMembers.FirstOrDefaultAsync(m => m.GroupId == id && m.MemberId == memberId.Value, ct);
        if (membership is null) return NotFound(Fail("Você não participa deste grupo."));

        if (membership.Role == GroupRoles.Owner && await IsLastOwnerAsync(id, ct))
            return BadRequest(Fail("Você é o dono do grupo. Exclua o grupo em vez de sair dele."));

        _db.groupMembers.Remove(membership);
        await _db.SaveChangesAsync(ct);
        return Ok(new ResponseDto());
    }

    // -------------------------------------------------------------- Membros

    /// <summary>Membros do grupo (dono, admins e depois os demais): apenas para membros.</summary>
    [HttpGet("{id:int}/members")]
    public async Task<ActionResult> Members(int id, [FromQuery] int pageNumber = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        var access = await GetAccessAsync(id, ct);
        if (access is null) return NotFound(Fail(GrupoNaoEncontrado));
        if (!access.CanView) return Forbidden(SomenteMembros);

        pageNumber = Math.Max(pageNumber, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = _db.groupMembers.AsNoTracking().Where(m => m.GroupId == id);
        var total = await query.CountAsync(ct);
        var items = await query
            .OrderBy(m => m.Role == GroupRoles.Owner ? 0 : m.Role == GroupRoles.Admin ? 1 : 2)
            .ThenBy(m => m.JoinedAt)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(m => new GroupMemberDto { MemberId = m.MemberId, Role = m.Role, JoinedAt = m.JoinedAt })
            .ToListAsync(ct);

        items.ForEach(m => m.Role = NormalizeRole(m.Role)!);
        WritePagination(total, pageNumber, pageSize);
        return Ok(new ResponseDto { Result = items });
    }

    /// <summary>addMember(): dono, admins (ou Admin global) incluem um colaborador diretamente.</summary>
    [HttpPost("{id:int}/members")]
    public async Task<ActionResult> AddMember(int id, [FromBody] AddGroupMemberDto dto, CancellationToken ct)
    {
        var access = await GetAccessAsync(id, ct);
        if (access is null) return NotFound(Fail(GrupoNaoEncontrado));
        if (!access.CanManage) return Forbidden("Apenas o dono ou um admin do grupo pode adicionar membros.");

        if (dto.MemberId == Guid.Empty) return BadRequest(Fail("Membro inválido."));
        if (await IsGroupMemberAsync(id, dto.MemberId, ct))
            return BadRequest(Fail("Este colaborador já participa do grupo."));

        var currentMemberId = access.MemberId ?? Guid.Empty;
        IReadOnlyDictionary<Guid, MemberInfo> members;
        try
        {
            members = await _identidade.LookupAsync(new[] { dto.MemberId, currentMemberId }, BearerToken, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao validar o membro {MemberId}", dto.MemberId);
            return StatusCode(StatusCodes.Status502BadGateway, Fail("Não foi possível validar o colaborador. Tente novamente."));
        }
        if (!members.ContainsKey(dto.MemberId)) return BadRequest(Fail("Colaborador não encontrado."));

        var now = DateTime.UtcNow;
        _db.groupMembers.Add(new GroupMember { GroupId = id, MemberId = dto.MemberId, Role = GroupRoles.Member, JoinedAt = now });

        // Uma solicitação pendente do mesmo colaborador fica aprovada
        var pending = await _db.groupJoinRequests
            .FirstOrDefaultAsync(r => r.GroupId == id && r.MemberId == dto.MemberId && r.Status == JoinRequestStatus.Pending, ct);
        if (pending is not null)
        {
            pending.Status = JoinRequestStatus.Approved;
            pending.DecidedAt = now;
            pending.DecidedByMemberId = access.MemberId;
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return BadRequest(Fail("Este colaborador já participa do grupo."));
        }

        var addedBy = members.TryGetValue(currentMemberId, out var me) ? me.Nome : null;
        await _notifications.NotifyAsync(
            new[] { dto.MemberId },
            $"{addedBy ?? "Um colaborador"} adicionou você ao grupo {access.GroupName}.",
            $"/grupos/{id}",
            BearerToken,
            ct);

        return Ok(new ResponseDto());
    }

    /// <summary>
    /// Remove um membro: dono, admins ou Admin global. Ninguém remove o dono; apenas o dono
    /// (ou Admin global) remove um admin.
    /// </summary>
    [HttpDelete("{id:int}/members/{memberId:guid}")]
    public async Task<ActionResult> RemoveMember(int id, Guid memberId, CancellationToken ct)
    {
        var access = await GetAccessAsync(id, ct);
        if (access is null) return NotFound(Fail(GrupoNaoEncontrado));
        if (!access.CanManage) return Forbidden("Apenas o dono ou um admin do grupo pode remover membros.");

        var membership = await _db.groupMembers.FirstOrDefaultAsync(m => m.GroupId == id && m.MemberId == memberId, ct);
        if (membership is null) return NotFound(Fail("Membro não encontrado no grupo."));

        if (membership.Role == GroupRoles.Owner)
            return BadRequest(Fail("O dono do grupo não pode ser removido."));
        if (membership.Role == GroupRoles.Admin && !access.CanManageAdmins)
            return Forbidden("Apenas o dono pode remover um admin do grupo.");

        _db.groupMembers.Remove(membership);
        await _db.SaveChangesAsync(ct);
        return Ok(new ResponseDto());
    }

    /// <summary>Promove a admin ou rebaixa a membro: somente o dono (ou Admin global). O dono não muda de papel.</summary>
    [HttpPut("{id:int}/members/{memberId:guid}/role")]
    public async Task<ActionResult> ChangeRole(int id, Guid memberId, [FromBody] ChangeGroupRoleDto dto, CancellationToken ct)
    {
        var access = await GetAccessAsync(id, ct);
        if (access is null) return NotFound(Fail(GrupoNaoEncontrado));
        if (!access.CanManageAdmins) return Forbidden("Apenas o dono do grupo pode promover ou rebaixar admins.");

        var role = dto.Role?.Trim().ToLowerInvariant();
        if (role is not (GroupRoles.Admin or GroupRoles.Member))
            return BadRequest(Fail("Papel inválido. Use \"admin\" ou \"member\"."));

        var membership = await _db.groupMembers.FirstOrDefaultAsync(m => m.GroupId == id && m.MemberId == memberId, ct);
        if (membership is null) return NotFound(Fail("Membro não encontrado no grupo."));
        if (membership.Role == GroupRoles.Owner) return BadRequest(Fail("O papel do dono do grupo não pode ser alterado."));

        if (membership.Role != role)
        {
            membership.Role = role;
            await _db.SaveChangesAsync(ct);

            if (role == GroupRoles.Admin)
            {
                await _notifications.NotifyAsync(
                    new[] { memberId },
                    $"Você agora é admin do grupo {access.GroupName}.",
                    $"/grupos/{id}",
                    BearerToken,
                    ct);
            }
        }

        return Ok(new ResponseDto { Result = new { MemberId = memberId, Role = role } });
    }

    // -------------------------------------------------------------- Helpers

    private async Task<bool> IsLastOwnerAsync(int groupId, CancellationToken ct) =>
        await _db.groupMembers.CountAsync(m => m.GroupId == groupId && m.Role == GroupRoles.Owner, ct) <= 1;
}

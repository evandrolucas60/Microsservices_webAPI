using System.Text.Json;
using Intranet.Services.ComunidadesAPI.Data;
using Intranet.Services.ComunidadesAPI.Models;
using Intranet.Services.ComunidadesAPI.Models.DTOs;
using Intranet.Services.ComunidadesAPI.Services;
using BuildingBlocks.Shared.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Intranet.Services.ComunidadesAPI.Controllers;

/// <summary>
/// Papel do usuário atual em um grupo. O membro vem sempre do token; o papel global
/// "Admin" (JWT) mantém todos os poderes de gestão.
/// </summary>
public sealed class GroupAccess
{
    public int GroupId { get; init; }
    public string GroupName { get; init; } = string.Empty;

    /// <summary>Membro do token (null = usuário sem perfil de colaborador).</summary>
    public Guid? MemberId { get; init; }

    /// <summary>Papel no grupo ("owner", "admin", "member") ou null se não participa.</summary>
    public string? Role { get; init; }

    public bool IsGlobalAdmin { get; init; }

    /// <summary>Papel superAdmin do portal (painel de gestão).</summary>
    public bool IsSuperAdmin { get; init; }

    public bool IsMember => Role is not null;
    public bool IsOwner => Role == GroupRoles.Owner;

    /// <summary>Edita nome/descrição/capa e exclui o grupo: somente o dono ou o superAdmin.</summary>
    public bool CanEditGroup => IsSuperAdmin || IsOwner;

    /// <summary>Vê o conteúdo do grupo (posts, membros): membros e Admin global.</summary>
    public bool CanView => IsMember || IsGlobalAdmin;

    /// <summary>Aprova solicitações, gerencia membros e modera posts.</summary>
    public bool CanManage => IsGlobalAdmin || Role is GroupRoles.Owner or GroupRoles.Admin;

    /// <summary>Promove/rebaixa admins: somente o dono (ou Admin global).</summary>
    public bool CanManageAdmins => IsGlobalAdmin || IsOwner;
}

/// <summary>Infraestrutura comum dos controllers de grupos (token, permissões, paginação, imagens).</summary>
public abstract class GroupControllerBase : ControllerBase
{
    protected const int MaxPageSize = 50;
    private const string AdminRole = "Admin";

    protected const string PerfilNaoEncontrado = "Seu perfil de colaborador não foi encontrado. Abra a página de perfil e tente novamente.";
    protected const string GrupoNaoEncontrado = "Grupo não encontrado.";
    protected const string SomenteMembros = "Apenas membros do grupo podem ver e participar deste conteúdo.";

    protected readonly AppDbContext _db;
    protected readonly IdentidadeClient _identidade;
    protected readonly NotificationClient _notifications;
    protected readonly ILogger _logger;

    protected GroupControllerBase(AppDbContext db, IdentidadeClient identidade, NotificationClient notifications, ILogger logger)
    {
        _db = db;
        _identidade = identidade;
        _notifications = notifications;
        _logger = logger;
    }

    protected string? CurrentUserId => User.FindFirst("id")?.Value;
    protected bool IsAdmin => User.IsInRole(AdminRole);
    protected bool IsSuperAdmin => User.HasAnyRole(AdminPolicies.Roles(HttpContext.RequestServices.GetRequiredService<IConfiguration>()));

    protected string? BearerToken
    {
        get
        {
            var header = Request.Headers.Authorization.ToString();
            return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : null;
        }
    }

    protected async Task<Guid?> CurrentMemberIdAsync(CancellationToken ct)
    {
        var userId = CurrentUserId;
        return userId is null ? null : await _identidade.GetMemberIdAsync(userId, BearerToken, ct);
    }

    protected static ResponseDto Fail(string message) => new() { IsSuccess = false, Message = message };

    /// <summary>403 com mensagem (o front exibe o "message" da API).</summary>
    protected ObjectResult Forbidden(string message = "Você não tem permissão para esta ação.") =>
        StatusCode(StatusCodes.Status403Forbidden, Fail(message));

    /// <summary>Grupo + papel do usuário atual; null se o grupo não existir.</summary>
    protected async Task<GroupAccess?> GetAccessAsync(int groupId, CancellationToken ct)
    {
        var memberId = await CurrentMemberIdAsync(ct);
        var mid = memberId ?? Guid.Empty;

        var group = await _db.groups.AsNoTracking()
            .Where(g => g.GroupId == groupId)
            .Select(g => new
            {
                g.Name,
                Role = g.Members.Where(m => m.MemberId == mid).Select(m => m.Role).FirstOrDefault(),
            })
            .FirstOrDefaultAsync(ct);
        if (group is null) return null;

        return new GroupAccess
        {
            GroupId = groupId,
            GroupName = group.Name,
            MemberId = memberId,
            Role = memberId is null ? null : NormalizeRole(group.Role),
            IsGlobalAdmin = IsAdmin,
            IsSuperAdmin = IsSuperAdmin,
        };
    }

    /// <summary>Valores desconhecidos contam como "member".</summary>
    protected static string? NormalizeRole(string? role) => role switch
    {
        null => null,
        GroupRoles.Owner or GroupRoles.Admin or GroupRoles.Member => role,
        _ => GroupRoles.Member,
    };

    protected void WritePagination(int total, int pageNumber, int pageSize)
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

    /// <summary>Imagem guardada no banco, com tipo correto e sem "sniffing" pelo navegador.</summary>
    protected FileContentResult ImageFile(byte[] data, string contentType)
    {
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(data, contentType);
    }

    /// <summary>Membros do grupo (para notificações), opcionalmente sem um membro.</summary>
    protected Task<List<Guid>> GroupMemberIdsAsync(int groupId, Guid? except, CancellationToken ct, params string[] roles)
    {
        var query = _db.groupMembers.AsNoTracking().Where(m => m.GroupId == groupId);
        if (except.HasValue) query = query.Where(m => m.MemberId != except.Value);
        if (roles.Length > 0) query = query.Where(m => roles.Contains(m.Role));
        return query.Select(m => m.MemberId).ToListAsync(ct);
    }

    protected Task<bool> IsGroupMemberAsync(int groupId, Guid memberId, CancellationToken ct) =>
        _db.groupMembers.AnyAsync(m => m.GroupId == groupId && m.MemberId == memberId, ct);

    protected async Task<string?> MemberNameAsync(Guid memberId, CancellationToken ct)
    {
        try
        {
            var members = await _identidade.LookupAsync(new[] { memberId }, BearerToken, ct);
            return members.TryGetValue(memberId, out var m) ? m.Nome : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Não foi possível obter o nome do membro {MemberId}", memberId);
            return null;
        }
    }
}

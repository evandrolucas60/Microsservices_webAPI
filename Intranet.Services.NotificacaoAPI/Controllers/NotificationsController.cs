using System.Text.Json;
using Intranet.Services.NotificacaoAPI.Data;
using Intranet.Services.NotificacaoAPI.Models;
using Intranet.Services.NotificacaoAPI.Models.DTOs;
using Intranet.Services.NotificacaoAPI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Intranet.Services.NotificacaoAPI.Controllers;

/// <summary>
/// Notificações in-app do próprio colaborador (sininho). A criação não passa mais por HTTP:
/// os outros serviços publicam NotificacaoSolicitada no RabbitMQ (ver Consumers).
/// </summary>
[Route("api/notifications")]
[ApiController]
[Authorize]
public class NotificationsController : ControllerBase
{
    private const int MaxPageSize = 50;

    private readonly AppDbContext _db;
    private readonly MemberResolver _members;

    public NotificationsController(AppDbContext db, MemberResolver members)
    {
        _db = db;
        _members = members;
    }

    private async Task<Guid?> CurrentMemberIdAsync(CancellationToken ct)
    {
        var userId = User.FindFirst("id")?.Value;
        return string.IsNullOrEmpty(userId) ? null : await _members.GetMemberIdAsync(userId, ct);
    }
    /// <summary>Notificações in-app do colaborador autenticado (mais recentes primeiro).</summary>
    [HttpGet("me")]
    public async Task<ActionResult> Mine([FromQuery] bool unreadOnly = false, [FromQuery] int pageNumber = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        var memberId = await CurrentMemberIdAsync(ct);
        if (memberId is null) return NotFound(new ResponseDto { IsSuccess = false, Message = "Membro não encontrado." });

        pageNumber = Math.Max(pageNumber, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = _db.pushNotifications.AsNoTracking().Where(n => n.RecipientMemberId == memberId.Value);
        if (unreadOnly) query = query.Where(n => n.ReadAt == null);

        var total = await query.CountAsync(ct);
        var totalPages = (int)Math.Ceiling(total / (double)pageSize);

        var items = await query
            .OrderByDescending(n => n.CreatedOn)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(n => new NotificationDto
            {
                NotificationId = n.NotificationId,
                Content = n.Content,
                Category = n.Category,
                Link = n.Link,
                CreatedOn = n.CreatedOn,
                ReadAt = n.ReadAt,
            })
            .ToListAsync(ct);

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

    [HttpGet("me/unread-count")]
    public async Task<ActionResult> UnreadCount(CancellationToken ct)
    {
        var memberId = await CurrentMemberIdAsync(ct);
        if (memberId is null) return Ok(new ResponseDto { Result = new { Count = 0 } });

        var count = await _db.pushNotifications.CountAsync(n => n.RecipientMemberId == memberId.Value && n.ReadAt == null, ct);
        return Ok(new ResponseDto { Result = new { Count = count } });
    }

    [HttpPatch("{id:int}/read")]
    public async Task<ActionResult> MarkAsRead(int id, CancellationToken ct)
    {
        var memberId = await CurrentMemberIdAsync(ct);
        if (memberId is null) return Forbid();

        // Filtra pelo destinatário na consulta: ids de outros membros respondem 404 (não revela existência)
        var notification = await _db.pushNotifications
            .FirstOrDefaultAsync(n => n.NotificationId == id && n.RecipientMemberId == memberId.Value, ct);
        if (notification is null) return NotFound(new ResponseDto { IsSuccess = false, Message = "Notificação não encontrada." });

        notification.ReadAt ??= DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new ResponseDto());
    }

    [HttpPost("me/read-all")]
    public async Task<ActionResult> MarkAllAsRead(CancellationToken ct)
    {
        var memberId = await CurrentMemberIdAsync(ct);
        if (memberId is null) return Forbid();

        var now = DateTime.UtcNow;
        var updated = await _db.pushNotifications
            .Where(n => n.RecipientMemberId == memberId.Value && n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now), ct);

        return Ok(new ResponseDto { Result = new { Updated = updated } });
    }
}

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
/// Notificações: criação pelos serviços da intranet (chave de serviço) e
/// leitura pelo próprio colaborador (sininho / notificações in-app).
/// </summary>
[Route("api/notifications")]
[ApiController]
[Authorize]
public class NotificationsController : ControllerBase
{
    private const int MaxRecipients = 500;
    private const int MaxPageSize = 50;

    private readonly AppDbContext _db;
    private readonly ServiceKeyValidator _serviceKey;
    private readonly RecipientsClient _recipients;
    private readonly MemberResolver _members;
    private readonly ILogger<NotificationsController> _logger;

    public NotificationsController(
        AppDbContext db,
        ServiceKeyValidator serviceKey,
        RecipientsClient recipients,
        MemberResolver members,
        ILogger<NotificationsController> logger)
    {
        _db = db;
        _serviceKey = serviceKey;
        _recipients = recipients;
        _members = members;
        _logger = logger;
    }

    private string? BearerToken
    {
        get
        {
            var header = Request.Headers.Authorization.ToString();
            return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : null;
        }
    }

    private async Task<Guid?> CurrentMemberIdAsync(CancellationToken ct)
    {
        var userId = User.FindFirst("id")?.Value;
        return string.IsNullOrEmpty(userId) ? null : await _members.GetMemberIdAsync(userId, BearerToken, ct);
    }

    /// <summary>
    /// Uso interno: cria uma notificação in-app (PushNotification) para cada destinatário e,
    /// com SendEmail, também uma EmailNotification (enviada em segundo plano).
    /// </summary>
    [HttpPost]
    public async Task<ActionResult> Create([FromBody] CreateNotificationRequest request, CancellationToken ct)
    {
        if (!_serviceKey.IsValid(Request)) return Forbid();

        var recipients = request.RecipientMemberIds?.Where(id => id != Guid.Empty).Distinct().ToList() ?? new();
        if (recipients.Count == 0) return BadRequest(new ResponseDto { IsSuccess = false, Message = "Informe ao menos um destinatário." });
        if (recipients.Count > MaxRecipients) return BadRequest(new ResponseDto { IsSuccess = false, Message = $"Máximo de {MaxRecipients} destinatários." });
        if (!NotificationCategories.IsValid(request.Category)) return BadRequest(new ResponseDto { IsSuccess = false, Message = "Categoria inválida." });

        var content = request.Content?.Trim() ?? string.Empty;
        if (content.Length == 0) return BadRequest(new ResponseDto { IsSuccess = false, Message = "Informe o conteúdo." });
        if (content.Length > 500) content = content[..500];

        // Link apenas relativo ao portal (evita redirecionar para sites externos)
        var link = request.Link?.Trim();
        if (!string.IsNullOrEmpty(link) && (!link.StartsWith('/') || link.StartsWith("//") || link.Length > 300))
            return BadRequest(new ResponseDto { IsSuccess = false, Message = "Link inválido: use um caminho relativo (ex.: /mensagens/1)." });

        var now = DateTime.UtcNow;

        // In-app: já entregue ao ser gravada
        foreach (var memberId in recipients)
        {
            _db.pushNotifications.Add(new PushNotification
            {
                RecipientMemberId = memberId,
                Content = content,
                Category = request.Category,
                Link = link,
                CreatedOn = now,
                Status = NotificationStatus.Sent,
                SentAt = now,
            });
        }

        // E-mail: busca os endereços no IdentidadeAPI e deixa pendente para o worker
        var emailCount = 0;
        if (request.SendEmail)
        {
            try
            {
                var wanted = recipients.ToHashSet();
                var withEmail = (await _recipients.GetAllAsync(BearerToken, ct)).Where(r => wanted.Contains(r.MemberId));
                foreach (var recipient in withEmail)
                {
                    _db.emailNotifications.Add(new EmailNotification
                    {
                        RecipientMemberId = recipient.MemberId,
                        Email = recipient.Email.Trim(),
                        Content = content,
                        Category = request.Category,
                        Link = link,
                        CreatedOn = now,
                        Status = NotificationStatus.Pending,
                    });
                    emailCount++;
                }
            }
            catch (Exception ex)
            {
                // As notificações in-app são gravadas mesmo assim
                _logger.LogWarning(ex, "Não foi possível obter os e-mails dos destinatários; apenas notificações in-app serão criadas.");
            }
        }

        await _db.SaveChangesAsync(ct);

        return Ok(new ResponseDto { Result = new { InApp = recipients.Count, Emails = emailCount } });
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

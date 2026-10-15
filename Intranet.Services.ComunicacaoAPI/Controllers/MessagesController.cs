using System.Text.Json;
using BuildingBlocks.Contracts.Administracao;
using BuildingBlocks.Shared.Security;
using Intranet.Services.ComunicacaoAPI.Data;
using Intranet.Services.ComunicacaoAPI.Models;
using Intranet.Services.ComunicacaoAPI.Models.DTOs;
using Intranet.Services.ComunicacaoAPI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Intranet.Services.ComunicacaoAPI.Controllers;

/// <summary>
/// Mensagens entre colaboradores (Member sends Message). O remetente vem sempre do token;
/// leitura e anexos apenas para participantes (remetente ou destinatários).
/// </summary>
[Route("api/messages")]
[ApiController]
[Authorize]
public class MessagesController : ControllerBase
{
    private const int MaxRecipients = 50;
    private const int MaxFiles = 5;
    private const long MaxFileBytes = 10 * 1024 * 1024;
    private const int MaxBodyLength = 5000;
    private const int MaxPageSize = 50;
    private const int PreviewLength = 140;

    // Tipos de anexo aceitos (o download é sempre como attachment)
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp",
        ".pdf", ".txt", ".csv",
        ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".mp4", ".mp3",
    };

    private readonly AppDbContext _db;
    private readonly IdentidadeClient _identidade;
    private readonly NotificationClient _notifications;
    private readonly ILogger<MessagesController> _logger;

    public MessagesController(AppDbContext db, IdentidadeClient identidade, NotificationClient notifications, ILogger<MessagesController> logger)
    {
        _db = db;
        _identidade = identidade;
        _notifications = notifications;
        _logger = logger;
    }

    private string? CurrentUserId => User.FindFirst("id")?.Value;

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
        var userId = CurrentUserId;
        return userId is null ? null : await _identidade.GetMemberIdAsync(userId, BearerToken, ct);
    }

    private static ResponseDto Fail(string message) => new() { IsSuccess = false, Message = message };

    private const string PerfilNaoEncontrado = "Seu perfil de colaborador não foi encontrado. Abra a página de perfil e tente novamente.";

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

    // ------------------------------------------------------------------ Envio

    /// <summary>Envia uma mensagem (texto e/ou anexos) para um ou mais colaboradores.</summary>
    [HttpPost]
    [RequerServico(ServicosRestringiveis.Mensagens)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxFiles * MaxFileBytes + 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxFiles * MaxFileBytes + 1024 * 1024)]
    public async Task<ActionResult> Send([FromForm] SendMessageForm form, CancellationToken ct)
    {
        var userId = CurrentUserId;
        if (userId is null) return Unauthorized();

        var senderId = await CurrentMemberIdAsync(ct);
        if (senderId is null) return BadRequest(Fail(PerfilNaoEncontrado));

        var body = form.MessageBody?.Trim() ?? string.Empty;
        var files = form.Files ?? new List<IFormFile>();
        if (body.Length == 0 && files.Count == 0) return BadRequest(Fail("Escreva a mensagem ou anexe um arquivo."));
        if (body.Length > MaxBodyLength) return BadRequest(Fail($"A mensagem deve ter no máximo {MaxBodyLength} caracteres."));

        var recipientIds = (form.RecipientMemberIds ?? new List<Guid>())
            .Where(id => id != Guid.Empty && id != senderId.Value)
            .Distinct()
            .ToList();
        if (recipientIds.Count == 0) return BadRequest(Fail("Informe ao menos um destinatário."));
        if (recipientIds.Count > MaxRecipients) return BadRequest(Fail($"Máximo de {MaxRecipients} destinatários por mensagem."));

        var fileError = ValidateFiles(files);
        if (fileError is not null) return BadRequest(Fail(fileError));

        // Destinatários precisam existir no IdentidadeAPI
        IReadOnlyDictionary<Guid, MemberInfo> members;
        try
        {
            members = await _identidade.LookupAsync(recipientIds.Append(senderId.Value), BearerToken, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao validar destinatários");
            return StatusCode(StatusCodes.Status502BadGateway, Fail("Não foi possível validar os destinatários. Tente novamente."));
        }

        var missing = recipientIds.Where(id => !members.ContainsKey(id)).ToList();
        if (missing.Count > 0) return BadRequest(Fail("Há destinatários que não existem."));

        var now = DateTime.UtcNow;
        var message = new Message
        {
            SenderMemberId = senderId.Value,
            SenderUserId = userId,
            MessageBody = body,
            CreatedAt = now,
        };
        foreach (var id in recipientIds)
        {
            message.Recipients.Add(new MessageRecipient { MemberId = id, AddedAt = now });
        }
        foreach (var file in files)
        {
            message.Media.Add(await ToMediaAsync(file, ct));
        }

        _db.messages.Add(message);
        await _db.SaveChangesAsync(ct);

        // Message creates Notification
        var senderName = members.TryGetValue(senderId.Value, out var sender) ? sender.Nome : null;
        await _notifications.NotifyAsync(
            recipientIds,
            $"{senderName ?? "Um colaborador"} enviou uma mensagem para você.",
            $"/mensagens/{message.MessageId}",
            BearerToken,
            ct: ct);

        return Ok(new ResponseDto { Result = new { message.MessageId } });
    }

    // --------------------------------------------------------------- Leitura

    /// <summary>
    /// Caixa de entrada agrupada por conversa: uma linha por conversa (mensagem original + respostas),
    /// com a mensagem recebida mais recente, o total de mensagens e quantas ainda não li.
    /// </summary>
    [HttpGet("inbox")]
    public async Task<ActionResult> Inbox([FromQuery] bool unreadOnly = false, [FromQuery] int pageNumber = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        var memberId = await CurrentMemberIdAsync(ct);
        if (memberId is null) return BadRequest(Fail(PerfilNaoEncontrado));
        var me = memberId.Value;

        pageNumber = Math.Max(pageNumber, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var conversas = _db.messageRecipients.AsNoTracking()
            .Where(r => r.MemberId == me && r.DeletedAt == null)
            .GroupBy(r => r.Message.ThreadId ?? r.MessageId)
            .Select(g => new
            {
                ThreadId = g.Key,
                UltimaMensagemId = g.Max(r => r.MessageId),
                UltimaEm = g.Max(r => r.Message.CreatedAt),
                NaoLidas = g.Count(r => r.ReadAt == null),
            });
        if (unreadOnly) conversas = conversas.Where(c => c.NaoLidas > 0);

        var total = await conversas.CountAsync(ct);
        var pagina = await conversas
            .OrderByDescending(c => c.UltimaEm)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var ids = pagina.Select(c => c.UltimaMensagemId).ToList();
        var threadIds = pagina.Select(c => c.ThreadId).ToList();
        var mensagens = await _db.messageRecipients.AsNoTracking()
            .Where(r => r.MemberId == me && ids.Contains(r.MessageId))
            .Select(r => new
            {
                r.MessageId,
                r.Message.SenderMemberId,
                Preview = r.Message.MessageBody.Length > PreviewLength ? r.Message.MessageBody.Substring(0, PreviewLength) : r.Message.MessageBody,
                r.Message.CreatedAt,
                r.ReadAt,
                RecipientsCount = r.Message.Recipients.Count,
                MediaCount = r.Message.Media.Count,
                IsReply = r.Message.ThreadId != null,
            })
            .ToDictionaryAsync(x => x.MessageId, ct);
        // Total de mensagens de cada conversa (visíveis para mim: enviadas por mim ou recebidas)
        var totais = await _db.messages.AsNoTracking()
            .Where(m => threadIds.Contains(m.ThreadId ?? m.MessageId))
            .Where(m => (m.SenderMemberId == me && m.SenderDeletedAt == null) || m.Recipients.Any(r => r.MemberId == me && r.DeletedAt == null))
            .GroupBy(m => m.ThreadId ?? m.MessageId)
            .Select(g => new { ThreadId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ThreadId, x => x.Count, ct);

        var items = pagina.Where(c => mensagens.ContainsKey(c.UltimaMensagemId)).Select(c =>
        {
            var m = mensagens[c.UltimaMensagemId];
            return new MessageSummaryDto
            {
                MessageId = m.MessageId,
                SenderMemberId = m.SenderMemberId,
                Preview = m.Preview,
                CreatedAt = m.CreatedAt,
                ReadAt = c.NaoLidas > 0 ? null : m.ReadAt,
                RecipientsCount = m.RecipientsCount,
                MediaCount = m.MediaCount,
                ThreadId = c.ThreadId,
                IsReply = m.IsReply,
                ThreadCount = totais.GetValueOrDefault(c.ThreadId, 1),
                UnreadCount = c.NaoLidas,
            };
        }).ToList();

        WritePagination(total, pageNumber, pageSize);
        return Ok(new ResponseDto { Result = items });
    }

    /// <summary>Mensagens enviadas pelo colaborador autenticado.</summary>
    [HttpGet("sent")]
    public async Task<ActionResult> Sent([FromQuery] int pageNumber = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        var memberId = await CurrentMemberIdAsync(ct);
        if (memberId is null) return BadRequest(Fail(PerfilNaoEncontrado));

        pageNumber = Math.Max(pageNumber, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = _db.messages.AsNoTracking().Where(m => m.SenderMemberId == memberId.Value && m.SenderDeletedAt == null);
        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(m => m.CreatedAt)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(m => new MessageSummaryDto
            {
                MessageId = m.MessageId,
                SenderMemberId = m.SenderMemberId,
                Preview = m.MessageBody.Length > PreviewLength ? m.MessageBody.Substring(0, PreviewLength) : m.MessageBody,
                CreatedAt = m.CreatedAt,
                RecipientsCount = m.Recipients.Count,
                MediaCount = m.Media.Count,
                ThreadId = m.ThreadId ?? m.MessageId,
                IsReply = m.ThreadId != null,
            })
            .ToListAsync(ct);

        WritePagination(total, pageNumber, pageSize);
        return Ok(new ResponseDto { Result = items });
    }

    [HttpGet("unread-count")]
    public async Task<ActionResult> UnreadCount(CancellationToken ct)
    {
        var memberId = await CurrentMemberIdAsync(ct);
        if (memberId is null) return Ok(new ResponseDto { Result = new { Count = 0 } });

        var count = await _db.messageRecipients.CountAsync(r => r.MemberId == memberId.Value && r.ReadAt == null && r.DeletedAt == null, ct);
        return Ok(new ResponseDto { Result = new { Count = count } });
    }

    /// <summary>
    /// Detalhes das mensagens visíveis para o colaborador (remetente que não excluiu, ou destinatário
    /// que não excluiu), em ordem cronológica.
    /// </summary>
    private async Task<List<MessageDetailDto>> LoadVisibleAsync(IQueryable<Message> query, Guid me, CancellationToken ct)
    {
        return await query
            .Where(m => (m.SenderMemberId == me && m.SenderDeletedAt == null) || m.Recipients.Any(r => r.MemberId == me && r.DeletedAt == null))
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.MessageId)
            .Select(m => new MessageDetailDto
            {
                MessageId = m.MessageId,
                SenderMemberId = m.SenderMemberId,
                MessageBody = m.MessageBody,
                CreatedAt = m.CreatedAt,
                IsSender = m.SenderMemberId == me && m.SenderDeletedAt == null,
                IsRecipient = m.Recipients.Any(r => r.MemberId == me && r.DeletedAt == null),
                MyAcknowledgedAt = m.Recipients.Where(r => r.MemberId == me).Select(r => r.AcknowledgedAt).FirstOrDefault(),
                ThreadId = m.ThreadId ?? m.MessageId,
                ParentMessageId = m.ParentMessageId,
                Recipients = m.Recipients
                    .OrderBy(r => r.AddedAt)
                    .Select(r => new MessageRecipientDto { MemberId = r.MemberId, AddedAt = r.AddedAt, ReadAt = r.ReadAt, AcknowledgedAt = r.AcknowledgedAt })
                    .ToList(),
                Media = m.Media
                    .Select(x => new MessageMediaDto { MessageMediaId = x.MessageMediaId, FileName = x.FileName, ContentType = x.ContentType, Size = x.Size })
                    .ToList(),
            })
            .AsSplitQuery()
            .ToListAsync(ct);
    }

    private Task MarkReadAsync(IEnumerable<int> messageIds, Guid me, CancellationToken ct)
    {
        var ids = messageIds.ToList();
        var now = DateTime.UtcNow;
        return _db.messageRecipients
            .Where(r => ids.Contains(r.MessageId) && r.MemberId == me && r.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ReadAt, now), ct);
    }

    /// <summary>Lê uma mensagem (participantes). Para o destinatário, marca como lida.</summary>
    [HttpGet("{id:int}")]
    public async Task<ActionResult> Get(int id, CancellationToken ct)
    {
        var memberId = await CurrentMemberIdAsync(ct);
        if (memberId is null) return BadRequest(Fail(PerfilNaoEncontrado));

        var message = (await LoadVisibleAsync(_db.messages.AsNoTracking().Where(m => m.MessageId == id), memberId.Value, ct)).FirstOrDefault();
        if (message is null) return NotFound(Fail("Mensagem não encontrada."));

        if (message.IsRecipient) await MarkReadAsync(new[] { id }, memberId.Value, ct);
        return Ok(new ResponseDto { Result = message });
    }

    /// <summary>
    /// Conversa completa a partir de qualquer mensagem dela: a original e as respostas que o
    /// colaborador pode ver, em ordem. Marca como lidas as que ele recebeu.
    /// </summary>
    [HttpGet("{id:int}/thread")]
    public async Task<ActionResult> Thread(int id, CancellationToken ct)
    {
        var memberId = await CurrentMemberIdAsync(ct);
        if (memberId is null) return BadRequest(Fail(PerfilNaoEncontrado));
        var me = memberId.Value;

        var threadId = await _db.messages.AsNoTracking()
            .Where(m => m.MessageId == id)
            .Select(m => (int?)(m.ThreadId ?? m.MessageId))
            .FirstOrDefaultAsync(ct);
        if (threadId is null) return NotFound(Fail("Mensagem não encontrada."));

        var messages = await LoadVisibleAsync(
            _db.messages.AsNoTracking().Where(m => m.MessageId == threadId || m.ThreadId == threadId), me, ct);
        if (!messages.Any(m => m.MessageId == id)) return NotFound(Fail("Mensagem não encontrada."));

        await MarkReadAsync(messages.Where(m => m.IsRecipient).Select(m => m.MessageId), me, ct);
        return Ok(new ResponseDto { Result = new MessageThreadDto { ThreadId = threadId.Value, Messages = messages } });
    }

    /// <summary>
    /// Responde uma mensagem: a resposta fica anexada à conversa da mensagem original e vai para
    /// todos os participantes da mensagem respondida (remetente e destinatários), menos quem responde.
    /// </summary>
    [HttpPost("{id:int}/reply")]
    [RequerServico(ServicosRestringiveis.Mensagens)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxFiles * MaxFileBytes + 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxFiles * MaxFileBytes + 1024 * 1024)]
    public async Task<ActionResult> Reply(int id, [FromForm] ReplyMessageForm form, CancellationToken ct)
    {
        var userId = CurrentUserId;
        if (userId is null) return Unauthorized();
        var senderId = await CurrentMemberIdAsync(ct);
        if (senderId is null) return BadRequest(Fail(PerfilNaoEncontrado));
        var me = senderId.Value;

        var original = await _db.messages.AsNoTracking()
            .Where(m => m.MessageId == id)
            .Select(m => new
            {
                m.MessageId,
                m.ThreadId,
                m.SenderMemberId,
                m.SenderDeletedAt,
                Recipients = m.Recipients.Select(r => new { r.MemberId, r.DeletedAt }).ToList(),
            })
            .FirstOrDefaultAsync(ct);
        if (original is null) return NotFound(Fail("Mensagem não encontrada."));

        var canSee = (original.SenderMemberId == me && original.SenderDeletedAt == null)
            || original.Recipients.Any(r => r.MemberId == me && r.DeletedAt == null);
        if (!canSee) return NotFound(Fail("Mensagem não encontrada."));

        var body = form.MessageBody?.Trim() ?? string.Empty;
        var files = form.Files ?? new List<IFormFile>();
        if (body.Length == 0 && files.Count == 0) return BadRequest(Fail("Escreva a resposta ou anexe um arquivo."));
        if (body.Length > MaxBodyLength) return BadRequest(Fail($"A resposta deve ter no máximo {MaxBodyLength} caracteres."));
        var fileError = ValidateFiles(files);
        if (fileError is not null) return BadRequest(Fail(fileError));

        var recipientIds = original.Recipients.Select(r => r.MemberId)
            .Append(original.SenderMemberId)
            .Where(m => m != me)
            .Distinct()
            .ToList();
        if (recipientIds.Count == 0) return BadRequest(Fail("Não há outros participantes nesta conversa."));

        var now = DateTime.UtcNow;
        var reply = new Message
        {
            SenderMemberId = me,
            SenderUserId = userId,
            MessageBody = body,
            CreatedAt = now,
            ThreadId = original.ThreadId ?? original.MessageId,
            ParentMessageId = original.MessageId,
        };
        foreach (var rid in recipientIds) reply.Recipients.Add(new MessageRecipient { MemberId = rid, AddedAt = now });
        foreach (var file in files) reply.Media.Add(await ToMediaAsync(file, ct));

        _db.messages.Add(reply);
        await _db.SaveChangesAsync(ct);

        var senderName = await SenderNameAsync(me, ct);
        await _notifications.NotifyAsync(recipientIds, $"{senderName ?? "Um colaborador"} respondeu uma mensagem.", $"/mensagens/{reply.MessageId}", BearerToken, ct: ct);

        return Ok(new ResponseDto { Result = new { reply.MessageId, reply.ThreadId } });
    }

    /// <summary>
    /// "Registrar ciência": o destinatário confirma que leu e está ciente da mensagem.
    /// O remetente vê a data na lista de destinatários e recebe uma notificação.
    /// </summary>
    [HttpPost("{id:int}/ciencia")]
    public async Task<ActionResult> Acknowledge(int id, CancellationToken ct)
    {
        var memberId = await CurrentMemberIdAsync(ct);
        if (memberId is null) return BadRequest(Fail(PerfilNaoEncontrado));
        var me = memberId.Value;

        var recipient = await _db.messageRecipients.Include(r => r.Message)
            .FirstOrDefaultAsync(r => r.MessageId == id && r.MemberId == me && r.DeletedAt == null, ct);
        if (recipient is null) return NotFound(Fail("Mensagem não encontrada na sua caixa de entrada."));

        if (recipient.AcknowledgedAt is null)
        {
            var now = DateTime.UtcNow;
            recipient.AcknowledgedAt = now;
            recipient.ReadAt ??= now;
            await _db.SaveChangesAsync(ct);

            var name = await SenderNameAsync(me, ct);
            await _notifications.NotifyAsync(new[] { recipient.Message.SenderMemberId },
                $"{name ?? "Um colaborador"} registrou ciência da sua mensagem.", $"/mensagens/{id}", BearerToken, ct: ct);
        }

        return Ok(new ResponseDto { Result = new { MessageId = id, recipient.AcknowledgedAt } });
    }

    private async Task<string?> SenderNameAsync(Guid memberId, CancellationToken ct)
    {
        try
        {
            var members = await _identidade.LookupAsync(new[] { memberId }, BearerToken, ct);
            return members.TryGetValue(memberId, out var m) ? m.Nome : null;
        }
        catch (Exception ex)
        {
            // O nome só enfeita o texto da notificação
            _logger.LogWarning(ex, "Não foi possível obter o nome do membro {MemberId}", memberId);
            return null;
        }
    }

    /// <summary>
    /// Exclui a conversa inteira da caixa de quem pediu (mensagens recebidas e enviadas por ele).
    /// Os demais participantes continuam vendo.
    /// </summary>
    [HttpDelete("{id:int}/thread")]
    public async Task<ActionResult> DeleteThread(int id, CancellationToken ct)
    {
        var memberId = await CurrentMemberIdAsync(ct);
        if (memberId is null) return BadRequest(Fail(PerfilNaoEncontrado));
        var me = memberId.Value;

        var threadId = await _db.messages.AsNoTracking()
            .Where(m => m.MessageId == id)
            .Select(m => (int?)(m.ThreadId ?? m.MessageId))
            .FirstOrDefaultAsync(ct);
        if (threadId is null) return NotFound(Fail("Mensagem não encontrada."));

        var messages = await _db.messages.Include(m => m.Recipients)
            .Where(m => (m.MessageId == threadId || m.ThreadId == threadId) && (m.SenderMemberId == me || m.Recipients.Any(r => r.MemberId == me)))
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        var changed = false;
        foreach (var message in messages)
        {
            if (message.SenderMemberId == me && message.SenderDeletedAt is null)
            {
                message.SenderDeletedAt = now;
                changed = true;
            }
            foreach (var r in message.Recipients.Where(r => r.MemberId == me && r.DeletedAt is null))
            {
                r.DeletedAt = now;
                r.ReadAt ??= now;
                changed = true;
            }
        }
        if (!changed) return NotFound(Fail("Mensagem não encontrada."));

        // Ninguém mais vê a conversa: remove de vez (anexos em cascata)
        var orphans = messages.Where(m => m.SenderDeletedAt is not null && m.Recipients.All(r => r.DeletedAt is not null)).ToList();
        if (orphans.Count == messages.Count) _db.messages.RemoveRange(orphans);

        await _db.SaveChangesAsync(ct);
        return Ok(new ResponseDto());
    }

    /// <summary>Download de anexo (participantes). Sempre como attachment.</summary>
    [HttpGet("{id:int}/media/{mediaId:int}")]
    public async Task<ActionResult> DownloadMedia(int id, int mediaId, CancellationToken ct)
    {
        var memberId = await CurrentMemberIdAsync(ct);
        if (memberId is null) return BadRequest(Fail(PerfilNaoEncontrado));

        var isParticipant = await _db.messages.AsNoTracking().AnyAsync(m =>
            m.MessageId == id &&
            (m.SenderMemberId == memberId.Value || m.Recipients.Any(r => r.MemberId == memberId.Value)), ct);
        if (!isParticipant) return Forbid();

        var media = await _db.messageMedia.AsNoTracking()
            .FirstOrDefaultAsync(x => x.MessageId == id && x.MessageMediaId == mediaId, ct);
        if (media is null) return NotFound(Fail("Anexo não encontrado."));

        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(media.Content, media.ContentType, media.FileName);
    }

    /// <summary>
    /// Exclui a mensagem da caixa de quem pediu: o destinatário a remove da caixa de entrada e
    /// o remetente, da caixa de enviadas. Os demais participantes continuam vendo a mensagem.
    /// </summary>
    [HttpDelete("{id:int}")]
    public async Task<ActionResult> Delete(int id, CancellationToken ct)
    {
        var memberId = await CurrentMemberIdAsync(ct);
        if (memberId is null) return BadRequest(Fail(PerfilNaoEncontrado));

        var message = await _db.messages.Include(m => m.Recipients).FirstOrDefaultAsync(m => m.MessageId == id, ct);
        if (message is null) return NotFound(Fail("Mensagem não encontrada."));

        var now = DateTime.UtcNow;
        var changed = false;
        if (message.SenderMemberId == memberId.Value && message.SenderDeletedAt is null)
        {
            message.SenderDeletedAt = now;
            changed = true;
        }
        var recipient = message.Recipients.FirstOrDefault(r => r.MemberId == memberId.Value && r.DeletedAt == null);
        if (recipient is not null)
        {
            recipient.DeletedAt = now;
            recipient.ReadAt ??= now;
            changed = true;
        }
        if (!changed) return NotFound(Fail("Mensagem não encontrada."));

        // Ninguém mais vê a mensagem: remove de vez (anexos em cascata)
        if (message.SenderDeletedAt is not null && message.Recipients.All(r => r.DeletedAt is not null))
        {
            _db.messages.Remove(message);
        }

        await _db.SaveChangesAsync(ct);
        return Ok(new ResponseDto());
    }

    // ------------------------------------------------------------ addMember()

    /// <summary>addMember(): inclui um destinatário na mensagem (apenas o remetente).</summary>
    [HttpPost("{id:int}/members")]
    public async Task<ActionResult> AddMember(int id, [FromBody] AddMessageMemberDto dto, CancellationToken ct)
    {
        var message = await _db.messages.Include(m => m.Recipients).FirstOrDefaultAsync(m => m.MessageId == id, ct);
        if (message is null) return NotFound(Fail("Mensagem não encontrada."));
        if (message.SenderUserId != CurrentUserId) return Forbid();

        if (dto.MemberId == Guid.Empty || dto.MemberId == message.SenderMemberId)
            return BadRequest(Fail("Membro inválido."));
        if (message.Recipients.Any(r => r.MemberId == dto.MemberId))
            return BadRequest(Fail("Este colaborador já participa da mensagem."));
        if (message.Recipients.Count >= MaxRecipients)
            return BadRequest(Fail($"Máximo de {MaxRecipients} destinatários por mensagem."));

        IReadOnlyDictionary<Guid, MemberInfo> members;
        try
        {
            members = await _identidade.LookupAsync(new[] { dto.MemberId, message.SenderMemberId }, BearerToken, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao validar o membro {MemberId}", dto.MemberId);
            return StatusCode(StatusCodes.Status502BadGateway, Fail("Não foi possível validar o colaborador. Tente novamente."));
        }
        if (!members.ContainsKey(dto.MemberId)) return BadRequest(Fail("Colaborador não encontrado."));

        message.Recipients.Add(new MessageRecipient { MemberId = dto.MemberId, AddedAt = DateTime.UtcNow });
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Inclusão simultânea: o índice único (MessageId, MemberId) já garante um registro
            return BadRequest(Fail("Este colaborador já participa da mensagem."));
        }

        var senderName = members.TryGetValue(message.SenderMemberId, out var sender) ? sender.Nome : null;
        await _notifications.NotifyAsync(
            new[] { dto.MemberId },
            $"{senderName ?? "Um colaborador"} incluiu você em uma mensagem.",
            $"/mensagens/{message.MessageId}",
            BearerToken,
            ct: ct);

        return Ok(new ResponseDto());
    }

    // -------------------------------------------------------------- Helpers

    private static string? ValidateFiles(List<IFormFile> files)
    {
        if (files.Count > MaxFiles) return $"Máximo de {MaxFiles} anexos por mensagem.";
        foreach (var file in files)
        {
            if (file.Length == 0) return $"O arquivo {file.FileName} está vazio.";
            if (file.Length > MaxFileBytes) return $"O arquivo {file.FileName} passa de 10 MB.";
            if (!AllowedExtensions.Contains(Path.GetExtension(file.FileName)))
                return $"Tipo de arquivo não permitido: {file.FileName}.";
        }
        return null;
    }

    private static async Task<MessageMedia> ToMediaAsync(IFormFile file, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);

        // Nome sem caminho e com tamanho limitado
        var fileName = Path.GetFileName(file.FileName);
        if (fileName.Length > 255) fileName = fileName[^255..];

        var contentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType;
        // Content-Type inválido derrubaria o download (FileContentResult faz o parse do valor)
        if (contentType.Length > 100 || !Microsoft.Net.Http.Headers.MediaTypeHeaderValue.TryParse(contentType, out _))
            contentType = "application/octet-stream";

        return new MessageMedia
        {
            FileName = fileName,
            ContentType = contentType,
            Size = file.Length,
            Content = buffer.ToArray(),
        };
    }
}

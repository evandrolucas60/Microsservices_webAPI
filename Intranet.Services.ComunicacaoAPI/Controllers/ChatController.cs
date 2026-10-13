using System.Linq.Expressions;
using Intranet.Services.ComunicacaoAPI.Data;
using Intranet.Services.ComunicacaoAPI.Models;
using Intranet.Services.ComunicacaoAPI.Models.DTOs;
using Intranet.Services.ComunicacaoAPI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Intranet.Services.ComunicacaoAPI.Controllers;

/// <summary>
/// Chat privado entre dois colaboradores. Apenas os dois participantes leem ou escrevem
/// na conversa; o remetente vem sempre do token. Eventos em tempo real via /ws/chat.
/// As mensagens só ficam lidas quando o destinatário chama POST .../read (clique no chat).
/// </summary>
[Route("api/chat")]
[ApiController]
[Authorize]
public class ChatController : ControllerBase
{
    private const int MaxBodyLength = 2000;
    private const int MaxPageSize = 100;

    private readonly AppDbContext _db;
    private readonly IdentidadeClient _identidade;
    private readonly ChatConnectionManager _connections;
    private readonly NotificationClient _notifications;
    private readonly ILogger<ChatController> _logger;

    public ChatController(AppDbContext db, IdentidadeClient identidade, ChatConnectionManager connections, NotificationClient notifications, ILogger<ChatController> logger)
    {
        _db = db;
        _identidade = identidade;
        _connections = connections;
        _notifications = notifications;
        _logger = logger;
    }

    private const string PerfilNaoEncontrado = "Seu perfil de colaborador não foi encontrado. Abra a página de perfil e tente novamente.";

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
        return userId is null ? null : await _identidade.GetMemberIdAsync(userId, BearerToken, ct);
    }

    private static ResponseDto Fail(string message) => new() { IsSuccess = false, Message = message };

    /// <summary>Conversa do colaborador (null quando não existe ou ele não participa).</summary>
    private Task<Conversation?> FindConversationAsync(int conversationId, Guid me, CancellationToken ct) =>
        _db.conversations.FirstOrDefaultAsync(c => c.ConversationId == conversationId && (c.MemberAId == me || c.MemberBId == me), ct);

    /// <summary>Mensagens visíveis para o colaborador (posteriores ao "limpar conversa" dele).</summary>
    private static Expression<Func<ChatMessage, bool>> VisibleTo(Guid me) => m =>
        (m.Conversation.MemberAId == me && (m.Conversation.MemberAClearedAt == null || m.CreatedAt > m.Conversation.MemberAClearedAt)) ||
        (m.Conversation.MemberBId == me && (m.Conversation.MemberBClearedAt == null || m.CreatedAt > m.Conversation.MemberBClearedAt));

    /// <summary>Projeção sem o conteúdo binário da imagem (RecipientMemberId é preenchido depois).</summary>
    private static readonly Expression<Func<ChatMessage, ChatMessageDto>> AsDto = m => new ChatMessageDto
    {
        ChatMessageId = m.ChatMessageId,
        ConversationId = m.ConversationId,
        SenderMemberId = m.SenderMemberId,
        RecipientMemberId = m.SenderMemberId == m.Conversation.MemberAId ? m.Conversation.MemberBId : m.Conversation.MemberAId,
        Body = m.DeletedAt == null ? m.Body : string.Empty,
        CreatedAt = m.CreatedAt,
        ReadAt = m.ReadAt,
        Deleted = m.DeletedAt != null,
        AttachmentId = m.DeletedAt == null && m.Attachment != null ? m.Attachment.ChatAttachmentId : null,
        AttachmentName = m.DeletedAt == null && m.Attachment != null ? m.Attachment.FileName : null,
    };

    private static Expression<Func<ChatMessage, bool>> UnreadFor(Guid me) => m =>
        m.SenderMemberId != me && m.ReadAt == null && m.DeletedAt == null;

    /// <summary>Conversas do colaborador (mais recentes primeiro), com não lidas e última mensagem.</summary>
    [HttpGet("conversations")]
    public async Task<ActionResult> ListConversations(CancellationToken ct)
    {
        var me = await CurrentMemberIdAsync(ct);
        if (me is null) return BadRequest(Fail(PerfilNaoEncontrado));
        var memberId = me.Value;

        var conversations = await _db.conversations.AsNoTracking()
            .Where(c => (c.MemberAId == memberId || c.MemberBId == memberId) && c.LastMessageAt != null)
            .OrderByDescending(c => c.LastMessageAt)
            .Take(MaxPageSize)
            .Select(c => new { c.ConversationId, c.MemberAId, c.MemberBId, c.LastMessageAt })
            .ToListAsync(ct);

        var ids = conversations.Select(c => c.ConversationId).ToList();
        var visible = _db.chatMessages.AsNoTracking().Where(m => ids.Contains(m.ConversationId)).Where(VisibleTo(memberId));

        var unread = await visible.Where(UnreadFor(memberId))
            .GroupBy(m => m.ConversationId)
            .Select(g => new { ConversationId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ConversationId, x => x.Count, ct);

        var lastIds = await visible
            .GroupBy(m => m.ConversationId)
            .Select(g => g.Max(m => m.ChatMessageId))
            .ToListAsync(ct);
        var lastMessages = await _db.chatMessages.AsNoTracking()
            .Where(m => lastIds.Contains(m.ChatMessageId))
            .Select(AsDto)
            .ToDictionaryAsync(m => m.ConversationId, ct);

        var items = conversations.Select(c => new ConversationDto
        {
            ConversationId = c.ConversationId,
            OtherMemberId = c.MemberAId == memberId ? c.MemberBId : c.MemberAId,
            LastMessageAt = c.LastMessageAt,
            UnreadCount = unread.GetValueOrDefault(c.ConversationId),
            LastMessage = lastMessages.GetValueOrDefault(c.ConversationId),
        }).ToList();

        return Ok(new ResponseDto { Result = items });
    }

    /// <summary>Abre (ou cria) a conversa com outro colaborador.</summary>
    [HttpPost("conversations")]
    public async Task<ActionResult> StartConversation([FromBody] StartConversationRequest request, CancellationToken ct)
    {
        var me = await CurrentMemberIdAsync(ct);
        if (me is null) return BadRequest(Fail(PerfilNaoEncontrado));
        if (request.MemberId == Guid.Empty || request.MemberId == me.Value)
            return BadRequest(Fail("Escolha outro colaborador para conversar."));

        var (a, b) = Conversation.Pair(me.Value, request.MemberId);
        var conversation = await _db.conversations.AsNoTracking().FirstOrDefaultAsync(c => c.MemberAId == a && c.MemberBId == b, ct);

        if (conversation is null)
        {
            try
            {
                var found = await _identidade.LookupAsync(new[] { request.MemberId }, BearerToken, ct);
                if (!found.ContainsKey(request.MemberId)) return NotFound(Fail("Colaborador não encontrado."));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _logger.LogWarning(ex, "IdentidadeAPI indisponível ao iniciar conversa");
                return StatusCode(StatusCodes.Status503ServiceUnavailable, Fail("Não foi possível validar o colaborador. Tente novamente."));
            }

            conversation = new Conversation { MemberAId = a, MemberBId = b, CreatedAt = DateTime.UtcNow };
            _db.conversations.Add(conversation);
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Os dois abriram a conversa ao mesmo tempo: usa a que foi criada
                _db.ChangeTracker.Clear();
                conversation = await _db.conversations.AsNoTracking().FirstAsync(c => c.MemberAId == a && c.MemberBId == b, ct);
            }
        }

        var unread = await _db.chatMessages
            .Where(m => m.ConversationId == conversation.ConversationId)
            .Where(VisibleTo(me.Value))
            .CountAsync(UnreadFor(me.Value), ct);

        return Ok(new ResponseDto
        {
            Result = new ConversationDto
            {
                ConversationId = conversation.ConversationId,
                OtherMemberId = conversation.OtherMember(me.Value),
                LastMessageAt = conversation.LastMessageAt,
                UnreadCount = unread,
            }
        });
    }

    /// <summary>Histórico da conversa (ordem cronológica). beforeId pagina para mensagens mais antigas.</summary>
    [HttpGet("conversations/{id:int}/messages")]
    public async Task<ActionResult> ListMessages(int id, [FromQuery] int? beforeId, [FromQuery] int take = 30, CancellationToken ct = default)
    {
        var me = await CurrentMemberIdAsync(ct);
        if (me is null) return BadRequest(Fail(PerfilNaoEncontrado));

        var conversation = await FindConversationAsync(id, me.Value, ct);
        if (conversation is null) return NotFound(Fail("Conversa não encontrada."));

        take = Math.Clamp(take, 1, MaxPageSize);
        var query = _db.chatMessages.AsNoTracking().Where(m => m.ConversationId == id).Where(VisibleTo(me.Value));
        if (beforeId is not null) query = query.Where(m => m.ChatMessageId < beforeId.Value);

        var page = await query.OrderByDescending(m => m.ChatMessageId).Take(take + 1).Select(AsDto).ToListAsync(ct);
        var hasMore = page.Count > take;
        var items = page.Take(take).OrderBy(m => m.ChatMessageId).ToList();

        return Ok(new ResponseDto { Result = new { items, hasMore } });
    }

    [HttpPost("conversations/{id:int}/messages")]
    public async Task<ActionResult> SendMessage(int id, [FromBody] SendChatMessageRequest request, CancellationToken ct)
    {
        var me = await CurrentMemberIdAsync(ct);
        if (me is null) return BadRequest(Fail(PerfilNaoEncontrado));

        var body = request.Body?.Trim() ?? string.Empty;
        if (body.Length == 0) return BadRequest(Fail("A mensagem não pode ficar vazia."));
        if (body.Length > MaxBodyLength) return BadRequest(Fail($"A mensagem deve ter no máximo {MaxBodyLength} caracteres."));

        var conversation = await FindConversationAsync(id, me.Value, ct);
        if (conversation is null) return NotFound(Fail("Conversa não encontrada."));

        return Ok(new ResponseDto { Result = await CreateMessageAsync(conversation, me.Value, body, null, ct) });
    }

    /// <summary>Envia uma imagem (JPG, PNG, GIF ou WebP, até 5 MB) com legenda opcional.</summary>
    [HttpPost("conversations/{id:int}/images")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(ImageFiles.MaxBytes + 64 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = ImageFiles.MaxBytes + 64 * 1024)]
    public async Task<ActionResult> SendImage(int id, [FromForm] SendChatImageForm form, CancellationToken ct)
    {
        var me = await CurrentMemberIdAsync(ct);
        if (me is null) return BadRequest(Fail(PerfilNaoEncontrado));

        var body = form.Body?.Trim() ?? string.Empty;
        if (body.Length > MaxBodyLength) return BadRequest(Fail($"A legenda deve ter no máximo {MaxBodyLength} caracteres."));

        var conversation = await FindConversationAsync(id, me.Value, ct);
        if (conversation is null) return NotFound(Fail("Conversa não encontrada."));

        var image = await ImageFiles.ReadAsync(form.File, ct);
        if (image.Error is not null) return BadRequest(Fail(image.Error));

        var attachment = new ChatAttachment
        {
            FileName = image.FileName,
            ContentType = image.ContentType!,
            Size = image.Content!.LongLength,
            Content = image.Content,
        };
        return Ok(new ResponseDto { Result = await CreateMessageAsync(conversation, me.Value, body, attachment, ct) });
    }

    private async Task<ChatMessageDto> CreateMessageAsync(Conversation conversation, Guid me, string body, ChatAttachment? attachment, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var message = new ChatMessage { ConversationId = conversation.ConversationId, SenderMemberId = me, Body = body, CreatedAt = now, Attachment = attachment };
        _db.chatMessages.Add(message);
        conversation.LastMessageAt = now;
        await _db.SaveChangesAsync(ct);

        var dto = new ChatMessageDto
        {
            ChatMessageId = message.ChatMessageId,
            ConversationId = message.ConversationId,
            SenderMemberId = me,
            RecipientMemberId = conversation.OtherMember(me),
            Body = body,
            CreatedAt = now,
            AttachmentId = attachment?.ChatAttachmentId,
            AttachmentName = attachment?.FileName,
        };

        // Destinatário e as outras abas do remetente
        await _connections.SendToMembersAsync(new[] { conversation.MemberAId, conversation.MemberBId }, new { type = "message", message = dto });

        // Destinatário offline: a mensagem fica guardada (não lida) e ele recebe uma notificação.
        // Só na primeira mensagem não lida, para não gerar uma notificação por mensagem.
        if (!_connections.IsOnline(dto.RecipientMemberId))
        {
            var unreadFromMe = await _db.chatMessages.CountAsync(m =>
                m.ConversationId == conversation.ConversationId && m.SenderMemberId == me && m.ReadAt == null && m.DeletedAt == null, ct);
            if (unreadFromMe == 1)
            {
                await NotifyOfflineRecipientAsync(dto.RecipientMemberId, me, ct);
            }
        }

        return dto;
    }

    private async Task NotifyOfflineRecipientAsync(Guid recipient, Guid sender, CancellationToken ct)
    {
        try
        {
            var found = await _identidade.LookupAsync(new[] { sender }, BearerToken, ct);
            var name = found.TryGetValue(sender, out var info) && !string.IsNullOrWhiteSpace(info.Nome) ? info.Nome : "Um colaborador";
            // O link abre a conversa no chat flutuante (?chat=<remetente>)
            await _notifications.NotifyAsync(new[] { recipient }, $"{name} enviou uma mensagem no chat.", $"/?chat={sender}", BearerToken, false, ct);
        }
        catch (Exception ex)
        {
            // A mensagem já foi gravada; a notificação é apenas um aviso
            _logger.LogWarning(ex, "Não foi possível notificar o destinatário offline {MemberId}", recipient);
        }
    }

    /// <summary>Imagem da conversa (somente participantes, e não oculta por "limpar conversa").</summary>
    [HttpGet("attachments/{attachmentId:int}")]
    public async Task<ActionResult> GetAttachment(int attachmentId, CancellationToken ct)
    {
        var me = await CurrentMemberIdAsync(ct);
        if (me is null) return BadRequest(Fail(PerfilNaoEncontrado));

        var attachment = await _db.chatAttachments.AsNoTracking()
            .Where(a => a.ChatAttachmentId == attachmentId && a.Message.DeletedAt == null)
            .Where(a => a.Message.Conversation.MemberAId == me.Value || a.Message.Conversation.MemberBId == me.Value)
            .Select(a => new { a.Content, a.ContentType, a.FileName })
            .FirstOrDefaultAsync(ct);
        if (attachment is null) return NotFound(Fail("Imagem não encontrada."));

        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers.CacheControl = "private, max-age=86400";
        return File(attachment.Content, attachment.ContentType);
    }

    /// <summary>Apaga a própria mensagem para os dois participantes.</summary>
    [HttpDelete("conversations/{id:int}/messages/{messageId:int}")]
    public async Task<ActionResult> DeleteMessage(int id, int messageId, CancellationToken ct)
    {
        var me = await CurrentMemberIdAsync(ct);
        if (me is null) return BadRequest(Fail(PerfilNaoEncontrado));

        var conversation = await FindConversationAsync(id, me.Value, ct);
        if (conversation is null) return NotFound(Fail("Conversa não encontrada."));

        var message = await _db.chatMessages.Include(m => m.Attachment)
            .FirstOrDefaultAsync(m => m.ConversationId == id && m.ChatMessageId == messageId, ct);
        if (message is null) return NotFound(Fail("Mensagem não encontrada."));
        if (message.SenderMemberId != me.Value) return StatusCode(StatusCodes.Status403Forbidden, Fail("Você só pode apagar as suas mensagens."));
        if (message.DeletedAt is not null) return Ok(new ResponseDto());

        message.DeletedAt = DateTime.UtcNow;
        message.Body = string.Empty;
        if (message.Attachment is not null) _db.chatAttachments.Remove(message.Attachment);
        await _db.SaveChangesAsync(ct);

        await _connections.SendToMembersAsync(new[] { conversation.MemberAId, conversation.MemberBId },
            new { type = "deleted", conversationId = id, chatMessageId = messageId });

        return Ok(new ResponseDto());
    }

    /// <summary>Limpa a conversa apenas para quem pediu (o outro participante mantém o histórico).</summary>
    [HttpDelete("conversations/{id:int}/messages")]
    public async Task<ActionResult> ClearConversation(int id, CancellationToken ct)
    {
        var me = await CurrentMemberIdAsync(ct);
        if (me is null) return BadRequest(Fail(PerfilNaoEncontrado));

        var conversation = await FindConversationAsync(id, me.Value, ct);
        if (conversation is null) return NotFound(Fail("Conversa não encontrada."));

        var now = DateTime.UtcNow;
        if (conversation.MemberAId == me.Value) conversation.MemberAClearedAt = now;
        else conversation.MemberBClearedAt = now;
        await _db.SaveChangesAsync(ct);

        // As outras abas de quem limpou também esvaziam a janela
        await _connections.SendToMembersAsync(new[] { me.Value }, new { type = "cleared", conversationId = id });

        return Ok(new ResponseDto());
    }

    /// <summary>
    /// Marca como lidas as mensagens recebidas na conversa. Chamado pelo cliente somente
    /// quando o usuário clica na janela do chat (abrir a página não marca nada como lido).
    /// </summary>
    [HttpPost("conversations/{id:int}/read")]
    public async Task<ActionResult> MarkRead(int id, CancellationToken ct)
    {
        var me = await CurrentMemberIdAsync(ct);
        if (me is null) return BadRequest(Fail(PerfilNaoEncontrado));

        var conversation = await FindConversationAsync(id, me.Value, ct);
        if (conversation is null) return NotFound(Fail("Conversa não encontrada."));

        var now = DateTime.UtcNow;
        var updated = await _db.chatMessages
            .Where(m => m.ConversationId == id && m.SenderMemberId != me.Value && m.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.ReadAt, now), ct);

        if (updated > 0)
        {
            // Remetente vê "visualizada"; as outras abas do leitor param de piscar
            await _connections.SendToMembersAsync(new[] { conversation.MemberAId, conversation.MemberBId },
                new { type = "read", conversationId = id, readerMemberId = me.Value, readAt = now });
        }

        return Ok(new ResponseDto { Result = new { updated } });
    }

    /// <summary>Colaboradores conectados ao chat agora.</summary>
    [HttpGet("presence")]
    public ActionResult Presence() => Ok(new ResponseDto { Result = _connections.OnlineMembers() });

    [HttpGet("unread-count")]
    public async Task<ActionResult> UnreadCount(CancellationToken ct)
    {
        var me = await CurrentMemberIdAsync(ct);
        if (me is null) return BadRequest(Fail(PerfilNaoEncontrado));
        var memberId = me.Value;

        var count = await _db.chatMessages
            .Where(m => m.Conversation.MemberAId == memberId || m.Conversation.MemberBId == memberId)
            .Where(VisibleTo(memberId))
            .CountAsync(UnreadFor(memberId), ct);

        return Ok(new ResponseDto { Result = count });
    }
}

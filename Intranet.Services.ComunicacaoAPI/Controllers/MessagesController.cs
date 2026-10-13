using System.Text.Json;
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

    /// <summary>Mensagens recebidas pelo colaborador autenticado.</summary>
    [HttpGet("inbox")]
    public async Task<ActionResult> Inbox([FromQuery] bool unreadOnly = false, [FromQuery] int pageNumber = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        var memberId = await CurrentMemberIdAsync(ct);
        if (memberId is null) return BadRequest(Fail(PerfilNaoEncontrado));

        pageNumber = Math.Max(pageNumber, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = _db.messageRecipients.AsNoTracking().Where(r => r.MemberId == memberId.Value && r.DeletedAt == null);
        if (unreadOnly) query = query.Where(r => r.ReadAt == null);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(r => r.Message.CreatedAt)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(r => new MessageSummaryDto
            {
                MessageId = r.MessageId,
                SenderMemberId = r.Message.SenderMemberId,
                Preview = r.Message.MessageBody.Length > PreviewLength
                    ? r.Message.MessageBody.Substring(0, PreviewLength)
                    : r.Message.MessageBody,
                CreatedAt = r.Message.CreatedAt,
                ReadAt = r.ReadAt,
                RecipientsCount = r.Message.Recipients.Count,
                MediaCount = r.Message.Media.Count,
            })
            .ToListAsync(ct);

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

    /// <summary>Lê a mensagem (participantes). Para o destinatário, marca como lida.</summary>
    [HttpGet("{id:int}")]
    public async Task<ActionResult> Get(int id, CancellationToken ct)
    {
        var memberId = await CurrentMemberIdAsync(ct);
        if (memberId is null) return BadRequest(Fail(PerfilNaoEncontrado));

        var message = await _db.messages.AsNoTracking()
            .Where(m => m.MessageId == id)
            .Select(m => new
            {
                m.MessageId,
                m.SenderMemberId,
                m.MessageBody,
                m.CreatedAt,
                m.SenderDeletedAt,
                RecipientDeletedAt = m.Recipients.Where(r => r.MemberId == memberId.Value).Select(r => r.DeletedAt).FirstOrDefault(),
                Recipients = m.Recipients
                    .OrderBy(r => r.AddedAt)
                    .Select(r => new MessageRecipientDto { MemberId = r.MemberId, AddedAt = r.AddedAt, ReadAt = r.ReadAt })
                    .ToList(),
                Media = m.Media
                    .Select(x => new MessageMediaDto { MessageMediaId = x.MessageMediaId, FileName = x.FileName, ContentType = x.ContentType, Size = x.Size })
                    .ToList(),
            })
            .FirstOrDefaultAsync(ct);

        if (message is null) return NotFound(Fail("Mensagem não encontrada."));

        // Excluída pelo colaborador: deixa de existir para ele
        var isSender = message.SenderMemberId == memberId.Value && message.SenderDeletedAt == null;
        var isRecipient = message.Recipients.Any(r => r.MemberId == memberId.Value) && message.RecipientDeletedAt == null;
        if (!isSender && !isRecipient && (message.SenderMemberId == memberId.Value || message.Recipients.Any(r => r.MemberId == memberId.Value)))
            return NotFound(Fail("Mensagem não encontrada."));
        if (!isSender && !isRecipient) return Forbid();

        if (isRecipient)
        {
            var now = DateTime.UtcNow;
            await _db.messageRecipients
                .Where(r => r.MessageId == id && r.MemberId == memberId.Value && r.ReadAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.ReadAt, now), ct);
        }

        return Ok(new ResponseDto
        {
            Result = new MessageDetailDto
            {
                MessageId = message.MessageId,
                SenderMemberId = message.SenderMemberId,
                MessageBody = message.MessageBody,
                CreatedAt = message.CreatedAt,
                IsSender = isSender,
                Recipients = message.Recipients,
                Media = message.Media,
            }
        });
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

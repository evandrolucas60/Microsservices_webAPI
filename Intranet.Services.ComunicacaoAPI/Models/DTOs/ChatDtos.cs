namespace Intranet.Services.ComunicacaoAPI.Models.DTOs;

public class ChatMessageDto
{
    public int ChatMessageId { get; set; }
    public int ConversationId { get; set; }
    public Guid SenderMemberId { get; set; }
    public Guid RecipientMemberId { get; set; }
    public string Body { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? ReadAt { get; set; }

    /// <summary>Apagada pelo remetente (o texto e a imagem não são enviados).</summary>
    public bool Deleted { get; set; }

    /// <summary>Imagem anexada (baixe em api/chat/attachments/{AttachmentId}).</summary>
    public int? AttachmentId { get; set; }
    public string? AttachmentName { get; set; }
}

public class ConversationDto
{
    public int ConversationId { get; set; }
    public Guid OtherMemberId { get; set; }
    public DateTime? LastMessageAt { get; set; }
    public ChatMessageDto? LastMessage { get; set; }
    public int UnreadCount { get; set; }
}

public class StartConversationRequest
{
    public Guid MemberId { get; set; }
}

public class SendChatMessageRequest
{
    public string? Body { get; set; }
}

/// <summary>Envio de imagem (multipart/form-data), com legenda opcional.</summary>
public class SendChatImageForm
{
    public IFormFile? File { get; set; }
    public string? Body { get; set; }
}

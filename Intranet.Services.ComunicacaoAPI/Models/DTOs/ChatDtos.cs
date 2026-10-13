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

    /// <summary>"image", "audio" ou "document".</summary>
    public string? AttachmentKind { get; set; }
    public long? AttachmentSize { get; set; }

    /// <summary>"text" ou "nudge" (chamar atenção).</summary>
    public string Kind { get; set; } = "text";
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

/// <summary>Envio de arquivo (multipart/form-data): imagem, áudio gravado ou documento, com legenda opcional.</summary>
public class SendChatImageForm
{
    public IFormFile? File { get; set; }
    public string? Body { get; set; }

    /// <summary>"audio" quando o arquivo é uma gravação do microfone.</summary>
    public string? Kind { get; set; }
}

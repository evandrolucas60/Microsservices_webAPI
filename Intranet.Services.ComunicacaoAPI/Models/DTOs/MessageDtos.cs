namespace Intranet.Services.ComunicacaoAPI.Models.DTOs
{
    public class ResponseDto
    {
        public object? Result { get; set; }
        public bool IsSuccess { get; set; } = true;
        public string Message { get; set; } = "";
    }

    /// <summary>Envio de mensagem (multipart/form-data).</summary>
    public class SendMessageForm
    {
        public string? MessageBody { get; set; }
        public List<Guid> RecipientMemberIds { get; set; } = new();
        public List<IFormFile> Files { get; set; } = new();
    }

    /// <summary>addMember(): inclui um destinatário na mensagem.</summary>
    public class AddMessageMemberDto
    {
        public Guid MemberId { get; set; }
    }

    /// <summary>Item das listas de entrada/enviadas.</summary>
    public class MessageSummaryDto
    {
        public int MessageId { get; set; }
        public Guid SenderMemberId { get; set; }
        public string Preview { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime? ReadAt { get; set; }
        public int RecipientsCount { get; set; }
        public int MediaCount { get; set; }
    }

    public class MessageRecipientDto
    {
        public Guid MemberId { get; set; }
        public DateTime AddedAt { get; set; }
        public DateTime? ReadAt { get; set; }
    }

    public class MessageMediaDto
    {
        public int MessageMediaId { get; set; }
        public string FileName { get; set; } = string.Empty;
        public string ContentType { get; set; } = string.Empty;
        public long Size { get; set; }
    }

    public class MessageDetailDto
    {
        public int MessageId { get; set; }
        public Guid SenderMemberId { get; set; }
        public string MessageBody { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public bool IsSender { get; set; }
        public List<MessageRecipientDto> Recipients { get; set; } = new();
        public List<MessageMediaDto> Media { get; set; } = new();
    }
}

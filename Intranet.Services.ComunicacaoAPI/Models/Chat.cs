namespace Intranet.Services.ComunicacaoAPI.Models;

/// <summary>
/// Conversa privada entre dois colaboradores (chat estilo Messenger). O par é guardado
/// ordenado (MemberAId &lt; MemberBId) para existir uma única conversa por dupla.
/// </summary>
public class Conversation
{
    public int ConversationId { get; set; }
    public Guid MemberAId { get; set; }
    public Guid MemberBId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastMessageAt { get; set; }

    /// <summary>"Limpar conversa" de cada participante: mensagens até esta data ficam ocultas só para ele.</summary>
    public DateTime? MemberAClearedAt { get; set; }
    public DateTime? MemberBClearedAt { get; set; }

    public ICollection<ChatMessage> Messages { get; set; } = new List<ChatMessage>();

    public bool HasMember(Guid memberId) => MemberAId == memberId || MemberBId == memberId;

    public Guid OtherMember(Guid memberId) => MemberAId == memberId ? MemberBId : MemberAId;

    public DateTime? ClearedAtFor(Guid memberId) => MemberAId == memberId ? MemberAClearedAt : MemberBClearedAt;

    /// <summary>Par ordenado usado na chave única da conversa.</summary>
    public static (Guid A, Guid B) Pair(Guid x, Guid y) => x.CompareTo(y) < 0 ? (x, y) : (y, x);
}

/// <summary>Mensagem de uma conversa; ReadAt marca quando o destinatário a visualizou.</summary>
public class ChatMessage
{
    public int ChatMessageId { get; set; }
    public int ConversationId { get; set; }
    public Conversation Conversation { get; set; } = null!;
    public Guid SenderMemberId { get; set; }
    public string Body { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReadAt { get; set; }

    /// <summary>Apagada pelo remetente (para os dois): o texto e a imagem são removidos.</summary>
    public DateTime? DeletedAt { get; set; }

    /// <summary>"text" (mensagem, com ou sem anexo) ou "nudge" (chamar atenção). Ver <see cref="ChatMessageKinds"/>.</summary>
    public string Kind { get; set; } = ChatMessageKinds.Text;

    public ChatAttachment? Attachment { get; set; }
}

/// <summary>Arquivo enviado na conversa (imagem, áudio ou documento), armazenado no banco.</summary>
public class ChatAttachment
{
    public int ChatAttachmentId { get; set; }

    /// <summary>"image", "audio" ou "document".</summary>
    public string Kind { get; set; } = "image";
    public int ChatMessageId { get; set; }
    public ChatMessage Message { get; set; } = null!;
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long Size { get; set; }
    public byte[] Content { get; set; } = Array.Empty<byte>();
}

public static class ChatMessageKinds
{
    public const string Text = "text";

    /// <summary>Chamar atenção: faz a tela do destinatário tremer (limite de 1 por minuto).</summary>
    public const string Nudge = "nudge";
}

namespace Intranet.Services.ComunicacaoAPI.Models
{
    /// <summary>
    /// Message do diagrama: sendTo (Member[]), messageBody, media (blob[]) e addMember().
    /// Enviada por um Member (1) e pode ter vários destinatários.
    /// </summary>
    public class Message
    {
        public int MessageId { get; set; }

        /// <summary>Membro remetente (IdentidadeAPI).</summary>
        public Guid SenderMemberId { get; set; }

        /// <summary>Claim "id" do remetente: base da autorização de addMember().</summary>
        public string SenderUserId { get; set; } = string.Empty;

        public string MessageBody { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>Excluída pelo remetente (some apenas da caixa de enviadas dele).</summary>
        public DateTime? SenderDeletedAt { get; set; }

        /// <summary>sendTo: Member[]</summary>
        public ICollection<MessageRecipient> Recipients { get; set; } = new List<MessageRecipient>();

        /// <summary>media: blob[]</summary>
        public ICollection<MessageMedia> Media { get; set; } = new List<MessageMedia>();
    }

    /// <summary>Destinatário da mensagem (um por membro), com controle de leitura.</summary>
    public class MessageRecipient
    {
        public int MessageRecipientId { get; set; }
        public int MessageId { get; set; }
        public Message Message { get; set; } = null!;

        public Guid MemberId { get; set; }
        public DateTime AddedAt { get; set; } = DateTime.UtcNow;
        public DateTime? ReadAt { get; set; }

        /// <summary>Excluída pelo destinatário (some apenas da caixa de entrada dele).</summary>
        public DateTime? DeletedAt { get; set; }
    }

    /// <summary>Anexo (blob) armazenado no banco.</summary>
    public class MessageMedia
    {
        public int MessageMediaId { get; set; }
        public int MessageId { get; set; }
        public Message Message { get; set; } = null!;

        public string FileName { get; set; } = string.Empty;
        public string ContentType { get; set; } = string.Empty;
        public long Size { get; set; }
        public byte[] Content { get; set; } = Array.Empty<byte>();
    }
}

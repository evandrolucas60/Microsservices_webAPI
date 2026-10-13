namespace Intranet.Services.NotificacaoAPI.Models.DTOs
{
    public class ResponseDto
    {
        public object? Result { get; set; }
        public bool IsSuccess { get; set; } = true;
        public string Message { get; set; } = "";
    }

    /// <summary>
    /// Pedido interno (ComunicacaoAPI, ComunidadesAPI, ConteudoAPI...) para notificar membros.
    /// Sempre cria a notificação in-app (Push); com SendEmail também cria a EmailNotification.
    /// </summary>
    public class CreateNotificationRequest
    {
        public List<Guid> RecipientMemberIds { get; set; } = new();
        public string Content { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string? Link { get; set; }
        public bool SendEmail { get; set; }
    }

    /// <summary>Notificação in-app exibida ao colaborador.</summary>
    public class NotificationDto
    {
        public int NotificationId { get; set; }
        public string Content { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string? Link { get; set; }
        public DateTime CreatedOn { get; set; }
        public DateTime? ReadAt { get; set; }
    }
}

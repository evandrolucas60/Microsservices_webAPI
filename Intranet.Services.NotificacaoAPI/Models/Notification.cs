namespace Intranet.Services.NotificacaoAPI.Models
{
    public enum NotificationStatus
    {
        Pending = 0,
        Sent = 1,
        Failed = 2,
    }

    /// <summary>Origem da notificação (quem a criou, conforme o diagrama de classes).</summary>
    public static class NotificationCategories
    {
        public const string Message = "message";
        public const string Group = "group";
        public const string ConnectionInvitation = "connection";
        public const string Post = "post";
        public const string Comment = "comment";
        public const string Recommendation = "recommendation";
        public const string Article = "article";

        public static readonly string[] All =
        {
            Message, Group, ConnectionInvitation, Post, Comment, Recommendation, Article,
        };

        public static bool IsValid(string? category) => category is not null && All.Contains(category);
    }

    /// <summary>
    /// Notification (abstrata) do diagrama: notificationId, createdOn, content e send().
    /// Persistida em uma única tabela (TPH) com as especializações EmailNotification e PushNotification.
    /// O envio (send) é feito pelo <see cref="Services.INotificationSender"/> do canal correspondente.
    /// </summary>
    public abstract class Notification
    {
        public int NotificationId { get; set; }

        /// <summary>Membro destinatário (IdentidadeAPI).</summary>
        public Guid RecipientMemberId { get; set; }

        public string Content { get; set; } = string.Empty;

        /// <summary>Ver <see cref="NotificationCategories"/>.</summary>
        public string Category { get; set; } = string.Empty;

        /// <summary>Caminho no frontend para abrir o item notificado (ex.: /mensagens/12).</summary>
        public string? Link { get; set; }

        public DateTime CreatedOn { get; set; } = DateTime.UtcNow;

        public NotificationStatus Status { get; set; } = NotificationStatus.Pending;
        public int Attempts { get; set; }
        public DateTime? SentAt { get; set; }
        public string? LastError { get; set; }

        /// <summary>Quando o destinatário leu (notificações in-app).</summary>
        public DateTime? ReadAt { get; set; }
    }

    /// <summary>EmailNotification do diagrama: enviada por SMTP ao e-mail do colaborador.</summary>
    public class EmailNotification : Notification
    {
        public string Email { get; set; } = string.Empty;
    }

    /// <summary>
    /// PushNotification do diagrama: notificação in-app (sininho da intranet).
    /// PhoneNumber fica disponível para um futuro envio a dispositivo móvel/SMS.
    /// </summary>
    public class PushNotification : Notification
    {
        public string? PhoneNumber { get; set; }
    }
}

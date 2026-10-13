namespace Intranet.Services.NotificacaoAPI.Models
{
    /// <summary>Pedido de newsletter enviado pelo ConteudoAPI ao publicar um artigo.</summary>
    public class ArticleNewsletterRequest
    {
        public int ArticleId { get; set; }
        public string Type { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string? Summary { get; set; }
        public string? CoverImageUrl { get; set; }
        public Guid AuthorMemberId { get; set; }
    }

    /// <summary>Colaborador que recebe e-mails (vindo do IdentidadeAPI).</summary>
    public class Recipient
    {
        public Guid MemberId { get; set; }
        public string? Nome { get; set; }
        public string Email { get; set; } = string.Empty;
    }

    /// <summary>Trabalho na fila de envio da newsletter.</summary>
    public record NewsletterJob(ArticleNewsletterRequest Article, string? AuthorName, IReadOnlyList<Recipient> Recipients);

    /// <summary>Configuração "Smtp" do appsettings.</summary>
    public class SmtpOptions
    {
        public string? Host { get; set; }
        public int Port { get; set; } = 587;
        public bool EnableSsl { get; set; } = true;
        public string? User { get; set; }
        public string? Password { get; set; }
        public string FromAddress { get; set; } = "nao-responda@arcomix.com.br";
        public string FromName { get; set; } = "Arco-Net";
    }

    /// <summary>Configuração "Newsletter" do appsettings.</summary>
    public class NewsletterOptions
    {
        /// <summary>false = apenas registra em log o que seria enviado (útil em desenvolvimento).</summary>
        public bool Enabled { get; set; } = false;

        /// <summary>Endereço do frontend usado nos links dos e-mails.</summary>
        public string PortalUrl { get; set; } = "http://localhost:5173";

        /// <summary>Pausa entre e-mails para não sobrecarregar o servidor SMTP.</summary>
        public int DelayBetweenEmailsMs { get; set; } = 200;
    }
}

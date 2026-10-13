using System.ComponentModel.DataAnnotations;

namespace Intranet.Services.IdentidadeAPI.Models;

/// <summary>
/// Certificado (PDF) enviado pelo próprio colaborador. O arquivo fica no banco (bytea);
/// as listagens projetam apenas os metadados, sem carregar o conteúdo.
/// </summary>
public class Certificate
{
    public const int TitleMaxLength = 200;
    public const int IssuerMaxLength = 200;
    public const int FileNameMaxLength = 255;

    [Key]
    public Guid CertificateId { get; set; } = Guid.NewGuid();

    // Dono: membro do usuário do token no momento do envio
    public Guid MemberId { get; set; }

    [Required]
    [MaxLength(TitleMaxLength)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(IssuerMaxLength)]
    public string? Issuer { get; set; }

    public DateOnly? IssueDate { get; set; }

    [Required]
    [MaxLength(FileNameMaxLength)]
    public string FileName { get; set; } = string.Empty;

    public long FileSize { get; set; }

    [Required]
    public byte[] Content { get; set; } = Array.Empty<byte>();

    // UTC
    public DateTime CreatedAt { get; set; }
}

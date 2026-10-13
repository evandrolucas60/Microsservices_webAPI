namespace Intranet.Services.IdentidadeAPI.Models.DTOs;

/// <summary>Metadados de um certificado (o arquivo é baixado por api/Certificados/{id}/arquivo).</summary>
public class CertificateDto
{
    public Guid CertificateId { get; set; }
    public Guid MemberId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Issuer { get; set; }
    public DateOnly? IssueDate { get; set; }
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Envio de certificado (multipart/form-data).</summary>
public class CertificateUploadDto
{
    public string? Title { get; set; }
    public string? Issuer { get; set; }
    public DateOnly? IssueDate { get; set; }
    public IFormFile? File { get; set; }
}

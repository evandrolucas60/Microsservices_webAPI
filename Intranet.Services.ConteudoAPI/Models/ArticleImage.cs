namespace Intranet.Services.ConteudoAPI.Models;

/// <summary>
/// Imagem enviada do computador do autor (capa do artigo). Fica no banco e é servida
/// publicamente pelo id (Guid não sequencial), pois também aparece na newsletter por e-mail.
/// </summary>
public class ArticleImage
{
    public Guid ArticleImageId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long Size { get; set; }
    public byte[] Content { get; set; } = Array.Empty<byte>();

    /// <summary>Claim "id" de quem enviou.</summary>
    public string UploadedByUserId { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

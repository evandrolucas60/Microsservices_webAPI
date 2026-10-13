namespace Intranet.Services.ConteudoAPI.Models.DTOs;

public class CommentDto
{
    public int CommentId { get; set; }
    public string? Text { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int PostId { get; set; }
    public Guid MemberId { get; set; }
    public int TotalLikes { get; set; }

    /// <summary>Se o colaborador autenticado já curtiu (calculado pelo token).</summary>
    public bool LikedByMe { get; set; }
}

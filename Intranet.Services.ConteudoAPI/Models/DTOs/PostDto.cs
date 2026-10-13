namespace Intranet.Services.ConteudoAPI.Models.DTOs;

public class PostDto
{
    public int PostId { get; set; }
    public Guid MemberId { get; set; }
    public string? Text { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int TotalLikes { get; set; }
    public int TotalComments { get; set; }

    /// <summary>Se o colaborador autenticado já curtiu (calculado pelo token).</summary>
    public bool LikedByMe { get; set; }
}

/// <summary>Estado da curtida após curtir/descurtir.</summary>
public class LikeStateDto
{
    public int TotalLikes { get; set; }
    public bool LikedByMe { get; set; }
}

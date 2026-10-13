namespace Intranet.Services.ConteudoAPI.Models;

/// <summary>Curtida em artigo; única por (ArticleId, UserId).</summary>
public class ArticleLike
{
    public int ArticleLikeId { get; set; }
    public int ArticleId { get; set; }
    public Article Article { get; set; } = null!;

    /// <summary>Claim "id" do JWT de quem curtiu.</summary>
    public string UserId { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

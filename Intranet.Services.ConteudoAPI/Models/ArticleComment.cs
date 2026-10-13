namespace Intranet.Services.ConteudoAPI.Models;

public class ArticleComment
{
    public int ArticleCommentId { get; set; }
    public int ArticleId { get; set; }
    public Article Article { get; set; } = null!;

    /// <summary>Membro autor (para exibir nome/foto).</summary>
    public Guid MemberId { get; set; }

    /// <summary>Claim "id" do JWT do autor: base da autorização para excluir.</summary>
    public string UserId { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

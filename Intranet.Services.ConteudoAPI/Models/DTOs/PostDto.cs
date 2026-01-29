namespace Intranet.Services.ConteudoAPI.Models.DTOs;

public class PostDto
{
    public int PostId { get; set; }

    // 🔹 Relacionamento 1–1
    public int MemberId { get; set; }

    // 🔹 Relacionamento 1–N
    public ICollection<Comment> Comments { get; set; } = new List<Comment>();

    // 🔹 Dados
    public string Text { get; set; }
    public int TotalLikes { get; set; }
    public int TotalShares { get; set; }
    public DateTime TimeStamp { get; set; }
}

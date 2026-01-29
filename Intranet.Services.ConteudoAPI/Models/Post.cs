using System.ComponentModel.DataAnnotations;

namespace Intranet.Services.ConteudoAPI.Models;

public class Post
{
    public int PostId { get; set; }

    // 🔹 Relacionamento 1–1
    public int MemberId { get; set; }

    // 🔹 Relacionamento 1–N
    public ICollection<Comment> Comments { get; set; } = new List<Comment>();

    // 🔹 Dados
    [Required]
    public string Text { get; set; }
    public int TotalLikes { get; set; }
    public int TotalShares { get; set; }
    
    [Required]
    [DisplayFormat(DataFormatString = "{0:dd-MM-yyyy}", ApplyFormatInEditMode = true)]
    public DateTime TimeStamp { get; set; }
}

using System.ComponentModel.DataAnnotations;

namespace Intranet.Services.ConteudoAPI.Models;

public class Post
{
    public int PostId { get; set; }
    public int MemberId { get; set; }
    public string Text { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<Like> Likes { get; set; } = new List<Like>();
}

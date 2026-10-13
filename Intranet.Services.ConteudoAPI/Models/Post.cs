using System.ComponentModel.DataAnnotations;

namespace Intranet.Services.ConteudoAPI.Models;

public class Post
{
    public int PostId { get; set; }
    public Guid MemberId { get; set; }
    public string? Text { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<PostLike> Likes { get; set; } = new List<PostLike>();

    /// <summary>
    /// Compartilhamento (repost): post original exibido dentro deste. Fica null se o original
    /// for excluído; o autor original continua registrado em <see cref="SharedFromMemberId"/>.
    /// </summary>
    public int? SharedFromPostId { get; set; }
    public Post? SharedFromPost { get; set; }
    public Guid? SharedFromMemberId { get; set; }
}

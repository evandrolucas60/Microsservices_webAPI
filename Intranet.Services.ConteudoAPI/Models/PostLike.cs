namespace Intranet.Services.ConteudoAPI.Models
{
    public class PostLike
    {
        public int PostLikeId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public int PostId { get; set; }
        public Post Post { get; set; }
        public Guid MemberId { get; set; }
    }
}

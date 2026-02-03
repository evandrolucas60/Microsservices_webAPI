namespace Intranet.Services.ConteudoAPI.Models
{
    public class Like
    {
        public int LikeId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public int PostId { get; set; }
        public Post Post { get; set; }
        public int MemberId { get; set; }
    }
}

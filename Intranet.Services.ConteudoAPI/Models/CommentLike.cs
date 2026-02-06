namespace Intranet.Services.ConteudoAPI.Models
{
    public class CommentLike
    {
        public int CommentLikeId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public int CommentId { get; set; }
        public Comment Comment { get; set; }
        public Guid MemberId { get; set; }
    }
}

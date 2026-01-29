namespace Intranet.Services.ConteudoAPI.Models.DTOs;

public class CommentDto
{
    public int CommentId { get; set; }
    public string Text { get; set; }
    public int TotalLikes { get; set; }
    public DateTime TimeStamp { get; set; }

    public int PostId { get; set; }
}

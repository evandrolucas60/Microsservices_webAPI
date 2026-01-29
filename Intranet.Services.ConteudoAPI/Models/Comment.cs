using System.ComponentModel.DataAnnotations;

namespace Intranet.Services.ConteudoAPI.Models;

public class Comment
{
    public int CommentId { get; set; }
    [Required]
    public string Text { get; set; }
    public int TotalLikes { get; set; }

    [Required]
    [DisplayFormat(DataFormatString = "{0:dd-MM-yyyy}", ApplyFormatInEditMode = true)]
    public DateTime TimeStamp { get; set; }

    public int PostId { get; set; }
    public Post Post { get; set; }

    
}

using AutoMapper.Execution;
using Intranet.Services.ConteudoAPI.Models.DTOs;
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

    // 🔹 Relacionamento com Post
    public int PostId { get; set; }
    public Post Post { get; set; }

    // 🔹 Relacionamento com autor do comentário
    public int MemberId { get; set; }
}
using AutoMapper.Execution;
using Intranet.Services.ConteudoAPI.Models.DTOs;
using System.ComponentModel.DataAnnotations;

namespace Intranet.Services.ConteudoAPI.Models;

public class Comment
{
    public int CommentId { get; set; }
    public string Text { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int PostId { get; set; }
    public Post Post { get; set; }
    public int MemberId { get; set; }
}
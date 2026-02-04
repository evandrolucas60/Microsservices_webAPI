using Intranet.Services.ConteudoAPI.Data;
using Intranet.Services.ConteudoAPI.Models;
using Intranet.Services.ConteudoAPI.Models.DTOs;
using Intranet.Services.ConteudoAPI.Repository.IRepository;
using Microsoft.EntityFrameworkCore;

namespace Intranet.Services.ConteudoAPI.Repository;

public class CommentRepository : Repository<Comment>, ICommentRepository
{
    public CommentRepository(AppDbContext context) : base(context) { }

    public async Task<List<CommentDto>> GetAllByMemberIdWithLikesAsync(int postId)
    {
        return await _context.comments
            .AsNoTracking()
            .Where(p => p.PostId == postId)
            .Select(p => new CommentDto
            {
                CommentId = p.CommentId,
                MemberId = p.MemberId,
                PostId = p.PostId,
                Text = p.Text,
                CreatedAt = p.CreatedAt,
                TotalLikes = p.Likes.Count()
            })
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync();
    }
}

using Intranet.Services.ConteudoAPI.Data;
using Intranet.Services.ConteudoAPI.Models;
using Intranet.Services.ConteudoAPI.Models.DTOs;
using Intranet.Services.ConteudoAPI.Repository.IRepository;
using Microsoft.EntityFrameworkCore;

namespace Intranet.Services.ConteudoAPI.Repository;

public class PostRepository : Repository<Post>, IPostRepository
{
    public PostRepository(AppDbContext context) : base(context) { }

    public async Task<List<PostDto>> GetAllByMemberIdWithLikesAsync(int memberId)
    {
        return await _context.posts
            .AsNoTracking()
            .Where(p => p.MemberId == memberId)
            .Select(p => new PostDto
            {
                PostId = p.PostId,
                MemberId = p.MemberId,
                Text = p.Text,
                CreatedAt = p.CreatedAt,
                TotalLikes = p.Likes.Count()
            })
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync();
    }
}

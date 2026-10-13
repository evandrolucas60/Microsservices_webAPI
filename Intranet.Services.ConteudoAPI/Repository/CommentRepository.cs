using Intranet.Services.ConteudoAPI.Data;
using Intranet.Services.ConteudoAPI.Models;
using Intranet.Services.ConteudoAPI.Models.DTOs;
using Intranet.Services.ConteudoAPI.Pagination;
using Intranet.Services.ConteudoAPI.Pagination.Parameters;
using Intranet.Services.ConteudoAPI.Repository.IRepository;
using Microsoft.EntityFrameworkCore;

namespace Intranet.Services.ConteudoAPI.Repository;

public class CommentRepository : Repository<Comment>, ICommentRepository
{
    public CommentRepository(AppDbContext context) : base(context) { }

    public async Task<PagedList<CommentDto>> GetByPostAsync(CommentsParametersFilterData parameters, int postId, Guid? viewerMemberId)
    {
        var viewer = viewerMemberId ?? Guid.Empty;
        var query = _context.comments.AsNoTracking().Where(c => c.PostId == postId);

        var pageNumber = Math.Max(parameters.PageNumber, 1);
        var pageSize = Math.Max(parameters.PageSize, 1);

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(c => c.CreatedAt)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new CommentDto
            {
                CommentId = c.CommentId,
                MemberId = c.MemberId,
                PostId = c.PostId,
                Text = c.Text,
                CreatedAt = c.CreatedAt,
                TotalLikes = c.Likes.Count,
                LikedByMe = viewer != Guid.Empty && c.Likes.Any(l => l.MemberId == viewer),
            })
            .ToListAsync();

        return new PagedList<CommentDto>(items, total, pageNumber, pageSize);
    }
}

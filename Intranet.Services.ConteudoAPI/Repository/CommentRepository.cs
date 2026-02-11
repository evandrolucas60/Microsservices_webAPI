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

    public async Task<PagedList<CommentDto>> GetCommentsFilterDateAsync(CommentsParametersFilterData commentsParametersFilterData, int postId)
    {
        var comments = await _context.comments
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

        var commentsFiltered = PagedList<CommentDto>.toPagedList(
            comments.AsQueryable()
            .OrderByDescending(p => p.CreatedAt),
            commentsParametersFilterData.PageNumber,
            commentsParametersFilterData.PageSize);

        return commentsFiltered;
    }

    public async Task<PagedList<Comment>> GetCommentsFilterTextAsync(CommentsParameterFilterText commentsParameterFilterText)
    {
        var comments = await GetAllAsync();

        if (!string.IsNullOrEmpty(commentsParameterFilterText.Text))
        {
            string textFilter = commentsParameterFilterText.Text;
            comments = comments.Where(p => p.Text.IndexOf(textFilter,
                StringComparison.OrdinalIgnoreCase) >= 0);
        }

        var commentsFiltered = PagedList<Comment>.toPagedList(
            comments.AsQueryable()
            .OrderBy(p => p.Text),
            commentsParameterFilterText.PageNumber,
            commentsParameterFilterText.PageSize);

        return commentsFiltered;
    }

}

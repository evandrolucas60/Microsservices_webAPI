using Intranet.Services.ConteudoAPI.Data;
using Intranet.Services.ConteudoAPI.Models;
using Intranet.Services.ConteudoAPI.Models.DTOs;
using Intranet.Services.ConteudoAPI.Pagination;
using Intranet.Services.ConteudoAPI.Pagination.Parameters;
using Intranet.Services.ConteudoAPI.Repository.IRepository;
using Microsoft.EntityFrameworkCore;

namespace Intranet.Services.ConteudoAPI.Repository;

public class PostRepository : Repository<Post>, IPostRepository
{
    public PostRepository(AppDbContext context) : base(context) { }

    public async Task<List<PostDto>> GetAllByMemberIdWithLikesAsync(Guid memberId)
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
    public async Task<PagedList<Post>> GetPostsFilterDateAsync(PostsParametersFilterDate postsParametersFilterDate)
    {
        var posts = await GetAllAsync();

        if (postsParametersFilterDate.CreatedAt != default(DateTime))
        {
            DateTime postsFilter = postsParametersFilterDate.CreatedAt;
            posts = posts.Where(p => p.CreatedAt.Date == postsFilter.Date);
        }

        var postsFiltered = PagedList<Post>.toPagedList(
            posts.AsQueryable()
            .OrderByDescending(p => p.CreatedAt),
            postsParametersFilterDate.PageNumber,
            postsParametersFilterDate.PageSize);

        return postsFiltered;
    }

    public async Task<PagedList<Post>> GetPostsFilterTextAsync(PostsParametersFilterText postsParametersFilterText)
    {
        var posts = await GetAllAsync();

        if (!string.IsNullOrEmpty(postsParametersFilterText.Text))
        {
            string textFilter = postsParametersFilterText.Text;
            posts = posts.Where(p => p.Text.IndexOf(textFilter,
                StringComparison.OrdinalIgnoreCase) >= 0);
        }

        var postsFiltered = PagedList<Post>.toPagedList(
            posts.AsQueryable()
            .OrderBy(p => p.Text),
            postsParametersFilterText.PageNumber,
            postsParametersFilterText.PageSize);

        return postsFiltered;
    }
}

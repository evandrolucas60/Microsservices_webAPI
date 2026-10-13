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

    // Projeção única usada por todas as listagens: contagens e "já curti" calculados no banco
    private IQueryable<PostDto> Project(IQueryable<Post> query, Guid? viewerMemberId)
    {
        var viewer = viewerMemberId ?? Guid.Empty;
        return query.Select(p => new PostDto
        {
            PostId = p.PostId,
            MemberId = p.MemberId,
            Text = p.Text,
            CreatedAt = p.CreatedAt,
            TotalLikes = p.Likes.Count,
            TotalComments = _context.comments.Count(c => c.PostId == p.PostId),
            LikedByMe = viewer != Guid.Empty && p.Likes.Any(l => l.MemberId == viewer),
        });
    }

    public async Task<PagedList<PostDto>> GetFeedAsync(PostsParametersFilterDate parameters, Guid? viewerMemberId)
    {
        var query = _context.posts.AsNoTracking().AsQueryable();

        if (parameters.CreatedAt != default)
        {
            // Dia inteiro em UTC (antes filtrava em memória após carregar a tabela toda)
            var start = DateTime.SpecifyKind(parameters.CreatedAt.Date, DateTimeKind.Utc);
            var end = start.AddDays(1);
            query = query.Where(p => p.CreatedAt >= start && p.CreatedAt < end);
        }

        var pageNumber = Math.Max(parameters.PageNumber, 1);
        var pageSize = Math.Max(parameters.PageSize, 1);

        var total = await query.CountAsync();
        var items = await Project(query.OrderByDescending(p => p.CreatedAt), viewerMemberId)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return new PagedList<PostDto>(items, total, pageNumber, pageSize);
    }

    public Task<List<PostDto>> GetAllByMemberIdWithLikesAsync(Guid memberId, Guid? viewerMemberId) =>
        Project(_context.posts.AsNoTracking().Where(p => p.MemberId == memberId).OrderByDescending(p => p.CreatedAt), viewerMemberId)
            .ToListAsync();

    public Task<PostDto?> GetDetailAsync(int postId, Guid? viewerMemberId) =>
        Project(_context.posts.AsNoTracking().Where(p => p.PostId == postId), viewerMemberId)
            .FirstOrDefaultAsync();
}

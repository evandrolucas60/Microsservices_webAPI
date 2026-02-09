using Intranet.Services.ConteudoAPI.Models;
using Intranet.Services.ConteudoAPI.Models.DTOs;
using Intranet.Services.ConteudoAPI.Pagination;
using Intranet.Services.ConteudoAPI.Pagination.Parameters;

namespace Intranet.Services.ConteudoAPI.Repository.IRepository;

public interface IPostRepository : IRepository<Post>
{
    Task<PagedList<Post>> GetPostsFilterDateAsync(PostsParametersFilterDate postsParametersFilterDate);
    Task<PagedList<Post>> GetPostsFilterTextAsync(PostsParametersFilterText postsParametersFilterText);
    Task<List<PostDto>> GetAllByMemberIdWithLikesAsync(Guid memberId);
}

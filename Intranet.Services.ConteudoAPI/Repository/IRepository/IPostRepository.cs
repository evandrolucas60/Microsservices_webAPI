using Intranet.Services.ConteudoAPI.Models;
using Intranet.Services.ConteudoAPI.Models.DTOs;
using Intranet.Services.ConteudoAPI.Pagination;
using Intranet.Services.ConteudoAPI.Pagination.Parameters;

namespace Intranet.Services.ConteudoAPI.Repository.IRepository;

public interface IPostRepository : IRepository<Post>
{
    /// <summary>Feed paginado no banco, com curtidas/comentários e se o leitor já curtiu.</summary>
    Task<PagedList<PostDto>> GetFeedAsync(PostsParametersFilterDate parameters, Guid? viewerMemberId);

    /// <summary>Posts de um membro, com curtidas/comentários e se o leitor já curtiu.</summary>
    Task<List<PostDto>> GetAllByMemberIdWithLikesAsync(Guid memberId, Guid? viewerMemberId);

    /// <summary>Um post, com curtidas/comentários e se o leitor já curtiu.</summary>
    Task<PostDto?> GetDetailAsync(int postId, Guid? viewerMemberId);
}

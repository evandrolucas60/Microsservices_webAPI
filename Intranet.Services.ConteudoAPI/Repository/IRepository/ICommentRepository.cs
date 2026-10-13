using Intranet.Services.ConteudoAPI.Models;
using Intranet.Services.ConteudoAPI.Models.DTOs;
using Intranet.Services.ConteudoAPI.Pagination;
using Intranet.Services.ConteudoAPI.Pagination.Parameters;

namespace Intranet.Services.ConteudoAPI.Repository.IRepository;

public interface ICommentRepository : IRepository<Comment>
{
    /// <summary>Comentários de um post, paginados no banco, com curtidas e se o leitor já curtiu.</summary>
    Task<PagedList<CommentDto>> GetByPostAsync(CommentsParametersFilterData parameters, int postId, Guid? viewerMemberId);
}

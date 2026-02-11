using Intranet.Services.ConteudoAPI.Models;
using Intranet.Services.ConteudoAPI.Models.DTOs;
using Intranet.Services.ConteudoAPI.Pagination;
using Intranet.Services.ConteudoAPI.Pagination.Parameters;

namespace Intranet.Services.ConteudoAPI.Repository.IRepository;

public interface ICommentRepository : IRepository<Comment>
{
    Task<PagedList<CommentDto>> GetCommentsFilterDateAsync(CommentsParametersFilterData commentsParametersFilterData, int postId);
    Task<PagedList<Comment>> GetCommentsFilterTextAsync(CommentsParameterFilterText commentsParameterFilterText);
    Task<List<CommentDto>> GetAllByMemberIdWithLikesAsync(int postId);
}

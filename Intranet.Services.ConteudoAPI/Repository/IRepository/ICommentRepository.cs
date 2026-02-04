using Intranet.Services.ConteudoAPI.Models;
using Intranet.Services.ConteudoAPI.Models.DTOs;

namespace Intranet.Services.ConteudoAPI.Repository.IRepository;

public interface ICommentRepository : IRepository<Comment>
{
    Task<List<CommentDto>> GetAllByMemberIdWithLikesAsync(int postId);
}

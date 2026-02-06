using Intranet.Services.ConteudoAPI.Models;
using Intranet.Services.ConteudoAPI.Models.DTOs;

namespace Intranet.Services.ConteudoAPI.Repository.IRepository;

public interface IPostRepository : IRepository<Post>
{
    Task<List<PostDto>> GetAllByMemberIdWithLikesAsync(Guid memberId);
}

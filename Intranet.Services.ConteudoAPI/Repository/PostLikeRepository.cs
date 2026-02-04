using Intranet.Services.ConteudoAPI.Data;
using Intranet.Services.ConteudoAPI.Models;
using Intranet.Services.ConteudoAPI.Repository.IRepository;

namespace Intranet.Services.ConteudoAPI.Repository
{
    public class PostLikeRepository : Repository<PostLike>, IPostLikeRepository
    {
        public PostLikeRepository(AppDbContext context) : base(context) { }
    }
}

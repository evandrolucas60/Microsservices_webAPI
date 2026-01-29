using Intranet.Services.ConteudoAPI.Data;
using Intranet.Services.ConteudoAPI.Models;
using Intranet.Services.ConteudoAPI.Repository.IRepository;

namespace Intranet.Services.ConteudoAPI.Repository;

public class PostRepository : Repository<Post>, IPostRepository
{
    public PostRepository(AppDbContext context) : base(context) { }
}

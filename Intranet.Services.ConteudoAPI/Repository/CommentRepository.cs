using Intranet.Services.ConteudoAPI.Data;
using Intranet.Services.ConteudoAPI.Models;
using Intranet.Services.ConteudoAPI.Repository.IRepository;

namespace Intranet.Services.ConteudoAPI.Repository;

public class CommentRepository : Repository<Comment>, ICommentRepository
{
    public CommentRepository(AppDbContext context) : base(context) { }
}

using Intranet.Services.IdentidadeAPI.Data;
using Intranet.Services.IdentidadeAPI.Models;
using Intranet.Services.IdentidadeAPI.Repository.IRepository;

namespace Intranet.Services.IdentidadeAPI.Repository;

public class WorkRepository : Repository<Work>, IWorkRepository
{
    public WorkRepository(AppDbContext context) : base(context) { }
}

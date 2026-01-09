using Intranet.Services.IdentidadeAPI.Data;
using Intranet.Services.IdentidadeAPI.Models;
using Intranet.Services.IdentidadeAPI.Repository.IRepository;

namespace Intranet.Services.IdentidadeAPI.Repository;

public class ProfileRepository : Repository<UserProfile>, IProfileRepository
{
    public ProfileRepository(AppDbContext context) : base(context) { }
}

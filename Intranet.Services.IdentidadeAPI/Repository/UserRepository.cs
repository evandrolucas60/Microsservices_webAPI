using Intranet.Services.IdentidadeAPI.Data;
using Intranet.Services.IdentidadeAPI.Models.DTOs;
using Intranet.Services.IdentidadeAPI.Repository.IRepository;

namespace Intranet.Services.IdentidadeAPI.Repository
{
    public class UserRepository : Repository<UserDto>, IUserRepository
    {
        public UserRepository(AppDbContext context) : base(context) { }
    }
}

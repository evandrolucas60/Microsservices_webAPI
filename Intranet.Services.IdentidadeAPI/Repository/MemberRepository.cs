using Intranet.Services.IdentidadeAPI.Data;
using Intranet.Services.IdentidadeAPI.Models;
using Intranet.Services.IdentidadeAPI.Repository.IRepository;

namespace Intranet.Services.IdentidadeAPI.Repository;

public class MemberRepository : Repository<Member>, IMemberRepository
{
    public MemberRepository(AppDbContext context) : base(context) { }
}

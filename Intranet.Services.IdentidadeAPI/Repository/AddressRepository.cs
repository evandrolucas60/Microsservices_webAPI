using Intranet.Services.IdentidadeAPI.Data;
using Intranet.Services.IdentidadeAPI.Models;
using Intranet.Services.IdentidadeAPI.Repository.IRepository;

namespace Intranet.Services.IdentidadeAPI.Repository
{
    public class AddressRepository : Repository<Address>, IAddressRepository
    {
        public AddressRepository(AppDbContext context) : base(context) {}
    }
}

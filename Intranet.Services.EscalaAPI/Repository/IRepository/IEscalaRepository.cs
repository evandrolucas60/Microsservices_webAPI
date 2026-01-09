using Intranet.Services.EscalaAPI.Models;
using Intranet.Services.EscalaAPI.Pagination;

namespace Intranet.Services.EscalaAPI.Repository.IRepository;

public interface IEscalaRepository : IRepository<Escala>
{
    Task<PagedList<Escala>> GetEscalasAsync(EscalaParameters escalasParameters);
}

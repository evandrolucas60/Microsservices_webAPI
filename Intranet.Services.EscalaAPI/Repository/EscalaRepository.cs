using Intranet.Services.EscalaAPI.Data;
using Intranet.Services.EscalaAPI.Models;
using Intranet.Services.EscalaAPI.Pagination;
using Intranet.Services.EscalaAPI.Repository.IRepository;

namespace Intranet.Services.EscalaAPI.Repository;

public class EscalaRepository : Repository<Escala>, IEscalaRepository
{
    public EscalaRepository(AppDbContext context) : base(context) { }
    public async Task<PagedList<Escala>> GetEscalasAsync(EscalaParameters escalasParameters)
    {
        var escalas = await GetAllAsync();
        var escalasOrdenadas = escalas.OrderBy(c => c.Nome).AsQueryable();
        var resultado = PagedList<Escala>.toPagedList(escalasOrdenadas, escalasParameters.PageNumber, escalasParameters.PageSize);
        return resultado;
    }
}

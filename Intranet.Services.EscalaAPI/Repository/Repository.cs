using Intranet.Services.EscalaAPI.Data;
using Intranet.Services.EscalaAPI.Repository.IRepository;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;

namespace Intranet.Services.EscalaAPI.Repository;

public class Repository<T> : IRepository<T> where T : class
{
    protected readonly AppDbContext _context;

    public Repository(AppDbContext context)
    {
        _context = context;
    }
    public async Task<IEnumerable<T>> GetAllAsync()
    {
        return await _context.Set<T>().AsNoTracking().ToListAsync();
    }

    public async Task<T> GetAsync(Expression<Func<T, bool>> predicate)
    {
        return await _context.Set<T>().AsTracking().FirstOrDefaultAsync(predicate);
    }

    public T? Create(T Entity)
    {
        _context.Set<T>().Add(Entity);
        return Entity;
    }

    public T? Update(T Entity)
    {
        _context.Set<T>().Update(Entity);
        return Entity;
    }

    public T? Delete(T Entity)
    {
        _context.Set<T>().Remove(Entity);
        return Entity;
    }   
}

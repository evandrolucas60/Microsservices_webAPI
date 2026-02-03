using Intranet.Services.ConteudoAPI.Data;
using Intranet.Services.ConteudoAPI.Repository.IRepository;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace Intranet.Services.ConteudoAPI.Repository;

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

    public async Task<IEnumerable<T>> GetAllAsync(Expression<Func<T, bool>> predicate)
    {
        return await _context.Set<T>().AsTracking().Where(predicate).ToListAsync();

    }

    public async Task<T?> GetAsync(
     Expression<Func<T, bool>> predicate,
     string? includeProperties = null
 )
    {
        IQueryable<T> query = _context.Set<T>();

        if (predicate != null)
            query = query.Where(predicate);

        if (!string.IsNullOrWhiteSpace(includeProperties))
        {
            foreach (var includeProp in includeProperties
                         .Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                query = query.Include(includeProp.Trim());
            }
        }

        return await query.AsTracking().FirstOrDefaultAsync();
    }

    public async Task<int> CountAsync()
        => await _context.Set<T>().CountAsync();

    public async Task<int> CountAsync(Expression<Func<T, bool>> predicate)
        => await _context.Set<T>().CountAsync(predicate);


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

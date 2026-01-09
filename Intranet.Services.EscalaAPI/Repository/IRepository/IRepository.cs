using System.Linq.Expressions;

namespace Intranet.Services.EscalaAPI.Repository.IRepository;

public interface IRepository<T>
{
    Task<IEnumerable<T>> GetAllAsync();
    Task<T> GetAsync(Expression<Func<T, bool>> predicate);
    T? Create(T Entity);
    T? Update(T Entity);
    T? Delete(T Entity);
}

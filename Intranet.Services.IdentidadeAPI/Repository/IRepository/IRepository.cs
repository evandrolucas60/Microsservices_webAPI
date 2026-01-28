using System.Linq.Expressions;

namespace Intranet.Services.IdentidadeAPI.Repository.IRepository;

public interface IRepository<T>
{
    Task<IEnumerable<T>> GetAllAsync();
    Task<IEnumerable<T>> GetAllAsync(Expression<Func<T, bool>> predicate);
    //Task<T> GetAsync(Expression<Func<T, bool>> predicate);

    Task<T> GetAsync(
    Expression<Func<T, bool>> predicate,
    string? includeProperties = null
    );

    T? Create(T Entity);
    T? Update(T Entity);
    T? Delete(T Entity);
}

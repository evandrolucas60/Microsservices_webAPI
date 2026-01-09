using Intranet.Services.EscalaAPI.Data;
using Intranet.Services.EscalaAPI.Repository.IRepository;

namespace Intranet.Services.EscalaAPI.Repository;

public class UnityOfWork : IUnityOfWork
{
    private IEscalaRepository? _escalaRepository;
    public AppDbContext _context;

    public UnityOfWork(AppDbContext context)
    {
        _context = context;
    }

    public IEscalaRepository EscalaRepository
    {
        get
        {
            return _escalaRepository = _escalaRepository ?? new EscalaRepository(_context);
        }
    }

    public async Task CommitAsync()
    {
        await _context.SaveChangesAsync();
    }

    public void Dispose()
    {
        _context.Dispose();
    }
}

using Intranet.Services.DocumentoAPI.Data;
using Intranet.Services.DocumentoAPI.Repository.IRepository;

namespace Intranet.Services.EscalaAPI.Repository;

public class UnityOfWork : IUnityOfWork
{
    private IDocumentoRepository? _escalaRepository;
    public AppDbContext _context;

    public UnityOfWork(AppDbContext context)
    {
        _context = context;
    }

    public IDocumentoRepository EscalaRepository
    {
        get
        {
            return _escalaRepository = _escalaRepository ?? new DocumentoRepository(_context);
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

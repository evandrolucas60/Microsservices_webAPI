namespace Intranet.Services.DocumentoAPI.Repository.IRepository;

public interface IUnityOfWork
{
    IDocumentoRepository EscalaRepository { get; }
    Task CommitAsync();
}

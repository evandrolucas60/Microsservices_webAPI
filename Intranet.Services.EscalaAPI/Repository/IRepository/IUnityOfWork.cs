namespace Intranet.Services.EscalaAPI.Repository.IRepository;

public interface IUnityOfWork
{
    IEscalaRepository EscalaRepository { get; }
    Task CommitAsync();
}

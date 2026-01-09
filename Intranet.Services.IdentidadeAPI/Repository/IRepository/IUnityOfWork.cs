namespace Intranet.Services.IdentidadeAPI.Repository.IRepository;

public interface IUnityOfWork
{
    IProfileRepository ProfileRepository { get; }
    IMemberRepository MemberRepository { get; }
    IUserRepository UserRepository { get; }
    IWorkRepository WorkRepository { get; }
    IEducationRepository EducationRepository { get; }
    Task CommitAsync();
}

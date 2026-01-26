namespace Intranet.Services.IdentidadeAPI.Repository.IRepository;

public interface IUnityOfWork
{
    IAddressRepository AddressRepository { get; }
    IProfileRepository ProfileRepository { get; }
    IMemberRepository MemberRepository { get; }
    IUserRepository UserRepository { get; }
    IWorkRepository WorkRepository { get; }
    IEducationRepository EducationRepository { get; }
    Task CommitAsync();
}

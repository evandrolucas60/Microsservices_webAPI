using Intranet.Services.IdentidadeAPI.Data;
using Intranet.Services.IdentidadeAPI.Repository.IRepository;

namespace Intranet.Services.IdentidadeAPI.Repository;

public class UnityOfWork : IUnityOfWork
{
    private IProfileRepository _profileRepository;
    private IMemberRepository _memberRepository;
    private IUserRepository _userRepository;
    private IWorkRepository? _workRepository;
    private IEducationRepository? _educationRepository;
    public AppDbContext _context;

    public UnityOfWork(AppDbContext context)
    {
        _context = context;
    }
    public IProfileRepository ProfileRepository
    {
        get
        {
            return _profileRepository = _profileRepository ?? new ProfileRepository(_context);
        }
    }

    public IMemberRepository MemberRepository
    {
        get
        {
            return _memberRepository = _memberRepository ?? new MemberRepository(_context);
        }
    }

    public IUserRepository UserRepository
    {
        get
        {
            return _userRepository = _userRepository ?? new UserRepository(_context);
        }
    }

    public IWorkRepository WorkRepository
    {
        get
        {
            return _workRepository = _workRepository ?? new WorkRepository(_context);
        }
    }

    public IEducationRepository EducationRepository
    {
        get
        {
            return _educationRepository = _educationRepository ?? new EducationRepository(_context);
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

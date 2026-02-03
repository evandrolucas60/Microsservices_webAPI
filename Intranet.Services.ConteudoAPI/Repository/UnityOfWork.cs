using Intranet.Services.ConteudoAPI.Data;
using Intranet.Services.ConteudoAPI.Repository.IRepository;

namespace Intranet.Services.ConteudoAPI.Repository;

public class UnityOfWork : IUnityOfWork
{
    private IPostRepository _postRepository ;
    private ICommentRepository _commentRepository;
    private ILikeRepository _likeRepository;
    public AppDbContext _context;

    public UnityOfWork(AppDbContext context)
    {
        _context = context;
    }

    public ILikeRepository LikeRepository
    {
        get
        {
            return _likeRepository = _likeRepository ?? new LikeRepository(_context);
        }
    }

    public IPostRepository PostRepository
    {
        get
        {
            return _postRepository = _postRepository ?? new PostRepository(_context);
        }
    }

    public ICommentRepository CommentRepository
    {
        get
        {
            return _commentRepository = _commentRepository ?? new CommentRepository(_context);
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

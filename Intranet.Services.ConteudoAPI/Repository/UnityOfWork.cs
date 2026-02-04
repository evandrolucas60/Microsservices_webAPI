using Intranet.Services.ConteudoAPI.Data;
using Intranet.Services.ConteudoAPI.Repository.IRepository;

namespace Intranet.Services.ConteudoAPI.Repository;

public class UnityOfWork : IUnityOfWork
{
    private IPostRepository _postRepository ;
    private ICommentRepository _commentRepository;
    private IPostLikeRepository _postLikeRepository;
    private ICommentLikeRepository _commentLikeRepository;
    public AppDbContext _context;

    public UnityOfWork(AppDbContext context)
    {
        _context = context;
    }

    public ICommentLikeRepository CommentLikeRepository
    {
        get
        {
            return _commentLikeRepository = _commentLikeRepository ?? new CommentLikeRepository(_context);
        }
    }

    public IPostLikeRepository PostLikeRepository
    {
        get
        {
            return _postLikeRepository = _postLikeRepository ?? new PostLikeRepository(_context);
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

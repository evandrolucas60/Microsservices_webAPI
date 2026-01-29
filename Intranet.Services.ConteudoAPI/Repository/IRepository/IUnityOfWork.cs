namespace Intranet.Services.ConteudoAPI.Repository.IRepository;

public interface IUnityOfWork
{
    IPostRepository PostRepository { get; }
    ICommentRepository CommentRepository { get; }

    Task CommitAsync();
}

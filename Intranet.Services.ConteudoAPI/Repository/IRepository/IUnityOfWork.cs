namespace Intranet.Services.ConteudoAPI.Repository.IRepository;

public interface IUnityOfWork
{
    IPostRepository PostRepository { get; }
    ICommentRepository CommentRepository { get; }
    ILikeRepository LikeRepository { get; }

    Task CommitAsync();
}

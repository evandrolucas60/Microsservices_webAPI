namespace Intranet.Services.ConteudoAPI.Models.DTOs;

public class PostDto
{
    public int PostId { get; set; }
    public Guid MemberId { get; set; }
    public string? Text { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int TotalLikes { get; set; }
    public int TotalComments { get; set; }

    /// <summary>Se o colaborador autenticado já curtiu (calculado pelo token).</summary>
    public bool LikedByMe { get; set; }

    /// <summary>Quantas vezes o post foi compartilhado.</summary>
    public int TotalShares { get; set; }

    /// <summary>Compartilhamento: autor do post original (mesmo que o original tenha sido excluído).</summary>
    public Guid? SharedFromMemberId { get; set; }

    /// <summary>Post original (null se não for compartilhamento ou se o original foi excluído).</summary>
    public SharedPostDto? SharedPost { get; set; }

    /// <summary>Se o colaborador autenticado já compartilhou este post.</summary>
    public bool SharedByMe { get; set; }
}

/// <summary>Post original exibido dentro de um compartilhamento.</summary>
public class SharedPostDto
{
    public int PostId { get; set; }
    public Guid MemberId { get; set; }
    public string? Text { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Compartilhar um post na própria timeline, com comentário opcional.</summary>
public class SharePostDto
{
    public string? Text { get; set; }
}

/// <summary>Estado da curtida após curtir/descurtir.</summary>
public class LikeStateDto
{
    public int TotalLikes { get; set; }
    public bool LikedByMe { get; set; }
}

/// <summary>Quem curtiu (post ou comentário), com nome e foto vindos do IdentidadeAPI.</summary>
public class LikerDto
{
    public Guid MemberId { get; set; }
    public string? Nome { get; set; }
    public string? Foto { get; set; }
    public DateTime CurtidoEm { get; set; }
}

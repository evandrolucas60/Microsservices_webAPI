namespace Intranet.Services.ConteudoAPI.Models.DTOs;

/// <summary>Item da listagem de artigos (sem o conteúdo completo).</summary>
public class ArticleListItemDto
{
    public int ArticleId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public string? CoverImageUrl { get; set; }
    public string? VideoUrl { get; set; }
    public Guid MemberId { get; set; }
    public DateTime CreatedAt { get; set; }
    public int TotalLikes { get; set; }
    public int TotalComments { get; set; }
}

/// <summary>Artigo completo para a página de leitura.</summary>
public class ArticleDetailDto : ArticleListItemDto
{
    public string? Content { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public bool LikedByMe { get; set; }
    public bool IsAuthor { get; set; }
}

/// <summary>Criação/edição de artigo.</summary>
public class ArticleUpsertDto
{
    public string Type { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public string? Content { get; set; }
    public string? VideoUrl { get; set; }
    public string? CoverImageUrl { get; set; }

    /// <summary>Membro do autor (exibição). A autoria em si vem do token.</summary>
    public Guid MemberId { get; set; }
}

public class ArticleCommentDto
{
    public int ArticleCommentId { get; set; }
    public int ArticleId { get; set; }
    public Guid MemberId { get; set; }
    public string Text { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public bool IsAuthor { get; set; }
}

public class ArticleCommentCreateDto
{
    public string Text { get; set; } = string.Empty;
    public Guid MemberId { get; set; }
}

public class ArticleLikeResultDto
{
    public int TotalLikes { get; set; }
    public bool LikedByMe { get; set; }
}

/// <summary>Contrato enviado ao NotificacaoAPI para disparar a newsletter.</summary>
public class ArticleNewsletterRequest
{
    public int ArticleId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public string? CoverImageUrl { get; set; }

    /// <summary>O NotificacaoAPI resolve o nome do autor pela lista de colaboradores.</summary>
    public Guid AuthorMemberId { get; set; }
}

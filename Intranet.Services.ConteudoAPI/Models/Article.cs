namespace Intranet.Services.ConteudoAPI.Models;

/// <summary>
/// Conteúdo publicado na seção Artigos: artigo, vídeo ou informativo.
/// Todo artigo novo é enviado como newsletter para os colaboradores.
/// </summary>
public class Article
{
    public int ArticleId { get; set; }

    /// <summary>"artigo", "video" ou "informativo" (ver <see cref="ArticleTypes"/>).</summary>
    public string Type { get; set; } = ArticleTypes.Artigo;

    public string Title { get; set; } = string.Empty;
    public string? Summary { get; set; }

    /// <summary>HTML do editor (sanitizado no frontend na exibição).</summary>
    public string? Content { get; set; }

    public string? VideoUrl { get; set; }
    public string? CoverImageUrl { get; set; }

    /// <summary>Membro autor (IdentidadeAPI), usado para exibir nome/foto.</summary>
    public Guid MemberId { get; set; }

    /// <summary>Claim "id" do JWT do autor: base da autorização para editar/excluir.</summary>
    public string AuthorUserId { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    /// <summary>Quando a newsletter foi aceita pelo NotificacaoAPI (null = não enviada).</summary>
    public DateTime? NewsletterQueuedAt { get; set; }

    public ICollection<ArticleComment> Comments { get; set; } = new List<ArticleComment>();
    public ICollection<ArticleLike> Likes { get; set; } = new List<ArticleLike>();
}

public static class ArticleTypes
{
    public const string Artigo = "artigo";
    public const string Video = "video";
    public const string Informativo = "informativo";

    public static readonly string[] All = { Artigo, Video, Informativo };

    public static bool IsValid(string? type) => type is not null && All.Contains(type);
}

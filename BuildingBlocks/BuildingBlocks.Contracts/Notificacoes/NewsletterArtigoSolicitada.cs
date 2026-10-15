namespace BuildingBlocks.Contracts.Notificacoes;

/// <summary>
/// Evento: um artigo foi publicado e a newsletter deve ser enviada a todos os colaboradores.
/// Publicado pelo ConteudoAPI; consumido pelo NotificacaoAPI.
/// </summary>
public record NewsletterArtigoSolicitada
{
    public int ArtigoId { get; init; }

    /// <summary>"artigo", "video" ou "informativo".</summary>
    public string Tipo { get; init; } = string.Empty;

    public string Titulo { get; init; } = string.Empty;
    public string? Resumo { get; init; }
    public string? CapaUrl { get; init; }
    public Guid AutorMemberId { get; init; }
}

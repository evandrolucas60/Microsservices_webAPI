namespace BuildingBlocks.Contracts.Notificacoes;

/// <summary>
/// Evento: um serviço pede que colaboradores sejam notificados (in-app e, opcionalmente, por e-mail).
/// Publicado por Conteúdo, Comunicação e Comunidades; consumido pelo NotificacaoAPI.
/// </summary>
public record NotificacaoSolicitada
{
    public Guid[] DestinatariosMemberIds { get; init; } = Array.Empty<Guid>();

    /// <summary>Texto curto (até 500 caracteres).</summary>
    public string Conteudo { get; init; } = string.Empty;

    /// <summary>Ver <see cref="CategoriasNotificacao"/>.</summary>
    public string Categoria { get; init; } = string.Empty;

    /// <summary>Caminho relativo no portal (ex.: /post/10).</summary>
    public string? Link { get; init; }

    public bool EnviarEmail { get; init; }

    /// <summary>Serviço que pediu a notificação (para logs).</summary>
    public string ServicoOrigem { get; init; } = string.Empty;

    public DateTime SolicitadaEm { get; init; } = DateTime.UtcNow;
}

/// <summary>Categorias aceitas pelo NotificacaoAPI.</summary>
public static class CategoriasNotificacao
{
    public const string Mensagem = "message";
    public const string Grupo = "group";
    public const string Post = "post";
    public const string Comentario = "comment";
    public const string Artigo = "article";
}

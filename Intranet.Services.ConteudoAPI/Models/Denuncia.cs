namespace Intranet.Services.ConteudoAPI.Models;

/// <summary>
/// Denúncia de conteúdo (post, artigo ou comentário de post) feita por um colaborador
/// e analisada pelo superAdmin no painel de gestão. Autor e resumo do alvo são guardados
/// no momento da denúncia, para continuarem visíveis mesmo se o conteúdo for removido.
/// </summary>
public class Denuncia
{
    public int Id { get; set; }

    /// <summary>"post", "artigo" ou "comentario" (ver <see cref="DenunciaTipos"/>).</summary>
    public string Tipo { get; set; } = string.Empty;

    /// <summary>PostId, ArticleId ou CommentId, conforme o tipo.</summary>
    public int AlvoId { get; set; }

    /// <summary>Autor do conteúdo denunciado (registrado na criação da denúncia).</summary>
    public Guid AlvoAutorMemberId { get; set; }

    /// <summary>Título ou texto do conteúdo, sem HTML (até 200 caracteres), registrado na criação.</summary>
    public string? AlvoResumo { get; set; }

    /// <summary>Ver <see cref="DenunciaMotivos"/>.</summary>
    public string Motivo { get; set; } = string.Empty;

    public string? Detalhes { get; set; }

    public Guid DenuncianteMemberId { get; set; }

    /// <summary>"pendente", "resolvida" ou "descartada" (ver <see cref="DenunciaStatus"/>).</summary>
    public string Status { get; set; } = DenunciaStatus.Pendente;

    public DateTime CriadaEm { get; set; } = DateTime.UtcNow;

    /// <summary>Claim "id" do superAdmin que analisou a denúncia.</summary>
    public string? ResolvidaPorUserId { get; set; }
    public DateTime? ResolvidaEm { get; set; }

    /// <summary>Observação da administração ao resolver/descartar.</summary>
    public string? Resolucao { get; set; }
}

public static class DenunciaTipos
{
    public const string Post = "post";
    public const string Artigo = "artigo";
    public const string Comentario = "comentario";

    public static readonly string[] Todos = { Post, Artigo, Comentario };

    public static bool IsValid(string? tipo) => tipo is not null && Todos.Contains(tipo);
}

public static class DenunciaMotivos
{
    public static readonly string[] Todos = { "spam", "ofensivo", "assedio", "falso", "inapropriado", "outro" };

    public static bool IsValid(string? motivo) => motivo is not null && Todos.Contains(motivo);
}

public static class DenunciaStatus
{
    public const string Pendente = "pendente";
    public const string Resolvida = "resolvida";
    public const string Descartada = "descartada";

    public static readonly string[] Todos = { Pendente, Resolvida, Descartada };

    public static bool IsValid(string? status) => status is not null && Todos.Contains(status);
}

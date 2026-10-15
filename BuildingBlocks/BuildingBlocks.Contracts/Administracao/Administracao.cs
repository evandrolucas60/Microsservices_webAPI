namespace BuildingBlocks.Contracts.Administracao;

/// <summary>
/// Serviços que o superAdmin pode restringir por usuário (painel de gestão).
/// As chaves são usadas no atributo [RequerServico("...")] dos endpoints.
/// </summary>
public static class ServicosRestringiveis
{
    public const string Publicacoes = "publicacoes";
    public const string Comentarios = "comentarios";
    public const string Curtidas = "curtidas";
    public const string Artigos = "artigos";
    public const string Chat = "chat";
    public const string Mensagens = "mensagens";
    public const string Grupos = "grupos";
    public const string Arquivos = "arquivos";

    /// <summary>Chave → nome exibido.</summary>
    public static readonly IReadOnlyDictionary<string, string> Nomes = new Dictionary<string, string>
    {
        [Publicacoes] = "Publicar e compartilhar posts no feed",
        [Comentarios] = "Comentar posts e artigos",
        [Curtidas] = "Curtir posts, comentários e artigos",
        [Artigos] = "Publicar e editar artigos",
        [Chat] = "Usar o chat",
        [Mensagens] = "Enviar e responder mensagens",
        [Grupos] = "Participar de grupos (criar, solicitar acesso, publicar)",
        [Arquivos] = "Enviar arquivos (lojas do GC e certificados)",
    };

    public static bool IsValid(string? chave) => chave is not null && Nomes.ContainsKey(chave);
}

/// <summary>
/// Evento: bloqueio/restrições de um usuário mudaram (publicado pelo AuthAPI).
/// Cada serviço atualiza o seu cache imediatamente.
/// </summary>
public record RestricoesUsuarioAlteradas
{
    public string UserId { get; init; } = string.Empty;
    public bool Bloqueado { get; init; }
    public DateTime? BloqueadoAte { get; init; }
    public string[] Servicos { get; init; } = Array.Empty<string>();
    public DateTime Em { get; init; } = DateTime.UtcNow;
}

/// <summary>Consulta (requisição/resposta) ao AuthAPI: o usuário está bloqueado? Quais serviços estão restritos?</summary>
public record ObterRestricoesUsuario
{
    public string UserId { get; init; } = string.Empty;
}

public record RestricoesUsuarioResposta
{
    public string UserId { get; init; } = string.Empty;
    public bool Bloqueado { get; init; }
    public DateTime? BloqueadoAte { get; init; }
    public string[] Servicos { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Evento: erro inesperado (HTTP 5xx) em algum serviço, com o usuário que o recebeu.
/// Publicado pelo ExceptionHandlingMiddleware compartilhado; armazenado pelo AuditoriaAPI.
/// </summary>
public record ErroRegistrado
{
    public string Servico { get; init; } = string.Empty;
    public string? UserId { get; init; }
    public string Metodo { get; init; } = string.Empty;
    public string Caminho { get; init; } = string.Empty;
    public int Status { get; init; }
    public string Mensagem { get; init; } = string.Empty;

    /// <summary>Tipo, mensagem e pilha da exceção (truncado).</summary>
    public string? Excecao { get; init; }
    public string? CorrelationId { get; init; }
    public DateTime Em { get; init; } = DateTime.UtcNow;
}

/// <summary>Evento: tentativa de login (sucesso ou falha). Publicado pelo AuthAPI; armazenado pelo AuditoriaAPI.</summary>
public record AcessoRegistrado
{
    public string? UserId { get; init; }
    public string? UserName { get; init; }
    public bool Sucesso { get; init; }

    /// <summary>Motivo da falha (credenciais inválidas, conta bloqueada...).</summary>
    public string? Motivo { get; init; }
    public string? Ip { get; init; }
    public DateTime Em { get; init; } = DateTime.UtcNow;
}

namespace Intranet.Services.AuditoriaAPI.Models;

/// <summary>Erro inesperado (HTTP 5xx) registrado por algum serviço (evento ErroRegistrado).</summary>
public class ErroLog
{
    public long Id { get; set; }
    public string Servico { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public string Metodo { get; set; } = string.Empty;
    public string Caminho { get; set; } = string.Empty;
    public int Status { get; set; }
    public string Mensagem { get; set; } = string.Empty;

    /// <summary>Tipo, mensagem e pilha da exceção.</summary>
    public string? Excecao { get; set; }
    public string? CorrelationId { get; set; }
    public DateTime Em { get; set; }
}

/// <summary>Tentativa de login, com sucesso ou falha (evento AcessoRegistrado do AuthAPI).</summary>
public class AcessoLog
{
    public long Id { get; set; }
    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public bool Sucesso { get; set; }
    public string? Motivo { get; set; }
    public string? Ip { get; set; }
    public DateTime Em { get; set; }
}

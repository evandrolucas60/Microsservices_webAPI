namespace Intranet.Services.AuditoriaAPI.Models.DTOs;

/// <summary>Envelope padrão: { result, isSuccess, message }.</summary>
public class ResponseDto
{
    public object? Result { get; set; }
    public bool IsSuccess { get; set; } = true;
    public string Message { get; set; } = "";
}

public class ErroDto
{
    public long Id { get; set; }
    public string Servico { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public string Metodo { get; set; } = string.Empty;
    public string Caminho { get; set; } = string.Empty;
    public int Status { get; set; }
    public string Mensagem { get; set; } = string.Empty;
    public string? CorrelationId { get; set; }
    public DateTime Em { get; set; }
}

public class ErroDetalheDto : ErroDto
{
    public string? Excecao { get; set; }
}

public class ErrosResumoDto
{
    public int Total { get; set; }
    public List<ErrosPorServicoDto> PorServico { get; set; } = new();
    public List<ErrosPorDiaDto> PorDia { get; set; } = new();
    public List<ErrosPorUsuarioDto> TopUsuarios { get; set; } = new();
}

public class ErrosPorServicoDto
{
    public string Servico { get; set; } = string.Empty;
    public int Total { get; set; }
}

public class ErrosPorDiaDto
{
    /// <summary>yyyy-MM-dd (UTC).</summary>
    public string Dia { get; set; } = string.Empty;
    public int Total { get; set; }
}

public class ErrosPorUsuarioDto
{
    public string UserId { get; set; } = string.Empty;
    public int Total { get; set; }
}

public class AcessoDto
{
    public long Id { get; set; }
    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public bool Sucesso { get; set; }
    public string? Motivo { get; set; }
    public string? Ip { get; set; }
    public DateTime Em { get; set; }
}

public class AcessosMetricasDto
{
    public int TotalLogins { get; set; }
    public int UsuariosUnicos { get; set; }
    public int Falhas { get; set; }
    public List<AcessosPorDiaDto> PorDia { get; set; } = new();
}

public class AcessosPorDiaDto
{
    /// <summary>yyyy-MM-dd (UTC).</summary>
    public string Dia { get; set; } = string.Empty;
    public int Logins { get; set; }
    public int UsuariosUnicos { get; set; }
    public int Falhas { get; set; }
}

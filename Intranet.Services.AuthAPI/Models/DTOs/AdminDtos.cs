namespace Intranet.Services.AuthAPI.Models.DTOs;

/// <summary>Envelope padrão das respostas do painel de gestão: { result, isSuccess, message }.</summary>
public class ResponseDto
{
    public object? Result { get; set; }
    public bool IsSuccess { get; set; } = true;
    public string Message { get; set; } = "";
}

/// <summary>Usuário no painel de gestão (dados da conta + colaborador vindo do IdentidadeAPI).</summary>
public class UsuarioAdminDto
{
    public string UserId { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? UserName { get; set; }
    public string[] Roles { get; set; } = Array.Empty<string>();
    public bool Bloqueado { get; set; }

    /// <summary>Fim do bloqueio (UTC); null = indeterminado ou não bloqueado.</summary>
    public DateTime? BloqueadoAte { get; set; }
    public string? MotivoBloqueio { get; set; }
    public string[] Restricoes { get; set; } = Array.Empty<string>();

    // Colaborador (IdentidadeAPI); null quando indisponível ou sem cadastro
    public Guid? MemberId { get; set; }
    public string? Nome { get; set; }
    public string? Foto { get; set; }
    public string? Departamento { get; set; }
    public string? Cargo { get; set; }
    public Guid? GestorMemberId { get; set; }
    public DateTime? UltimoAcesso { get; set; }
}

public class BloquearUsuarioRequest
{
    public string? Motivo { get; set; }

    /// <summary>Fim do bloqueio; null = indeterminado.</summary>
    public DateTime? Ate { get; set; }
}

public class RestricoesUsuarioRequest
{
    public string[]? Servicos { get; set; }
    public string? Motivo { get; set; }
}

public class CriarRoleRequest
{
    public string? Nome { get; set; }
}

public class AtribuirRoleRequest
{
    public string? Role { get; set; }
}

public class ServicoRestringivelDto
{
    public string Chave { get; set; } = string.Empty;
    public string Nome { get; set; } = string.Empty;
}

public class RoleDto
{
    public string Nome { get; set; } = string.Empty;
    public int TotalUsuarios { get; set; }
}

namespace Intranet.Services.AuthAPI.Models;

/// <summary>
/// Bloqueio e restrições definidos pelo superAdmin (painel de gestão). O bloqueio em si usa o
/// lockout do Identity (LockoutEnd); aqui ficam o motivo, quem fez e os serviços restritos.
/// </summary>
public class UserRestriction
{
    /// <summary>Id do usuário (AspNetUsers.Id); chave primária e estrangeira.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Chaves de ServicosRestringiveis que o usuário não pode usar.</summary>
    public string[] Servicos { get; set; } = Array.Empty<string>();

    public string? MotivoRestricao { get; set; }

    /// <summary>Motivo do bloqueio manual (null quando o usuário não está bloqueado pelo superAdmin).</summary>
    public string? MotivoBloqueio { get; set; }
    public DateTime? BloqueadoEm { get; set; }
    public string? BloqueadoPorUserId { get; set; }

    public string? AtualizadoPorUserId { get; set; }
    public DateTime AtualizadoEm { get; set; }
}

namespace BuildingBlocks.Contracts.Identidade;

/// <summary>Colaboradores das contas informadas (UserId do AuthAPI). Usado pelo painel de gestão.</summary>
public record ObterMembrosPorUsuarios
{
    public string[] UserIds { get; init; } = Array.Empty<string>();
}

public record MembrosPorUsuariosResposta
{
    public MembroDeUsuario[] Itens { get; init; } = Array.Empty<MembroDeUsuario>();
}

public record MembroDeUsuario
{
    public string UserId { get; init; } = string.Empty;
    public Guid MemberId { get; init; }
    public string? Nome { get; init; }
    public string? Foto { get; init; }
    public string? Departamento { get; init; }
    public string? Cargo { get; init; }
    public Guid? GestorMemberId { get; init; }
    public DateTime? UltimoAcesso { get; init; }
}

namespace BuildingBlocks.Contracts.Autenticacao;

/// <summary>
/// Evento: um usuário fez login no portal. Publicado pelo AuthAPI; o IdentidadeAPI consome
/// e atualiza o último acesso do colaborador (painel "Most Recent Signin" da home).
/// </summary>
public record UsuarioAutenticado
{
    /// <summary>Id do usuário (claim "id" do JWT / Member.UserId).</summary>
    public string UserId { get; init; } = string.Empty;

    public DateTime AutenticadoEm { get; init; } = DateTime.UtcNow;
}

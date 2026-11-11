namespace BuildingBlocks.Contracts.Identidade;

/// <summary>
/// Evento: uma conta nova foi criada no portal (cadastro em api/auth/register). Publicado pelo AuthAPI;
/// o IdentidadeAPI consome e cria/completa o Member + Work + Profile do colaborador, para que o card
/// da página de Ramais já apareça preenchido. Idempotente: só preenche campos ainda vazios.
/// </summary>
public record ColaboradorCadastrado
{
    /// <summary>Id do usuário no AuthAPI (claim "id" do JWT / Member.UserId).</summary>
    public string UserId { get; init; } = string.Empty;

    public string Email { get; init; } = string.Empty;

    /// <summary>Nome completo (Member.Name).</summary>
    public string Nome { get; init; } = string.Empty;

    /// <summary>Setor (Work.Department); um dos valores de <see cref="DadosCadastroColaborador.Setores"/>.</summary>
    public string Setor { get; init; } = string.Empty;

    /// <summary>Cargo (Work.Title).</summary>
    public string Cargo { get; init; } = string.Empty;

    /// <summary>Loja/unidade (Work.Location); um dos valores de <see cref="DadosCadastroColaborador.Lojas"/>.</summary>
    public string Loja { get; init; } = string.Empty;

    /// <summary>Ramal interno (Work.Ramal), só dígitos.</summary>
    public string Ramal { get; init; } = string.Empty;

    /// <summary>Telefone no formato (81) 99999-9999 ou (81) 3333-4444 (Member.Phone).</summary>
    public string Telefone { get; init; } = string.Empty;

    public DateTime Em { get; init; } = DateTime.UtcNow;
}

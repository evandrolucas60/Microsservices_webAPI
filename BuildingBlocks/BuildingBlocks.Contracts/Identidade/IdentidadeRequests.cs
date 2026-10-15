namespace BuildingBlocks.Contracts.Identidade;

// Consultas ao IdentidadeAPI por requisição/resposta (MassTransit request client).
// O IdentidadeAPI é o dono dos dados de colaboradores; os demais serviços não acessam o banco dele.

/// <summary>Membro do usuário autenticado (claim "id" do JWT).</summary>
public record ObterMembroPorUsuario
{
    public string UserId { get; init; } = string.Empty;
}

public record MembroPorUsuarioResposta
{
    public bool Encontrado { get; init; }
    public Guid MemberId { get; init; }
    public string? Nome { get; init; }
    public string? Departamento { get; init; }
}

/// <summary>Nome e foto dos membros informados (ids inexistentes ficam de fora da resposta).</summary>
public record ObterMembros
{
    public Guid[] MemberIds { get; init; } = Array.Empty<Guid>();
}

public record MembrosResposta
{
    public MembroResumo[] Membros { get; init; } = Array.Empty<MembroResumo>();
}

public record MembroResumo
{
    public Guid MemberId { get; init; }
    public string? Nome { get; init; }
    public string? Foto { get; init; }
}

/// <summary>Colaboradores de um setor (ex.: "T.I" para a escala).</summary>
public record ObterColaboradoresPorDepartamento
{
    public string Departamento { get; init; } = string.Empty;
}

public record ColaboradoresResposta
{
    public ColaboradorResumo[] Colaboradores { get; init; } = Array.Empty<ColaboradorResumo>();
}

public record ColaboradorResumo
{
    public Guid MemberId { get; init; }
    public string? Nome { get; init; }
    public string? Email { get; init; }
    public string? Cargo { get; init; }
    public string? Departamento { get; init; }
    public string? Foto { get; init; }
}

/// <summary>E-mails dos colaboradores (newsletter e notificações por e-mail). MemberIds vazio = todos.</summary>
public record ObterDestinatariosEmail
{
    public Guid[] MemberIds { get; init; } = Array.Empty<Guid>();
}

public record DestinatariosEmailResposta
{
    public DestinatarioEmail[] Destinatarios { get; init; } = Array.Empty<DestinatarioEmail>();
}

public record DestinatarioEmail
{
    public Guid MemberId { get; init; }
    public string Email { get; init; } = string.Empty;
    public string? Nome { get; init; }
}

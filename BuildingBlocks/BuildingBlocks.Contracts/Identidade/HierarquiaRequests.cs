namespace BuildingBlocks.Contracts.Identidade;

// Hierarquia de gestores (gestor direto de cada colaborador), mantida pelo IdentidadeAPI.
// Usada por outros serviços em fluxos de aprovação (ex.: painel de projetos: o gestor aprova o liderado).

/// <summary>Cadeia de gestores de um membro, do gestor direto até o topo da hierarquia.</summary>
public record ObterCadeiaGestores
{
    public Guid MemberId { get; init; }
}

public record CadeiaGestoresResposta
{
    public Guid MemberId { get; init; }

    /// <summary>false quando o membro não existe (Gestores fica vazio).</summary>
    public bool Encontrado { get; init; }

    /// <summary>Gestores em ordem: [0] = gestor direto, depois o gestor dele, até o topo. Vazio = sem gestor.</summary>
    public MembroResumo[] Gestores { get; init; } = Array.Empty<MembroResumo>();
}

/// <summary>Confere se GestorMemberId é gestor (direto ou em qualquer nível acima) de MemberId.</summary>
public record VerificarGestor
{
    public Guid GestorMemberId { get; init; }
    public Guid MemberId { get; init; }
}

public record VerificarGestorResposta
{
    /// <summary>GestorMemberId é o gestor direto de MemberId.</summary>
    public bool EhGestorDireto { get; init; }

    /// <summary>GestorMemberId aparece em algum nível da cadeia de gestores de MemberId (inclui o direto).</summary>
    public bool EhGestorNaCadeia { get; init; }
}

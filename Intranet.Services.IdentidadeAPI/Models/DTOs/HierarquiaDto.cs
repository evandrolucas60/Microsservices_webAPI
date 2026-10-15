namespace Intranet.Services.IdentidadeAPI.Models.DTOs;

/// <summary>Resumo de um colaborador na hierarquia (sem dados pessoais).</summary>
public class MembroHierarquiaDto
{
    public Guid MemberId { get; set; }
    public string? Nome { get; set; }
    public string? Cargo { get; set; }
    public string? Departamento { get; set; }
    public string? Foto { get; set; }
    public Guid? GestorMemberId { get; set; }
}

/// <summary>Hierarquia de um colaborador: gestor direto, cadeia acima e liderados diretos.</summary>
public class HierarquiaMembroDto
{
    public MembroHierarquiaDto Membro { get; set; } = null!;
    public MembroHierarquiaDto? Gestor { get; set; }

    /// <summary>Gestores do direto ([0]) até o topo.</summary>
    public List<MembroHierarquiaDto> Cadeia { get; set; } = new();

    /// <summary>true quando a cadeia passou do limite de níveis (ou há ciclo antigo nos dados).</summary>
    public bool CadeiaIncompleta { get; set; }

    /// <summary>Liderados diretos, por nome.</summary>
    public List<MembroHierarquiaDto> Liderados { get; set; } = new();

    public int TotalCadeia { get; set; }
    public int TotalLiderados { get; set; }
}

/// <summary>Corpo de PUT api/Hierarquia/{memberId}/gestor (null remove o gestor).</summary>
public class AlterarGestorDto
{
    public Guid? GestorMemberId { get; set; }
}

/// <summary>Nó do organograma (a árvore é montada no frontend pelo GestorMemberId).</summary>
public class OrganogramaNoDto : MembroHierarquiaDto
{
    /// <summary>Gestor de fora do setor consultado, incluído para completar a cadeia.</summary>
    public bool ForaDoSetor { get; set; }
}

public class OrganogramaDto
{
    /// <summary>Setor consultado (null = empresa toda).</summary>
    public string? Departamento { get; set; }
    public List<OrganogramaNoDto> Nos { get; set; } = new();

    /// <summary>Colaboradores do setor (sem contar os gestores de fora).</summary>
    public int TotalDoSetor { get; set; }

    /// <summary>true quando o limite de colaboradores foi atingido e a lista foi cortada.</summary>
    public bool Truncado { get; set; }
}

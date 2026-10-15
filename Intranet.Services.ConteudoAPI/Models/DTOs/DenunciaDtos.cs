namespace Intranet.Services.ConteudoAPI.Models.DTOs;

/// <summary>Corpo de POST api/denuncias.</summary>
public class DenunciaCreateDto
{
    public string? Tipo { get; set; }
    public int AlvoId { get; set; }
    public string? Motivo { get; set; }
    public string? Detalhes { get; set; }
}

/// <summary>Corpo de descartar / remover conteúdo (painel de gestão).</summary>
public class DenunciaAcaoDto
{
    public string? Observacao { get; set; }
}

public class DenunciaAdminDto
{
    public int Id { get; set; }
    public string Tipo { get; set; } = string.Empty;
    public int AlvoId { get; set; }
    public bool AlvoExiste { get; set; }
    public string? AlvoResumo { get; set; }
    public Guid AlvoAutorMemberId { get; set; }

    /// <summary>Link relativo no portal (/post/{id} ou /artigos/{id}); null se não houver como abrir.</summary>
    public string? AlvoLink { get; set; }
    public string Motivo { get; set; } = string.Empty;
    public string? Detalhes { get; set; }
    public Guid DenuncianteMemberId { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime CriadaEm { get; set; }
    public int TotalDenunciasDoAlvo { get; set; }
    public string? ResolvidaPorUserId { get; set; }
    public DateTime? ResolvidaEm { get; set; }
    public string? Resolucao { get; set; }
}

public class DenunciasResumoDto
{
    public int Pendentes { get; set; }
    public int Resolvidas { get; set; }
    public int Descartadas { get; set; }
}

public class MetricasConteudoDiaDto
{
    public string Dia { get; set; } = string.Empty;
    public int Posts { get; set; }
    public int Comentarios { get; set; }
    public int Curtidas { get; set; }
    public int Artigos { get; set; }
    public int Compartilhamentos { get; set; }
}

public class MetricasConteudoTotaisDto
{
    public int Posts { get; set; }
    public int Comentarios { get; set; }
    public int Curtidas { get; set; }
    public int Artigos { get; set; }
    public int Compartilhamentos { get; set; }
}

public class MetricasAutorDto
{
    public Guid MemberId { get; set; }
    public string? Nome { get; set; }
    public int Posts { get; set; }
    public int Artigos { get; set; }
    public int Comentarios { get; set; }
}

public class MetricasConteudoDto
{
    public List<MetricasConteudoDiaDto> PorDia { get; set; } = new();
    public MetricasConteudoTotaisDto Totais { get; set; } = new();
    public List<MetricasAutorDto> TopAutores { get; set; } = new();
}

public class ArquivoArtigoAdminDto
{
    public Guid Id { get; set; }
    public string Nome { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long Tamanho { get; set; }
    public string EnviadoPorUserId { get; set; } = string.Empty;
    public DateTime CriadoEm { get; set; }
    public string Url { get; set; } = string.Empty;
    public bool EmUso { get; set; }
}

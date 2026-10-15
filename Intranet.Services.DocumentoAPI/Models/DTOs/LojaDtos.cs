namespace Intranet.Services.DocumentoAPI.Models.DTOs;

/// <summary>Criação ou renomeação de loja.</summary>
public class LojaNomeDto
{
    public string? Nome { get; set; }
}

/// <summary>Envio de um ou vários arquivos para uma seção da loja (multipart/form-data).</summary>
public class LojaUploadForm
{
    public List<IFormFile>? Arquivos { get; set; }
    public string? Descricao { get; set; }
}

/// <summary>Metadados do arquivo (sem o conteúdo binário).</summary>
public class LojaArquivoDto
{
    public int Id { get; set; }
    public int LojaId { get; set; }
    public string Secao { get; set; } = string.Empty;
    public string Tipo { get; set; } = string.Empty;
    public string NomeArquivo { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long Tamanho { get; set; }
    public string? Descricao { get; set; }
    public string? EnviadoPor { get; set; }
    public DateTime CriadoEm { get; set; }
}

/// <summary>Loja na listagem, com a contagem de arquivos por seção.</summary>
public class LojaResumoDto
{
    public int Id { get; set; }
    public string Nome { get; set; } = string.Empty;
    public DateTime CriadoEm { get; set; }
    public DateTime AtualizadoEm { get; set; }
    public int TotalArquivos { get; set; }
    public Dictionary<string, int> ArquivosPorSecao { get; set; } = new();
}

/// <summary>Seção da loja com seus arquivos (somente metadados).</summary>
public class LojaSecaoDto
{
    public string Nome { get; set; } = string.Empty;
    public int TotalArquivos { get; set; }
    public List<LojaArquivoDto> Arquivos { get; set; } = new();
}

/// <summary>Detalhe da loja: todas as seções fixas, na ordem oficial.</summary>
public class LojaDetalheDto
{
    public int Id { get; set; }
    public string Nome { get; set; } = string.Empty;
    public DateTime CriadoEm { get; set; }
    public DateTime AtualizadoEm { get; set; }
    public string? CriadoPor { get; set; }
    public int TotalArquivos { get; set; }
    public List<LojaSecaoDto> Secoes { get; set; } = new();
}

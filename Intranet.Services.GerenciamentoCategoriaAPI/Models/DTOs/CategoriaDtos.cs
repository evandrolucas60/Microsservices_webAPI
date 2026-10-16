using System.Text.Json.Serialization;

namespace Intranet.Services.GerenciamentoCategoriaAPI.Models.DTOs;

/// <summary>Criação ou alteração de categoria. Ordem omitida: no fim da lista (criação) ou mantida (alteração).</summary>
public class CategoriaSalvarDto
{
    public string? Nome { get; set; }
    public string? Descricao { get; set; }
    public int? Ordem { get; set; }
}

/// <summary>Envio de um item (multipart/form-data): campos titulo, descricao, secao e arquivo.</summary>
public class CategoriaItemUploadForm
{
    public string? Titulo { get; set; }
    public string? Descricao { get; set; }
    public string? Secao { get; set; }
    public IFormFile? Arquivo { get; set; }
}

/// <summary>Alteração dos dados de um item (o arquivo não muda; para trocar, exclua e envie de novo).</summary>
public class CategoriaItemAlterarDto
{
    public string? Titulo { get; set; }
    public string? Descricao { get; set; }

    /// <summary>Vazio remove a seção do item.</summary>
    public string? Secao { get; set; }
}

/// <summary>Categoria na listagem e no detalhe.</summary>
public class CategoriaDto
{
    public int Id { get; set; }
    public string Nome { get; set; } = string.Empty;
    public string? Descricao { get; set; }
    public int Ordem { get; set; }
    public int TotalItens { get; set; }
    public DateTime CriadoEm { get; set; }
    public DateTime AtualizadoEm { get; set; }

    /// <summary>Somente para quem pode gerenciar (não expõe ids de usuário a anônimos).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CriadoPor { get; set; }
}

/// <summary>Detalhe da categoria, com as quantidades por tipo e por seção.</summary>
public class CategoriaDetalheDto : CategoriaDto
{
    public int TotalImagens { get; set; }
    public int TotalDocumentos { get; set; }

    /// <summary>Quantidade de itens por seção (só as seções com itens; itens sem seção não entram).</summary>
    public Dictionary<string, int> ItensPorSecao { get; set; } = new();
}

/// <summary>Metadados do item (sem o conteúdo binário).</summary>
public class CategoriaItemDto
{
    public int Id { get; set; }
    public int CategoriaId { get; set; }
    public string Titulo { get; set; } = string.Empty;
    public string? Descricao { get; set; }
    public string? Secao { get; set; }
    public string Tipo { get; set; } = string.Empty;
    public string NomeArquivo { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long Tamanho { get; set; }
    public DateTime EnviadoEm { get; set; }

    /// <summary>Somente para quem pode gerenciar (não expõe ids de usuário a anônimos).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EnviadoPor { get; set; }
}

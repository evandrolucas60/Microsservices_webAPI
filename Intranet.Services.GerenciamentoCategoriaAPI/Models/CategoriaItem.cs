namespace Intranet.Services.GerenciamentoCategoriaAPI.Models;

/// <summary>Tipos de arquivo de um item da categoria.</summary>
public static class CategoriaItemTipos
{
    public const string Imagem = "image";
    public const string Documento = "document";

    /// <summary>Filtro "tipo" da consulta: imagem/image ou documento/document. Vazio = todos; inválido = null.</summary>
    public static bool TentarInterpretar(string? valor, out string? tipo)
    {
        tipo = null;
        if (string.IsNullOrWhiteSpace(valor)) return true;
        switch (valor.Trim().ToLowerInvariant())
        {
            case "imagem":
            case "imagens":
            case Imagem:
                tipo = Imagem;
                return true;
            case "documento":
            case "documentos":
            case Documento:
                tipo = Documento;
                return true;
            default:
                return false;
        }
    }
}

/// <summary>
/// Material/orientação de uma categoria (foto ou documento), com título, descrição e seção opcional.
/// O conteúdo fica no banco (bytea) e nunca é carregado nas listagens (use projeção sem <see cref="Conteudo"/>).
/// </summary>
public class CategoriaItem
{
    public const int TituloMaxLength = 200;
    public const int DescricaoMaxLength = 2000;
    public const int NomeArquivoMaxLength = 255;

    public int CategoriaItemId { get; set; }

    public int CategoriaId { get; set; }
    public Categoria Categoria { get; set; } = null!;

    public string Titulo { get; set; } = string.Empty;
    public string? Descricao { get; set; }

    /// <summary>Uma das seções de <see cref="GCSecoes.Todas"/> (opcional).</summary>
    public string? Secao { get; set; }

    /// <summary><see cref="CategoriaItemTipos.Imagem"/> ou <see cref="CategoriaItemTipos.Documento"/>.</summary>
    public string Tipo { get; set; } = CategoriaItemTipos.Documento;

    /// <summary>Nome original (sanitizado) do arquivo enviado.</summary>
    public string NomeArquivo { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long Tamanho { get; set; }
    public byte[] Conteudo { get; set; } = Array.Empty<byte>();

    /// <summary>Id do usuário (claim "id" do token) que enviou o item.</summary>
    public string? EnviadoPor { get; set; }

    public DateTime EnviadoEm { get; set; }
}

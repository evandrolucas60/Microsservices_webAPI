namespace Intranet.Services.DocumentoAPI.Models;

/// <summary>Tipos de arquivo de uma seção da loja.</summary>
public static class LojaArquivoTipos
{
    public const string Imagem = "image";
    public const string Documento = "document";
}

/// <summary>
/// Foto ou documento de uma seção da loja. O conteúdo fica no banco (bytea) e nunca
/// é carregado nas listagens (use projeção sem <see cref="Conteudo"/>).
/// </summary>
public class LojaArquivo
{
    public const int NomeArquivoMaxLength = 255;
    public const int DescricaoMaxLength = 300;

    public int LojaArquivoId { get; set; }

    public int LojaId { get; set; }
    public Loja Loja { get; set; } = null!;

    /// <summary>Uma das seções fixas de <see cref="LojaSecoes.Todas"/>.</summary>
    public string Secao { get; set; } = string.Empty;

    /// <summary><see cref="LojaArquivoTipos.Imagem"/> ou <see cref="LojaArquivoTipos.Documento"/>.</summary>
    public string Tipo { get; set; } = LojaArquivoTipos.Documento;

    public string NomeArquivo { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long Tamanho { get; set; }
    public byte[] Conteudo { get; set; } = Array.Empty<byte>();

    public string? Descricao { get; set; }

    /// <summary>Id do usuário (claim "id" do token) que enviou o arquivo.</summary>
    public string? EnviadoPor { get; set; }

    public DateTime CriadoEm { get; set; }
}

namespace Intranet.Services.DocumentoAPI.Models;

/// <summary>
/// Loja do Gerenciamento por Categoria (GC), ex.: "Beberibe".
/// Os arquivos (fotos e documentos) ficam organizados nas seções fixas de <see cref="LojaSecoes"/>.
/// </summary>
public class Loja
{
    public const int NomeMaxLength = 100;

    public int LojaId { get; set; }

    /// <summary>Nome exibido (sem espaços nas pontas).</summary>
    public string Nome { get; set; } = string.Empty;

    /// <summary>Nome em minúsculas, usado no índice único (nome não se repete, ignorando maiúsculas/minúsculas).</summary>
    public string NomeNormalizado { get; set; } = string.Empty;

    public DateTime CriadoEm { get; set; }
    public DateTime AtualizadoEm { get; set; }

    /// <summary>Id do usuário (claim "id" do token) que criou a loja.</summary>
    public string? CriadoPor { get; set; }

    public ICollection<LojaArquivo> Arquivos { get; set; } = new List<LojaArquivo>();

    public static string Normalizar(string nome) => nome.Trim().ToLowerInvariant();
}

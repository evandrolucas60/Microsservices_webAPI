namespace Intranet.Services.GerenciamentoCategoriaAPI.Models;

/// <summary>
/// Categoria do Gerenciamento por Categoria (GC), ex.: "Mercearia". Reúne os materiais e
/// orientações de exposição (<see cref="CategoriaItem"/>).
/// </summary>
public class Categoria
{
    public const int NomeMaxLength = 100;
    public const int DescricaoMaxLength = 1000;

    public int CategoriaId { get; set; }

    /// <summary>Nome exibido (sem espaços nas pontas).</summary>
    public string Nome { get; set; } = string.Empty;

    /// <summary>Nome em minúsculas, usado no índice único (nome não se repete, ignorando maiúsculas/minúsculas).</summary>
    public string NomeNormalizado { get; set; } = string.Empty;

    public string? Descricao { get; set; }

    /// <summary>Posição na navegação do GC (menor primeiro; empate pelo nome).</summary>
    public int Ordem { get; set; }

    /// <summary>Id do usuário (claim "id" do token) que criou a categoria.</summary>
    public string? CriadoPor { get; set; }

    public DateTime CriadoEm { get; set; }
    public DateTime AtualizadoEm { get; set; }

    public ICollection<CategoriaItem> Itens { get; set; } = new List<CategoriaItem>();

    public static string Normalizar(string nome) => nome.Trim().ToLowerInvariant();
}

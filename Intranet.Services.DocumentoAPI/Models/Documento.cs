using System.ComponentModel.DataAnnotations;

namespace Intranet.Services.DocumentoAPI.Models;
public class Documento
{
    public int Id { get; set; }
    [Required(ErrorMessage = "O nome é obrigatório")]
    [StringLength(80)]
    public string? Nome { get; set; }
    public string? DocUrl { get; set; }
    [Required(ErrorMessage = "A qual setor/departamento esse documento pertence?")]
    public string? Departamento { get; set; }
    [Required(ErrorMessage = "Defina uma breve descrição do documento")]
    public string? Descricao { get; set; }
    [Required(ErrorMessage = "Defina a data de Criação do Documento")]
    public DateTime DateTime { get; set; }
}

using System.ComponentModel.DataAnnotations;

namespace Intranet.Services.EscalaAPI.Models;
public class Escala
{
    [Key]
    public int Id { get; set; }
    [Required]
    public string? Nome { get; set; }
    [Required]
    public string? Funcao { get; set; }
    [Required]
    public string? Email { get; set; }
    [Required]
    public string? Telefone { get; set; }
    [Required]
    [DisplayFormat(DataFormatString = "{0:dd-MM-yyyy}", ApplyFormatInEditMode = true)]
    public DateTime DateTime { get; set; }
    [Required]
    public string? Horario { get; set; }
    public string? Dia { get; set; }
    public string? ProfileImg { get; set; }

    /// <summary>Colaborador da T.I escalado (IdentidadeAPI): Nome, Funcao, Email e ProfileImg vêm do cadastro dele.</summary>
    public Guid? MemberId { get; set; }
}

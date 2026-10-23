using System.ComponentModel.DataAnnotations;

namespace Intranet.Services.IdentidadeAPI.Models.DTOs;

public class WorkDto
{
    public int WorkId { get; set; }

    // Cargo, loja e setor formam o card do colaborador na página de Ramais
    [Required(ErrorMessage = "Informe o cargo.")]
    public string Title { get; set; }

    // Opcionais (perfis criados automaticamente começam vazios); null é gravado como ""
    public string? Company { get; set; }

    [Required(ErrorMessage = "Selecione a loja.")]
    public string Location { get; set; }
    public DateTime? AdmissionDate { get; set; }
    public string? Registration { get; set; }
    public string? Description { get; set; }

    [Required(ErrorMessage = "Selecione o setor.")]
    public string Department { get; set; }
    public string? Ramal { get; set; }
    public Guid MemberId { get; set; }
}

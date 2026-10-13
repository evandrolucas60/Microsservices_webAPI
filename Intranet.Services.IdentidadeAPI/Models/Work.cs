using System.ComponentModel.DataAnnotations;

namespace Intranet.Services.IdentidadeAPI.Models;

public class Work
{
    [Key]
    public int WorkId { get; set; }

    public string Title { get; set; }
    public string Company { get; set; }
    public string Location { get; set; }
    // Opcional: perfis criados automaticamente ainda não têm data de admissão
    [DisplayFormat(DataFormatString = "{0:dd-MM-yyyy}", ApplyFormatInEditMode = true)]
    public DateTime? AdmissionDate { get; set; }
    public string Registration { get; set; }
    public string Description { get; set; }
    public string Department { get; set; }

    // Ramal telefônico interno (usado na página de Ramais)
    public string? Ramal { get; set; }

    public Guid MemberId { get; set; }
}

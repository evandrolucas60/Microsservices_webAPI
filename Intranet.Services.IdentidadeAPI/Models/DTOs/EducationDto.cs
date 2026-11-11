using System.ComponentModel.DataAnnotations;

namespace Intranet.Services.IdentidadeAPI.Models.DTOs;

public class EducationDto
{
    public int EducationId { get; set; }

    [Required(ErrorMessage = "Informe a instituição de ensino.")]
    public string School { get; set; }

    [Required(ErrorMessage = "Selecione o nível de escolaridade.")]
    public string Degree { get; set; }
    public DateTime FromYear { get; set; }
    public DateTime ToYear { get; set; }

    [Required(ErrorMessage = "Informe o nome do curso.")]
    public string Description { get; set; }

    public Guid ProfileId { get; set; }
}

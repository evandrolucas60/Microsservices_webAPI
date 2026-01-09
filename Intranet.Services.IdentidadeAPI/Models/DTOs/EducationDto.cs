namespace Intranet.Services.IdentidadeAPI.Models.DTOs;

public class EducationDto
{
    public int EducationId { get; set; }

    public string School { get; set; }
    public string Degree { get; set; }
    public string FromYear { get; set; }
    public string ToYear { get; set; }
    public string Description { get; set; }

    public int ProfileId { get; set; }
}

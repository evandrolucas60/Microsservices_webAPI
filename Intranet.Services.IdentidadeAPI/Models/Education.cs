using System.ComponentModel.DataAnnotations;

namespace Intranet.Services.IdentidadeAPI.Models;

public class Education
{
    [Key]
    public int EducationId { get; set; }

    public string School { get; set; }
    public string Degree { get; set; }
    [Required]
    [DisplayFormat(DataFormatString = "{0:dd-MM-yyyy}", ApplyFormatInEditMode = true)]
    public DateTime FromYear { get; set; }
    [Required]
    [DisplayFormat(DataFormatString = "{0:dd-MM-yyyy}", ApplyFormatInEditMode = true)]
    public DateTime ToYear { get; set; }
    public string Description { get; set; }

    public int ProfileId { get; set; }
}

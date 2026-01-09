using System.ComponentModel.DataAnnotations;

namespace Intranet.Services.IdentidadeAPI.Models;

public class Work
{
    [Key]
    public int WorkId { get; set; }

    public string Title { get; set; }
    public string Company { get; set; }
    public string Location { get; set; }
    public DateTime AdmissionDate { get; set; }
    public string Registration { get; set; }
    public string Description { get; set; }

    public int ProfileId { get; set; }
}

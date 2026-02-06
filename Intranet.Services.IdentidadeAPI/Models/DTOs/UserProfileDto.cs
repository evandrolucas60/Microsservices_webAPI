using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Intranet.Services.IdentidadeAPI.Models.DTOs;

public class UserProfileDto
{
    public Guid? ProfileId { get; set; }
    public Guid? MemberId { get; set; }

    public WorkDto? Work { get; set; }
    public List<EducationDto> Educations { get; set; } = new();
    public string? ProfilePicture { get; set; }
    public string? CoverPhoto { get; set; }
    public string? Gender { get; set; }
}

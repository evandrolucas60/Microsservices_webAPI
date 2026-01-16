using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Intranet.Services.IdentidadeAPI.Models.DTOs;

public class UserProfileDto
{
    public int ProfileId { get; set; }
    public int MemberId { get; set; }

    public WorkDto? Work { get; set; }
    public EducationDto Educations { get; set; } = new();

    public string? ProfilePicture { get; set; }
    public string? CoverPhoto { get; set; }
    public string? Gender { get; set; }
}

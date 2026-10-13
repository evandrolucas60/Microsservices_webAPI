namespace Intranet.Services.IdentidadeAPI.Models.DTOs;

public class MemberDto : Person
{
    public Guid MemberId { get; set; }
    public string? UserId { get; set; }
    public DateTime dateOfMembership { get; set; }
    public DateOnly? BirthDate { get; set; }
}

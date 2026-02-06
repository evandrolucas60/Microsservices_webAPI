using System.ComponentModel.DataAnnotations;

namespace Intranet.Services.IdentidadeAPI.Models;

public class Member : Person
{
    [Key]
    public Guid MemberId { get; set; } = Guid.NewGuid();
    public string? UserId { get; set; }
    [Required]
    [DisplayFormat(DataFormatString = "{0:dd-MM-yyyy}", ApplyFormatInEditMode = true)]
    public DateTime dateOfMembership { get; set; }
}

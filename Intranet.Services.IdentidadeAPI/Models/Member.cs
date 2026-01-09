using System.ComponentModel.DataAnnotations;

namespace Intranet.Services.IdentidadeAPI.Models;

public class Member : Person
{
    [Key]
    public int MemberId { get; set; }

    public string? UserId { get; set; }
    public DateTime dateOfMembership { get; set; }
}

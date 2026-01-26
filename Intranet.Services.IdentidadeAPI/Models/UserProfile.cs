using Intranet.Services.IdentidadeAPI.Models.DTOs;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Intranet.Services.IdentidadeAPI.Models;

public class UserProfile
{
    [Key]
    public int ProfileId { get; set; }


    // 🔹 Relacionamento 1–1
    public int MemberId { get; set; }


    // 🔹 Relacionamento 1–1
    public Work Work{ get; set; }


    // 🔹 Relacionamento 1–N
    public ICollection<Education> ProfileEducations { get; set; } = new List<Education>();


    // 🔹 Dados do perfil
    public string? ProfilePicture { get; set; }
    public string? CoverPhoto { get; set; }
    public string? Gender { get; set; }

}

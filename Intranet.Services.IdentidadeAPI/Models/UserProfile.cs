using System.ComponentModel.DataAnnotations;
using Intranet.Services.IdentidadeAPI.Utility;

namespace Intranet.Services.IdentidadeAPI.Models;

public class UserProfile
{
    [Key]
    public Guid ProfileId { get; set; } = Guid.NewGuid();


    // 🔹 Relacionamento 1–1
    public Guid MemberId { get; set; }


    // 🔹 Relacionamento 1–1
    public Work Work{ get; set; }


    // 🔹 Relacionamento 1–N
    public ICollection<Education> ProfileEducations { get; set; }


    // 🔹 Dados do perfil
    public string? ProfilePicture { get; set; }
    public string? CoverPhoto { get; set; }
    public string? Gender { get; set; }


    // 🔹 Redes sociais (URLs validadas em Utility/SocialLinkValidator; gravadas só pelo próprio usuário)
    [MaxLength(SocialLinkValidator.MaxLength)]
    public string? TwitterUrl { get; set; }

    [MaxLength(SocialLinkValidator.MaxLength)]
    public string? FacebookUrl { get; set; }

    [MaxLength(SocialLinkValidator.MaxLength)]
    public string? InstagramUrl { get; set; }

    [MaxLength(SocialLinkValidator.MaxLength)]
    public string? LinkedinUrl { get; set; }
}

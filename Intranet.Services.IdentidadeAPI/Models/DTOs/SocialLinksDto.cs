namespace Intranet.Services.IdentidadeAPI.Models.DTOs;

/// <summary>Links das redes sociais do perfil (vazio remove o link).</summary>
public class SocialLinksDto
{
    public string? TwitterUrl { get; set; }
    public string? FacebookUrl { get; set; }
    public string? InstagramUrl { get; set; }
    public string? LinkedinUrl { get; set; }
}

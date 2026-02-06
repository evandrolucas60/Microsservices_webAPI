namespace Intranet.Services.IdentidadeAPI.Models.DTOs
{
    public class UpdatePartialUserProfileDto
    {
        public string? Gender { get; set; }

        public IFormFile? ProfilePicture { get; set; }

        public IFormFile? CoverPhoto { get; set; }
    }
}

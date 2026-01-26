namespace Intranet.Services.IdentidadeAPI.Models.DTOs
{
    public class EducationPatchByProfileDto
    {
        public int EducationId { get; set; }   // identifica qual education atualizar

        public string? School { get; set; }
        public string? Degree { get; set; }
        public DateTime? FromYear { get; set; }
        public DateTime? ToYear { get; set; }
        public string? Description { get; set; }
    }
}

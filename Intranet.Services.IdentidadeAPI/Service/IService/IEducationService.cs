using Intranet.Services.IdentidadeAPI.Models.DTOs;

namespace Intranet.Services.IdentidadeAPI.Service.IService
{
    public interface IEducationService
    {
        Task<List<EducationDto>> GetEducationsByProfileId(int profileId);
        Task<List<EducationDto>> UpsertEducationsAsync(int profileId, List<EducationDto> educations);
    }
}

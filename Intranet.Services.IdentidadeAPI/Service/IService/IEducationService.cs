using Intranet.Services.IdentidadeAPI.Models.DTOs;

namespace Intranet.Services.IdentidadeAPI.Service.IService
{
    public interface IEducationService
    {
        Task<List<EducationDto>> GetEducationsByProfileId(Guid profileId);
        Task<List<EducationDto>> UpsertEducationsAsync(Guid profileId, List<EducationDto> educations);
    }
}

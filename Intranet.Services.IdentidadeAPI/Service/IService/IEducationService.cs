using Intranet.Services.IdentidadeAPI.Models.DTOs;

namespace Intranet.Services.IdentidadeAPI.Service.IService
{
    public interface IEducationService
    {
        Task<EducationDto> GetEducationsByProfileId(int profileId);
    }
}

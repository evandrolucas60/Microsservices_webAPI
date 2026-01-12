using Intranet.Services.IdentidadeAPI.Models.DTOs;

namespace Intranet.Services.IdentidadeAPI.Service.IService
{
    public interface IEducationService
    {
        Task<IEnumerable<EducationDto>> GetEducationsByProfileId(int profileId);
    }
}

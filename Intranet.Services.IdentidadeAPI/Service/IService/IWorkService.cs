using Intranet.Services.IdentidadeAPI.Models.DTOs;

namespace Intranet.Services.IdentidadeAPI.Service.IService
{
    public interface IWorkService
    {
        Task<WorkDto> GetWorkByProfileId(int ProfileId);
    }
}

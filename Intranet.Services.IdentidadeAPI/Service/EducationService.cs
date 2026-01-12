using Intranet.Services.IdentidadeAPI.Models.DTOs;
using Intranet.Services.IdentidadeAPI.Service.IService;
using Newtonsoft.Json;

namespace Intranet.Services.IdentidadeAPI.Service
{
    public class EducationService : IEducationService
    {
        private readonly IHttpClientFactory _httpClientFactory;

        public EducationService(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }

        public async Task<IEnumerable<EducationDto>> GetEducationsByProfileId(int profileId)
        {
            var client = _httpClientFactory.CreateClient("Education");
            var response = await client.GetAsync($"/api/Education/{profileId}");
            var apiContent = await response.Content.ReadAsStringAsync();

            var resp = JsonConvert.DeserializeObject<ResponseDto>(apiContent);

            if (resp != null && resp.IsSuccess)
            {
                return JsonConvert.DeserializeObject<IEnumerable<EducationDto>>(Convert.ToString(resp.Result));
            }

            return new List<EducationDto>();
        }
    }
}

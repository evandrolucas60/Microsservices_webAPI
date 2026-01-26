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

        public async Task<List<EducationDto>> GetEducationsByProfileId(int profileId)
        {
            var client = _httpClientFactory.CreateClient("Education");
            var response = await client.GetAsync($"/api/Education/GetByProfile/{profileId}");

            if (!response.IsSuccessStatusCode)
                return new List<EducationDto>();

            var apiContent = await response.Content.ReadAsStringAsync();

            var resp = JsonConvert.DeserializeObject<ResponseDto>(apiContent);

            if (resp != null && resp.IsSuccess && resp.Result != null)
            {
                var json = Convert.ToString(resp.Result);
                return JsonConvert.DeserializeObject<List<EducationDto>>(json) ?? new List<EducationDto>();
            }

            return new List<EducationDto>();
        }

        public async Task<List<EducationDto>> UpsertEducationsAsync(int profileId, List<EducationDto> educationDto)
        {
            var client = _httpClientFactory.CreateClient("Education");

            foreach (var edu in educationDto)
                edu.ProfileId = profileId;

            var jsonContent = new StringContent(
                JsonConvert.SerializeObject(educationDto),
                System.Text.Encoding.UTF8,
                "application/json"
             );

            var response = await client.PostAsync($"api/Education/batch", content: jsonContent);
            var apiContente = await response.Content.ReadAsStringAsync();

            var resp = JsonConvert.DeserializeObject<ResponseDto>(apiContente);

            if (resp != null && resp.IsSuccess)
            {
                return JsonConvert.DeserializeObject<List<EducationDto>>(
                        Convert.ToString(resp.Result));
            }
            return new List<EducationDto>();

        }
    }
}

using Intranet.Services.IdentidadeAPI.Models.DTOs;
using Intranet.Services.IdentidadeAPI.Service.IService;
using Newtonsoft.Json;

namespace Intranet.Services.IdentidadeAPI.Service
{
    public class WorkService : IWorkService
    {
        private readonly IHttpClientFactory _httpClientFactory;

        public WorkService(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }

        public async Task<WorkDto> GetWorkByProfileId(int MemberId)
        {
            var client = _httpClientFactory.CreateClient("Work");
            var response = await client.GetAsync($"api/Work/{MemberId}");
            var apiContente = await response.Content.ReadAsStringAsync();

            var resp = JsonConvert.DeserializeObject<ResponseDto>(apiContente);
            
            if (resp != null && resp.IsSuccess)
            {
                return JsonConvert.DeserializeObject<WorkDto>(Convert.ToString(resp.Result));
            }
            return new WorkDto();
        }
    }
}

using Intranet.Services.IdentidadeAPI.Models.DTOs;
using Intranet.Services.IdentidadeAPI.Service.IService;
using Newtonsoft.Json;
using System.Net.Http.Headers;

namespace Intranet.Services.IdentidadeAPI.Service
{
    public class WorkService : IWorkService
    {
        private readonly IHttpClientFactory _httpClientFactory;

        public WorkService(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }

        public async Task<WorkDto> GetWorkByMemberId(int MemberId)
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

        public async Task<WorkDto> UpsertWorkAsync(int memberId, WorkDto workDto)
        {
            workDto.MemberId = memberId;

            var client = _httpClientFactory.CreateClient("Work");
            var jsonContent = new StringContent(JsonConvert.SerializeObject(workDto), System.Text.Encoding.UTF8, "application/json");
            var response = await client.PostAsync($"api/Work", content : jsonContent);
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

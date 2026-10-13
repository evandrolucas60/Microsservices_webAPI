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

        public async Task<WorkDto> GetWorkByMemberId(Guid MemberId)
        {
            var client = _httpClientFactory.CreateClient("Work");
            var response = await client.GetAsync($"api/Work/GetworkbyMember/{MemberId}");
            var apiContente = await response.Content.ReadAsStringAsync();

            var resp = JsonConvert.DeserializeObject<ResponseDto>(apiContente);
            
            if (resp != null && resp.IsSuccess)
            {
                return JsonConvert.DeserializeObject<WorkDto>(Convert.ToString(resp.Result));
            }
            return new WorkDto();
        }

        public async Task<WorkDto> UpsertWorkAsync(Guid memberId, WorkDto workDto)
        {
            workDto.MemberId = memberId;

            var client = _httpClientFactory.CreateClient("Work");
            var jsonContent = new StringContent(JsonConvert.SerializeObject(workDto), System.Text.Encoding.UTF8, "application/json");
            var response = await client.PostAsync($"api/Work", content : jsonContent);
            var apiContente = await response.Content.ReadAsStringAsync();

            // Falhas (inclusive 401/403 da checagem de dono) não podem ser tratadas como sucesso
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Falha ao gravar trabalho: HTTP {(int)response.StatusCode} {apiContente}");

            var resp = JsonConvert.DeserializeObject<ResponseDto>(apiContente);

            if (resp != null && resp.IsSuccess)
            {
                return JsonConvert.DeserializeObject<WorkDto>(Convert.ToString(resp.Result));
            }
            throw new InvalidOperationException($"Falha ao gravar trabalho: {resp?.Message}");
        }
    }
}

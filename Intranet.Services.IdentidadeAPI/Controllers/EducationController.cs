using AutoMapper;
using Intranet.Services.IdentidadeAPI.Models;
using Intranet.Services.IdentidadeAPI.Models.DTOs;
using Intranet.Services.IdentidadeAPI.Repository.IRepository;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Intranet.Services.IdentidadeAPI.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class EducationController : ControllerBase
    {
        private readonly IUnityOfWork _uof;
        private readonly ILogger<EducationController> _logger;
        private ResponseDto _response;
        private IMapper _mapper;

        public EducationController(IUnityOfWork uof, ILogger<EducationController> logger, IMapper mapper)
        {
            _uof = uof;
            _logger = logger;
            _response = new ResponseDto();
            _mapper = mapper;
        }

        [HttpGet]
        public async Task<ActionResult<IEnumerable<Education>>> GetAsync()
        {
            try
            {
                var objList = await _uof.EducationRepository.GetAllAsync();
                _response.Result = _mapper.Map<IEnumerable<EducationDto>>(objList);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Nenhum dado foi encontrado");
                _response.IsSuccess = false;
                _response.Message = ex.Message;
            }
            return Ok(_response);
        }

        [HttpGet("{id:int}")]
        public async Task<ActionResult<Education>> GetAsync(int id)
        {
            try
            {
                var obj = await _uof.EducationRepository.GetAsync(c => c.EducationId == id);
                _response.Result = _mapper.Map<EducationDto>(obj);

                if (obj is null)
                {
                    _logger.LogWarning("Objeto não Encontrado ou não existe");
                    return NotFound("Objeto não existe...");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Nenhum dado foi encontrado");
                _response.IsSuccess = false;
                _response.Message = ex.Message;

            }
            return Ok(_response);
        }

        [HttpPost]
        public async Task<ActionResult> Post([FromBody] EducationDto educationDto)
        {
            try
            {
                Education obj = _mapper.Map<Education>(educationDto);
                var categoriaCriada = _uof.EducationRepository.Create(obj);
                await _uof.CommitAsync();

                _response.Result = _mapper.Map<EducationDto>(obj);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Dados Inválidos");
                _response.IsSuccess = false;
                _response.Message = ex.Message;
            }
            return Ok(_response);
        }

        [HttpPut("{id:int}")]
        public async Task<ActionResult> Put(int id, EducationDto educationDto)
        {
            try
            {
                Education obj = _mapper.Map<Education>(educationDto);
                _uof.EducationRepository.Update(obj);
                await _uof.CommitAsync();

                if (id != obj.EducationId)
                {
                    _logger.LogWarning("Dados inválidos");
                    return BadRequest("Dados inválidos");
                }

                _response.Result = _mapper.Map<EducationDto>(obj);
            }
            catch (Exception ex)
            {
                _response.IsSuccess = false;
                _response.Message = ex.Message;
            }
            return Ok(_response);
        }

        [HttpDelete("{id:int}")]
        public async Task<ActionResult> Delete(int id)
        {
            try
            {
                Education obj = await _uof.EducationRepository.GetAsync(c => c.EducationId == id);

                if (obj is null)
                {
                    _logger.LogWarning($"Objeto não Encontrado ou não existe");
                    return NotFound("Objeto não existe...");
                }

                var objExcluido = _uof.EducationRepository.Delete(obj);
                await _uof.CommitAsync();
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Erro interno ao tentar deletar: {ex.Message}");
            }

            return Ok(_response);
        }
    }
}

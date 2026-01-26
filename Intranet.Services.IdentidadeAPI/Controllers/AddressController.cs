using AutoMapper;
using Intranet.Services.IdentidadeAPI.Models;
using Intranet.Services.IdentidadeAPI.Models.DTOs;
using Intranet.Services.IdentidadeAPI.Repository.IRepository;
using Microsoft.AspNetCore.Mvc;

namespace Intranet.Services.IdentidadeAPI.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AddressController : ControllerBase
    {
        private readonly IUnityOfWork _uof;
        private readonly ILogger<AddressController> _logger;
        private ResponseDto _response;
        private IMapper _mapper;

        public AddressController(IUnityOfWork uof, ILogger<AddressController> logger, IMapper mapper)
        {
            _uof = uof;
            _logger = logger;
            _response = new ResponseDto();
            _mapper = mapper;
        }

        [HttpGet]
        public async Task<ActionResult<IEnumerable<Address>>> GetAsync()
        {
            try
            {
                var objList = await _uof.AddressRepository.GetAllAsync();
                _response.Result = _mapper.Map<IEnumerable<AddressDto>>(objList);
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
        public async Task<ActionResult<Address>> GetAsync(int id)
        {
            try
            {
                var obj = await _uof.AddressRepository.GetAsync(c => c.AddressId == id);
                _response.Result = _mapper.Map<AddressDto>(obj);

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

        [HttpGet("GetAddressbyMember/{id:int}")]
        public async Task<ActionResult<Address>> GetByProfileAsync(int id)
        {
            try
            {
                var obj = await _uof.AddressRepository.GetAsync(c => c.MemberId == id);
                _response.Result = _mapper.Map<AddressDto>(obj);

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
        public async Task<ActionResult> Post([FromBody] AddressDto addressDto)
        {
            try
            {
                Address obj = _mapper.Map<Address>(addressDto);
                var categoriaCriada = _uof.AddressRepository.Create(obj);
                await _uof.CommitAsync();

                _response.Result = _mapper.Map<AddressDto>(obj);
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
        public async Task<ActionResult> Put(int id, AddressDto addressDto)
        {
            try
            {
                Address obj = _mapper.Map<Address>(addressDto);
                _uof.AddressRepository.Update(obj);
                await _uof.CommitAsync();

                if (id != obj.AddressId)
                {
                    _logger.LogWarning("Dados inválidos");
                    return BadRequest("Dados inválidos");
                }

                _response.Result = _mapper.Map<AddressDto>(obj);
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
                Address obj = await _uof.AddressRepository.GetAsync(c => c.AddressId == id);


                if (obj is null)
                {
                    _logger.LogWarning($"Objeto não Encontrado ou não existe");
                    return NotFound("Objeto não existe...");
                }

                var objExcluido = _uof.AddressRepository.Delete(obj);
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

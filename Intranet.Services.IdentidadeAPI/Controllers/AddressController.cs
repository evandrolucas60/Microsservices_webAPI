using AutoMapper;
using AutoMapper.Execution;
using Intranet.Services.IdentidadeAPI.Models;
using Intranet.Services.IdentidadeAPI.Models.DTOs;
using Intranet.Services.IdentidadeAPI.Repository.IRepository;
using Intranet.Services.IdentidadeAPI.Utility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Intranet.Services.IdentidadeAPI.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class AddressController : ControllerBase
    {
        private readonly IUnityOfWork _uof;
        private readonly ILogger<AddressController> _logger;
        private readonly IOwnershipChecker _ownership;
        private ResponseDto _response;
        private IMapper _mapper;

        public AddressController(IUnityOfWork uof, ILogger<AddressController> logger, IMapper mapper, IOwnershipChecker ownership)
        {
            _uof = uof;
            _logger = logger;
            _response = new ResponseDto();
            _mapper = mapper;
            _ownership = ownership;
        }

        [HttpGet]
        public async Task<ActionResult<IEnumerable<Address>>> GetAsync()
        {
            // Endereços são dados pessoais: listagem geral só para Admin
            if (!_ownership.IsAdmin(User)) return Forbid();

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
            if (!await _ownership.OwnsAddressAsync(User, id)) return Forbid();

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

        [HttpGet("GetAddressbyMember/{memberId:Guid}")]
        public async Task<ActionResult<Address>> GetByProfileAsync(Guid memberId)
        {
            if (!await _ownership.OwnsMemberAsync(User, memberId)) return Forbid();

            try
            {
                var obj = await _uof.AddressRepository.GetAsync(c => c.MemberId == memberId);
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
            if (!await _ownership.OwnsMemberAsync(User, addressDto.MemberId)) return Forbid();

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
            // Valida antes de gravar (antes o id era conferido só depois do commit)
            if (id != addressDto.AddressId)
            {
                _logger.LogWarning("Dados inválidos");
                return BadRequest("Dados inválidos");
            }

            if (!await _ownership.OwnsAddressAsync(User, id) || !await _ownership.OwnsMemberAsync(User, addressDto.MemberId))
                return Forbid();

            try
            {
                Address obj = _mapper.Map<Address>(addressDto);
                _uof.AddressRepository.Update(obj);
                await _uof.CommitAsync();

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
            if (!await _ownership.OwnsAddressAsync(User, id)) return Forbid();

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

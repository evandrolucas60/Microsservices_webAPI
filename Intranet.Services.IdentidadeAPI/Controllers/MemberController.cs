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
    public class MemberController : ControllerBase
    {
        private readonly IUnityOfWork _uof;
        private readonly ILogger<MemberController> _logger;
        private ResponseDto _response;
        private IMapper _mapper;

        public MemberController(IUnityOfWork uof, ILogger<MemberController> logger, IMapper mapper)
        {
            _uof = uof;
            _logger = logger;
            _response = new ResponseDto();
            _mapper = mapper;
        }

        [HttpGet]
        public async Task<ActionResult<IEnumerable<Member>>> GetAsync()
        {
            try
            {
                var objList = await _uof.MemberRepository.GetAllAsync();
                _response.Result = _mapper.Map<IEnumerable<MemberDto>>(objList);
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
        public async Task<ActionResult<Member>> GetAsync(int id)
        {
            try
            {
                var obj = await _uof.MemberRepository.GetAsync(c => c.MemberId == id);
                _response.Result = _mapper.Map<MemberDto>(obj);

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
        public async Task<ActionResult> Post([FromBody] MemberDto memberDto)
        {
            try
            {
                Member obj = _mapper.Map<Member>(memberDto);
                var membroCriado = _uof.MemberRepository.Create(obj);
                await _uof.CommitAsync();

                _response.Result = _mapper.Map<MemberDto>(obj);
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
        public async Task<ActionResult> Put(int id, MemberDto memberDto)
        {
            try
            {
                Member obj = _mapper.Map<Member>(memberDto);
                _uof.MemberRepository.Update(obj);
                await _uof.CommitAsync();

                if (id != obj.MemberId)
                {
                    _logger.LogWarning("Dados inválidos");
                    return BadRequest("Dados inválidos");
                }

                _response.Result = _mapper.Map<MemberDto>(obj);
            }
            catch (Exception ex)
            {
                _response.IsSuccess = false;
                _response.Message = ex.Message;
            }
            return Ok(_response);
        }

        [HttpPatch("UpdatePartial/{memberId:int}")]
        public async Task<ActionResult> PatchMemberAsync(int memberId, [FromBody] MemberDto memberDto)
        {
            try
            {
                var member = await _uof.MemberRepository.GetAsync(
                    m => m.MemberId == memberId,
                    includeProperties: null
                );

                if (member == null)
                    return NotFound("Membro não encontrado");

                // 🔹 Patch parcial (somente campos enviados)
                if (!string.IsNullOrWhiteSpace(memberDto.Name))
                    member.Name = memberDto.Name;

                if (!string.IsNullOrWhiteSpace(memberDto.Email))
                    member.Email = memberDto.Email;

                if (!string.IsNullOrWhiteSpace(memberDto.Phone))
                    member.Phone = memberDto.Phone;

                _uof.MemberRepository.Update(member);
                await _uof.CommitAsync();

                _response.Result = _mapper.Map<MemberDto>(member);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao aplicar PATCH no memberId {memberId}", memberId);
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
                Member obj = await _uof.MemberRepository.GetAsync(c => c.MemberId == id);


                if (obj is null)
                {
                    _logger.LogWarning($"Objeto não Encontrado ou não existe");
                    return NotFound("Objeto não existe...");
                }

                var objExcluido = _uof.MemberRepository.Delete(obj);
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

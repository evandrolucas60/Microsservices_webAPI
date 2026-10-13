using AutoMapper;
using Intranet.Services.IdentidadeAPI.Models;
using Intranet.Services.IdentidadeAPI.Models.DTOs;
using Intranet.Services.IdentidadeAPI.Repository.IRepository;
using Intranet.Services.IdentidadeAPI.Utility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Intranet.Services.IdentidadeAPI.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class MemberController : ControllerBase
    {
        private readonly IUnityOfWork _uof;
        private readonly ILogger<MemberController> _logger;
        private readonly IOwnershipChecker _ownership;
        private ResponseDto _response;
        private IMapper _mapper;

        public MemberController(IUnityOfWork uof, ILogger<MemberController> logger, IMapper mapper, IOwnershipChecker ownership)
        {
            _uof = uof;
            _logger = logger;
            _response = new ResponseDto();
            _mapper = mapper;
            _ownership = ownership;
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

        [HttpGet("{memberId:Guid}")]
        public async Task<ActionResult<Member>> GetAsync(Guid memberId)
        {
            try
            {
                var obj = await _uof.MemberRepository.GetAsync(c => c.MemberId == memberId);
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
            // Só é possível criar o membro do próprio usuário (ou Admin)
            if (!_ownership.IsUser(User, memberDto.UserId)) return Forbid();

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

        [HttpPut("{menberId:Guid}")]
        public async Task<ActionResult> Put(Guid menberId, MemberDto memberDto)
        {
            if (menberId != memberDto.MemberId) return BadRequest("Dados inválidos");
            if (!await _ownership.OwnsMemberAsync(User, menberId)) return Forbid();

            try
            {
                var existing = await _uof.MemberRepository.GetAsync(m => m.MemberId == menberId);
                if (existing is null) return NotFound("Membro não encontrado");

                // Vínculo com o usuário (UserId) não pode ser trocado pelo cliente
                var userId = existing.UserId;
                _mapper.Map(memberDto, existing);
                existing.UserId = userId;
                existing.MemberId = menberId;

                _uof.MemberRepository.Update(existing);
                await _uof.CommitAsync();

                _response.Result = _mapper.Map<MemberDto>(existing);
            }
            catch (Exception ex)
            {
                _response.IsSuccess = false;
                _response.Message = ex.Message;
            }
            return Ok(_response);
        }

        [HttpPatch("UpdatePartial/{memberId:Guid}")]
        public async Task<ActionResult> PatchMemberAsync(Guid memberId, [FromBody] MemberDto memberDto)
        {
            if (!await _ownership.OwnsMemberAsync(User, memberId)) return Forbid();

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

                if (memberDto.BirthDate.HasValue)
                    member.BirthDate = memberDto.BirthDate;

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


        [HttpDelete("{memberId:Guid}")]
        public async Task<ActionResult> Delete(Guid memberId)
        {
            // Excluir um colaborador é ação administrativa
            if (!_ownership.IsAdmin(User)) return Forbid();

            try
            {
                Member obj = await _uof.MemberRepository.GetAsync(c => c.MemberId == memberId);


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

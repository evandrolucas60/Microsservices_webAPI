using AutoMapper;
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
    public class EducationController : ControllerBase
    {
        private readonly IUnityOfWork _uof;
        private readonly ILogger<EducationController> _logger;
        private readonly IOwnershipChecker _ownership;
        private ResponseDto _response;
        private IMapper _mapper;

        public EducationController(IUnityOfWork uof, ILogger<EducationController> logger, IMapper mapper, IOwnershipChecker ownership)
        {
            _uof = uof;
            _logger = logger;
            _response = new ResponseDto();
            _mapper = mapper;
            _ownership = ownership;
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

        // Perfil público: cargo/setor e formação aparecem para visitantes
    [AllowAnonymous]
    [HttpGet("GetByProfile/{profileId:Guid}")]
        public async Task<ActionResult<Education>> GetByProfileAsync(Guid profileId)
        {
            try
            {
                var list = await _uof.EducationRepository.GetAllAsync(c => c.ProfileId == profileId);
                _response.Result = _mapper.Map<List<EducationDto>>(list);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Nenhum dado foi encontrado");
                _response.IsSuccess = false;
                _response.Message = ex.Message;

            }
            return Ok(_response);
        }

        [HttpPost("batch")]
        public async Task<ActionResult> BatchUpsert([FromBody] List<EducationDto> educationsDto)
        {
            try
            {
                if (educationsDto == null || !educationsDto.Any())
                    return BadRequest("Lista de educações vazia.");

                if (educationsDto.Any(e => e.ProfileId == Guid.Empty))
                    return BadRequest("Todas as educações devem possuir ProfileId válido.");

                foreach (var profileId in educationsDto.Select(e => e.ProfileId).Distinct())
                {
                    if (!await _ownership.OwnsProfileAsync(User, profileId)) return Forbid();
                }

                // Itens existentes precisam pertencer a um profile do usuário
                foreach (var educationId in educationsDto.Where(e => e.EducationId != 0).Select(e => e.EducationId))
                {
                    if (!await _ownership.OwnsEducationAsync(User, educationId)) return Forbid();
                }

                var result = new List<EducationDto>();

                foreach (var dto in educationsDto)
                {
                    var entity = _mapper.Map<Education>(dto);
                    entity.FromYear = ToUtcDate(entity.FromYear);
                    entity.ToYear = ToUtcDate(entity.ToYear);

                    entity.ProfileId = dto.ProfileId;

                    if (entity.EducationId == 0)
                    {
                        _uof.EducationRepository.Create(entity);
                    }
                    else
                    {
                        _uof.EducationRepository.Update(entity);
                    }

                    result.Add(_mapper.Map<EducationDto>(entity));
                }

                await _uof.CommitAsync();

                _response.Result = result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro no batch upsert de educations");
                _response.IsSuccess = false;
                _response.Message = ex.Message;
            }

            return Ok(_response);
        }

        [HttpPost]
        public async Task<ActionResult> Post([FromBody] EducationDto educationDto)
        {
            if (!await _ownership.OwnsProfileAsync(User, educationDto.ProfileId)) return Forbid();

            try
            {
                Education obj = _mapper.Map<Education>(educationDto);
                obj.FromYear = ToUtcDate(obj.FromYear);
                obj.ToYear = ToUtcDate(obj.ToYear);
                var edu = _uof.EducationRepository.Create(obj);
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

        [HttpPut("profile/{profileId:Guid}")]
        public async Task<ActionResult> UpdateByProfile(
            Guid profileId,
            [FromBody] List<EducationDto> educationsDto)
        {
            if (!await _ownership.OwnsProfileAsync(User, profileId)) return Forbid();

            try
            {
                if (educationsDto == null || !educationsDto.Any())
                    return BadRequest("Lista de educações vazia.");

                if (educationsDto.Any(e => e.ProfileId != profileId))
                    return BadRequest("ProfileId do payload não corresponde ao ProfileId da rota.");

                var existing = await _uof.EducationRepository
                    .GetAllAsync(e => e.ProfileId == profileId);

                var existingDict = existing.ToDictionary(e => e.EducationId);

                var result = new List<EducationDto>();

                foreach (var dto in educationsDto)
                {
                    var entity = _mapper.Map<Education>(dto);
                    entity.FromYear = ToUtcDate(entity.FromYear);
                    entity.ToYear = ToUtcDate(entity.ToYear);

                    entity.ProfileId = profileId;

                    if (entity.EducationId == 0)
                    {
                        _uof.EducationRepository.Create(entity);
                    }
                    else if (existingDict.ContainsKey(entity.EducationId))
                    {
                        _uof.EducationRepository.Update(entity);
                    }
                    else
                    {
                        return BadRequest($"EducationId {entity.EducationId} não pertence ao Profile {profileId}");
                    }

                    result.Add(_mapper.Map<EducationDto>(entity));
                }

                await _uof.CommitAsync();

                _response.Result = result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao atualizar educations do profile {ProfileId}", profileId);
                _response.IsSuccess = false;
                _response.Message = ex.Message;
            }

            return Ok(_response);
        }


        [HttpDelete("{id:int}")]
        public async Task<ActionResult> Delete(int id)
        {
            if (!await _ownership.OwnsEducationAsync(User, id)) return Forbid();

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


        // Datas do formulário ("2020-01-01") chegam sem fuso; o Npgsql exige UTC em colunas timestamptz
        private static DateTime ToUtcDate(DateTime value) =>
            DateTime.SpecifyKind(value.Date, DateTimeKind.Utc);

        [HttpPatch("profile/{profileId:Guid}")]
        public async Task<ActionResult> PatchEducationsByProfile( Guid profileId, [FromBody] List<EducationPatchByProfileDto> patchList)
        {
            if (!await _ownership.OwnsProfileAsync(User, profileId)) return Forbid();

            try
            {
                if (patchList == null || !patchList.Any())
                    return BadRequest(new ResponseDto { IsSuccess = false, Message = "Nenhuma formação foi enviada." });

                // Busca apenas as educations do profile (no banco, não em memória)
                var profileEducations = (await _uof.EducationRepository
                    .GetAllAsync(e => e.ProfileId == profileId))
                    .ToList();

                foreach (var patch in patchList)
                {
                    var entity = profileEducations
                        .FirstOrDefault(e => e.EducationId == patch.EducationId);

                    // EducationId 0 (ou inexistente no profile) = nova formação.
                    // Antes respondia 404 quando o profile ainda não tinha nenhuma.
                    if (entity == null)
                    {
                        if (string.IsNullOrWhiteSpace(patch.School) ||
                            string.IsNullOrWhiteSpace(patch.Degree) ||
                            !patch.FromYear.HasValue ||
                            !patch.ToYear.HasValue)
                        {
                            return BadRequest(new ResponseDto { IsSuccess = false, Message = "Para incluir uma formação informe instituição, nível, início e término." });
                        }

                        entity = new Education
                        {
                            ProfileId = profileId,
                            School = patch.School,
                            Degree = patch.Degree,
                            FromYear = ToUtcDate(patch.FromYear.Value),
                            ToYear = ToUtcDate(patch.ToYear.Value),
                            Description = patch.Description ?? string.Empty,
                        };
                        _uof.EducationRepository.Create(entity);
                        profileEducations.Add(entity);
                        continue;
                    }

                    if (patch.School != null)
                        entity.School = patch.School;

                    if (patch.Degree != null)
                        entity.Degree = patch.Degree;

                    if (patch.FromYear.HasValue)
                        entity.FromYear = ToUtcDate(patch.FromYear.Value);

                    if (patch.ToYear.HasValue)
                        entity.ToYear = ToUtcDate(patch.ToYear.Value);

                    if (patch.Description != null)
                        entity.Description = patch.Description;

                    _uof.EducationRepository.Update(entity);
                }

                await _uof.CommitAsync();

                _response.Result = profileEducations.Select(e => _mapper.Map<EducationDto>(e)).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao aplicar PATCH por profileId {ProfileId}", profileId);
                _response.IsSuccess = false;
                _response.Message = ex.Message;
            }

            return Ok(_response);
        }

    }
}

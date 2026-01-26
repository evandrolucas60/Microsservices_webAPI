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

        [HttpGet("GetByProfile/{id:int}")]
        public async Task<ActionResult<Education>> GetByProfileAsync(int id)
        {
            try
            {
                var list = await _uof.EducationRepository.GetAllAsync(c => c.ProfileId == id);
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

                if (educationsDto.Any(e => e.ProfileId <= 0))
                    return BadRequest("Todas as educações devem possuir ProfileId válido.");

                var result = new List<EducationDto>();

                foreach (var dto in educationsDto)
                {
                    var entity = _mapper.Map<Education>(dto);

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

        [HttpPut("profile/{profileId:int}")]
        public async Task<ActionResult> UpdateByProfile(
            int profileId,
            [FromBody] List<EducationDto> educationsDto)
        {
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


        [HttpPatch("profile/{profileId:int}")]
        public async Task<ActionResult> PatchEducationsByProfile( int profileId, [FromBody] List<EducationPatchByProfileDto> patchList)
        {
            try
            {
                // Busca todas as educations do profile
                var educations = await _uof.EducationRepository.GetAllAsync();

                var profileEducations = educations
                    .Where(e => e.ProfileId == profileId)
                    .ToList();

                if (!profileEducations.Any())
                    return NotFound("Nenhuma education encontrada para este profile.");

                foreach (var patch in patchList)
                {
                    var entity = profileEducations
                        .FirstOrDefault(e => e.EducationId == patch.EducationId);

                    if (entity == null)
                        continue;

                    if (patch.School != null)
                        entity.School = patch.School;

                    if (patch.Degree != null)
                        entity.Degree = patch.Degree;

                    if (patch.FromYear.HasValue)
                        entity.FromYear = patch.FromYear.Value;

                    if (patch.ToYear.HasValue)
                        entity.ToYear = patch.ToYear.Value;

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

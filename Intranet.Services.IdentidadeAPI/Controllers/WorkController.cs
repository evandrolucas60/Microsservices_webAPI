using AutoMapper;
using Intranet.Services.IdentidadeAPI.Models;
using Intranet.Services.IdentidadeAPI.Models.DTOs;
using Intranet.Services.IdentidadeAPI.Repository.IRepository;
using Intranet.Services.IdentidadeAPI.Utility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Intranet.Services.IdentidadeAPI.Controllers;

[Route("api/[controller]")]
[ApiController]
[Authorize]
public class WorkController : ControllerBase
{
    private readonly IUnityOfWork _uof;
    private readonly ILogger<WorkController> _logger;
    private readonly IOwnershipChecker _ownership;
    private ResponseDto _response;
    private IMapper _mapper;

    public WorkController(IUnityOfWork uof, ILogger<WorkController> logger, IMapper mapper, IOwnershipChecker ownership)
    {
        _uof = uof;
        _logger = logger;
        _response = new ResponseDto();
        _mapper = mapper;
        _ownership = ownership;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<Work>>> GetAsync()
    {
        try
        {
            var objList = await _uof.WorkRepository.GetAllAsync();
            _response.Result = _mapper.Map<IEnumerable<WorkDto>>(objList);
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
    public async Task<ActionResult<Work>> GetAsync(int id)
    {
        try
        {
            var obj = await _uof.WorkRepository.GetAsync(c => c.WorkId == id);
            _response.Result = _mapper.Map<WorkDto>(obj);

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

    [HttpGet("GetworkbyMember/{memberId:Guid}")]
    public async Task<ActionResult<Work>> GetByProfileAsync(Guid memberId)
    {
        try
        {
            var obj = await _uof.WorkRepository.GetAsync(c => c.MemberId == memberId);
            _response.Result = _mapper.Map<WorkDto>(obj);

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

    // "2020-03-01" chega como DateTime sem fuso; o Npgsql exige UTC em colunas timestamptz
    private static DateTime? ToUtcDate(DateTime? value) =>
        value.HasValue ? DateTime.SpecifyKind(value.Value.Date, DateTimeKind.Utc) : null;

    /// <summary>
    /// Cria o trabalho do membro ou, se já existir (um por membro), atualiza o existente.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult> Post([FromBody] WorkDto workDto)
    {
        if (!await _ownership.OwnsMemberAsync(User, workDto.MemberId)) return Forbid();

        try
        {
            var existing = await _uof.WorkRepository.GetAsync(w => w.MemberId == workDto.MemberId);
            Work obj;
            if (existing is null)
            {
                obj = _mapper.Map<Work>(workDto);
                obj.WorkId = 0;
                obj.AdmissionDate = ToUtcDate(obj.AdmissionDate);
                _uof.WorkRepository.Create(obj);
            }
            else
            {
                var workId = existing.WorkId;
                _mapper.Map(workDto, existing);
                existing.WorkId = workId;
                existing.AdmissionDate = ToUtcDate(existing.AdmissionDate);
                _uof.WorkRepository.Update(existing);
                obj = existing;
            }
            await _uof.CommitAsync();

            _response.Result = _mapper.Map<WorkDto>(obj);
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
    public async Task<ActionResult> Put(int id, WorkDto workDto)
    {
        // Valida antes de gravar (antes o id era conferido só depois do commit)
        if (id != workDto.WorkId)
        {
            _logger.LogWarning("Dados inválidos");
            return BadRequest("Dados inválidos");
        }

        // Precisa ser dono do registro atual e do membro informado (impede "mover" para outro membro)
        if (!await _ownership.OwnsWorkAsync(User, id) || !await _ownership.OwnsMemberAsync(User, workDto.MemberId))
            return Forbid();

        try
        {
            Work obj = _mapper.Map<Work>(workDto);
            obj.AdmissionDate = ToUtcDate(obj.AdmissionDate);
            _uof.WorkRepository.Update(obj);
            await _uof.CommitAsync();

            _response.Result = _mapper.Map<WorkDto>(obj);
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
        if (!await _ownership.OwnsWorkAsync(User, id)) return Forbid();

        try
        {
            Work obj = await _uof.WorkRepository.GetAsync(c => c.WorkId == id);
            
            
            if (obj is null)
            {
                _logger.LogWarning($"Objeto não Encontrado ou não existe");
                return NotFound("Objeto não existe...");
            }

            var objExcluido = _uof.WorkRepository.Delete(obj);
            await _uof.CommitAsync();
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Erro interno ao tentar deletar: {ex.Message}");
        }

        return Ok(_response);
    }
}

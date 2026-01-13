using AutoMapper;
using Intranet.Services.IdentidadeAPI.Models;
using Intranet.Services.IdentidadeAPI.Models.DTOs;
using Intranet.Services.IdentidadeAPI.Repository.IRepository;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Intranet.Services.IdentidadeAPI.Controllers;

[Route("api/[controller]")]
[ApiController]
public class WorkController : ControllerBase
{
    private readonly IUnityOfWork _uof;
    private readonly ILogger<WorkController> _logger;
    private ResponseDto _response;
    private IMapper _mapper;

    public WorkController(IUnityOfWork uof, ILogger<WorkController> logger, IMapper mapper)
    {
        _uof = uof;
        _logger = logger;
        _response = new ResponseDto();
        _mapper = mapper;
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

    [HttpGet("GetworkbyMember/{id:int}")]
    public async Task<ActionResult<Work>> GetByProfileAsync(int id)
    {
        try
        {
            var obj = await _uof.WorkRepository.GetAsync(c => c.MemberId == id);
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

    [HttpPost]
    public async Task<ActionResult> Post([FromBody] WorkDto workDto)
    {
        try
        {
            Work obj = _mapper.Map<Work>(workDto);
            var categoriaCriada = _uof.WorkRepository.Create(obj);
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
        try
        {
            Work obj = _mapper.Map<Work>(workDto);
            _uof.WorkRepository.Update(obj);
            await _uof.CommitAsync();

            if (id != obj.WorkId)
            {
                _logger.LogWarning("Dados inválidos");
                return BadRequest("Dados inválidos");
            }

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

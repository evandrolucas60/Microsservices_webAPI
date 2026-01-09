using Intranet.Services.EscalaAPI.Models;
using Intranet.Services.EscalaAPI.Pagination;
using Intranet.Services.EscalaAPI.Repository.IRepository;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;

namespace Intranet.Services.EscalaAPI.Controllers;

[Route("api/[controller]")]
[ApiController]
public class EscalaController : ControllerBase
{
    private readonly IUnityOfWork _uof;
    private readonly ILogger<EscalaController> _logger;

    public EscalaController(IUnityOfWork uof, ILogger<EscalaController> logger)
    {
        _uof = uof;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<Escala>>> GetAsync()
    {
        var escalas = await _uof.EscalaRepository.GetAllAsync();
        return Ok(escalas);
    }

    [HttpGet("{id:int}", Name = "obterEscala")]
    public async Task<ActionResult<Escala>> GetAsync(int id)
    {
        var escala = await _uof.EscalaRepository.GetAsync(c => c.Id == id);
        if (escala is null)
        {
            _logger.LogWarning("Escala não Encontrada");
            return NotFound("Escala não Encontrada");
        }
        return Ok(escala);
    }

    [HttpGet("pagination")]
    public async Task<ActionResult<IEnumerable<Escala>>> GetEscalasAsync([FromQuery] EscalaParameters parameters)
    {
        var escalas = await _uof.EscalaRepository.GetEscalasAsync(parameters);
        return ObterEscalas(escalas);
    }

    private ActionResult<IEnumerable<Escala>> ObterEscalas(PagedList<Escala> escalas)
    {
        var metadata = new
        {
            escalas.TotalCount,
            escalas.PageSize,
            escalas.CurrentPage,
            escalas.TotalPages,
            escalas.HasNext,
            escalas.HasPrevious,
        };
        Response.Headers.Append("X-Pagination", JsonConvert.SerializeObject(metadata));
        return Ok(escalas);
    }

    [HttpPost]
    [Authorize(Roles = "ADMIN")]
    public async Task<ActionResult> Post(Escala escala)
    {
        if (escala is null)
        {
            _logger.LogWarning("Dados Inválidos");
            return BadRequest("Dados Inválidos");
        }

        var categoriaCriada = _uof.EscalaRepository.Create(escala);
        await _uof.CommitAsync();

        return new CreatedAtRouteResult("obterEscala", new { id = escala.Id, categoriaCriada });
    }

    [HttpPut("{id:int}")]
    [Authorize(Roles = "ADMIN")]
    public async Task<ActionResult> Put(int id, Escala escala)
    {
        if (id != escala.Id)
        {
            _logger.LogWarning("Dados inválidos");
            return BadRequest("Dados inválidos");
        }
        _uof.EscalaRepository.Update(escala);
        await _uof.CommitAsync();

        return Ok(escala);
    }

    [HttpDelete("{id:int}")]
    [Authorize(Roles = "ADMIN")]
    public async Task<ActionResult> Delete(int id)
    {
        try
        {
            var escalas = await _uof.EscalaRepository.GetAsync(c => c.Id == id);
            if (escalas is null)
            {
                _logger.LogWarning($"Escala com ID {id} não encontrada.");
                return NotFound("Escala não existe...");
            }

            var escalaExcluida = _uof.EscalaRepository.Delete(escalas);
            await _uof.CommitAsync();
            return Ok();
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Erro interno ao deletar a escala: {ex.Message}");
        }
    }
}

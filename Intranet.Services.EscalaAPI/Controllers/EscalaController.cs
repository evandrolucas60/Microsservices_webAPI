using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Intranet.Services.EscalaAPI.Models;
using Intranet.Services.EscalaAPI.Pagination;
using Intranet.Services.EscalaAPI.Repository.IRepository;
using Intranet.Services.EscalaAPI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;

namespace Intranet.Services.EscalaAPI.Controllers;

/// <summary>Dados informados por quem cria/edita a escala; o resto vem do cadastro do colaborador.</summary>
public class EscalaUpsertDto
{
    /// <summary>Colaborador da T.I escalado.</summary>
    public Guid MemberId { get; set; }

    /// <summary>Telefone do plantão.</summary>
    public string? Telefone { get; set; }

    /// <summary>Data do plantão (yyyy-MM-dd).</summary>
    public DateOnly? Data { get; set; }

    /// <summary>Horário, ex.: "07:00 - 13:00".</summary>
    public string? Horario { get; set; }
}

/// <summary>
/// Escala da T.I. A consulta é pública; criar, editar e excluir é restrito aos papéis (roles)
/// superAdmin e TI do login. Só colaboradores do setor T.I podem ser escalados; nome, cargo,
/// e-mail e foto são preenchidos pelo servidor a partir do colaborador escolhido.
/// </summary>
[Route("api/[controller]")]
[ApiController]
public class EscalaController : ControllerBase
{
    private static readonly CultureInfo PtBr = new("pt-BR");
    private static readonly Regex TelefoneValido = new(@"^[0-9()+\-\s]{8,20}$", RegexOptions.Compiled);

    private readonly IUnityOfWork _uof;
    private readonly IdentidadeClient _identidade;
    private readonly IConfiguration _configuration;
    private readonly ILogger<EscalaController> _logger;

    public EscalaController(IUnityOfWork uof, IdentidadeClient identidade, IConfiguration configuration, ILogger<EscalaController> logger)
    {
        _uof = uof;
        _identidade = identidade;
        _configuration = configuration;
        _logger = logger;
    }

    private string? BearerToken
    {
        get
        {
            var header = Request.Headers.Authorization.ToString();
            return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : null;
        }
    }

    private static ObjectResult Error(int status, string message) => new(new { isSuccess = false, message }) { StatusCode = status };

    /// <summary>
    /// Pode criar/editar/excluir escalas: papel (role) do login superAdmin ou TI, sem diferenciar
    /// maiúsculas. Configurável em Escala:RolesGerenciamento.
    /// </summary>
    private Task<bool> PodeGerenciarAsync(CancellationToken ct)
    {
        _ = ct;
        var permitidos = _configuration.GetSection("Escala:RolesGerenciamento").Get<string[]>() is { Length: > 0 } roles
            ? roles
            : new[] { "superAdmin", "TI" };
        var podeGerenciar = User.Identity?.IsAuthenticated == true && User.Claims
            .Where(c => c.Type == ClaimTypes.Role || c.Type == "role")
            .Any(c => permitidos.Contains(c.Value.Trim(), StringComparer.OrdinalIgnoreCase));
        return Task.FromResult(podeGerenciar);
    }

    // ---------------------------------------------------------------- Consulta (pública)

    [HttpGet]
    public async Task<ActionResult<IEnumerable<Escala>>> GetAsync()
    {
        var escalas = await _uof.EscalaRepository.GetAllAsync();
        return Ok(escalas.OrderBy(e => e.DateTime).ThenBy(e => e.Horario));
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

    /// <summary>Se o usuário atual pode criar/editar/excluir escalas (false sem login).</summary>
    [HttpGet("permissoes")]
    public async Task<ActionResult> Permissoes(CancellationToken ct) =>
        Ok(new { podeGerenciar = User.Identity?.IsAuthenticated == true && await PodeGerenciarAsync(ct), departamento = _identidade.Departamento });

    /// <summary>Colaboradores que podem ser escalados (setor T.I). Apenas para quem gerencia as escalas.</summary>
    [HttpGet("colaboradores")]
    [Authorize]
    public async Task<ActionResult> Colaboradores(CancellationToken ct)
    {
        if (!await PodeGerenciarAsync(ct)) return Error(StatusCodes.Status403Forbidden, "Apenas superAdmin ou TI podem gerenciar as escalas.");
        try
        {
            var lista = await _identidade.GetColaboradoresDoSetorAsync(BearerToken, ct);
            return Ok(lista.OrderBy(c => c.Nome));
        }
        catch (BuildingBlocks.Shared.Exceptions.ServicoIndisponivelException ex)
        {
            _logger.LogWarning(ex, "IdentidadeAPI indisponível ao listar colaboradores da T.I");
            return Error(StatusCodes.Status503ServiceUnavailable, "Não foi possível carregar os colaboradores. Tente novamente.");
        }
    }

    // ---------------------------------------------------------------- Gerenciamento (T.I)

    [HttpPost]
    [Authorize]
    public async Task<ActionResult> Post([FromBody] EscalaUpsertDto dto, CancellationToken ct)
    {
        if (!await PodeGerenciarAsync(ct)) return Error(StatusCodes.Status403Forbidden, "Apenas superAdmin ou TI podem criar escalas.");

        var escala = new Escala();
        var erro = await AplicarAsync(dto, escala, ct);
        if (erro is not null) return erro;

        _uof.EscalaRepository.Create(escala);
        await _uof.CommitAsync();
        return new CreatedAtRouteResult("obterEscala", new { id = escala.Id }, escala);
    }

    [HttpPut("{id:int}")]
    [Authorize]
    public async Task<ActionResult> Put(int id, [FromBody] EscalaUpsertDto dto, CancellationToken ct)
    {
        if (!await PodeGerenciarAsync(ct)) return Error(StatusCodes.Status403Forbidden, "Apenas superAdmin ou TI podem editar escalas.");

        var escala = await _uof.EscalaRepository.GetAsync(c => c.Id == id);
        if (escala is null) return Error(StatusCodes.Status404NotFound, "Escala não encontrada.");

        var erro = await AplicarAsync(dto, escala, ct);
        if (erro is not null) return erro;

        _uof.EscalaRepository.Update(escala);
        await _uof.CommitAsync();
        return Ok(escala);
    }

    [HttpDelete("{id:int}")]
    [Authorize]
    public async Task<ActionResult> Delete(int id, CancellationToken ct)
    {
        if (!await PodeGerenciarAsync(ct)) return Error(StatusCodes.Status403Forbidden, "Apenas superAdmin ou TI podem excluir escalas.");

        var escala = await _uof.EscalaRepository.GetAsync(c => c.Id == id);
        if (escala is null) return Error(StatusCodes.Status404NotFound, "Escala não encontrada.");

        _uof.EscalaRepository.Delete(escala);
        await _uof.CommitAsync();
        return Ok();
    }

    /// <summary>Valida os dados e preenche a escala a partir do colaborador da T.I escolhido.</summary>
    private async Task<ObjectResult?> AplicarAsync(EscalaUpsertDto dto, Escala escala, CancellationToken ct)
    {
        var telefone = dto.Telefone?.Trim() ?? string.Empty;
        var horario = dto.Horario?.Trim() ?? string.Empty;

        if (dto.MemberId == Guid.Empty) return Error(StatusCodes.Status400BadRequest, "Selecione o colaborador da T.I.");
        if (!TelefoneValido.IsMatch(telefone)) return Error(StatusCodes.Status400BadRequest, "Informe o telefone do plantão (somente números, espaços, parênteses, + e -).");
        if (dto.Data is null) return Error(StatusCodes.Status400BadRequest, "Informe a data do plantão.");
        if (horario.Length == 0 || horario.Length > 50) return Error(StatusCodes.Status400BadRequest, "Informe o horário (ex.: 07:00 - 13:00).");

        List<ColaboradorInfo> colaboradores;
        try
        {
            colaboradores = await _identidade.GetColaboradoresDoSetorAsync(BearerToken, ct);
        }
        catch (BuildingBlocks.Shared.Exceptions.ServicoIndisponivelException ex)
        {
            _logger.LogWarning(ex, "IdentidadeAPI indisponível ao salvar escala");
            return Error(StatusCodes.Status503ServiceUnavailable, "Não foi possível validar o colaborador. Tente novamente.");
        }

        var colaborador = colaboradores.FirstOrDefault(c => c.MemberId == dto.MemberId);
        if (colaborador is null) return Error(StatusCodes.Status400BadRequest, "O colaborador escolhido não pertence à T.I.");

        var data = dto.Data.Value;
        escala.MemberId = colaborador.MemberId;
        escala.Nome = colaborador.Nome ?? string.Empty;
        escala.Funcao = colaborador.Cargo ?? string.Empty;
        escala.Email = colaborador.Email ?? string.Empty;
        escala.ProfileImg = colaborador.Foto;
        escala.Telefone = telefone;
        escala.Horario = horario;
        escala.DateTime = DateTime.SpecifyKind(data.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        escala.Dia = PtBr.DateTimeFormat.GetDayName(data.DayOfWeek);
        return null;
    }
}

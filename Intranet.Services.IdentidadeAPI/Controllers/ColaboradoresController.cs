using System.Text.Json;
using Intranet.Services.IdentidadeAPI.Data;
using Intranet.Services.IdentidadeAPI.Models;
using Intranet.Services.IdentidadeAPI.Models.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Intranet.Services.IdentidadeAPI.Controllers;

/// <summary>
/// Listas geradas automaticamente a partir dos perfis dos colaboradores:
/// aniversariantes do mês, aniversários de tempo de empresa e ramais.
/// </summary>
[Route("api/[controller]")]
[ApiController]
[Authorize]
public class ColaboradoresController : ControllerBase
{
    private const int MaxPageSize = 100;

    private readonly AppDbContext _db;
    private readonly ILogger<ColaboradoresController> _logger;

    public ColaboradoresController(AppDbContext db, ILogger<ColaboradoresController> logger)
    {
        _db = db;
        _logger = logger;
    }

    // Inicializador de propriedades (não construtor) para o EF conseguir compor Where/OrderBy depois
    private sealed class Linha
    {
        public Member Member { get; init; } = null!;
        public Work? Work { get; init; }
        public UserProfile? Profile { get; init; }
    }

    // Member LEFT JOIN Work LEFT JOIN Profile
    private IQueryable<Linha> Colaboradores() =>
        from m in _db.Members.AsNoTracking()
        join w in _db.Works.AsNoTracking() on m.MemberId equals w.MemberId into works
        from w in works.DefaultIfEmpty()
        join p in _db.Profiles.AsNoTracking() on m.MemberId equals p.MemberId into profiles
        from p in profiles.DefaultIfEmpty()
        select new Linha { Member = m, Work = w, Profile = p };

    private static ColaboradorDto ToDto(Linha l) => new()
    {
        MemberId = l.Member.MemberId,
        Nome = l.Member.Name,
        Email = l.Member.Email,
        Telefone = l.Member.Phone,
        Ramal = l.Work?.Ramal,
        Cargo = l.Work?.Title,
        Departamento = l.Work?.Department,
        Loja = l.Work?.Location,
        Foto = l.Profile?.ProfilePicture,
        TwitterUrl = l.Profile?.TwitterUrl,
        FacebookUrl = l.Profile?.FacebookUrl,
        InstagramUrl = l.Profile?.InstagramUrl,
        LinkedinUrl = l.Profile?.LinkedinUrl,
    };

    private static bool MesValido(int mes) => mes is >= 1 and <= 12;

    /// <summary>Aniversariantes do mês (padrão: mês atual), ordenados pelo dia.</summary>
    [AllowAnonymous]
    [HttpGet("aniversariantes")]
    public async Task<ActionResult> Aniversariantes([FromQuery] int? mes)
    {
        var mesConsulta = mes ?? DateTime.Today.Month;
        if (!MesValido(mesConsulta)) return BadRequest(new ResponseDto { IsSuccess = false, Message = "Mês inválido (1 a 12)." });

        var linhas = await Colaboradores()
            .Where(l => l.Member.BirthDate != null && l.Member.BirthDate.Value.Month == mesConsulta)
            .ToListAsync();

        var result = linhas
            .Select(l =>
            {
                var dto = ToDto(l);
                dto.DiaAniversario = l.Member.BirthDate!.Value.Day;
                dto.MesAniversario = l.Member.BirthDate!.Value.Month;
                return dto;
            })
            .OrderBy(d => d.DiaAniversario)
            .ThenBy(d => d.Nome)
            .ToList();

        return Ok(new ResponseDto { Result = result });
    }

    /// <summary>
    /// Colaboradores que completam anos de empresa no mês (padrão: mês atual).
    /// Considera apenas quem completa pelo menos 1 ano no ano corrente.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("tempo-de-empresa")]
    public async Task<ActionResult> TempoDeEmpresa([FromQuery] int? mes)
    {
        var hoje = DateTime.UtcNow;
        var mesConsulta = mes ?? hoje.Month;
        if (!MesValido(mesConsulta)) return BadRequest(new ResponseDto { IsSuccess = false, Message = "Mês inválido (1 a 12)." });

        var linhas = await Colaboradores()
            .Where(l => l.Work != null
                && l.Work.AdmissionDate != null
                && l.Work.AdmissionDate.Value.Month == mesConsulta
                && l.Work.AdmissionDate.Value.Year < hoje.Year)
            .ToListAsync();

        var result = linhas
            .Select(l =>
            {
                var dto = ToDto(l);
                var admissao = l.Work!.AdmissionDate!.Value;
                dto.DataAdmissao = admissao;
                dto.AnosDeEmpresa = hoje.Year - admissao.Year;
                return dto;
            })
            .OrderBy(d => d.DataAdmissao!.Value.Day)
            .ThenByDescending(d => d.AnosDeEmpresa)
            .ToList();

        return Ok(new ResponseDto { Result = result });
    }

    /// <summary>
    /// Membro do usuário autenticado (claim "id" do token). Usado por outros serviços
    /// para definir a autoria sem confiar no MemberId enviado pelo cliente.
    /// </summary>
    [HttpGet("me")]
    public async Task<ActionResult> Me()
    {
        var userId = User.FindFirst("id")?.Value;
        if (string.IsNullOrEmpty(userId)) return Unauthorized();

        // Departamento: usado por outros serviços para permissões por setor (ex.: escalas da T.I)
        var member = await Colaboradores()
            .Where(l => l.Member.UserId == userId)
            .Select(l => new { l.Member.MemberId, Nome = l.Member.Name, Departamento = l.Work != null ? l.Work.Department : null })
            .FirstOrDefaultAsync();

        if (member is null) return NotFound(new ResponseDto { IsSuccess = false, Message = "Membro não encontrado." });
        return Ok(new ResponseDto { Result = member });
    }

    /// <summary>
    /// Confere quais MemberIds existem e devolve nome/foto (usado por ComunicacaoAPI e
    /// ComunidadesAPI para validar destinatários e membros de grupo).
    /// </summary>
    [HttpPost("lookup")]
    public async Task<ActionResult> Lookup([FromBody] List<Guid> memberIds)
    {
        var ids = memberIds?.Where(id => id != Guid.Empty).Distinct().Take(500).ToList() ?? new();
        if (ids.Count == 0) return Ok(new ResponseDto { Result = Array.Empty<object>() });

        var result = await Colaboradores()
            .Where(l => ids.Contains(l.Member.MemberId))
            .Select(l => new
            {
                l.Member.MemberId,
                Nome = l.Member.Name,
                Foto = l.Profile != null ? l.Profile.ProfilePicture : null,
            })
            .ToListAsync();

        return Ok(new ResponseDto { Result = result });
    }

    /// <summary>
    /// Destinatários da newsletter: todos os membros com e-mail cadastrado
    /// (usado pelo NotificacaoAPI ao publicar um artigo).
    /// </summary>
    [HttpGet("destinatarios")]
    public async Task<ActionResult> Destinatarios()
    {
        var result = await _db.Members.AsNoTracking()
            .Where(m => m.Email != null && m.Email != "")
            .OrderBy(m => m.Name)
            .Select(m => new { m.MemberId, Nome = m.Name, m.Email })
            .ToListAsync();

        return Ok(new ResponseDto { Result = result });
    }

    /// <summary>
    /// Lista de ramais/contatos, paginada. Filtros: busca (nome, e-mail, ramal ou cargo) e departamento.
    /// A paginação vai no header X-Pagination (mesmo formato usado pelo frontend).
    /// Pública: a lista de ramais pode ser consultada sem login.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("ramais")]
    public async Task<ActionResult> Ramais(
        [FromQuery] string? busca,
        [FromQuery] string? departamento,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 48)
    {
        pageNumber = Math.Max(pageNumber, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = Colaboradores();

        if (!string.IsNullOrWhiteSpace(busca))
        {
            var termo = $"%{busca.Trim()}%";
            query = query.Where(l =>
                EF.Functions.ILike(l.Member.Name ?? "", termo) ||
                EF.Functions.ILike(l.Member.Email ?? "", termo) ||
                (l.Work != null && (
                    EF.Functions.ILike(l.Work.Ramal ?? "", termo) ||
                    EF.Functions.ILike(l.Work.Title ?? "", termo))));
        }

        if (!string.IsNullOrWhiteSpace(departamento))
        {
            query = query.Where(l => l.Work != null && l.Work.Department == departamento);
        }

        var total = await query.CountAsync();
        var totalPages = (int)Math.Ceiling(total / (double)pageSize);

        var linhas = await query
            .OrderBy(l => l.Member.Name)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var pagination = new
        {
            TotalCount = total,
            PageSize = pageSize,
            CurrentPage = pageNumber,
            TotalPages = totalPages,
            HasNext = pageNumber < totalPages,
            HasPrevious = pageNumber > 1,
        };
        Response.Headers["X-Pagination"] = JsonSerializer.Serialize(pagination);

        return Ok(new ResponseDto { Result = linhas.Select(ToDto).ToList() });
    }
}

using BuildingBlocks.Shared.Exceptions;
using BuildingBlocks.Shared.Security;
using Intranet.Services.IdentidadeAPI.Data;
using Intranet.Services.IdentidadeAPI.Models.DTOs;
using Intranet.Services.IdentidadeAPI.Service.IService;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Intranet.Services.IdentidadeAPI.Controllers;

/// <summary>
/// Gestor direto e organograma por setor. Consultar exige login; alterar o gestor é restrito aos
/// papéis de Hierarquia:RolesGerenciamento (padrão superAdmin e Admin) e ninguém altera o próprio gestor
/// (as aprovações dos projetos vão depender dessa relação).
/// </summary>
[Route("api/[controller]")]
[ApiController]
[Authorize]
public class HierarquiaController : ControllerBase
{
    private static readonly string[] RolesPadrao = { "superAdmin", "Admin" };

    private readonly IHierarquiaService _hierarquia;
    private readonly AppDbContext _db;
    private readonly IConfiguration _configuration;

    public HierarquiaController(IHierarquiaService hierarquia, AppDbContext db, IConfiguration configuration)
    {
        _hierarquia = hierarquia;
        _db = db;
        _configuration = configuration;
    }

    private bool PodeGerenciar()
    {
        var roles = _configuration.GetSection("Hierarquia:RolesGerenciamento").Get<string[]>() is { Length: > 0 } configuradas
            ? configuradas
            : RolesPadrao;
        return User.HasAnyRole(roles);
    }

    /// <summary>Se o usuário pode alterar gestores (exibe os controles de edição no frontend).</summary>
    [HttpGet("permissoes")]
    public ActionResult Permissoes() =>
        Ok(new ResponseDto { Result = new { PodeGerenciar = PodeGerenciar() } });

    /// <summary>
    /// Organograma: colaboradores do setor (vazio = empresa toda, até 2000) e os gestores acima
    /// deles, mesmo de outros setores (foraDoSetor = true). A árvore é montada pelo gestorMemberId.
    /// </summary>
    [HttpGet("organograma")]
    public async Task<ActionResult> Organograma([FromQuery] string? departamento, CancellationToken ct)
    {
        var result = await _hierarquia.ObterOrganogramaAsync(departamento, ct);
        return Ok(new ResponseDto { Result = result });
    }

    /// <summary>Gestor direto, cadeia de gestores até o topo e liderados diretos do colaborador.</summary>
    [HttpGet("{memberId:guid}")]
    public async Task<ActionResult> Obter(Guid memberId, CancellationToken ct)
    {
        var result = await _hierarquia.ObterHierarquiaAsync(memberId, ct);
        return Ok(new ResponseDto { Result = result });
    }

    /// <summary>Define o gestor direto (gestorMemberId null remove). Devolve a hierarquia atualizada.</summary>
    [HttpPut("{memberId:guid}/gestor")]
    public async Task<ActionResult> AlterarGestor(Guid memberId, [FromBody] AlterarGestorDto? dto, CancellationToken ct)
    {
        if (!PodeGerenciar())
            throw new ForbiddenException("Apenas administradores podem alterar o gestor de um colaborador.");

        // Nem administradores escolhem o próprio gestor
        var userId = User.GetUserId();
        if (!string.IsNullOrEmpty(userId))
        {
            var proprio = await _db.Members.AsNoTracking()
                .AnyAsync(m => m.MemberId == memberId && m.UserId == userId, ct);
            if (proprio) throw new ForbiddenException("Você não pode alterar o seu próprio gestor.");
        }

        var result = await _hierarquia.AlterarGestorAsync(memberId, dto?.GestorMemberId, ct);
        return Ok(new ResponseDto { Result = result, Message = "Gestor atualizado." });
    }
}

using System.Text.Json;
using BuildingBlocks.Contracts.Administracao;
using BuildingBlocks.Shared.Exceptions;
using BuildingBlocks.Shared.Security;
using Intranet.Services.AuthAPI.Models.DTOs;
using Intranet.Services.AuthAPI.Service;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Intranet.Services.AuthAPI.Controllers;

/// <summary>
/// Painel de gestão (superAdmin): usuários, bloqueio, restrições por serviço e papéis.
/// O esquema padrão deste serviço é o do Identity, por isso o JWT é indicado explicitamente.
/// </summary>
[Route("api/admin")]
[ApiController]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = AdminPolicies.SuperAdmin)]
public class AdminController : ControllerBase
{
    private const int MaxPageSize = 100;

    private readonly AdminUsuariosService _service;

    public AdminController(AdminUsuariosService service)
    {
        _service = service;
    }

    private string AdminUserId => User.GetUserId() ?? throw new ForbiddenException("Usuário não identificado.");

    // ---------------------------------------------------------------- usuários

    [HttpGet("usuarios")]
    public async Task<ActionResult> Usuarios(
        [FromQuery] string? busca,
        [FromQuery] string? role,
        [FromQuery] bool? bloqueados,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        pageNumber = Math.Max(pageNumber, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var (itens, total) = await _service.ListarAsync(busca, role, bloqueados, pageNumber, pageSize, ct);
        var totalPages = (int)Math.Ceiling(total / (double)pageSize);

        Response.Headers["X-Pagination"] = JsonSerializer.Serialize(new
        {
            TotalCount = total,
            PageSize = pageSize,
            CurrentPage = pageNumber,
            TotalPages = totalPages,
            HasNext = pageNumber < totalPages,
            HasPrevious = pageNumber > 1,
        });

        return Ok(new ResponseDto { Result = itens });
    }

    [HttpGet("usuarios/{userId}")]
    public async Task<ActionResult> Usuario(string userId, CancellationToken ct) =>
        Ok(new ResponseDto { Result = await _service.ObterAsync(userId, ct) });

    [HttpPost("usuarios/{userId}/bloquear")]
    public async Task<ActionResult> Bloquear(string userId, [FromBody] BloquearUsuarioRequest request, CancellationToken ct) =>
        Ok(new ResponseDto { Result = await _service.BloquearAsync(AdminUserId, userId, request, ct), Message = "Usuário bloqueado." });

    [HttpPost("usuarios/{userId}/desbloquear")]
    public async Task<ActionResult> Desbloquear(string userId, CancellationToken ct) =>
        Ok(new ResponseDto { Result = await _service.DesbloquearAsync(AdminUserId, userId, ct), Message = "Usuário desbloqueado." });

    [HttpPut("usuarios/{userId}/restricoes")]
    public async Task<ActionResult> Restricoes(string userId, [FromBody] RestricoesUsuarioRequest request, CancellationToken ct) =>
        Ok(new ResponseDto { Result = await _service.DefinirRestricoesAsync(AdminUserId, userId, request, ct), Message = "Restrições atualizadas." });

    [HttpPost("usuarios/{userId}/roles")]
    public async Task<ActionResult> AdicionarRole(string userId, [FromBody] AtribuirRoleRequest request, CancellationToken ct) =>
        Ok(new ResponseDto { Result = await _service.AdicionarRoleAsync(AdminUserId, userId, request.Role, ct), Message = "Papel atribuído." });

    [HttpDelete("usuarios/{userId}/roles/{role}")]
    public async Task<ActionResult> RemoverRole(string userId, string role, CancellationToken ct) =>
        Ok(new ResponseDto { Result = await _service.RemoverRoleAsync(AdminUserId, userId, role, ct), Message = "Papel removido." });

    // ---------------------------------------------------------------- serviços restringíveis

    [HttpGet("servicos")]
    public ActionResult Servicos() =>
        Ok(new ResponseDto
        {
            Result = ServicosRestringiveis.Nomes
                .Select(s => new ServicoRestringivelDto { Chave = s.Key, Nome = s.Value })
                .ToList(),
        });

    // ---------------------------------------------------------------- papéis

    [HttpGet("roles")]
    public async Task<ActionResult> Roles(CancellationToken ct) =>
        Ok(new ResponseDto { Result = await _service.ListarRolesAsync(ct) });

    [HttpPost("roles")]
    public async Task<ActionResult> CriarRole([FromBody] CriarRoleRequest request)
    {
        var role = await _service.CriarRoleAsync(request.Nome);
        return Ok(new ResponseDto { Result = new { role.Nome }, Message = "Papel criado." });
    }

    [HttpDelete("roles/{nome}")]
    public async Task<ActionResult> ExcluirRole(string nome)
    {
        await _service.ExcluirRoleAsync(nome);
        return Ok(new ResponseDto { Message = "Papel excluído." });
    }
}

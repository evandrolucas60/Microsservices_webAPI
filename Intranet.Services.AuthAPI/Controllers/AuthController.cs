using BuildingBlocks.Contracts.Administracao;
using BuildingBlocks.Contracts.Autenticacao;
using BuildingBlocks.Contracts.Identidade;
using Intranet.Services.AuthAPI.Filters;
using Intranet.Services.AuthAPI.Models;
using Intranet.Services.AuthAPI.Models.DTOs;
using Intranet.Services.AuthAPI.Service;
using Intranet.Services.AuthAPI.Service.IService;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace Intranet.Services.AuthAPI.Controllers;

[Route("api/auth")]
[ApiController]
public class AuthController : ControllerBase
{
    private readonly ITokenService _tokenService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<IdentityRole> _roleManager;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthController> _logger;
    private readonly MassTransit.IPublishEndpoint _publish;

    public AuthController(ITokenService tokenService, UserManager<ApplicationUser> userManager, RoleManager<IdentityRole> roleManager, IConfiguration configuration, ILogger<AuthController> logger, MassTransit.IPublishEndpoint publish)
    {
        _publish = publish;
        _tokenService = tokenService;
        _userManager = userManager;
        _roleManager = roleManager;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpPost]
    [Route("CreateRole")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = Program.AdminRole)]
    public async Task<IActionResult> CreateRole(string roleName)
    {
        var roleExists = await _roleManager.RoleExistsAsync(roleName);

        if (!roleExists)
        {
            var roleResult = await _roleManager.CreateAsync(new IdentityRole(roleName));

            if (roleResult.Succeeded)
            {
                _logger.LogInformation(1, "Roles Added");

                return StatusCode(StatusCodes.Status200OK,
                    new Response
                    {
                        Status = "Success",
                        Message = $"Role {roleName} added successfully"
                    });
            }
            else
            {
                _logger.LogInformation(2, "Error");

                return StatusCode(StatusCodes.Status400BadRequest,
                    new Response
                    {
                        Status = "Error",
                        Message = $"Issue adding the new {roleName} role"
                    });
            }
        }
        return StatusCode(StatusCodes.Status400BadRequest,
            new Response
            {
                Status = "Error",
                Message = $"Role {roleName} Already exist."
            });
    }

    [HttpPost]
    [Route("AddUserToRole")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = Program.AdminRole)]
    public async Task<IActionResult> AddUserToRole(string email, string roleName)
    {
        var user = await _userManager.FindByEmailAsync(email);

        if (user != null)
        {
            var result = await _userManager.AddToRoleAsync(user, roleName);

            if (result.Succeeded)
            {
                _logger.LogInformation(1, $"user {user.Email} add to the {roleName} role");

                return StatusCode(StatusCodes.Status200OK,
                    new Response
                    {
                        Status = "Success",
                        Message = $"User {user.Email} added to the {roleName} role"
                    });
            }
            else
            {
                _logger.LogInformation(1, $"Error : Unable to add user {user.Email} to tthe {roleName} role");
                return StatusCode(StatusCodes.Status400BadRequest,
                    new Response
                    {
                        Status = "Error",
                        Message = $"Erro: Unable to add user {user.Email} to the {roleName} role"
                    });
            }
        }
        return BadRequest(new { error = "Unable to find user" });
    }

    [HttpPost]
    [Route("login")]
    public async Task<IActionResult> Login([FromBody] LoginModel model)
    {
        var user = string.IsNullOrWhiteSpace(model.Username) ? null : await _userManager.FindByNameAsync(model.Username);

        if (user is null)
        {
            await PublicarAcessoAsync(null, model.Username, false, MotivoUsuarioInexistente);
            return Unauthorized(new { isSuccess = false, message = MensagemCredenciaisInvalidas });
        }

        // CheckPasswordAsync não incrementa falhas: senha errada não bloqueia a conta.
        // O lockout do Identity é usado só para o bloqueio manual do superAdmin.
        if (string.IsNullOrEmpty(model.Password) || !await _userManager.CheckPasswordAsync(user, model.Password))
        {
            await PublicarAcessoAsync(user.Id, user.UserName, false, MotivoCredenciaisInvalidas);
            return Unauthorized(new { isSuccess = false, message = MensagemCredenciaisInvalidas });
        }

        // Senha correta, mas conta bloqueada pelo superAdmin
        if (AdminUsuariosService.EstaBloqueado(user))
        {
            await PublicarAcessoAsync(user.Id, user.UserName, false, MotivoContaBloqueada);
            return StatusCode(StatusCodes.Status403Forbidden, ContaBloqueada(user));
        }

        var userRoles = await _userManager.GetRolesAsync(user);

        var authClaims = new List<Claim>
        {
            new Claim(ClaimTypes.Name, user.UserName!),
            new Claim(ClaimTypes.Email, user.Email!),
            new Claim("id", user.Id!),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        foreach (var userRole in userRoles)
        {
            authClaims.Add(new Claim(ClaimTypes.Role, userRole));
        }

        var token = _tokenService.GenerateAccessToken(authClaims, _configuration);

        var refreshToken = _tokenService.GenerateRefreshToken();

        _ = int.TryParse(_configuration["JWT:RefreshTokenValidityInMinutes"], out int refreshTokenValidityInMinutes);

        user.RefreshTokenExpiryTime = DateTime.UtcNow.AddMinutes(refreshTokenValidityInMinutes);

        user.RefreshToken = refreshToken;

        await _userManager.UpdateAsync(user);

        // Último acesso (painel "Most Recent Signin"): evento assíncrono para o IdentidadeAPI.
        // Falha ao publicar não impede o login.
        try
        {
            await _publish.Publish(new UsuarioAutenticado { UserId = user.Id!, AutenticadoEm = DateTime.UtcNow }, HttpContext.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Não foi possível publicar o login do usuário {UserId}", user.Id);
        }

        await PublicarAcessoAsync(user.Id, user.UserName, true, null);

        return Ok(
            new
            {
                Token = new JwtSecurityTokenHandler().WriteToken(token),
                RefreshToken = refreshToken,
                Expiration = token.ValidTo,
                Id = user.Id,
            });
    }

    private const string MensagemCredenciaisInvalidas = "Usuário ou senha inválidos.";
    private const string MensagemContaBloqueada = "Sua conta está bloqueada. Procure a administração do portal.";
    private const string MotivoCredenciaisInvalidas = "credenciais inválidas";
    private const string MotivoContaBloqueada = "conta bloqueada";
    private const string MotivoUsuarioInexistente = "usuário inexistente";

    /// <summary>Corpo da recusa por bloqueio (login e renovação do token).</summary>
    private static object ContaBloqueada(ApplicationUser user)
    {
        var ate = AdminUsuariosService.BloqueadoAte(user);
        return new
        {
            isSuccess = false,
            message = ate is null
                ? MensagemContaBloqueada
                : $"Sua conta está bloqueada até {ate.Value:dd/MM/yyyy HH:mm} (UTC). Procure a administração do portal.",
            bloqueado = true,
            bloqueadoAte = ate,
        };
    }

    /// <summary>Registro de acesso para a auditoria (AuditoriaAPI). Falha ao publicar não afeta o login.</summary>
    private async Task PublicarAcessoAsync(string? userId, string? userName, bool sucesso, string? motivo)
    {
        try
        {
            var ip = HttpContext.Connection.RemoteIpAddress;
            if (ip is not null && ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await _publish.Publish(new AcessoRegistrado
            {
                UserId = userId,
                UserName = userName is { Length: > 256 } ? userName[..256] : userName,
                Sucesso = sucesso,
                Motivo = motivo,
                Ip = ip?.ToString(),
                Em = DateTime.UtcNow,
            }, timeout.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Não foi possível publicar o registro de acesso de {UserName}", userName);
        }
    }

    private const string MensagemEmailJaCadastrado = "Já existe uma conta com este e-mail.";

    /// <summary>
    /// Cria a conta e publica <see cref="ColaboradorCadastrado"/> para o IdentidadeAPI montar o
    /// card de Ramais (Member + Work + Profile). Erros: 400/409 { isSuccess, message, errors: { campo: [msgs] } }.
    /// </summary>
    [HttpPost]
    [Route("register")]
    [ErrosDeValidacao]
    public async Task<IActionResult> Register([FromBody] RegisterModel model)
    {
        var email = model.Email!.Trim();

        if (await _userManager.FindByNameAsync(email) is not null || await _userManager.FindByEmailAsync(email) is not null)
        {
            return EmailJaCadastrado();
        }

        ApplicationUser user = new()
        {
            Email = email,
            SecurityStamp = Guid.NewGuid().ToString(),
            UserName = email
        };
        var result = await _userManager.CreateAsync(user, model.Password!);

        if (!result.Succeeded)
        {
            // Cadastro simultâneo com o mesmo e-mail (o índice único do Identity barra o segundo)
            if (result.Errors.Any(e => e.Code is nameof(IdentityErrorDescriber.DuplicateUserName) or nameof(IdentityErrorDescriber.DuplicateEmail)))
            {
                return EmailJaCadastrado();
            }

            _logger.LogInformation("Cadastro recusado pelo Identity para {Email}: {Codigos}", email, string.Join(", ", result.Errors.Select(e => e.Code)));
            return BadRequest(new
            {
                isSuccess = false,
                message = ErrosDeValidacaoAttribute.MensagemPadrao,
                errors = ErrosIdentityPtBr.Traduzir(result.Errors, _userManager.Options.Password),
            });
        }

        // Dados do card de Ramais → IdentidadeAPI (ColaboradorCadastradoConsumer).
        // Se o RabbitMQ estiver fora, a conta continua criada: o Member/Profile nasce vazio no
        // primeiro acesso (GetProfileInfo) e o colaborador completa os dados em Configurações.
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await _publish.Publish(new ColaboradorCadastrado
            {
                UserId = user.Id,
                Email = email,
                Nome = model.Nome!.Trim(),
                Setor = model.Setor!.Trim(),
                Cargo = model.Cargo!.Trim(),
                Loja = model.Loja!.Trim(),
                Ramal = model.Ramal!.Trim(),
                Telefone = model.Telefone!.Trim(),
                Em = DateTime.UtcNow,
            }, timeout.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Conta {UserId} ({Email}) criada, mas não foi possível publicar ColaboradorCadastrado; o card de Ramais ficará incompleto até o colaborador preencher em Configurações. Dados: setor={Setor}, cargo={Cargo}, loja={Loja}, ramal={Ramal}",
                user.Id, email, model.Setor, model.Cargo, model.Loja, model.Ramal);
        }

        return Ok(new { isSuccess = true, message = "Conta criada! Faça login." });
    }

    private ObjectResult EmailJaCadastrado() => StatusCode(StatusCodes.Status409Conflict, new
    {
        isSuccess = false,
        message = MensagemEmailJaCadastrado,
        errors = new Dictionary<string, string[]> { [ErrosIdentityPtBr.CampoEmail] = [MensagemEmailJaCadastrado] },
    });

    [HttpPost]
    [Route("RefreshToken")]
    public async Task<IActionResult> RefreshToken(TokenModel tokenModel)
    {
        // Corpo, tokens ausentes ou access token malformado → 400 (antes estouravam como 500)
        if (tokenModel == null || string.IsNullOrWhiteSpace(tokenModel.AccessToken) || string.IsNullOrWhiteSpace(tokenModel.RefreshToken))
        {
            return BadRequest("Invalid client request");
        }
        string accessToken = tokenModel.AccessToken;
        string refreshToken = tokenModel.RefreshToken;

        var principal = _tokenService.GetClaimsPrincipalFromExpiredToken(accessToken, _configuration);

        string? userName = principal?.Identity?.Name;
        if (string.IsNullOrEmpty(userName))
        {
            return BadRequest("Invalid access token/refresh token");
        }
        var user = await _userManager.FindByNameAsync(userName);

        if (user == null
            || user.RefreshToken != refreshToken
            // A expiração é gravada em UTC (ver Login)
            || user.RefreshTokenExpiryTime <= DateTime.UtcNow
            )
        {
            return BadRequest("Invalid access token/refresh token - user null");
        }

        // Conta bloqueada pelo superAdmin: não renova (o bloqueio também revoga o refresh token)
        if (AdminUsuariosService.EstaBloqueado(user))
        {
            return StatusCode(StatusCodes.Status403Forbidden, ContaBloqueada(user));
        }

        // Claims montados a partir do usuário atual (papéis atualizados), não copiados do token antigo
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.Name, user.UserName!),
            new Claim(ClaimTypes.Email, user.Email!),
            new Claim("id", user.Id!),
        };
        foreach (var role in await _userManager.GetRolesAsync(user))
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        var newAccessToken = _tokenService.GenerateAccessToken(claims, _configuration);
        var newRefreshtoken = _tokenService.GenerateRefreshToken();
        user.RefreshToken = newRefreshtoken;

        await _userManager.UpdateAsync(user);

        return new ObjectResult(
            new
            {
                AccessToken = new JwtSecurityTokenHandler().WriteToken(newAccessToken),
                RefreshToken = newRefreshtoken,
            });
    }

    [HttpPost]
    [Route("revoke/{id}")]
    public async Task<IActionResult> Revoke(string id)
    {
        // Aceita access token expirado (assinatura válida): o logout costuma acontecer
        // justamente após a expiração e precisa invalidar o refresh token mesmo assim.
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return Unauthorized();

        var caller = _tokenService.GetClaimsPrincipalFromExpiredToken(header["Bearer ".Length..].Trim(), _configuration);
        if (caller is null) return Unauthorized();

        // Só o próprio usuário (claim "id") ou um Admin pode encerrar a sessão
        var callerId = caller.FindFirst("id")?.Value;
        if (!string.Equals(callerId, id, StringComparison.OrdinalIgnoreCase) && !caller.IsInRole(Program.AdminRole))
            return StatusCode(StatusCodes.Status403Forbidden);

        var user = await _userManager.FindByIdAsync(id);

        if (user == null) return BadRequest("Invaild username");

        // Revogando o refresh token
        user.RefreshToken = null;
        var result = await _userManager.UpdateAsync(user);

        if (!result.Succeeded)
        {
            // Caso ocorra algum erro durante a atualização do usuário
            return StatusCode(500, "Internal server error");
        }

        return NoContent(); // Logout bem-sucedido
    }
}

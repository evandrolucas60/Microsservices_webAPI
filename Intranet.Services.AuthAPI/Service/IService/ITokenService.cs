using System.Collections;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace Intranet.Services.AuthAPI.Service.IService
{
    public interface ITokenService
    {
        JwtSecurityToken GenerateAccessToken(IEnumerable<Claim> claims, IConfiguration _config);
        string GenerateRefreshToken();
        /// <summary>null quando o token é vazio, malformado ou tem assinatura inválida.</summary>
        ClaimsPrincipal? GetClaimsPrincipalFromExpiredToken(string token, IConfiguration _config);

    }
}

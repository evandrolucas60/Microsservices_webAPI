using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;

namespace Escala5x2.BuildingBlocks.Shared.Auth;

/// <summary>
/// Depois de validar o JWT da intranet, troca as roles da intranet pelo papel 5x2 e acrescenta as claims
/// que o <see cref="ICurrentUser"/> e as políticas usam: sub (Guid), name, role, store (loja principal), escala_store
/// (uma por loja permitida), escala_store_gestor (uma por loja em que é Gestor) (+ store_code, store_name e loja para
/// GET /api/me). As roles originais ficam em "intranet_role" (não valem nas políticas)
/// e a claim "id" da intranet é mantida (bloqueio de usuários e auditoria).
/// </summary>
public sealed class EscalaClaimsTransformation(IdentidadeEscalaService identidades, IHttpContextAccessor accessor) : IClaimsTransformation
{
    // Claims recalculadas aqui: as que vierem no token da intranet são descartadas
    private static readonly HashSet<string> Substituidas = new(StringComparer.Ordinal)
    {
        EscalaClaims.Sub, EscalaClaims.Name, EscalaClaims.Role, ClaimTypes.Role, EscalaClaims.Store,
        EscalaClaims.StoreCode, EscalaClaims.StoreName, EscalaClaims.Loja, EscalaClaims.IntranetRole,
        EscalaClaims.Stores, EscalaClaims.StoresGestor,
    };

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        // O ASP.NET pode chamar a transformação mais de uma vez na mesma requisição
        if (principal.Identities.Any(i => i.AuthenticationType == EscalaClaims.AuthenticationType)) return principal;
        if (principal.Identity?.IsAuthenticated != true) return principal;

        var ct = accessor.HttpContext?.RequestAborted ?? CancellationToken.None;
        IdentidadeEscala? identidade;
        try
        {
            identidade = await identidades.ObterAsync(principal, ct);
        }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is not OperationCanceledException)
        {
            // Cliente/gateway desistiu da requisição: nada foi guardado em cache. Vira OperationCanceledException,
            // que o UseServiceDefaults encerra em silêncio (499, sem log de erro).
            throw new OperationCanceledException("Requisição abortada pelo cliente.", ex, ct);
        }
        // Sem a claim "id" o token é recusado antes (OnTokenValidated); aqui só por garantia
        if (identidade is null) return new ClaimsPrincipal(new ClaimsIdentity());

        var claims = principal.Claims
            .Where(c => !Substituidas.Contains(c.Type))
            .Select(c => new Claim(c.Type, c.Value, c.ValueType, c.Issuer))
            .ToList();
        claims.AddRange(IdentidadeEscalaService.RolesIntranet(principal).Select(r => new Claim(EscalaClaims.IntranetRole, r)));
        claims.Add(new Claim(EscalaClaims.Sub, identidade.Sub.ToString()));
        claims.Add(new Claim(EscalaClaims.Name, identidade.Name));
        claims.Add(new Claim(EscalaClaims.Role, identidade.Role));
        if (identidade.StoreId is { } store) claims.Add(new Claim(EscalaClaims.Store, store.ToString()));
        if (identidade.StoreCode is { } code) claims.Add(new Claim(EscalaClaims.StoreCode, code));
        if (identidade.StoreName is { } storeName) claims.Add(new Claim(EscalaClaims.StoreName, storeName));
        if (identidade.Loja is { } loja) claims.Add(new Claim(EscalaClaims.Loja, loja));
        // Lojas permitidas (perfil ∪ roles de loja) e as que ele gerencia: ICurrentUser.StoreIds/RoleIn
        claims.AddRange(identidade.StoreIds.Select(id => new Claim(EscalaClaims.Stores, id.ToString())));
        claims.AddRange(identidade.ManagedStoreIds.Select(id => new Claim(EscalaClaims.StoresGestor, id.ToString())));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, EscalaClaims.AuthenticationType, EscalaClaims.Name, EscalaClaims.Role));
    }
}

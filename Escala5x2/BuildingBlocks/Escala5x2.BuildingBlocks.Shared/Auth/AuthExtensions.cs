using System.Text;
using BuildingBlocks.Shared.Security;
using Escala5x2.BuildingBlocks.Shared.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace Escala5x2.BuildingBlocks.Shared.Auth;

public static class AuthExtensions
{
    /// <summary>
    /// Autenticação pelo JWT da intranet (seção "ApiSettings", igual aos serviços da intranet) e cálculo da
    /// identidade 5x2 (papel/loja) por <see cref="EscalaClaimsTransformation"/>. O bloqueio de usuários
    /// (UserAccess da intranet) é aplicado em <c>UseServiceDefaults</c>. Requer <c>AddEventBus</c>
    /// (requisições ao IdentidadeAPI e ao AuthAPI pelo RabbitMQ).
    /// </summary>
    public static IServiceCollection AddJwtAuthentication(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var settings = configuration.GetSection(ApiSettings.Section).Get<ApiSettings>() ?? new ApiSettings();
        if (string.IsNullOrWhiteSpace(settings.Secret))
        {
            throw new InvalidOperationException("ApiSettings:Secret não configurado (mesma chave do AuthAPI da intranet).");
        }

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(o =>
            {
                // Claims com os nomes do JWT (id, unique_name, role); a transformação gera sub/name/role/store
                o.MapInboundClaims = false;
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(settings.Secret)),
                    ValidateIssuer = true,
                    ValidIssuer = settings.Issuer,
                    ValidateAudience = true,
                    ValidAudience = settings.Audience,
                    ValidateLifetime = true,
                    NameClaimType = EscalaClaims.Name,
                    RoleClaimType = EscalaClaims.Role
                };
                o.Events = new JwtBearerEvents
                {
                    // Token da intranet sem o id do usuário não identifica ninguém → 401
                    OnTokenValidated = context =>
                    {
                        if (string.IsNullOrWhiteSpace(context.Principal?.FindFirst(EscalaClaims.IntranetId)?.Value))
                        {
                            context.Fail("Token sem a claim \"id\" do usuário da intranet.");
                        }
                        return Task.CompletedTask;
                    }
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Authenticated, p => p.RequireAuthenticatedUser())
            .AddPolicy(Policies.Admin, p => p.RequireRole(Roles.Admin))
            .AddPolicy(Policies.Manager, p => p.RequireRole(Roles.Admin, Roles.Gestor))
            .AddPolicy(Policies.HumanResources, p => p.RequireRole(Roles.Admin, Roles.RH, Roles.Gestor))
            .AddPolicy(Policies.Legal, p => p.RequireRole(Roles.Admin, Roles.Juridico))
            .AddPolicy(Policies.Audit, p => p.RequireRole(Roles.Admin, Roles.Juridico))
            .AddPolicy(Policies.LgSync, p => p.RequireRole(Roles.Admin, Roles.RH));

        // Identidade 5x2: papel pelas roles da intranet e loja pelo perfil (IdentidadeAPI) + lojas do Store.Api
        services.AddMemoryCache();
        services.AddHttpContextAccessor();
        services.AddScoped<IdentidadeEscalaService>();
        services.AddScoped<IClaimsTransformation, EscalaClaimsTransformation>();
        services.AddTransient<InternalKeyHandler>();
        // Padrão: /internal/stores do Store.Api. O Store.Api substitui por consulta ao próprio banco.
        services.AddHttpClient<ILojasEscala, LojasEscalaHttpClient>(c =>
            {
                c.BaseAddress = new Uri(configuration["Services:Store"] ?? "http://localhost:7022");
                // Curto: a consulta acontece na autenticação e segura a requisição do usuário
                c.Timeout = TimeSpan.FromSeconds(5);
            })
            .AddHttpMessageHandler<InternalKeyHandler>()
            .AcceptLocalDevCertificate(environment);

        // Bloqueio de usuários pelo superAdmin da intranet (mesmo serviço/middleware dos serviços da intranet)
        // (com limite próprio de espera: RestricoesUsuarioEscala)
        services.AddScoped<RestricoesUsuarioService>();
        services.AddScoped<IRestricoesUsuarioService, RestricoesUsuarioEscala>();
        services.AddSingleton<IntranetBusMarker>();

        services.AddScoped<ICurrentUser, CurrentUser>();
        return services;
    }

    /// <summary>
    /// GET /api/me: identidade 5x2 do usuário logado (papel e lojas), usada pelo front para montar as telas.
    /// O gateway expõe como /api/escala5x2/me. Formato: userId (id da intranet), name, role, storeId (loja principal),
    /// storeCode, storeName, loja (do perfil), storeIds (lojas permitidas: perfil ∪ roles "Escala5x2-Loja-{code}") e
    /// managedStoreIds (lojas em que é Gestor). Papel efetivo por loja: <see cref="EscopoLojas"/> (o front repete a regra).
    /// </summary>
    public static RouteHandlerBuilder MapMeEndpoint(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/me", (HttpContext http, ICurrentUser current) =>
        {
            var user = http.User;
            return Results.Ok(new
            {
                userId = user.FindFirst(EscalaClaims.IntranetId)?.Value,
                name = user.FindFirst(EscalaClaims.Name)?.Value,
                role = user.FindFirst(EscalaClaims.Role)?.Value,
                storeId = current.StoreId,
                storeCode = user.FindFirst(EscalaClaims.StoreCode)?.Value,
                storeName = user.FindFirst(EscalaClaims.StoreName)?.Value,
                loja = user.FindFirst(EscalaClaims.Loja)?.Value,
                storeIds = current.StoreIds.ToList(),
                managedStoreIds = current.ManagedStoreIds.ToList(),
            });
        }).RequireAuthorization();
}

/// <summary>Indica ao <c>AddEventBus</c> que o serviço autentica usuários da intranet (registra os request clients).</summary>
internal sealed class IntranetBusMarker;

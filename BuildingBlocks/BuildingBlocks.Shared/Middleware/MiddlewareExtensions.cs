using Microsoft.AspNetCore.Builder;
using Serilog;

namespace BuildingBlocks.Shared.Middleware;

public static class MiddlewareExtensions
{
    /// <summary>Início do pipeline: id de correlação e log de cada requisição (Serilog).</summary>
    public static IApplicationBuilder UseSharedRequestLogging(this IApplicationBuilder app)
    {
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseSerilogRequestLogging(options =>
        {
            options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} respondeu {StatusCode} em {Elapsed:0.0} ms";
        });
        return app;
    }

    /// <summary>Tratamento de erros em JSON. Chame depois do UseCors.</summary>
    public static IApplicationBuilder UseSharedExceptionHandling(this IApplicationBuilder app) =>
        app.UseMiddleware<ExceptionHandlingMiddleware>();

    /// <summary>
    /// Barra usuários bloqueados pelo superAdmin e inclui o UserId nos logs.
    /// Chame depois do UseAuthentication (requer AddMessaging).
    /// </summary>
    public static IApplicationBuilder UseSharedUserAccess(this IApplicationBuilder app) =>
        app.UseMiddleware<UserAccessMiddleware>();
}

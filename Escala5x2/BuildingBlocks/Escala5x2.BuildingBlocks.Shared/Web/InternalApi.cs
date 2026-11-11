using BuildingBlocks.Shared.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Escala5x2.BuildingBlocks.Shared.Web;

/// <summary>
/// Protege endpoints /internal/* usados só entre serviços. O gateway não roteia /internal,
/// e a chamada precisa do cabeçalho X-Internal-Key igual a Internal:ApiKey.
/// </summary>
public static class InternalApi
{
    public const string HeaderName = "X-Internal-Key";

    public static RouteGroupBuilder MapInternalGroup(this IEndpointRouteBuilder app, string prefix = "/internal")
    {
        var group = app.MapGroup(prefix).AllowAnonymous().ExcludeFromDescription();
        group.AddEndpointFilter(async (ctx, next) =>
        {
            var expected = ctx.HttpContext.RequestServices.GetRequiredService<IConfiguration>()["Internal:ApiKey"];
            var provided = ctx.HttpContext.Request.Headers[HeaderName].ToString();
            if (string.IsNullOrEmpty(expected) || !string.Equals(expected, provided, StringComparison.Ordinal))
            {
                return Results.Unauthorized();
            }
            return await next(ctx);
        });
        return group;
    }
}

/// <summary>Adiciona X-Internal-Key e X-Correlation-Id às chamadas HTTP entre serviços.</summary>
public sealed class InternalKeyHandler(IConfiguration configuration, IHttpContextAccessor accessor) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.TryAddWithoutValidation(InternalApi.HeaderName, configuration["Internal:ApiKey"]);
        // O CorrelationIdMiddleware da intranet guarda o id de correlação em TraceIdentifier
        var correlation = accessor.HttpContext?.TraceIdentifier;
        if (!string.IsNullOrEmpty(correlation))
        {
            request.Headers.TryAddWithoutValidation(CorrelationIdMiddleware.HeaderName, correlation);
        }
        return base.SendAsync(request, cancellationToken);
    }
}
